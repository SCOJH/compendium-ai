# Compendium.Adapters.Bedrock

[![NuGet](https://img.shields.io/nuget/v/Compendium.Adapters.Bedrock.svg)](https://www.nuget.org/packages/Compendium.Adapters.Bedrock)

AWS Bedrock implementation of Compendium's `IAIProvider` — one adapter, every hosted
model family : **Anthropic Claude**, **Meta Llama**, **Mistral**, **Amazon Nova**,
**Amazon Titan**, **Cohere**. Text completions (sync + streaming) flow through
Bedrock's unified `Converse` / `ConverseStream` API; embeddings flow through
`InvokeModel` with per-family payload shapes.

This adapter is for callers who want AWS data-residency, IAM-based access control,
SigV4 auth, and Amazon's commercial terms — not the direct Anthropic API
(see [`Compendium.Adapters.Anthropic`](https://www.nuget.org/packages/Compendium.Adapters.Anthropic)
for that).

## Quick start

```csharp
using Compendium.Abstractions.AI;
using Compendium.Abstractions.AI.Models;
using Compendium.Adapters.Bedrock.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddCompendiumBedrock(o =>
{
    o.Region = "us-east-1";
    o.DefaultModelId = "anthropic.claude-3-5-sonnet-20241022-v2:0";
    o.EmbeddingModelId = "amazon.titan-embed-text-v2:0";
    // Credentials default to the AWS SDK chain (env, profile, IAM role).
});

var sp = services.BuildServiceProvider();
var ai = sp.GetRequiredService<IAIProvider>();

var result = await ai.CompleteAsync(new CompletionRequest
{
    Model = "anthropic.claude-3-haiku-20240307-v1:0",
    Messages = new List<Message> { Message.User("Hello, Bedrock.") },
});

Console.WriteLine(result.Value.Content);
```

Run the sample :

```bash
AWS_REGION=us-east-1 dotnet run --project samples/01-chat-with-claude-via-bedrock
```

## IAM policy

The runtime adapter needs only data-plane permissions :

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "BedrockRuntime",
      "Effect": "Allow",
      "Action": [
        "bedrock:Converse",
        "bedrock:ConverseStream",
        "bedrock:InvokeModel",
        "bedrock:InvokeModelWithResponseStream"
      ],
      "Resource": [
        "arn:aws:bedrock:*::foundation-model/*",
        "arn:aws:bedrock:*:*:inference-profile/*"
      ]
    }
  ]
}
```

Scope `Resource` down to specific model ARNs in production. The adapter does **not**
require `bedrock:ListFoundationModels` — the model catalog is curated in-package.

## Options

| Property            | Default                                          | Notes |
|---------------------|--------------------------------------------------|-------|
| `Region`            | _(required)_                                     | e.g. `us-east-1`. Bedrock model availability varies by region. |
| `AccessKey`         | `null`                                           | Optional override. Prefer IAM roles in production. |
| `SecretKey`         | `null`                                           | Paired with `AccessKey`. |
| `SessionToken`      | `null`                                           | Optional STS session token for assumed-role credentials. |
| `DefaultModelId`    | `anthropic.claude-3-5-sonnet-20241022-v2:0`       | Used when `CompletionRequest.Model` is null/blank. |
| `EmbeddingModelId`  | `amazon.titan-embed-text-v2:0`                   | Auto-detects Titan vs Cohere by id prefix. |
| `DefaultMaxTokens`  | `4096`                                           | Used when `CompletionRequest.MaxTokens` is null. |
| `Timeout`           | `120s`                                           | Per-request timeout. |
| `MaxRetries`        | `3`                                              | AWS SDK retry attempts for transient errors. |

Bind from `IConfiguration` :

```jsonc
{
  "Compendium": {
    "Adapters": {
      "Bedrock": {
        "Region": "us-east-1",
        "DefaultModelId": "anthropic.claude-3-5-sonnet-20241022-v2:0",
        "EmbeddingModelId": "amazon.titan-embed-text-v2:0"
      }
    }
  }
}
```

```csharp
services.AddCompendiumBedrock(Configuration);
```

## Model id routing

Bedrock's `Converse` API is family-agnostic — the adapter passes the model id
through as-is. Use the canonical Bedrock id format :

- `anthropic.claude-3-5-sonnet-20241022-v2:0`
- `anthropic.claude-3-haiku-20240307-v1:0` (cheapest Claude — great default for dev/CI)
- `meta.llama3-1-70b-instruct-v1:0`
- `mistral.mistral-large-2407-v1:0`
- `amazon.nova-pro-v1:0`

Cross-region inference profiles work too :

- `us.anthropic.claude-3-5-sonnet-20241022-v2:0`
- `eu.anthropic.claude-3-5-sonnet-20241022-v2:0`

Embeddings auto-detect the family by id prefix :

- `amazon.titan-embed-*` → Titan single-text per call, supports the `dimensions` hint.
- `cohere.embed-*` → Cohere batch (one HTTP call for the whole input list), defaults to
  `input_type = "search_document"`.

Unknown embedding ids fail fast with `AI.InvalidRequest`.

## Billing & throughput

Bedrock offers two billing modes per model :

1. **On-demand** — pay per token. The default; just call the adapter.
2. **Provisioned throughput** — pay for reserved capacity by the hour. Set
   `DefaultModelId` to the **provisioned model ARN** (`arn:aws:bedrock:...:provisioned-model/...`)
   and the adapter routes there.

Prompt caching (currently Anthropic-only on Bedrock) is **not** yet exposed by this
preview; it will land behind a `BedrockOptions.EnablePromptCaching` flag in a follow-up.

## Data residency

All requests stay within the configured `Region`. Bedrock does not train on your
prompts. For workloads with strict residency requirements, pick a region in the
appropriate AWS partition (`eu-central-1`, `ap-northeast-1`, GovCloud, ...) and
confirm model availability with `aws bedrock list-foundation-models`.

## Production checklist

- [ ] **IAM role**, not static access keys — use EKS pod identity or EC2 instance profile.
- [ ] **Region pinning** — set `Region` explicitly; do not rely on the AWS SDK's
      `AWS_REGION` env var alone in multi-region deployments.
- [ ] **Scope IAM resources** to specific model ARNs.
- [ ] **Set `MaxRetries`** in line with your latency SLO — Bedrock returns 429 under load.
- [ ] **Log via `ILogger<BedrockAIProvider>`** — turn it on for failed-request diagnostics.
- [ ] **Hook a health check** — `HealthCheckAsync()` sends a 1-token probe.
- [ ] **Cap `DefaultMaxTokens`** to avoid runaway costs from misbehaving callers.
- [ ] **Pin the model id** — Bedrock occasionally retires legacy versions.

## Structured output

`CompletionRequest.ResponseSchema` goes to Converse as `outputConfig.textFormat`
(`{type: "json_schema", structure: {jsonSchema: {name, schema}}}`, the schema as a JSON string) for Claude Sonnet, Opus
and Haiku 4.5 and later, Fable and Mythos, directly or through a cross-Region profile — not Claude Haiku 4.5 through the
India profile (`in.`). Other models get the request without it and a warning; `SupportsJsonSchema(model)` says which.
The first request with a new schema can take up to about 90 seconds while Bedrock compiles it: keep `Timeout` at 120 s
or more (its default); the compiled schema is cached for 24 hours. Every object needs `additionalProperties: false`; no
`minimum` / `maximum` / `minLength` / `maxLength`; at most 12 optional and 16 union-typed properties. Not with citations
(400). See the repository README, "Structured output".

## Tool calling & vision

Native Bedrock support for tool calling (`ToolConfiguration`) and vision
(`ImageBlock`) is **not** exposed on the `IAIProvider` surface — that contract is
text-only. The agent loop in `Compendium.Application` owns tool calling; a typed
Bedrock-native client surfacing vision and tools will land in a follow-up preview.

## Compatibility

| Package version | `Compendium.Abstractions.AI` | `AWSSDK.BedrockRuntime` |
|-----------------|------------------------------|-------------------------|
| `1.0.0-preview.0` | `1.0.1`                    | `4.0.17.9`              |

## License

MIT — see [LICENSE](LICENSE).
