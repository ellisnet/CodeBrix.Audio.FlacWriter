using System;
using System.Collections.Generic;
using System.Text;
using CodeBrix.Audio.FlacWriter.Interop;

namespace CodeBrix.Audio.FlacWriter.Internal;

/// <summary>
/// The part <see cref="FlacEncoder" /> and <see cref="FlacPacketEncoder" /> share: one started native encoder,
/// sample conversion and chunking, the header read-back and the patch list.
/// </summary>
/// <remarks>
/// Each public <c>Encode*</c> call is split into chunks of at most <see cref="MaxFramesPerNativeCall" /> frames,
/// converted into one reused 32-bit buffer, so neither the native output nor the managed buffers grow with the
/// size of what the caller passes. After each chunk <see cref="ChunkEncoded" /> runs, which is where the stream
/// encoder drains bytes to its <c>Stream</c>.
/// </remarks>
internal sealed class FlacNativeEncoder : IDisposable
{
    /// <summary>Frames handed to the native encoder per call.</summary>
    internal const int MaxFramesPerNativeCall = 4096;

    private readonly int[] conversion;

    private FlacEncoderHandle handle;
    private long framesWritten;
    private bool finished;
    private byte[] finalHeader;

    /// <summary>Creates, configures and starts a native encoder.</summary>
    /// <param name="channels">Channels, 1 to 8.</param>
    /// <param name="sampleRate">Sample rate, 1 to 655350.</param>
    /// <param name="options">Validated options.</param>
    /// <param name="packetMode">True for packet mode.</param>
    /// <exception cref="FlacEncoderException">The native library could not be loaded or refused the settings.</exception>
    internal FlacNativeEncoder(int channels, int sampleRate, FlacEncoderOptions options, bool packetMode)
    {
        FlacLibrary.EnsureLoaded();

        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = options.BitsPerSample;
        conversion = new int[MaxFramesPerNativeCall * channels];

        handle = new FlacEncoderHandle(CreateNative(channels, sampleRate, options));

        try
        {
            IntPtr native = handle.DangerousGetHandle();

            if (options.Comments != null)
            {
                foreach (KeyValuePair<string, string> comment in options.Comments)
                {
                    AddComment(native, comment.Key, comment.Value);
                }
            }

            if (options.PaddingBytes > 0)
            {
                FlacResult.ThrowIfFailed(FlacNative.EncoderSetPadding(native, options.PaddingBytes), "setting the padding", native);
            }

            if (options.SeekTableSamplesBetweenPoints > 0)
            {
                FlacResult.ThrowIfFailed(
                    FlacNative.EncoderSetSeekTable(native, options.SeekTableSamplesBetweenPoints), "requesting the seek table", native);
            }

            if (packetMode)
            {
                FlacResult.ThrowIfFailed(FlacNative.EncoderSetPacketMode(native, 1), "selecting packet mode", native);
            }

            int started = FlacNative.EncoderStart(native);
            if (started != FlacResult.Ok)
            {
                throw FlacResult.Failure(
                    started,
                    $"starting the stream ({channels} channel(s) at {sampleRate} Hz, {options.BitsPerSample}-bit, "
                    + $"level {options.CompressionLevel}, block size {(options.BlockSize == 0 ? "default" : options.BlockSize.ToString())}, "
                    + $"{(options.StreamableSubset ? "streamable subset" : "not subset")})",
                    native);
            }
        }
        catch
        {
            handle.Dispose();
            handle = null;
            throw;
        }
    }

    /// <summary>Runs after each chunk has been handed to the native encoder.</summary>
    internal Action ChunkEncoded { get; set; }

    /// <summary>The channel count.</summary>
    internal int Channels { get; }

    /// <summary>The sample rate in hertz.</summary>
    internal int SampleRate { get; }

    /// <summary>The stored bits per sample.</summary>
    internal int BitsPerSample { get; }

    /// <summary>Frames accepted so far.</summary>
    internal long FramesWritten => framesWritten;

    /// <summary>True once <see cref="Finish" /> has run.</summary>
    internal bool IsFinished => finished;

    /// <summary>True once released.</summary>
    internal bool IsReleased => handle == null;

    /// <summary>The live native handle.</summary>
    internal IntPtr Native
    {
        get
        {
            if (handle == null) throw new ObjectDisposedException(nameof(FlacNativeEncoder));
            return handle.DangerousGetHandle();
        }
    }

