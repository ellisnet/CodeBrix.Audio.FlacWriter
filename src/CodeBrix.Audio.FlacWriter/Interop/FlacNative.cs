using System;
using System.Runtime.InteropServices;

namespace CodeBrix.Audio.FlacWriter.Interop;

/// <summary>
/// The codebrix_flac entry points, as source-generated <c>LibraryImport</c> declarations mirroring
/// <c>flac-native-tools/shim/codebrix_flac.h</c> one for one.
/// </summary>
/// <remarks>
/// Every parameter is an opaque handle, a 32- or 64-bit integer or a byte / integer pointer - the header keeps
/// C <c>long</c>, callbacks and every libFLAC structure out of the contract - so one declaration set is right
/// on every platform. The static constructor installs the library resolver, so the first call through any of
/// these finds the native in the package's <c>runtimes/</c> layout.
/// </remarks>
internal static unsafe partial class FlacNative
{
    static FlacNative()
    {
        FlacLibrary.EnsureResolverInstalled();
    }

    /// <summary>Returns the contract version of the loaded library.</summary>
    /// <returns><c>CODEBRIX_FLAC_API_VERSION</c>.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_api_version")]
    internal static partial int ApiVersion();

    /// <summary>Returns libFLAC's own version string. The pointer is static and owned by the library.</summary>
    /// <returns>A pointer to a NUL-terminated ASCII string.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_library_version")]
    internal static partial IntPtr LibraryVersion();

    /// <summary>Creates an encoder; nothing is written until <see cref="EncoderStart" />.</summary>
    /// <param name="channels">Channel count, 1 to 8.</param>
    /// <param name="sampleRateHz">Sample rate, 1 to 655350.</param>
    /// <param name="bitsPerSample">Bits per sample, 4 to 32.</param>
    /// <param name="compressionLevel">0 to 8, or -1 for libFLAC's default.</param>
    /// <param name="blockSize">0 for libFLAC's choice, otherwise 16 to 65535.</param>
    /// <param name="verify">1 to decode every frame while encoding.</param>
    /// <param name="streamableSubset">1 to constrain to the streamable subset.</param>
    /// <param name="totalSamplesEstimate">The expected total frame count, or 0.</param>
    /// <param name="encoder">Receives the handle.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_create")]
    internal static partial int EncoderCreate(
        int channels,
        int sampleRateHz,
        int bitsPerSample,
        int compressionLevel,
        int blockSize,
        int verify,
        int streamableSubset,
        long totalSamplesEstimate,
        IntPtr* encoder);

    /// <summary>Adds a Vorbis comment. Before start only.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="tagUtf8">The NUL-terminated UTF-8 tag.</param>
    /// <param name="valueUtf8">The NUL-terminated UTF-8 value.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_add_comment")]
    internal static partial int EncoderAddComment(IntPtr encoder, byte* tagUtf8, byte* valueUtf8);

    /// <summary>Sets the size of the PADDING block (0 = none). Before start only.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="paddingBytes">The padding size in bytes.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_set_padding")]
    internal static partial int EncoderSetPadding(IntPtr encoder, int paddingBytes);

    /// <summary>Requests a SEEKTABLE with a point every <paramref name="samplesBetweenPoints" /> frames.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="samplesBetweenPoints">Frames between seek points; 0 = no table.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_set_seek_table")]
    internal static partial int EncoderSetSeekTable(IntPtr encoder, long samplesBetweenPoints);

    /// <summary>Turns packet mode on (1) or off (0). Before start only.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="enabled">1 for packet mode.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_set_packet_mode")]
    internal static partial int EncoderSetPacketMode(IntPtr encoder, int enabled);

    /// <summary>Initialises libFLAC and emits the stream header.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_start")]
    internal static partial int EncoderStart(IntPtr encoder);

