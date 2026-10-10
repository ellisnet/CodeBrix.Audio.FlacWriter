using System;

namespace CodeBrix.Audio.FlacWriter.Internal;

/// <summary>The float-to-integer quantisation the encoders apply to float input.</summary>
/// <remarks>
/// A float sample <c>f</c> becomes <c>round(f * 2^(bits-1))</c>, rounded to nearest with ties to even, clipped
/// to the signed range of <c>bits</c> bits; NaN becomes 0. So 1.0 maps to the largest positive value and -1.0 to
/// the most negative one. No dither is applied.
/// </remarks>
internal static class FlacSampleConverter
{
    /// <summary>Quantises one sample.</summary>
    /// <param name="sample">The float sample, nominally in [-1, 1].</param>
    /// <param name="bitsPerSample">The target width, 4 to 32.</param>
    /// <returns>The integer sample.</returns>
    internal static int Quantize(float sample, int bitsPerSample)
    {
        if (float.IsNaN(sample)) return 0;

        double scale = 1L << (bitsPerSample - 1);
        double max = scale - 1;
        double scaled = Math.Round(sample * scale);
        if (scaled > max) scaled = max;
        if (scaled < -scale) scaled = -scale;
        return (int)(long)scaled;
    }

    /// <summary>Quantises a run of samples.</summary>
    /// <param name="source">The float samples.</param>
    /// <param name="destination">Where to write the integers; at least as long as <paramref name="source" />.</param>
    /// <param name="bitsPerSample">The target width, 4 to 32.</param>
    internal static void FloatToInt(ReadOnlySpan<float> source, Span<int> destination, int bitsPerSample)
    {
        for (int i = 0; i < source.Length; i++) destination[i] = Quantize(source[i], bitsPerSample);
    }
}
