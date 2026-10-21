// -----------------------------------------------------------------------
// <copyright file="MistralReasoningTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.Mistral.Tests.TestSupport;

namespace Compendium.Adapters.Mistral.Tests.Services;

/// <summary>
/// Mistral's reasoning (Magistral, and the models that take <c>reasoning_effort</c>): asked for with <c>"high"</c>, the
/// thinking comes back as chunks of type <c>thinking</c> — a list where the answer is a string — and is returned apart from
/// the answer. A stream of such chunks used to fail to read at all: the delta's content was taken for a string.
/// </summary>
public class MistralReasoningTests
{
    private const string ThinkingStream =
        "data: {\"id\":\"m1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"17*23 \"}]}]}}]}\n\n"
        + "data: {\"id\":\"m1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"delta\":{\"content\":[{\"type\":\"thinking\",\"thinking\":[{\"type\":\"text\",\"text\":\"is 391.\"}]},{\"type\":\"text\",\"text\":\"It is \"}]}}]}\n\n"
        + "data: {\"id\":\"m1\",\"model\":\"magistral-medium-latest\",\"choices\":[{\"delta\":{\"content\":\"391.\"},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static async Task<(List<CompletionChunk> Chunks, string Body)> StreamAsync(ReasoningOptions? reasoning)
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
            .Respond("text/event-stream", ThinkingStream);
        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(TestFactories.SimpleCompletionRequest("magistral-medium-latest") with { Reasoning = reasoning }, CancellationToken.None))
        {
            r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.Message : null);
            chunks.Add(r.Value);
        }

        return (chunks, body!);
    }

    [Fact]
    public async Task AskedFor_TheThinkingStreamsApartFromTheAnswer_AndReasoningEffortIsHigh()
    {
        var (chunks, body) = await StreamAsync(new ReasoningOptions());

        using var sent = JsonDocument.Parse(body);
        sent.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be("17*23 is 391.");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("It is 391.", "the thinking is never part of the answer");
        chunks[^1].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task NotAskedFor_TheAnswerStillReads_WithoutTheThinking()
    {
        var (chunks, body) = await StreamAsync(null);

        using var sent = JsonDocument.Parse(body);
        sent.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("It is 391.");
    }

    [Fact]
    public async Task AResponse_ReturnsTheThinkingApartFromTheAnswer()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", """
            {"id":"m","model":"magistral-small-latest","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant",
             "content":[{"type":"thinking","thinking":[{"type":"text","text":"Weighing it."}]},{"type":"text","text":"Answer."}]}}]}
            """);

        var result = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest("magistral-small-latest") with { Reasoning = new ReasoningOptions() }, CancellationToken.None);

        result.Value.Content.Should().Be("Answer.");
        result.Value.Reasoning.Should().Be("Weighing it.");
    }
}
