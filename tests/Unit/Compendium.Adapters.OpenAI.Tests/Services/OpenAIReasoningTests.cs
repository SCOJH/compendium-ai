// -----------------------------------------------------------------------
// <copyright file="OpenAIReasoningTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.OpenAI.Tests.TestSupport;

namespace Compendium.Adapters.OpenAI.Tests.Services;

/// <summary>
/// The reasoning models' channel (<see cref="ReasoningOptions"/>): <c>reasoning_effort</c>, <c>max_completion_tokens</c>
/// instead of <c>max_tokens</c>, and none of the sampling parameters they refuse. Without it, the request is unchanged.
/// </summary>
public class OpenAIReasoningTests
{
    private static async Task<JsonElement> SentBodyAsync(CompletionRequest request)
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        string? body = null;
        handler.When(HttpMethod.Post, "*/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond("application/json", """{"id":"x","model":"o3","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        await sut.CompleteAsync(request, CancellationToken.None);
        return JsonDocument.Parse(body!).RootElement.Clone();
    }

    private static CompletionRequest Request(ReasoningOptions? reasoning) =>
        TestFactories.SimpleCompletionRequest("o3") with
        {
            MaxTokens = 4096,
            Temperature = 0.2f,
            TopP = 0.9f,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.1f,
            Reasoning = reasoning,
        };

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.Max, "high")]
    public async Task AReasoningRequest_SendsTheEffort_MaxCompletionTokens_AndNoSamplingParameter(ReasoningEffort effort, string wire)
    {
        var sent = await SentBodyAsync(Request(new ReasoningOptions { Effort = effort, BudgetTokens = 1000 }));

        sent.GetProperty("reasoning_effort").GetString().Should().Be(wire);
        sent.GetProperty("max_completion_tokens").GetInt32().Should().Be(5096, "the reasoning tokens count against it: the budget is headroom");
        sent.TryGetProperty("max_tokens", out _).Should().BeFalse("reasoning models refuse max_tokens");
        foreach (var refused in new[] { "temperature", "top_p", "frequency_penalty", "presence_penalty" })
        {
            sent.TryGetProperty(refused, out _).Should().BeFalse($"reasoning models refuse {refused}");
        }
    }

    [Fact]
    public async Task WithoutReasoning_TheRequestIsTheOneItWas()
    {
        var sent = await SentBodyAsync(Request(null));

        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        sent.TryGetProperty("max_completion_tokens", out _).Should().BeFalse();
        sent.GetProperty("max_tokens").GetInt32().Should().Be(4096);
        sent.GetProperty("temperature").GetSingle().Should().Be(0.2f);
    }

    [Fact]
    public async Task TheReasoningTokens_AreReportedInTheUsage()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", """
            {"id":"x","model":"o3","choices":[{"message":{"role":"assistant","content":"42"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":10,"completion_tokens":300,"total_tokens":310,"completion_tokens_details":{"reasoning_tokens":256}}}
            """);

        var result = await sut.CompleteAsync(Request(new ReasoningOptions()), CancellationToken.None);

        result.Value.Content.Should().Be("42");
        result.Value.Usage.CompletionTokens.Should().Be(300);
        result.Value.Usage.ReasoningTokens.Should().Be(256);
    }
}
