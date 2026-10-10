using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using CodeBrix.Audio.Wave;

namespace CodeBrix.Audio.FlacWriter.Tests;

/// <summary>
/// Generates the test signals (no recordings - every tone is synthesized here), encodes them, and decodes the
/// result back with CodeBrix.Audio's managed FLAC reader.
/// </summary>
internal static class TestAudio
{
    /// <summary>One decoded FLAC stream.</summary>
    internal sealed class Decoded
    {
        /// <summary>The channel count.</summary>
        public int Channels { get; init; }

        /// <summary>The sample rate.</summary>
        public int SampleRate { get; init; }

        /// <summary>The bit depth the audio was encoded at.</summary>
        public int BitsPerSample { get; init; }

        /// <summary>The total frame count the reader reports (0 when STREAMINFO does not record it).</summary>
        public long ReportedFrames { get; init; }

        /// <summary>Every comment, keyed by upper-case name.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Tags { get; init; }

        /// <summary>The decoded interleaved samples, as signed integers at the encoded bit depth.</summary>
        public int[] Samples { get; init; }
    }

    /// <summary>A float sine tone in [-amplitude, amplitude], with a different phase on each channel.</summary>
    /// <param name="frequency">Frequency in hertz.</param>
    /// <param name="amplitude">Peak amplitude.</param>
    /// <param name="sampleRate">Sample rate.</param>
    /// <param name="channels">Channels.</param>
    /// <param name="frames">Frames.</param>
    /// <returns>Interleaved samples.</returns>
    public static float[] Sine(double frequency, float amplitude, int sampleRate, int channels, int frames)
    {
        float[] samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                samples[(i * channels) + c] = (float)(amplitude * Math.Sin((2 * Math.PI * frequency * i / sampleRate) + c));
            }
        }

        return samples;
    }

    /// <summary>An integer sine tone at a bit depth, with a little seeded noise so frames are not trivial.</summary>
    /// <param name="frequency">Frequency in hertz.</param>
    /// <param name="sampleRate">Sample rate.</param>
    /// <param name="channels">Channels.</param>
    /// <param name="frames">Frames.</param>
    /// <param name="bitsPerSample">The bit depth the values must fit.</param>
    /// <param name="seed">Noise seed.</param>
    /// <returns>Interleaved samples within the signed range of <paramref name="bitsPerSample" /> bits.</returns>
    public static int[] SineInt(double frequency, int sampleRate, int channels, int frames, int bitsPerSample, int seed = 1)
    {
        Random random = new Random(seed);
        long full = 1L << (bitsPerSample - 1);
        double amplitude = full * 0.6;
        int noise = (int)Math.Max(1, full / 256);
        int[] samples = new int[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                double value = amplitude * Math.Sin((2 * Math.PI * frequency * (c + 1) * i / sampleRate) + c);
                samples[(i * channels) + c] = (int)Math.Round(value) + random.Next(-noise, noise + 1);
            }
        }

        return samples;
    }

    /// <summary>Quantises float samples exactly as the encoders do.</summary>
    /// <param name="samples">Float samples.</param>
    /// <param name="bitsPerSample">Bit depth.</param>
    /// <returns>The integers the encoder stores.</returns>
    public static int[] Quantize(float[] samples, int bitsPerSample)
    {
        int[] result = new int[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double scale = 1L << (bitsPerSample - 1);
            double value = Math.Round(samples[i] * scale);
            result[i] = (int)Math.Clamp(value, -scale, scale - 1);
        }

        return result;
    }

    /// <summary>Encodes integer samples in one call to a seekable memory stream.</summary>
    /// <param name="samples">Interleaved samples.</param>
    /// <param name="channels">Channels.</param>
    /// <param name="sampleRate">Sample rate.</param>
    /// <param name="options">Options, or null.</param>
    /// <returns>The FLAC file bytes.</returns>
    public static byte[] Encode(int[] samples, int channels, int sampleRate, FlacEncoderOptions options)
    {
        using MemoryStream output = new MemoryStream();
        using (FlacEncoder encoder = new FlacEncoder(output, channels, sampleRate, options))
        {
            encoder.EncodeInt32(samples);
        }

        return output.ToArray();
    }

    /// <summary>Encodes integer samples with the packet encoder, returning the final header and the packets.</summary>
    /// <param name="samples">Interleaved samples.</param>
    /// <param name="channels">Channels.</param>
    /// <param name="sampleRate">Sample rate.</param>
    /// <param name="options">Options, or null.</param>
    /// <param name="packets">Receives every packet in order.</param>
    /// <returns>The final stream header.</returns>
    public static byte[] EncodePackets(int[] samples, int channels, int sampleRate, FlacEncoderOptions options, out List<FlacPacket> packets)
    {
        packets = new List<FlacPacket>();
        using FlacPacketEncoder encoder = new FlacPacketEncoder(channels, sampleRate, options);
        int chunk = 1000 * channels;
        for (int offset = 0; offset < samples.Length; offset += chunk)
        {
            encoder.EncodeInt32(samples.AsSpan(offset, Math.Min(chunk, samples.Length - offset)));
            while (encoder.TryReadPacket(out FlacPacket packet)) packets.Add(packet);
        }

        encoder.Finish();
        while (encoder.TryReadPacket(out FlacPacket packet)) packets.Add(packet);
        return encoder.StreamHeader.ToArray();
    }

    /// <summary>Decodes a FLAC stream completely with CodeBrix.Audio's managed reader.</summary>
    /// <param name="flac">The encoded bytes.</param>
    /// <returns>What was decoded.</returns>
    public static Decoded Decode(byte[] flac)
    {
        using FlacFileReader reader = new FlacFileReader(new MemoryStream(flac));

        WaveFormat format = reader.WaveFormat;
        int container = format.BitsPerSample / 8;
        int shift = format.BitsPerSample - reader.SourceBitsPerSample;
        List<int> all = new List<int>();
        byte[] buffer = new byte[4096 * format.BlockAlign];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + container <= read; i += container)
            {
                all.Add(ReadSigned(buffer, i, container) >> shift);
            }
        }

        return new Decoded
        {
            Channels = format.Channels,
            SampleRate = format.SampleRate,
            BitsPerSample = reader.SourceBitsPerSample,
            ReportedFrames = reader.Length / format.BlockAlign,
            Tags = reader.Tags,
            Samples = all.ToArray(),
        };
    }

    /// <summary>The MD5 FLAC records in STREAMINFO for these samples: each sample little-endian in whole bytes.</summary>
    /// <param name="samples">Interleaved samples.</param>
    /// <param name="bitsPerSample">Bit depth.</param>
    /// <returns>The 16-byte digest.</returns>
    public static byte[] FlacMd5(int[] samples, int bitsPerSample)
    {
        int bytesPerSample = (bitsPerSample + 7) / 8;
        byte[] raw = new byte[samples.Length * bytesPerSample];
        for (int i = 0; i < samples.Length; i++)
        {
            for (int b = 0; b < bytesPerSample; b++) raw[(i * bytesPerSample) + b] = (byte)(samples[i] >> (8 * b));
        }

        return MD5.HashData(raw);
    }

    /// <summary>Reads a little-endian signed integer of 1 to 4 bytes.</summary>
    /// <param name="buffer">The bytes.</param>
    /// <param name="offset">Where it starts.</param>
    /// <param name="bytes">Its width in bytes.</param>
    /// <returns>The sign-extended value.</returns>
    public static int ReadSigned(byte[] buffer, int offset, int bytes)
    {
        if (bytes == 1) return buffer[offset] - 128;

        int value = 0;
        for (int b = 0; b < bytes; b++) value |= buffer[offset + b] << (8 * b);
        int unused = 32 - (8 * bytes);
        return (value << unused) >> unused;
    }
}
