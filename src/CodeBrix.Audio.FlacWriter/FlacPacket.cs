using System;

namespace CodeBrix.Audio.FlacWriter;

/// <summary>
/// One encoded FLAC frame from a <see cref="FlacPacketEncoder" />: the bytes a container stores as one packet,
/// with its position on the timeline.
/// </summary>
public readonly struct FlacPacket
{
    /// <summary>Creates a packet.</summary>
    /// <param name="data">The encoded FLAC frame, from its sync code to its CRC-16.</param>
    /// <param name="firstSample">The index (in frames) of the first sample the packet decodes to.</param>
    /// <param name="sampleCount">How many frames the packet decodes to.</param>
    public FlacPacket(ReadOnlyMemory<byte> data, long firstSample, int sampleCount)
    {
        Data = data;
        FirstSample = firstSample;
        SampleCount = sampleCount;
    }

    /// <summary>The encoded FLAC frame, from its sync code to its CRC-16 - exactly one frame, never part of one.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// The index, in frames (one sample per channel), of the first sample this packet decodes to - the
    /// container timestamp in samples. Divide by the sample rate for seconds.
    /// </summary>
    public long FirstSample { get; }

    /// <summary>How many frames (one sample per channel) this packet decodes to - the packet's duration in samples.</summary>
    public int SampleCount { get; }
}
