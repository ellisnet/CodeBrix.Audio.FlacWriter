/*
 * codebrix_flac.h - the fixed-ABI C surface of the codebrix_flac native library.
 *
 * This is the ONLY contract between the managed CodeBrix.Audio.FlacWriter assembly and the native
 * library. It deliberately exposes no libFLAC type, no C `long`, no struct and NO CALLBACK: every
 * parameter is an opaque handle, an int32_t, an int64_t, or a pointer to bytes, so one managed
 * DllImport set works on every RID (C `long` is 32-bit on Windows and 64-bit elsewhere).
 *
 * The library statically links libFLAC (Xiph, BSD-3) - encoder side only is used - and exports only
 * the functions below. Output never goes through a caller callback: libFLAC's write/seek/tell
 * callbacks are implemented INSIDE the shim over an internal byte buffer, and the caller drains
 * finished bytes with codebrix_flac_encoder_read_output. Because libFLAC rewrites the STREAMINFO
 * block (and the SEEKTABLE, when one was requested) at finish by seeking back, those rewrites are
 * recorded as PATCHES (offset + bytes) that the caller applies to its own stream when it can seek;
 * the shim also applies them to its own copy of the stream header so the header can be read back
 * complete (codebrix_flac_encoder_read_header) - the bytes a container stores as codec-private data.
 *
 * Packet mode (codebrix_flac_encoder_set_packet_mode before start) keeps every encoded FLAC frame
 * as a separate packet instead of appending it to the output stream, for containers that carry
 * one FLAC frame per packet. libFLAC hands the shim exactly one write per frame, so packet
 * boundaries are exact, never scanned for.
 *
 * Threading: a handle may be used from one thread at a time. Different handles are independent.
 */
#ifndef CODEBRIX_FLAC_H
#define CODEBRIX_FLAC_H

#include <stdint.h>

#if defined(_WIN32)
#  define CBF_EXPORT __declspec(dllexport)
#else
#  define CBF_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Versions ---------------------------------------------------------------------------- */

/* The version of THIS contract. The managed side refuses a library whose value differs. Bump it
 * whenever a function below changes shape. */
#define CODEBRIX_FLAC_API_VERSION 1

/* Returns CODEBRIX_FLAC_API_VERSION of the loaded library. */
CBF_EXPORT int32_t codebrix_flac_api_version(void);

/* Returns libFLAC's own version string (FLAC__VERSION_STRING, e.g. "1.5.0"). Static; never free. */
CBF_EXPORT const char* codebrix_flac_library_version(void);

/* ---- Result codes ------------------------------------------------------------------------ */

/* Every int32_t-returning function returns 0 (CBF_OK) or one of these negatives. Keep the numbers
 * stable (bump the API version if one must change). */
#define CBF_OK                      0
#define CBF_E_INVALID_ARGUMENT     -1   /* null handle/pointer, or a value out of the documented range */
#define CBF_E_OUT_OF_MEMORY        -2
#define CBF_E_INVALID_STATE        -3   /* e.g. a set_* call after start, write after finish */
#define CBF_E_UNSUPPORTED          -4   /* libFLAC refused the parameter combination at start */
#define CBF_E_VERIFY_MISMATCH      -5   /* verify mode decoded a frame that differs from the input */
#define CBF_E_FLAC_FAULT           -6   /* any other libFLAC failure (the state is in the message, see below) */

/* ---- Encoder ----------------------------------------------------------------------------- */

typedef struct codebrix_flac_encoder codebrix_flac_encoder;

/*
 * Creates an encoder; nothing is written until codebrix_flac_encoder_start.
 *   channels              1..8
 *   sample_rate_hz        1..655350
 *   bits_per_sample       4..32 (16 and 24 are the usual values; the input samples are int32_t
 *                         holding values of this width, sign-extended)
 *   compression_level     0..8, or -1 for libFLAC's default (5)
 *   block_size            0 = let libFLAC choose for the level; otherwise 16..65535 (libFLAC may
 *                         reject sizes the streamable subset forbids when streamable_subset is 1)
 *   verify                1 = decode every frame while encoding and fail on any mismatch
 *                         (CBF_E_VERIFY_MISMATCH from write/finish); 0 = no verification
 *   streamable_subset     1 = constrain to the FLAC "streamable subset" (what players must accept);
 *                         0 = allow non-subset parameter combinations
 *   total_samples_estimate frames expected in total, or 0 when unknown. Only used to size the
 *                         optional seek table and to fill STREAMINFO early; the final value is
 *                         always patched at finish.
 *   out_encoder           receives the handle on success, NULL on failure.
 * Returns CBF_OK or a CBF_E_* code.
 */
