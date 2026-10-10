using System;
using System.IO;

namespace CodeBrix.Audio.FlacWriter.Tests;

/// <summary>
/// A write-only stream that cannot seek and does not report a length or position - a pipe, in other words -
/// so a writer that tried to patch anything after the fact would fail here.
/// </summary>
internal sealed class ForwardOnlyStream : Stream
{
    private readonly MemoryStream written = new MemoryStream();

    /// <summary>The bytes written so far.</summary>
    /// <returns>A copy of them.</returns>
    public byte[] ToArray() => written.ToArray();

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("This stream cannot report a length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("This stream cannot report a position.");
        set => throw new NotSupportedException("This stream cannot seek.");
    }

    /// <inheritdoc />
    public override void Flush() => written.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("This stream cannot be read.");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("This stream cannot seek.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("This stream cannot be resized.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => written.Write(buffer);
}
