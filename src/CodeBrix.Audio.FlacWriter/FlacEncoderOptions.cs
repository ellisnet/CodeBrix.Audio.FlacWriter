using System;
using System.Collections.Generic;

namespace CodeBrix.Audio.FlacWriter;

/// <summary>Settings for a <see cref="FlacEncoder" /> or a <see cref="FlacPacketEncoder" />.</summary>
/// <remarks>
/// <para>
/// A null options argument means a fresh instance of this class: 16-bit samples, compression level 5, the block
/// size libFLAC picks for that level, verification ON, the streamable subset, no padding, no seek table and no
/// comments. The settings are read once, when the encoder is created; changing them afterwards has no effect on
/// that encoder.
/// </para>
/// <para>
/// FLAC is always lossless: no setting here changes the decoded audio. <see cref="CompressionLevel" /> trades
/// encoding time for file size, and <see cref="BitsPerSample" /> is the stored precision of the samples.
/// </para>
/// </remarks>
public sealed class FlacEncoderOptions
{
    /// <summary>The smallest <see cref="BitsPerSample" /> FLAC can store.</summary>
    public const int MinimumBitsPerSample = 4;

    /// <summary>The largest <see cref="BitsPerSample" /> FLAC can store.</summary>
    public const int MaximumBitsPerSample = 32;

    /// <summary>The default <see cref="BitsPerSample" />.</summary>
    public const int DefaultBitsPerSample = 16;

    /// <summary>The fastest <see cref="CompressionLevel" />.</summary>
    public const int MinimumCompressionLevel = 0;

    /// <summary>The smallest-output <see cref="CompressionLevel" />.</summary>
    public const int MaximumCompressionLevel = 8;

    /// <summary>The default <see cref="CompressionLevel" /> (libFLAC's own default).</summary>
    public const int DefaultCompressionLevel = 5;

    /// <summary>The smallest explicit <see cref="BlockSize" />.</summary>
    public const int MinimumBlockSize = 16;

    /// <summary>The largest explicit <see cref="BlockSize" />.</summary>
    public const int MaximumBlockSize = 65535;

    /// <summary>The largest <see cref="PaddingBytes" /> one metadata block can hold (a 24-bit length).</summary>
    public const int MaximumPaddingBytes = 16_777_215;

    /// <summary>The largest frame count STREAMINFO can record (a 36-bit field).</summary>
    public const long MaximumTotalSamples = (1L << 36) - 1;

    /// <summary>
    /// The stored precision of each sample, 4 to 32 bits; default 16. Float input is scaled and rounded to this
    /// width (with clipping beyond [-1, 1]); integer input must already fit it. The streamable subset allows only
    /// 8, 12, 16, 20, 24 and 32 - set <see cref="StreamableSubset" /> to false for any other width.
    /// </summary>
    public int BitsPerSample { get; set; } = DefaultBitsPerSample;

    /// <summary>
    /// libFLAC's compression level, 0 (fastest, largest) to 8 (slowest, smallest); default 5. Every level is
    /// lossless; the level only changes encoding effort and file size.
    /// </summary>
    public int CompressionLevel { get; set; } = DefaultCompressionLevel;

    /// <summary>
    /// The number of frames per FLAC block, or 0 (the default) for the size libFLAC uses at
    /// <see cref="CompressionLevel" /> (4096 at the usual levels). Otherwise 16 to 65535; the streamable subset
    /// allows at most 4608 at sample rates up to 48 kHz and at most 16384 above.
    /// </summary>
    public int BlockSize { get; set; }

    /// <summary>
    /// Whether libFLAC decodes every frame as it is produced and compares it with the input; default TRUE. A
    /// mismatch fails the encode with a <see cref="FlacEncoderException" /> whose
    /// <see cref="FlacEncoderException.IsVerifyMismatch" /> is true. Turning it off makes encoding faster.
    /// </summary>
    public bool Verify { get; set; } = true;

    /// <summary>
    /// Whether to keep to the FLAC "streamable subset" every player must accept; default true. Set it to false
    /// only to allow a bit depth other than 8, 12, 16, 20, 24 or 32, a block size outside the subset (more than
    /// 4608 frames at up to 48 kHz, more than 16384 above), or a sample rate the subset does not list.
    /// </summary>
    public bool StreamableSubset { get; set; } = true;

    /// <summary>
    /// The number of frames you expect to encode, or 0 (the default) when unknown. It sizes the seek table and
    /// lets STREAMINFO carry a total before the stream is finished - useful when the destination cannot seek. The
    /// real total is always written when the stream is finished and the destination can seek.
    /// </summary>
    public long TotalSamplesEstimate { get; set; }