CBF_EXPORT int32_t codebrix_flac_encoder_create(
    int32_t channels,
    int32_t sample_rate_hz,
    int32_t bits_per_sample,
    int32_t compression_level,
    int32_t block_size,
    int32_t verify,
    int32_t streamable_subset,
    int64_t total_samples_estimate,
    codebrix_flac_encoder** out_encoder);

/* Adds a Vorbis comment (tag=value, NUL-terminated UTF-8) to the VORBIS_COMMENT block. Before start
 * only; afterwards CBF_E_INVALID_STATE. The shim always sets the vendor string itself (libFLAC does). */
CBF_EXPORT int32_t codebrix_flac_encoder_add_comment(
    codebrix_flac_encoder* encoder,
    const char* tag_utf8,
    const char* value_utf8);

/* Size in bytes of the PADDING block written after the other metadata (0 = no PADDING block).
 * Before start only. Default when never called: 0. A container has no use for padding; a file
 * written for later tag editing benefits from a few KB. */
CBF_EXPORT int32_t codebrix_flac_encoder_set_padding(
    codebrix_flac_encoder* encoder,
    int32_t padding_bytes);

/* Requests a SEEKTABLE with one seek point every `samples_between_points` frames. Requires
 * total_samples_estimate > 0 at create (the table is laid out up front and filled at finish);
 * otherwise CBF_E_INVALID_STATE. 0 = no seek table (the default). Before start only. */
CBF_EXPORT int32_t codebrix_flac_encoder_set_seek_table(
    codebrix_flac_encoder* encoder,
    int64_t samples_between_points);

/* 1 = packet mode (see the file comment): encoded frames are kept as separate packets and are NOT
 * appended to the output stream; the stream header is still produced and readable through
 * codebrix_flac_encoder_read_header. 0 = stream mode (the default). Before start only. */
CBF_EXPORT int32_t codebrix_flac_encoder_set_packet_mode(
    codebrix_flac_encoder* encoder,
    int32_t enabled);

/* Initialises libFLAC and emits the stream header ("fLaC" marker + metadata blocks, STREAMINFO with
 * placeholder totals). Exactly once, before the first write. In stream mode the header bytes are
 * also the first bytes of the output. Returns CBF_E_UNSUPPORTED when libFLAC rejects the parameters. */
CBF_EXPORT int32_t codebrix_flac_encoder_start(codebrix_flac_encoder* encoder);

/*
 * Encodes `frame_count` frames of interleaved int32_t samples (frame = one sample per channel, so
 * the buffer holds frame_count * channels values), each holding a bits_per_sample-wide value.
 * Completed frames go to the output stream (stream mode) or to the packet queue (packet mode).
 * frame_count may be 0 (no-op). Returns CBF_OK, CBF_E_VERIFY_MISMATCH, or another CBF_E_* code.
 */
CBF_EXPORT int32_t codebrix_flac_encoder_write_int32(
    codebrix_flac_encoder* encoder,
    const int32_t* interleaved_samples,
    int32_t frame_count);

/*
 * Finishes the stream: flushes the last (short) frame, lets libFLAC rewrite STREAMINFO (total
 * samples, MD5 of the input, min/max frame and block sizes) and the SEEKTABLE if any - those
 * rewrites become the patch list - and, in verify mode, reports a final mismatch. After this,
 * write/finish return CBF_E_INVALID_STATE; read_output / packets / header / patches still drain.
 * Finishing a never-written encoder is valid and yields a valid empty FLAC stream.
 */
CBF_EXPORT int32_t codebrix_flac_encoder_finish(codebrix_flac_encoder* encoder);

/* ---- Output: stream mode ----------------------------------------------------------------- */

/* Bytes of finished output waiting to be read (header + frames in stream order). Negative only for a
 * null handle. */
CBF_EXPORT int64_t codebrix_flac_encoder_pending_output(codebrix_flac_encoder* encoder);

/* Copies up to `capacity` bytes of pending output into `destination`, removing them. Returns the
 * number of bytes copied (0 when nothing is pending) or a negative CBF_E_* code. Loop until 0.
 * Partial reads are fine: output is a byte stream. */
