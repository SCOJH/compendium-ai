// -----------------------------------------------------------------------
// <copyright file="AnthropicOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Anthropic.Configuration;

/// <summary>
/// Configuration options for the Anthropic AI provider adapter.
/// </summary>
public sealed class AnthropicOptions
{
    /// <summary>
    /// The default configuration section name (<c>Anthropic</c>).
    /// </summary>
    public const string SectionName = "Anthropic";

    /// <summary>
    /// The default Anthropic API base URL.
    /// </summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>
    /// The default <c>anthropic-version</c> header value.
    /// See <see href="https://docs.anthropic.com/en/api/versioning"/>.
    /// </summary>
    public const string DefaultAnthropicVersion = "2023-06-01";

    /// <summary>
    /// Gets or sets the Anthropic API key. Sent as the <c>x-api-key</c> header.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL for the Anthropic API. Defaults to
    /// <see cref="DefaultBaseUrl"/>.
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Gets or sets the value sent in the <c>anthropic-version</c> header.
    /// Defaults to <see cref="DefaultAnthropicVersion"/>.
    /// </summary>
    public string AnthropicVersion { get; set; } = DefaultAnthropicVersion;

    /// <summary>
    /// Gets or sets the Anthropic workspace the requests act in, sent as the <c>anthropic-workspace-id</c> header on every
    /// request when set (for example <c>wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ</c>). Required by a key that can act on more than
    /// one workspace (a personal or service-account key not scoped to one workspace); leave it null for a key that belongs
    /// to a single workspace — if sent, it must match that workspace. See
    /// <see href="https://platform.claude.com/docs/en/manage-claude/authentication#select-a-workspace"/>.
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Gets or sets the default model id used when <see cref="CompletionRequest.Model"/>
    /// is not specified. Defaults to <c>claude-3-7-sonnet-latest</c>.
    /// </summary>
    public string DefaultModel { get; set; } = "claude-3-7-sonnet-latest";

    /// <summary>
    /// Gets or sets the default <c>max_tokens</c> applied to outgoing requests when the
    /// caller does not provide one. Anthropic requires this field, so the adapter always
    /// sends a value. Defaults to <c>4096</c>.
    /// </summary>
    public int DefaultMaxTokens { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the HTTP timeout in seconds for non-streaming calls.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets whether to enable verbose request / response logging at
    /// <see cref="LogLevel.Debug"/>. Off by default; only enable in non-production
    /// environments because raw request bodies may contain sensitive prompts.
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>
    /// Gets or sets whether to opt into Anthropic's beta <c>cache_control</c> markers.
    /// When <see langword="true"/>, the adapter emits <c>cache_control: { type: "ephemeral" }</c>
    /// on the trailing system-prompt block of every request, enabling prompt caching
    /// for long, stable system prompts. Off by default. See
    /// <see href="https://docs.anthropic.com/en/docs/build-with-claude/prompt-caching"/>.
    /// </summary>
    public bool EnablePromptCaching { get; set; }

    /// <summary>
    /// Validates that all required values are set.
    /// </summary>
    /// <returns><see langword="true"/> when the options are usable.</returns>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(AnthropicVersion)
        && !string.IsNullOrWhiteSpace(DefaultModel)
        && DefaultMaxTokens > 0
        && TimeoutSeconds > 0;
}