    /// <summary>Encodes interleaved 32-bit integer frames.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="interleavedSamples">The samples, <paramref name="frameCount" /> times the channel count.</param>
    /// <param name="frameCount">How many frames there are.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_write_int32")]
    internal static partial int EncoderWriteInt32(IntPtr encoder, int* interleavedSamples, int frameCount);

    /// <summary>Finishes the stream and records the patches.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_finish")]
    internal static partial int EncoderFinish(IntPtr encoder);

    /// <summary>The number of output bytes waiting to be read.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The byte count, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_pending_output")]
    internal static partial long EncoderPendingOutput(IntPtr encoder);

    /// <summary>Copies pending output out and removes it from the buffer.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="destination">Where to copy to.</param>
    /// <param name="capacity">How many bytes fit.</param>
    /// <returns>The bytes copied, 0 when nothing is pending, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_read_output")]
    internal static partial int EncoderReadOutput(IntPtr encoder, byte* destination, int capacity);

    /// <summary>The length of the stream header in bytes.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The length, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_header_length")]
    internal static partial int EncoderHeaderLength(IntPtr encoder);

    /// <summary>Copies the stream header, with every patch so far applied.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="destination">Where to copy to.</param>
    /// <param name="capacity">How many bytes fit.</param>
    /// <returns>The bytes copied, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_read_header")]
    internal static partial int EncoderReadHeader(IntPtr encoder, byte* destination, int capacity);

    /// <summary>The number of patches recorded by finish.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The count, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_patch_count")]
    internal static partial int EncoderPatchCount(IntPtr encoder);

    /// <summary>The stream offset and length of one patch.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="index">The 0-based patch index.</param>
    /// <param name="offset">Receives the absolute byte offset from the start of the stream.</param>
    /// <param name="length">Receives the length in bytes.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_patch_info")]
    internal static partial int EncoderPatchInfo(IntPtr encoder, int index, long* offset, int* length);

    /// <summary>Copies one patch's bytes. Patches are not consumed.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="index">The 0-based patch index.</param>
    /// <param name="destination">Where to copy to.</param>
    /// <param name="capacity">How many bytes fit.</param>
    /// <returns>The bytes copied, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_read_patch")]
    internal static partial int EncoderReadPatch(IntPtr encoder, int index, byte* destination, int capacity);

    /// <summary>The number of encoded frames waiting in the packet queue.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The count, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_pending_packets")]
    internal static partial int EncoderPendingPackets(IntPtr encoder);

    /// <summary>Describes the oldest pending packet without removing it.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="length">Receives its length in bytes.</param>
    /// <param name="firstSample">Receives the index of its first sample.</param>
    /// <param name="sampleCount">Receives its sample (frame) count.</param>
    /// <returns>A <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_packet_info")]
    internal static partial int EncoderPacketInfo(IntPtr encoder, int* length, long* firstSample, int* sampleCount);

    /// <summary>Copies the oldest pending packet out and removes it from the queue.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="destination">Where to copy to.</param>
    /// <param name="capacity">How many bytes fit.</param>
    /// <returns>The bytes copied, or a negative <see cref="FlacResult" /> code.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_read_packet")]
    internal static partial int EncoderReadPacket(IntPtr encoder, byte* destination, int capacity);

    /// <summary>The total input frames accepted so far.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The count, or negative for a null handle.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_samples_written")]
    internal static partial long EncoderSamplesWritten(IntPtr encoder);

    /// <summary>libFLAC's textual state after the last failing call, or "" after a successful one.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>A pointer to a NUL-terminated ASCII string owned by the handle.</returns>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_last_state")]
    internal static partial IntPtr EncoderLastState(IntPtr encoder);

    /// <summary>Releases an encoder. A null handle is ignored.</summary>
    /// <param name="encoder">The encoder.</param>
    [LibraryImport(FlacLibrary.LibraryName, EntryPoint = "codebrix_flac_encoder_destroy")]
    internal static partial void EncoderDestroy(IntPtr encoder);
}
