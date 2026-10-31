// -----------------------------------------------------------------------
// <copyright file="CutStream.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Mistral.Tests.TestSupport;

/// <summary>A response body that gives its bytes, then fails as a dropped connection does.</summary>
internal sealed class CutStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count) =>
        Position < Length ? base.Read(buffer, offset, count) : throw new IOException("The connection was reset.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw new IOException("The connection was reset.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Position < Length ? base.ReadAsync(buffer, offset, count, cancellationToken) : throw new IOException("The connection was reset.");
}
