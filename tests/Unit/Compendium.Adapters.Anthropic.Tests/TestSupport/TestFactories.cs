// -----------------------------------------------------------------------
// <copyright file="TestFactories.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Anthropic.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Compendium.Adapters.Anthropic.Tests.TestSupport;

/// <summary>Helpers used by the unit tests to build SUTs with deterministic options.</summary>
internal static class TestFactories
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string DefaultApiKey = "sk-ant-test-key";

    /// <summary>The example workspace id of Anthropic's documentation.</summary>
    public const string WorkspaceId = "wrkspc_01JwQvzr7rXLA5AGx3HKfFUJ";

    public static AnthropicOptions DefaultOptions(Action<AnthropicOptions>? configure = null)
    {
        var options = new AnthropicOptions
        {
            ApiKey = DefaultApiKey,
            BaseUrl = DefaultBaseUrl,
            AnthropicVersion = AnthropicOptions.DefaultAnthropicVersion,
            DefaultModel = "claude-3-7-sonnet-latest",
            DefaultMaxTokens = 4096,
            TimeoutSeconds = 120,
            EnableLogging = false,
            EnablePromptCaching = false,
        };
        configure?.Invoke(options);
        return options;
    }

    public static (AnthropicHttpClient Client, MockHttpMessageHandler Handler) CreateHttpClient(
        Action<AnthropicOptions>? configure = null)
    {
        var handler = new MockHttpMessageHandler();
        var options = DefaultOptions(configure);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl),
        };
        var sut = new AnthropicHttpClient(
            httpClient,
            Options.Create(options),
            NullLogger<AnthropicHttpClient>.Instance);
        return (sut, handler);
    }

    public static AnthropicAIProvider CreateProvider(
        AnthropicHttpClient httpClient,
        Action<AnthropicOptions>? configure = null)
    {
        var options = DefaultOptions(configure);
        return new AnthropicAIProvider(
            httpClient,
            Options.Create(options),
            NullLogger<AnthropicAIProvider>.Instance);
    }

    public static CompletionRequest SimpleCompletionRequest(string? model = null)
    {
        return new CompletionRequest
        {
            Model = model ?? "claude-3-7-sonnet-latest",
            Messages = new List<Message> { Message.User("Hello") },
        };
    }

    /// <summary>Records log entries for assertions on diagnostic output.</summary>
    public sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
