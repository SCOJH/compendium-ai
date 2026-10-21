// -----------------------------------------------------------------------
// <copyright file="TestFactories.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.ZAi.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.ZAi.Tests.TestSupport;

internal static class TestFactories
{
    public const string DefaultApiKey = "zai-test-key";

    public static ZAiOptions DefaultOptions(Action<ZAiOptions>? configure = null)
    {
        var options = new ZAiOptions { ApiKey = DefaultApiKey, DefaultMaxTokens = 4096, EnableLogging = false };
        configure?.Invoke(options);
        return options;
    }

    public static (ZAiHttpClient Client, MockHttpMessageHandler Handler) CreateHttpClient(Action<ZAiOptions>? configure = null)
    {
        var handler = new MockHttpMessageHandler();
        var options = DefaultOptions(configure);
        var sut = new ZAiHttpClient(new HttpClient(handler), Options.Create(options), NullLogger<ZAiHttpClient>.Instance);
        return (sut, handler);
    }

    public static ZAiAIProvider CreateProvider(ZAiHttpClient httpClient, Action<ZAiOptions>? configure = null) =>
        new(httpClient, Options.Create(DefaultOptions(configure)), NullLogger<ZAiAIProvider>.Instance);

    /// <summary>A provider whose every chat request lands in <paramref name="body"/> and is answered with <paramref name="reply"/>.</summary>
    public static (ZAiAIProvider Sut, MockHttpMessageHandler Handler, Func<string?> Body) Replying(
        string mediaType,
        string reply,
        HttpStatusCode status = HttpStatusCode.OK,
        Action<ZAiOptions>? configure = null)
    {
        var (client, handler) = CreateHttpClient(configure);
        string? body = null;
        handler.When(HttpMethod.Post, "https://api.z.ai/api/paas/v4/chat/completions")
            .With(req =>
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return true;
            })
            .Respond(status, mediaType, reply);
        return (CreateProvider(client, configure), handler, () => body);
    }

    public static CompletionRequest Request(string model = "glm-5.3") =>
        new() { Model = model, Messages = new List<Message> { Message.User("Hello") } };
}
