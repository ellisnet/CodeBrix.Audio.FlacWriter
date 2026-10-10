using System;

namespace CodeBrix.Audio.FlacWriter.Internal;

/// <summary>The argument checks the two encoders share.</summary>
internal static class FlacArguments
{
    /// <summary>The most channels a FLAC stream holds.</summary>
    internal const int MaximumChannels = 8;

    /// <summary>The highest sample rate a FLAC stream holds, in hertz.</summary>
    internal const int MaximumSampleRate = 655_350;

    /// <summary>Checks a channel count and sample rate.</summary>
    /// <param name="channels">The channel count.</param>
    /// <param name="sampleRate">The sample rate in hertz.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either is out of range.</exception>
    internal static void Check(int channels, int sampleRate)
    {
        if (channels < 1 || channels > MaximumChannels)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "A FLAC stream holds 1 to 8 channels.");
        }

        if (sampleRate < 1 || sampleRate > MaximumSampleRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate), sampleRate, "A FLAC stream holds sample rates from 1 to 655350 Hz.");
        }
    }
}