    /// <summary>
    /// The size in bytes of a PADDING metadata block written after the other metadata, or 0 (the default) for
    /// none. A few kilobytes let a tag editor grow the comments later without rewriting the file.
    /// </summary>
    public int PaddingBytes { get; set; }

    /// <summary>
    /// The spacing, in frames, of the points in a SEEKTABLE metadata block, or 0 (the default) for no seek
    /// table. Needs <see cref="TotalSamplesEstimate" /> greater than 0, because the table is laid out before the
    /// audio is encoded; it is filled in when the stream is finished and the destination can seek.
    /// </summary>
    public long SeekTableSamplesBetweenPoints { get; set; }

    /// <summary>
    /// Vorbis comments (tags) written into the VORBIS_COMMENT block - for example <c>TITLE</c>, <c>ARTIST</c>,
    /// <c>ALBUM</c>. Names are ASCII 0x20 to 0x7D without <c>=</c>; values are any text without NUL characters and
    /// are written as UTF-8. The vendor string is written by the encoder itself.
    /// </summary>
    public IDictionary<string, string> Comments { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>A copy of these settings, with its own comment dictionary.</summary>
    /// <returns>The copy.</returns>
    internal FlacEncoderOptions Clone() =>
        new FlacEncoderOptions
        {
            BitsPerSample = BitsPerSample,
            CompressionLevel = CompressionLevel,
            BlockSize = BlockSize,
            Verify = Verify,
            StreamableSubset = StreamableSubset,
            TotalSamplesEstimate = TotalSamplesEstimate,
            PaddingBytes = PaddingBytes,
            SeekTableSamplesBetweenPoints = SeekTableSamplesBetweenPoints,
            Comments = Comments == null
                ? null
                : new Dictionary<string, string>(Comments, StringComparer.Ordinal),
        };

    /// <summary>Checks every setting, so a bad one is reported where it was made.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside its permitted range.</exception>
    /// <exception cref="ArgumentException">A combination of settings, or a comment, is not usable.</exception>
    internal void Validate()
    {
        if (BitsPerSample < MinimumBitsPerSample || BitsPerSample > MaximumBitsPerSample)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BitsPerSample), BitsPerSample, "FLAC stores 4 to 32 bits per sample.");
        }

        if (CompressionLevel < MinimumCompressionLevel || CompressionLevel > MaximumCompressionLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel), CompressionLevel, "The compression level must be between 0 and 8.");
        }

        if (BlockSize != 0 && (BlockSize < MinimumBlockSize || BlockSize > MaximumBlockSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BlockSize), BlockSize, "The block size must be 0 (libFLAC's choice) or between 16 and 65535.");
        }

        if (TotalSamplesEstimate < 0 || TotalSamplesEstimate > MaximumTotalSamples)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TotalSamplesEstimate), TotalSamplesEstimate, "The total samples estimate must be 0 (unknown) or a positive 36-bit count.");
        }

        if (PaddingBytes < 0 || PaddingBytes > MaximumPaddingBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PaddingBytes), PaddingBytes, "The padding must be between 0 and 16777215 bytes.");
        }

        if (SeekTableSamplesBetweenPoints < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SeekTableSamplesBetweenPoints), SeekTableSamplesBetweenPoints, "The seek point spacing must not be negative.");
        }

        if (SeekTableSamplesBetweenPoints > 0 && TotalSamplesEstimate == 0)
        {
            throw new ArgumentException(
                "A seek table needs TotalSamplesEstimate: the table is laid out before the audio is encoded.",
                nameof(SeekTableSamplesBetweenPoints));
        }

        if (Comments == null) return;

        foreach (KeyValuePair<string, string> comment in Comments)
        {
            if (string.IsNullOrEmpty(comment.Key))
            {
                throw new ArgumentException("A comment name must not be empty.", nameof(Comments));
            }

            foreach (char c in comment.Key)
            {
                if (c < 0x20 || c > 0x7D || c == '=')
                {
                    throw new ArgumentException(
                        $"The comment name '{comment.Key}' is not valid: names are ASCII 0x20 to 0x7D without '='.",
                        nameof(Comments));
                }
            }

            if (comment.Value == null)
            {
                throw new ArgumentException($"The comment '{comment.Key}' has no value.", nameof(Comments));
            }

            if (comment.Value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException(
                    $"The value of comment '{comment.Key}' contains a NUL character, which a Vorbis comment cannot carry.",
                    nameof(Comments));
            }
        }
    }
}
