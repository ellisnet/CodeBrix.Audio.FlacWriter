using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Audio.FlacWriter.Internal;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.FlacWriter;

/// <summary>
/// Writes <c>.flac</c> files through CodeBrix.Audio's <see cref="AudioFileWriterRegistry" />.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CodeBrixAudioFlac.Register()" /> registers one of these, so an application making that call can
/// write .flac by file name with no further change - including through CodeBrix.Audio's
/// <c>SoundFontRenderer.RenderToFile</c>, which picks its writer from the path's extension.
/// </para>
/// <para>
/// A SEEKABLE STREAM IS PREFERRED BUT NOT REQUIRED, so <see cref="RequiresSeekableStream" /> is false. On a
/// stream that can seek, finishing the file writes the total sample count and MD5 signature into STREAMINFO; on
/// one that cannot (a pipe, a network stream), those stay as placeholders and the file is still a valid FLAC
/// stream.
/// </para>
/// <para>
/// THE BIT DEPTH COMES FROM THE FORMAT WHEN IT IS PCM. The writer receives float samples from CodeBrix.Audio
/// and stores them at the bit depth of a PCM <see cref="WaveFormat" /> passed to <see cref="Create" /> (for
/// example <c>new WaveFormat(48000, 24, 2)</c> for 24-bit); for any other format - IEEE float included - at the
/// factory's <see cref="FlacEncoderOptions.BitsPerSample" />, 16 by default. Every other setting belongs to the
/// factory: register a factory built with <see cref="FlacEncoderOptions" /> of your own, after
/// <see cref="CodeBrixAudioFlac.Register()" />, to write .flac at other settings:
/// </para>
/// <code>
/// AudioFileWriterRegistry.Register(new FlacAudioFileWriterFactory(new FlacEncoderOptions { BitsPerSample = 24, CompressionLevel = 8 }));
/// </code>
/// </remarks>
public sealed class FlacAudioFileWriterFactory : IAudioFileWriterFactory
{
    private static readonly string[] FlacExtensions = [".flac"];

    private readonly FlacEncoderOptions options;

    /// <summary>Creates the factory with the encoder defaults - 16-bit, level 5, verify on, no comments.</summary>
    public FlacAudioFileWriterFactory() : this(null)
    {
    }

    /// <summary>Creates the factory with encoder settings of your own.</summary>
    /// <param name="options">
    /// The settings every writer this factory makes is built with, or null for the defaults. A copy is taken, so
    /// changing the instance afterwards does not change the factory.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its permitted range.</exception>
    /// <exception cref="ArgumentException">A combination of options, or a comment, is not usable.</exception>
    public FlacAudioFileWriterFactory(FlacEncoderOptions options)
    {
        this.options = options == null ? new FlacEncoderOptions() : options.Clone();
        this.options.Validate();
    }

    /// <summary>A copy of the encoder settings every writer this factory makes is built with.</summary>
    public FlacEncoderOptions Options => options.Clone();

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => FlacExtensions;

    /// <inheritdoc />
    /// <remarks>
    /// False: on a stream that cannot seek the file is still complete and valid, it only lacks the total sample
    /// count and MD5 signature, which a seekable stream receives when the file is finished.
    /// </remarks>
    public bool RequiresSeekableStream => false;

    /// <inheritdoc />
    /// <remarks>PCM at the factory's <see cref="FlacEncoderOptions.BitsPerSample" /> (16 by default).</remarks>
    public WaveFormat DefaultFormat(int sampleRate, int channels)
    {
        FlacArguments.Check(channels, sampleRate);
        return new WaveFormat(sampleRate, options.BitsPerSample, channels);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="WaveFormat.SampleRate" /> and <see cref="WaveFormat.Channels" /> are always read from
    /// <paramref name="format" />, and <see cref="WaveFormat.BitsPerSample" /> too when it is PCM. The stream is
    /// NOT closed by the writer.
    /// </remarks>
    public IAudioFileWriter Create(Stream stream, WaveFormat format)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (format == null) throw new ArgumentNullException(nameof(format));

        if (!stream.CanWrite)
        {
            throw new ArgumentException("A .flac file cannot be written to a stream that is not writable.", nameof(stream));
        }

        if (format.SampleRate < 1 || format.SampleRate > FlacArguments.MaximumSampleRate)
        {
            throw new ArgumentException(
                $"A FLAC stream holds sample rates from 1 to 655350 Hz, and this format asks for {format.SampleRate}.", nameof(format));
        }

        if (format.Channels < 1 || format.Channels > FlacArguments.MaximumChannels)
        {
            throw new ArgumentException(
                $"A FLAC stream holds 1 to 8 channels, and this format asks for {format.Channels}.", nameof(format));
        }

        FlacEncoderOptions effective = options.Clone();
        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            if (format.BitsPerSample < FlacEncoderOptions.MinimumBitsPerSample
                || format.BitsPerSample > FlacEncoderOptions.MaximumBitsPerSample)
            {
                throw new ArgumentException(
                    $"FLAC stores 4 to 32 bits per sample, and this PCM format asks for {format.BitsPerSample}.", nameof(format));
            }

            effective.BitsPerSample = format.BitsPerSample;
            effective.Validate();
        }

        WaveFormat stored = format.Encoding == WaveFormatEncoding.Pcm
            ? format
            : new WaveFormat(format.SampleRate, effective.BitsPerSample, format.Channels);

        return new FlacStreamAudioFileWriter(stream, stored, effective);
    }
}
