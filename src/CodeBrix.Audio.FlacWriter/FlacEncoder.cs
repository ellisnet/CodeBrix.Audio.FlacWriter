using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.FlacWriter.Internal;

namespace CodeBrix.Audio.FlacWriter;

/// <summary>
/// Encodes samples losslessly into a FLAC stream written to a <see cref="Stream" />.
/// </summary>
/// <remarks>
/// <para>
/// The stream header (<c>fLaC</c> plus the metadata blocks) is written as soon as the encoder is created. Each
/// <c>Encode</c> call hands interleaved samples to the native encoder and writes every FLAC frame that
/// completes; <see cref="Flush" /> (or <see cref="Dispose" />) writes the last, short frame and ends the stream.
/// </para>
/// <para>
/// THE HEADER IS FINISHED BY SEEKING BACK. STREAMINFO's total sample count, MD5 signature and frame-size range -
/// and the SEEKTABLE, when one was asked for - are only known at the end. When the output stream can
/// seek, <see cref="Flush" /> seeks back, writes them over the placeholders and returns to the end, and
/// <see cref="HeaderIsFinal" /> becomes true. When it cannot (a pipe, a network stream), the placeholders stay:
/// the file is still a valid FLAC stream that every decoder plays, it just does not state its own length.
/// <see cref="StreamHeader" /> holds the final header either way, so a caller that can rewrite the file later
/// can put it in place itself - its length never changes.
/// </para>
/// <para>
/// THE STREAM IS NOT OWNED. The caller opened it and the caller closes it, after disposing the encoder. Patch
/// offsets are relative to the stream's position when the encoder was created, so a FLAC stream can be written
/// into the middle of a larger seekable stream.
/// </para>
/// <para>
/// An instance is not thread-safe; use it from one thread at a time. Separate instances are independent.
/// </para>
/// </remarks>
public sealed class FlacEncoder : IDisposable
{
    private readonly Stream output;
    private readonly long startPosition;
    private readonly byte[] copyBuffer = new byte[16 * 1024];

    private FlacNativeEncoder encoder;
    private long framesWritten;
    private bool finished;
    private bool headerIsFinal;
    private byte[] finalHeader;