    /// <summary>Encodes float samples, scaled and rounded to <see cref="BitsPerSample" />.</summary>
    /// <param name="samples">Interleaved samples.</param>
    /// <returns>Whole frames consumed.</returns>
    internal int EncodeFloat(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / Channels;
        int done = 0;
        while (done < frames)
        {
            int chunk = Math.Min(MaxFramesPerNativeCall, frames - done);
            ReadOnlySpan<float> source = samples.Slice(done * Channels, chunk * Channels);
            FlacSampleConverter.FloatToInt(source, conversion, BitsPerSample);
            WriteChunk(chunk);
            done += chunk;
        }

        return frames;
    }

    /// <summary>Encodes 16-bit samples, widened to <see cref="BitsPerSample" />.</summary>
    /// <param name="samples">Interleaved samples.</param>
    /// <returns>Whole frames consumed.</returns>
    internal int EncodeInt16(ReadOnlySpan<short> samples)
    {
        if (BitsPerSample < 16)
        {
            throw new InvalidOperationException(
                $"16-bit samples cannot be stored losslessly at {BitsPerSample} bits per sample; scale them down and use EncodeInt32.");
        }

        int shift = BitsPerSample - 16;
        int frames = samples.Length / Channels;
        int done = 0;
        while (done < frames)
        {
            int chunk = Math.Min(MaxFramesPerNativeCall, frames - done);
            ReadOnlySpan<short> source = samples.Slice(done * Channels, chunk * Channels);
            for (int i = 0; i < source.Length; i++) conversion[i] = source[i] << shift;
            WriteChunk(chunk);
            done += chunk;
        }

        return frames;
    }

    /// <summary>Encodes 32-bit samples that already hold <see cref="BitsPerSample" />-wide values.</summary>
    /// <param name="samples">Interleaved samples.</param>
    /// <returns>Whole frames consumed.</returns>
    internal int EncodeInt32(ReadOnlySpan<int> samples)
    {
        int frames = samples.Length / Channels;
        int whole = frames * Channels;

        long min = -(1L << (BitsPerSample - 1));
        long max = (1L << (BitsPerSample - 1)) - 1;
        for (int i = 0; i < whole; i++)
        {
            int value = samples[i];
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(
                    "interleavedSamples",
                    value,
                    $"Sample {i} does not fit in {BitsPerSample} bits (the range is {min} to {max}); integer input must "
                    + "already be at the encoder's BitsPerSample.");
            }
        }

        int done = 0;
        while (done < frames)
        {
            int chunk = Math.Min(MaxFramesPerNativeCall, frames - done);
            samples.Slice(done * Channels, chunk * Channels).CopyTo(conversion);
            WriteChunk(chunk);
            done += chunk;
        }

