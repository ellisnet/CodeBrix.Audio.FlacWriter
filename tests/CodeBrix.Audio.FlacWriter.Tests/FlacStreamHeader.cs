using System;
using System.Collections.Generic;

namespace CodeBrix.Audio.FlacWriter.Tests;

/// <summary>A minimal parser of the FLAC stream header, written in the test from the format specification.</summary>
internal sealed class FlacStreamHeader
{
    /// <summary>One metadata block.</summary>
    internal sealed class Block
    {
        /// <summary>The block type (0 STREAMINFO, 1 PADDING, 3 SEEKTABLE, 4 VORBIS_COMMENT).</summary>
        public int Type { get; init; }

        /// <summary>Whether the last-metadata-block flag is set.</summary>
        public bool IsLast { get; init; }

        /// <summary>The offset of the block body (after its 4-byte header).</summary>
        public int BodyOffset { get; init; }

        /// <summary>The body length in bytes.</summary>
        public int Length { get; init; }
    }

    /// <summary>Every metadata block, in order.</summary>
    public List<Block> Blocks { get; } = new List<Block>();

    /// <summary>The length of the header - the offset of the first audio frame.</summary>
    public int Length { get; private set; }

    /// <summary>STREAMINFO minimum block size.</summary>
    public int MinimumBlockSize { get; private set; }

    /// <summary>STREAMINFO maximum block size.</summary>
    public int MaximumBlockSize { get; private set; }

    /// <summary>STREAMINFO minimum frame size.</summary>
    public int MinimumFrameSize { get; private set; }

    /// <summary>STREAMINFO maximum frame size.</summary>
    public int MaximumFrameSize { get; private set; }

    /// <summary>STREAMINFO sample rate.</summary>
    public int SampleRate { get; private set; }

    /// <summary>STREAMINFO channels.</summary>
    public int Channels { get; private set; }

    /// <summary>STREAMINFO bits per sample.</summary>
    public int BitsPerSample { get; private set; }

    /// <summary>STREAMINFO total samples (0 = unknown).</summary>
    public long TotalSamples { get; private set; }

    /// <summary>STREAMINFO MD5 signature.</summary>
    public byte[] Md5 { get; private set; }

    /// <summary>The raw bytes parsed.</summary>
    public byte[] Bytes { get; private set; }

    /// <summary>Parses the header at the start of <paramref name="data" />.</summary>
    /// <param name="data">A FLAC file or a stream header.</param>
    /// <returns>The parsed header.</returns>
    public static FlacStreamHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[0] != (byte)'f' || data[1] != (byte)'L' || data[2] != (byte)'a' || data[3] != (byte)'C')
        {
            throw new FormatException("Not a FLAC stream: no fLaC marker.");
        }

        FlacStreamHeader header = new FlacStreamHeader();
        int position = 4;
        while (true)
        {
            bool last = (data[position] & 0x80) != 0;
            int type = data[position] & 0x7F;
            int length = (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            header.Blocks.Add(new Block { Type = type, IsLast = last, BodyOffset = position + 4, Length = length });
            position += 4 + length;
            if (last) break;
        }

        header.Length = position;
        header.Bytes = data.Slice(0, position).ToArray();

        ReadOnlySpan<byte> s = data.Slice(header.Blocks[0].BodyOffset, 34);
        header.MinimumBlockSize = (s[0] << 8) | s[1];
        header.MaximumBlockSize = (s[2] << 8) | s[3];
        header.MinimumFrameSize = (s[4] << 16) | (s[5] << 8) | s[6];
        header.MaximumFrameSize = (s[7] << 16) | (s[8] << 8) | s[9];
        header.SampleRate = (s[10] << 12) | (s[11] << 4) | (s[12] >> 4);
        header.Channels = ((s[12] >> 1) & 0x07) + 1;
        header.BitsPerSample = (((s[12] & 0x01) << 4) | (s[13] >> 4)) + 1;
        header.TotalSamples = ((long)(s[13] & 0x0F) << 32) | ((long)s[14] << 24) | ((long)s[15] << 16) | ((long)s[16] << 8) | s[17];
        header.Md5 = s.Slice(18, 16).ToArray();
        return header;
    }

    /// <summary>The seek points of the SEEKTABLE block, as (sample number, byte offset) pairs.</summary>
    /// <returns>The points, or an empty list when there is no seek table.</returns>
    public List<KeyValuePair<long, long>> SeekPoints()
    {
        List<KeyValuePair<long, long>> points = new List<KeyValuePair<long, long>>();
        foreach (Block block in Blocks)
        {
            if (block.Type != 3) continue;
            for (int p = 0; p < block.Length / 18; p++)
            {
                int at = block.BodyOffset + (p * 18);
                points.Add(new KeyValuePair<long, long>(ReadUInt64(at), ReadUInt64(at + 8)));
            }
        }

        return points;
    }

    private long ReadUInt64(int at)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++) value = (value << 8) | Bytes[at + i];
        return (long)value;
    }
}