    /// <summary>Creates an encoder and writes the FLAC stream header to <paramref name="output" />.</summary>
    /// <param name="output">The stream to write to. It must be writable; it is not owned by the encoder.</param>
    /// <param name="channels">The number of interleaved channels, 1 to 8.</param>
    /// <param name="sampleRate">The sample rate in hertz, 1 to 655350 - for example 44100 or 48000.</param>
    /// <param name="options">
    /// The encoder settings, or null for the defaults (16-bit, level 5, verify on, streamable subset).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="output" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="output" /> is not writable, or a combination of options or a comment is not usable.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="channels" />, <paramref name="sampleRate" /> or an option is out of range.
    /// </exception>
    /// <exception cref="FlacEncoderException">
    /// The native library could not be loaded, or libFLAC refused the combination of settings - for example a
    /// block size or bit depth outside the streamable subset while <see cref="FlacEncoderOptions.StreamableSubset" />
    /// is on.
    /// </exception>
    public FlacEncoder(Stream output, int channels, int sampleRate, FlacEncoderOptions options = null)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite)
        {
            throw new ArgumentException("A FLAC stream cannot be written to a stream that is not writable.", nameof(output));
        }

        FlacArguments.Check(channels, sampleRate);
        options ??= new FlacEncoderOptions();
        options.Validate();

        this.output = output;
        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = options.BitsPerSample;
        startPosition = output.CanSeek ? output.Position : -1;

        encoder = new FlacNativeEncoder(channels, sampleRate, options, packetMode: false);
        try
        {
            encoder.ChunkEncoded = Drain;
            Drain();
        }
        catch
        {
            encoder.Dispose();
            encoder = null;
            throw;
        }
    }

    /// <summary>The number of interleaved channels the encoder was created with.</summary>
    public int Channels { get; }

    /// <summary>The sample rate, in hertz, the encoder was created with.</summary>
    public int SampleRate { get; }

    /// <summary>The stored bits per sample the encoder was created with.</summary>
    public int BitsPerSample { get; }

    /// <summary>The number of frames (one sample per channel) accepted by the <c>Encode</c> methods so far.</summary>
    public long FramesWritten => framesWritten;

    /// <summary>The duration of the audio accepted so far: <see cref="FramesWritten" /> at <see cref="SampleRate" />.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)framesWritten / SampleRate);

    /// <summary>True once <see cref="Flush" /> or <see cref="Dispose" /> has ended the stream.</summary>
    public bool IsFinished => finished;

    /// <summary>True once <see cref="Dispose" /> has run.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// True once <see cref="Flush" /> has written the final STREAMINFO (and SEEKTABLE) over the placeholders in
    /// the output - which happens only when the output stream can seek. False before the stream is finished, and
    /// false after it when the output could not seek.
    /// </summary>
    public bool HeaderIsFinal => headerIsFinal;

    /// <summary>
    /// The stream header - <c>fLaC</c> and every metadata block, up to the first audio frame. Before
    /// <see cref="Flush" /> it carries placeholder totals; after it, the final values (total samples, MD5,
    /// frame sizes, seek points) whether or not they could be written into the output. Its length never changes,
    /// so a caller holding a non-seekable output's bytes can overwrite their start with it later. Still readable
    /// after <see cref="Dispose" />.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The encoder was disposed without the stream being finished.</exception>
    public ReadOnlyMemory<byte> StreamHeader
    {
        get
        {
            if (finalHeader != null) return finalHeader;
            if (encoder == null) throw new ObjectDisposedException(nameof(FlacEncoder));
            return encoder.ReadHeader();
        }
    }

    /// <summary>
    /// Encodes interleaved float samples (channel 0, channel 1, ... for frame 0, then frame 1, ...) in [-1, 1],
    /// scaled to <see cref="BitsPerSample" /> bits, and writes every FLAC frame that completes.
    /// </summary>
    /// <param name="interleavedSamples">
    /// The samples. Each becomes <c>round(sample * 2^(BitsPerSample-1))</c>, clipped to the integer range; no
    /// dither is added. For bit-exact input use <see cref="EncodeInt32" /> or <see cref="EncodeInt16" />.
    /// </param>
    /// <returns>
    /// The number of whole frames consumed: the sample count divided by <see cref="Channels" />. A trailing
    /// partial frame is not consumed; pass it again at the start of the next call.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Flush" /> has already ended the stream.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public int Encode(ReadOnlySpan<float> interleavedSamples)
    {
        CheckWritable();
        return Track(encoder.EncodeFloat(interleavedSamples));
    }

    /// <summary>
    /// Encodes interleaved integer samples that already hold <see cref="BitsPerSample" />-bit values
    /// (sign-extended into 32 bits), exactly, and writes every FLAC frame that completes.
    /// </summary>
    /// <param name="interleavedSamples">The samples, each within the signed range of <see cref="BitsPerSample" /> bits.</param>
    /// <returns>The number of whole frames consumed; a trailing partial frame is not consumed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A sample does not fit in <see cref="BitsPerSample" /> bits; nothing was encoded.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Flush" /> has already ended the stream.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public int EncodeInt32(ReadOnlySpan<int> interleavedSamples)
    {
        CheckWritable();
        return Track(encoder.EncodeInt32(interleavedSamples));
    }

    /// <summary>
    /// Encodes interleaved 16-bit samples exactly. At 16 bits per sample they are stored as they are; at a wider
    /// <see cref="BitsPerSample" /> they are shifted up (a lossless widening, so 24-bit output decodes to the
    /// input times 256).
    /// </summary>
    /// <param name="interleavedSamples">The samples.</param>
    /// <returns>The number of whole frames consumed; a trailing partial frame is not consumed.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Flush" /> has already ended the stream, or <see cref="BitsPerSample" /> is below 16.
    /// </exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public int EncodeInt16(ReadOnlySpan<short> interleavedSamples)
    {
        CheckWritable();
        return Track(encoder.EncodeInt16(interleavedSamples));
    }

    /// <summary>
    /// Ends the stream: encodes the last (short) frame, writes it, and - when the output can seek - writes the
    /// final STREAMINFO and SEEKTABLE over the placeholders, returning to the end of the stream afterwards. Then
    /// flushes the output. Nothing can be encoded afterwards. Calling it again does nothing.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public void Flush()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FlacEncoder));
        if (finished) return;

        finished = true;
        encoder.Finish();
        Drain();
        finalHeader = encoder.ReadHeader();

        if (output.CanSeek && startPosition >= 0)
        {
            ApplyPatches(encoder.ReadPatches());
            headerIsFinal = true;
        }

        output.Flush();
    }

    /// <summary>
    /// Ends the stream if <see cref="Flush" /> has not already, then releases the native encoder. The output
    /// stream is left open. Calling it more than once does nothing.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;

        try
        {
            if (!finished && encoder != null) Flush();
        }
        finally
        {
            IsDisposed = true;
            encoder?.Dispose();
            encoder = null;
        }
    }

    private void ApplyPatches(List<KeyValuePair<long, byte[]>> patches)
    {
        if (patches.Count == 0) return;

        long end = output.Position;
        try
        {
            foreach (KeyValuePair<long, byte[]> patch in patches)
            {
                output.Position = startPosition + patch.Key;
                output.Write(patch.Value, 0, patch.Value.Length);
            }
        }
        finally
        {
            output.Position = end;
        }
    }

    private void Drain()
    {
        while (true)
        {
            int copied = encoder.ReadOutput(copyBuffer);
            if (copied == 0) return;
            output.Write(copyBuffer, 0, copied);
        }
    }

    private int Track(int frames)
    {
        framesWritten = encoder.FramesWritten;
        return frames;
    }

    private void CheckWritable()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FlacEncoder));
        if (finished)
        {
            throw new InvalidOperationException("The stream has been ended by Flush; nothing more can be encoded.");
        }
    }
}