CBF_EXPORT int32_t codebrix_flac_encoder_read_output(
    codebrix_flac_encoder* encoder,
    uint8_t* destination,
    int32_t capacity);

/* ---- Output: the stream header ----------------------------------------------------------- */

/* Length in bytes of the stream header (everything from "fLaC" up to the first audio frame).
 * Valid after start. The header's CONTENT changes at finish (STREAMINFO/SEEKTABLE patched); its
 * length never does. */
CBF_EXPORT int32_t codebrix_flac_encoder_header_length(codebrix_flac_encoder* encoder);

/* Copies the stream header, with every patch so far applied, into `destination` (capacity must be
 * >= header_length). Before finish: placeholder totals; after finish: the final header - the bytes
 * a container stores as the track's codec-private data. Returns bytes copied or a CBF_E_* code. */
CBF_EXPORT int32_t codebrix_flac_encoder_read_header(
    codebrix_flac_encoder* encoder,
    uint8_t* destination,
    int32_t capacity);

/* ---- Output: patches (what finish rewrote) ----------------------------------------------- */

/* Number of patches recorded (0 before finish). Each patch is a contiguous range of the output
 * stream, by absolute byte offset from the start of the stream, that libFLAC rewrote. In stream
 * mode a caller whose destination can seek applies them to make the file self-describing; a caller
 * that cannot seek leaves the placeholders (still a valid FLAC stream, with unknown total samples). */
CBF_EXPORT int32_t codebrix_flac_encoder_patch_count(codebrix_flac_encoder* encoder);

/* Offset and length of patch `index` (0-based). Returns CBF_OK or CBF_E_INVALID_ARGUMENT. */
CBF_EXPORT int32_t codebrix_flac_encoder_patch_info(
    codebrix_flac_encoder* encoder,
    int32_t index,
    int64_t* out_offset,
    int32_t* out_length);

/* Copies patch `index`'s bytes into `destination` (capacity must be >= its length). Returns bytes
 * copied or a CBF_E_* code. Patches are not consumed; they can be read again. */
CBF_EXPORT int32_t codebrix_flac_encoder_read_patch(
    codebrix_flac_encoder* encoder,
    int32_t index,
    uint8_t* destination,
    int32_t capacity);

/* ---- Output: packet mode ----------------------------------------------------------------- */

/* Number of encoded frames waiting in the packet queue (packet mode only; 0 otherwise). */
CBF_EXPORT int32_t codebrix_flac_encoder_pending_packets(codebrix_flac_encoder* encoder);

/* Describes the OLDEST pending packet without removing it: its length in bytes, the index of its
 * first sample (frame number * block size, i.e. the container timestamp in samples) and its
 * sample count. Returns CBF_OK, or CBF_E_INVALID_STATE when no packet is pending. */
CBF_EXPORT int32_t codebrix_flac_encoder_packet_info(
    codebrix_flac_encoder* encoder,
    int32_t* out_length,
    int64_t* out_first_sample,
    int32_t* out_sample_count);

/* Copies the oldest pending packet into `destination` (capacity must be >= its length) and removes
 * it from the queue. Returns bytes copied, or a CBF_E_* code (CBF_E_INVALID_STATE when none). */
CBF_EXPORT int32_t codebrix_flac_encoder_read_packet(
    codebrix_flac_encoder* encoder,
    uint8_t* destination,
    int32_t capacity);

/* ---- Progress / diagnostics -------------------------------------------------------------- */

/* Total input frames accepted so far (progress reporting). Negative only for a null handle. */
CBF_EXPORT int64_t codebrix_flac_encoder_samples_written(codebrix_flac_encoder* encoder);

/* libFLAC's textual state after the last failing call ("FLAC__STREAM_ENCODER_VERIFY_MISMATCH_IN_AUDIO_DATA",
 * "FLAC__STREAM_ENCODER_INIT_STATUS_INVALID_BLOCK_SIZE", ...), or "" when the last call succeeded.
 * Static per handle; valid until the next call on the handle. For exception messages. */
CBF_EXPORT const char* codebrix_flac_encoder_last_state(codebrix_flac_encoder* encoder);

/* Releases everything. NULL is ignored. The handle is invalid afterwards. */
CBF_EXPORT void codebrix_flac_encoder_destroy(codebrix_flac_encoder* encoder);

#ifdef __cplusplus
}
#endif

#endif /* CODEBRIX_FLAC_H */
