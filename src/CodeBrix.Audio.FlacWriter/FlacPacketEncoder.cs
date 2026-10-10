using System;
using CodeBrix.Audio.FlacWriter.Internal;

namespace CodeBrix.Audio.FlacWriter;

/// <summary>
/// Encodes samples losslessly into separate FLAC frames - one per packet - plus the FLAC stream header, for
/// containers that carry FLAC as a sequence of packets with the header as codec-private data.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>Encode</c> call queues every FLAC frame it completes; take them in order with
/// <see cref="TryReadPacket" />. <see cref="Finish" /> encodes the last (short) frame and finalises
/// <see cref="StreamHeader" />: STREAMINFO with the total sample count, MD5 signature and frame-size range.
/// Nothing is written anywhere - the packets and the header are the whole output.
/// </para>
/// <para>
/// The header, followed by every packet in order, is byte for byte the FLAC file <see cref="FlacEncoder" />
/// writes for the same input and options to a seekable stream.
/// </para>
/// <para>
/// An instance is not thread-safe; use it from one thread at a time. Separate instances are independent.
/// </para>
/// </remarks>
public sealed class FlacPacketEncoder : IDisposable
{
    private FlacNativeEncoder encoder;
    private long framesWritten;
    private bool finished;
    private byte[] finalHeader;

    /// <summary>Creates a packet-mode encoder. The stream header is available at once (with placeholder totals).</summary>
    /// <param name="channels">The number of interleaved channels, 1 to 8.</param>
    /// <param name="sampleRate">The sample rate in hertz, 1 to 655350.</param>
    /// <param name="options">The encoder settings, or null for the defaults (16-bit, level 5, verify on, streamable subset).</param>
    /// <exception cref="ArgumentException">A combination of options or a comment is not usable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="channels" />, <paramref name="sampleRate" /> or an option is out of range.
    /// </exception>
    /// <exception cref="FlacEncoderException">The native library could not be loaded, or libFLAC refused the settings.</exception>
    public FlacPacketEncoder(int channels, int sampleRate, FlacEncoderOptions options = null)
    {
        FlacArguments.Check(channels, sampleRate);
        options ??= new FlacEncoderOptions();
        options.Validate();

        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = options.BitsPerSample;
        encoder = new FlacNativeEncoder(channels, sampleRate, options, packetMode: true);
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

    /// <summary>True once <see cref="Finish" /> has ended the stream.</summary>
    public bool IsFinished => finished;

    /// <summary>True once <see cref="Dispose" /> has run.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>The number of encoded packets waiting to be taken with <see cref="TryReadPacket" />.</summary>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    public int PendingPackets
    {
        get
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(FlacPacketEncoder));
            return encoder.PendingPackets;
        }
    }

    /// <summary>
    /// The FLAC stream header - <c>fLaC</c>, STREAMINFO first, then the other metadata blocks - which a
    /// container stores as the track's codec-private data. Before <see cref="Finish" /> it carries placeholder
    /// totals; after it, the final values. Its length never changes. Still readable after <see cref="Dispose" />
    /// once the stream was finished.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The encoder was disposed before being finished.</exception>
    public ReadOnlyMemory<byte> StreamHeader
    {
        get
        {
            if (finalHeader != null) return finalHeader;
            if (IsDisposed) throw new ObjectDisposedException(nameof(FlacPacketEncoder));
            return encoder.ReadHeader();
        }
    }

    /// <summary>
    /// Encodes interleaved float samples in [-1, 1], scaled to <see cref="BitsPerSample" /> bits, and queues every
    /// FLAC frame that completes.
    /// </summary>
    /// <param name="interleavedSamples">
    /// The samples. Each becomes <c>round(sample * 2^(BitsPerSample-1))</c>, clipped; no dither is added.
    /// </param>
    /// <returns>The number of whole frames consumed; a trailing partial frame is not consumed.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish" /> has already ended the stream.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public int Encode(ReadOnlySpan<float> interleavedSamples)
    {
        CheckWritable();
        return Track(encoder.EncodeFloat(interleavedSamples));
    }

    /// <summary>
    /// Encodes interleaved integer samples that already hold <see cref="BitsPerSample" />-bit values, exactly,
    /// and queues every FLAC frame that completes.
    /// </summary>
    /// <param name="interleavedSamples">The samples, each within the signed range of <see cref="BitsPerSample" /> bits.</param>
    /// <returns>The number of whole frames consumed; a trailing partial frame is not consumed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A sample does not fit in <see cref="BitsPerSample" /> bits; nothing was encoded.</exception>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Finish" /> has already ended the stream.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public int EncodeInt32(ReadOnlySpan<int> interleavedSamples)
    {
        CheckWritable();
        return Track(encoder.EncodeInt32(interleavedSamples));
    }

    /// <summary>
    /// Encodes interleaved 16-bit samples exactly (shifted up when <see cref="BitsPerSample" /> is wider than 16)
    /// and queues every FLAC frame that completes.
    /// </summary>
    /// <param name="interleavedSamples">The samples.</param>
    /// <returns>The number of whole frames consumed; a trailing partial frame is not consumed.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Finish" /> has already ended the stream, or <see cref="BitsPerSample" /> is below 16.
    /// </exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public int EncodeInt16(ReadOnlySpan<short> interleavedSamples)
    {
        CheckWritable();
        return Track(encoder.EncodeInt16(interleavedSamples));
    }

    /// <summary>Takes the oldest queued packet, if there is one.</summary>
    /// <param name="packet">The packet, or the default value when none is queued.</param>
    /// <returns>True when a packet was taken.</returns>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed.</exception>
    public bool TryReadPacket(out FlacPacket packet)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FlacPacketEncoder));
        return encoder.TryReadPacket(out packet);
    }

    /// <summary>
    /// Ends the stream: encodes the last (short) frame into the queue and finalises <see cref="StreamHeader" />.
    /// Queued packets can still be taken afterwards; nothing more can be encoded. Calling it again does nothing.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The encoder has been disposed.</exception>
    /// <exception cref="FlacEncoderException">The native encoder failed, or verification found a mismatch.</exception>
    public void Finish()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FlacPacketEncoder));
        if (finished) return;

        finished = true;
        encoder.Finish();
        finalHeader = encoder.ReadHeader();
    }

    /// <summary>
    /// Releases the native encoder, discarding any packets not yet taken. It does NOT finish the stream: call
    /// <see cref="Finish" /> and take the remaining packets first. Calling it more than once does nothing.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        encoder?.Dispose();
        encoder = null;
    }

    private int Track(int frames)
    {
        framesWritten = encoder.FramesWritten;
        return frames;
    }

    private void CheckWritable()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FlacPacketEncoder));
        if (finished)
        {
            throw new InvalidOperationException("The stream has been ended by Finish; nothing more can be encoded.");
        }
    }
}
