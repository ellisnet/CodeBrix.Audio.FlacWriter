using System;
using System.IO;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.FlacWriter.Internal;

/// <summary>
/// The <see cref="IAudioFileWriter" /> seam over <see cref="FlacEncoder" />.
/// </summary>
/// <remarks>
/// The seam accepts any number of interleaved samples per call, while the encoder consumes whole frames only,
/// so a trailing partial frame is held here and completed by the next call. A partial frame still held when
/// the file is finished is dropped - it cannot be encoded without the rest of its channels.
/// </remarks>
internal sealed class FlacStreamAudioFileWriter : IAudioFileWriter
{
    private readonly WaveFormat format;
    private readonly float[] carry;

    private FlacEncoder encoder;
    private int carried;
    private long samplesWritten;
    private bool finished;

    /// <summary>Creates the adapter and its encoder, writing the FLAC stream header at once.</summary>
    /// <param name="stream">The stream to write to. It is not owned by this writer.</param>
    /// <param name="format">The stored format, reported as <see cref="WaveFormat" />; its rate and channel count are used.</param>
    /// <param name="options">The encoder settings, from the factory, with the bit depth already resolved.</param>
    internal FlacStreamAudioFileWriter(Stream stream, WaveFormat format, FlacEncoderOptions options)
    {
        this.format = format;
        carry = new float[format.Channels];
        encoder = new FlacEncoder(stream, format.Channels, format.SampleRate, options);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => format;

    /// <inheritdoc />
    public long SamplesWritten => samplesWritten;

    /// <inheritdoc />
    public void Write(float[] samples, int offset, int count)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));

        if (offset < 0 || offset > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset must fall inside the buffer.");
        }

        if (count < 0 || offset + count > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The count must not run past the end of the buffer.");
        }

        Write(new ReadOnlySpan<float>(samples, offset, count));
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        if (finished)
        {
            throw new InvalidOperationException("This FLAC file has been finished; nothing more can be written to it.");
        }

        samplesWritten += samples.Length;
        int channels = carry.Length;

        if (carried > 0)
        {
            int take = Math.Min(channels - carried, samples.Length);
            samples.Slice(0, take).CopyTo(carry.AsSpan(carried));
            carried += take;
            samples = samples.Slice(take);

            if (carried < channels) return;

            encoder.Encode(carry);
            carried = 0;
        }

        int consumed = encoder.Encode(samples) * channels;
        ReadOnlySpan<float> rest = samples.Slice(consumed);
        rest.CopyTo(carry);
        carried = rest.Length;
    }

    /// <inheritdoc />
    public void Finish()
    {
        if (finished) return;
        finished = true;

        encoder.Dispose();
        encoder = null;
    }

    /// <summary>Finishes the file if it is not finished already.</summary>
    public void Dispose() => Finish();
}
