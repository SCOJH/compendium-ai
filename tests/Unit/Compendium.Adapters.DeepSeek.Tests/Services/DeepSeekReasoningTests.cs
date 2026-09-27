// -----------------------------------------------------------------------
// <copyright file="DeepSeekReasoningTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Compendium.Adapters.DeepSeek.Tests.TestSupport;

namespace Compendium.Adapters.DeepSeek.Tests.Services;

/// <summary>
/// <c>deepseek-reasoner</c>'s <c>reasoning_content</c>, asked for (<see cref="ReasoningOptions"/>): returned apart from the
/// answer, as <see cref="CompletionChunk.ReasoningDelta"/>. Not asked for, it is dropped as before
/// (<c>StreamCompleteAsync_WithReasonerStream_AndDefaultOptions_DropsPureReasoningDeltas</c>).
/// </summary>
public class DeepSeekReasoningTests
{
    [Fact]
    public async Task AskedFor_TheReasoningStreamsApartFromTheAnswer()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        var stream = string.Join("\n",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"reasoning_content\":\"Hmm.\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"reasoning_content\":\" Let me try...\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{\"content\":\"42\"}}]}",
            "data: {\"id\":\"r1\",\"model\":\"deepseek-reasoner\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            string.Empty);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("text/event-stream", stream);

        var chunks = new List<CompletionChunk>();
        await foreach (var r in sut.StreamCompleteAsync(
            TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel) with { Reasoning = new ReasoningOptions() },
            CancellationToken.None))
        {
            chunks.Add(r.Value);
        }

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be("Hmm. Let me try...");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("42", "the reasoning is never part of the answer");
        chunks.Select(c => c.Index).Should().Equal(0, 1, 2, 3);
        chunks[^1].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task AResponse_ReturnsTheReasoningApartFromTheAnswer_WhenAskedFor()
    {
        var (httpClient, handler) = TestFactories.CreateHttpClient();
        var sut = TestFactories.CreateProvider(httpClient);
        handler.When(HttpMethod.Post, "*/chat/completions").Respond("application/json", """
            {"id":"d","model":"deepseek-reasoner","choices":[{"message":{"role":"assistant","content":"42","reasoning_content":"Hmm."},"finish_reason":"stop"}]}
            """);

        var asked = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel) with { Reasoning = new ReasoningOptions() }, CancellationToken.None);
        var unasked = await sut.CompleteAsync(TestFactories.SimpleCompletionRequest(DeepSeekOptions.ReasonerModel), CancellationToken.None);

        asked.Value.Content.Should().Be("42");
        asked.Value.Reasoning.Should().Be("Hmm.");
        unasked.Value.Reasoning.Should().BeNull();
    }
}