        return frames;
    }

    /// <summary>Finishes the native stream and caches the final header.</summary>
    internal void Finish()
    {
        if (finished) return;
        finished = true;

        IntPtr native = Native;
        FlacResult.ThrowIfFailed(FlacNative.EncoderFinish(native), "finishing the stream", native);
        finalHeader = ReadHeaderNative();
    }

    /// <summary>The stream header: placeholder totals before <see cref="Finish" />, final after.</summary>
    /// <returns>A copy of the header bytes.</returns>
    internal byte[] ReadHeader() => finalHeader ?? ReadHeaderNative();

    /// <summary>Reads every patch the native encoder recorded at finish.</summary>
    /// <returns>The patches, as (absolute stream offset, bytes) pairs.</returns>
    internal unsafe List<KeyValuePair<long, byte[]>> ReadPatches()
    {
        IntPtr native = Native;
        int count = FlacNative.EncoderPatchCount(native);
        FlacResult.ThrowIfFailed(count, "counting the patches", native);

        List<KeyValuePair<long, byte[]>> patches = new List<KeyValuePair<long, byte[]>>(count);
        for (int i = 0; i < count; i++)
        {
            long offset;
            int length;
            FlacResult.ThrowIfFailed(FlacNative.EncoderPatchInfo(native, i, &offset, &length), "describing a patch", native);

            byte[] bytes = new byte[length];
            if (length > 0)
            {
                fixed (byte* destination = bytes)
                {
                    int copied = FlacNative.EncoderReadPatch(native, i, destination, length);
                    FlacResult.ThrowIfFailed(copied, "reading a patch", native);
                }
            }

            patches.Add(new KeyValuePair<long, byte[]>(offset, bytes));
        }

        return patches;
    }

    /// <summary>Copies pending stream output into <paramref name="buffer" />.</summary>
    /// <param name="buffer">The destination.</param>
    /// <returns>Bytes copied; 0 when nothing is pending.</returns>
    internal unsafe int ReadOutput(byte[] buffer)
    {
        IntPtr native = Native;
        fixed (byte* destination = buffer)
        {
            int copied = FlacNative.EncoderReadOutput(native, destination, buffer.Length);
            FlacResult.ThrowIfFailed(copied, "reading encoded output", native);
            return copied;
        }
    }

    /// <summary>The number of packets waiting in packet mode.</summary>
    internal int PendingPackets
    {
        get
        {
            IntPtr native = Native;
            int count = FlacNative.EncoderPendingPackets(native);
            FlacResult.ThrowIfFailed(count, "counting the packets", native);
            return count;
        }
    }

    /// <summary>Removes and returns the oldest pending packet, if any.</summary>
    /// <param name="packet">The packet, or default.</param>
    /// <returns>True when a packet was returned.</returns>
    internal unsafe bool TryReadPacket(out FlacPacket packet)
    {
        packet = default;
        if (PendingPackets == 0) return false;

        IntPtr native = Native;
        int length;
        long firstSample;
        int sampleCount;
        FlacResult.ThrowIfFailed(
            FlacNative.EncoderPacketInfo(native, &length, &firstSample, &sampleCount), "describing a packet", native);

        byte[] data = new byte[length];
        fixed (byte* destination = data)
        {
            int copied = FlacNative.EncoderReadPacket(native, destination, length);
            FlacResult.ThrowIfFailed(copied, "reading a packet", native);
        }

        packet = new FlacPacket(data, firstSample, sampleCount);
        return true;
    }

    /// <summary>Releases the native encoder. Calling it more than once does nothing.</summary>
    public void Dispose()
    {
        handle?.Dispose();
        handle = null;
    }

    private unsafe void WriteChunk(int frames)
    {
        IntPtr native = Native;
        fixed (int* start = conversion)
        {
            int code = FlacNative.EncoderWriteInt32(native, start, frames);
            FlacResult.ThrowIfFailed(code, "encoding samples", native);
        }

        framesWritten += frames;
        ChunkEncoded?.Invoke();
    }

    private unsafe byte[] ReadHeaderNative()
    {
        IntPtr native = Native;
        int length = FlacNative.EncoderHeaderLength(native);
        FlacResult.ThrowIfFailed(length, "measuring the stream header", native);

        byte[] header = new byte[length];
        fixed (byte* destination = header)
        {
            int copied = FlacNative.EncoderReadHeader(native, destination, length);
            FlacResult.ThrowIfFailed(copied, "reading the stream header", native);
        }

        return header;
    }

    private static unsafe IntPtr CreateNative(int channels, int sampleRate, FlacEncoderOptions options)
    {
        IntPtr created = IntPtr.Zero;

        int code = FlacNative.EncoderCreate(
            channels,
            sampleRate,
            options.BitsPerSample,
            options.CompressionLevel,
            options.BlockSize,
            options.Verify ? 1 : 0,
            options.StreamableSubset ? 1 : 0,
            options.TotalSamplesEstimate,
            &created);

        if (code != FlacResult.Ok)
        {
            if (created != IntPtr.Zero) FlacNative.EncoderDestroy(created);

            throw new FlacEncoderException(
                $"The FLAC encoder could not be created for {channels} channel(s) at {sampleRate} Hz, "
                + $"{options.BitsPerSample}-bit, level {options.CompressionLevel}: {FlacResult.Describe(code)}.",
                code);
        }

        if (created == IntPtr.Zero)
        {
            throw new FlacEncoderException("The FLAC encoder reported success but returned no encoder.", FlacResult.FlacFault);
        }

        return created;
    }

    private static unsafe void AddComment(IntPtr native, string tag, string value)
    {
        byte[] tagBytes = NulTerminatedUtf8(tag);
        byte[] valueBytes = NulTerminatedUtf8(value);

        fixed (byte* t = tagBytes)
        fixed (byte* v = valueBytes)
        {
            FlacResult.ThrowIfFailed(FlacNative.EncoderAddComment(native, t, v), $"adding the comment '{tag}'", native);
        }
    }

    private static byte[] NulTerminatedUtf8(string text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        byte[] bytes = new byte[length + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }
}
