// -----------------------------------------------------------------------
// <copyright file="ZAiOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.ZAi.Configuration;

/// <summary>
/// Configuration options for the Z.ai AI provider (the GLM models, through Z.ai's own OpenAI-compatible API).
/// </summary>
/// <remarks>
/// The default endpoint is Z.ai's standard, pay-as-you-go API (https://docs.z.ai/api-reference/llm/chat-completion.md,
/// read 2026-10-20). Z.ai's GLM Coding Plan has its own endpoint, billing and behaviour (reasoning kept across turns by
/// default, a lenient effort scale); it is a <see cref="BaseUrl"/> away, not a default.
/// </remarks>
public sealed class ZAiOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "ZAi";

    /// <summary>Z.ai's standard API, international: <c>https://api.z.ai/api/paas/v4</c>.</summary>
    public const string InternationalBaseUrl = "https://api.z.ai/api/paas/v4";

    /// <summary>The same API for mainland China (BigModel): <c>https://open.bigmodel.cn/api/paas/v4</c>.</summary>
    public const string MainlandChinaBaseUrl = "https://open.bigmodel.cn/api/paas/v4";

    /// <summary>The default chat model: GLM 5.3.</summary>
    public const string DefaultChatModel = "glm-5.3";

    /// <summary>
    /// Gets or sets the Z.ai API key. Required.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL of the API. Default <see cref="InternationalBaseUrl"/>. The adapter posts to
    /// <c>chat/completions</c> under it.
    /// </summary>
    public string BaseUrl { get; set; } = InternationalBaseUrl;

    /// <summary>
    /// Gets or sets the default chat model. Default <see cref="DefaultChatModel"/>.
    /// </summary>
    public string DefaultModel { get; set; } = DefaultChatModel;

    /// <summary>
    /// Gets or sets the room given to the answer when the request sets no <c>MaxTokens</c>. A GLM's reasoning gets its own
    /// room on top (the GLM dialect), up to the model's output limit.
    /// </summary>
    public int DefaultMaxTokens { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the HTTP timeout in seconds. GLM reasons before it answers, at <c>max</c> by default: keep it generous.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets whether to enable verbose request/response logging at debug level.
    /// </summary>
    public bool EnableLogging { get; set; }
}
