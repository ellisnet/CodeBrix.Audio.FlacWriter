/*
 * codebrix_flac.c - implementation of codebrix_flac.h over libFLAC's stream encoder.
 *
 * Part of CodeBrix.Audio.FlacWriter's flac-native-tools. libFLAC (flac-1.5.0, BSD-3, unmodified) is
 * linked in statically and compiled with hidden visibility; only the functions of codebrix_flac.h
 * are exported.
 *
 * How output works (the header's file comment, in implementation terms):
 *   - libFLAC is initialised with FLAC__stream_encoder_init_stream and THREE callbacks implemented
 *     here (write / seek / tell). They operate on a VIRTUAL stream: `pos` is libFLAC's current
 *     write position and `end` the length of everything ever appended. No byte is lost or moved.
 *   - A write AT the end appends: metadata writes (libFLAC passes samples == 0) go to the output
 *     buffer and, while the header is still open (during init_stream), to the header copy too.
 *     Audio-frame writes (samples > 0; libFLAC issues exactly one per frame) go to the output buffer
 *     in stream mode, or become one packet each in packet mode. The virtual position advances in
 *     BOTH modes, so offsets (patches, seek points) are those of header + all frames concatenated.
 *   - A write BEFORE the end (libFLAC's finish seeks back to rewrite STREAMINFO and the SEEKTABLE)
 *     is a PATCH: recorded in the patch list (a write that continues exactly where the previous
 *     patch stopped extends that patch) and applied to the header copy where it falls inside the
 *     header. The pending output buffer is deliberately NOT patched: the output is always the bytes
 *     in the order first written, and applying the patches is the caller's job (and idempotent).
 *   - The header closes when init_stream returns; its length never changes afterwards.
 *
 * Threading: none inside (libFLAC is built with ENABLE_MULTITHREADING=OFF). One handle, one thread
 * at a time, as the header states.
 */

#include "codebrix_flac.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "FLAC/format.h"
#include "FLAC/metadata.h"
#include "FLAC/stream_encoder.h"

/* ---- internal types ------------------------------------------------------------------------- */

enum cbf_phase { CBF_CREATED = 0, CBF_STARTED = 1, CBF_FINISHED = 2, CBF_FAILED = 3 };

typedef struct cbf_bytes {
    uint8_t* data;
    size_t   len;
    size_t   cap;
} cbf_bytes;

typedef struct cbf_patch {
    uint64_t  offset;
    cbf_bytes bytes;
} cbf_patch;

typedef struct cbf_packet {
    uint8_t* data;
    int32_t  length;
    int64_t  first_sample;
    int32_t  sample_count;
} cbf_packet;

struct codebrix_flac_encoder {
    FLAC__StreamEncoder* enc;

    /* create-time parameters */
    uint32_t channels;
    uint32_t sample_rate;
    uint32_t bits_per_sample;
    int32_t  compression_level;
    uint32_t block_size;
    int      verify;
    int      streamable_subset;
    uint64_t total_samples_estimate;

    /* before-start options */
    FLAC__StreamMetadata* vorbis_comment;
    int32_t  padding_bytes;
    uint64_t seek_spacing;
    int      packet_mode;

    /* metadata array handed to libFLAC (must outlive the encoder) */
    FLAC__StreamMetadata* seek_table;
    FLAC__StreamMetadata* padding;
    FLAC__StreamMetadata* metadata[3];

    int phase;

    /* virtual stream */
    uint64_t pos;
    uint64_t end;

    /* stream output: bytes [out_read, out.len) are pending */
    cbf_bytes out;
    size_t    out_read;

    /* header copy */
    cbf_bytes header;
    int       header_open;

    /* patches */
    cbf_patch* patches;
    int32_t    patch_count;
    int32_t    patch_cap;

    /* packet queue: [pk_head, pk_count) are pending */
    cbf_packet* packets;
    int32_t     pk_head;
    int32_t     pk_count;
    int32_t     pk_cap;
    int64_t     packet_samples;   /* samples carried by every packet produced so far */

    int64_t samples_accepted;

    /* last_state: either a libFLAC static string or the shim's own text */
    const char* last_state;
    char        own_state[192];
    int         io_failed;        /* set by a callback that could not allocate */
};

/* ---- small helpers -------------------------------------------------------------------------- */

static int bytes_reserve(cbf_bytes* b, size_t extra)
{
    size_t need = b->len + extra;
    size_t cap;
    uint8_t* p;
    if (need < b->len) {
        return 0;
    }
    if (need <= b->cap) {
        return 1;
    }
    cap = b->cap ? b->cap : 256;
    while (cap < need) {
        if (cap > SIZE_MAX / 2) {
            cap = need;
            break;
        }
        cap *= 2;
    }
    p = (uint8_t*)realloc(b->data, cap);
    if (!p) {
        return 0;
    }
    b->data = p;
    b->cap = cap;
    return 1;
}

static int bytes_append(cbf_bytes* b, const uint8_t* src, size_t n)
{
    if (n == 0) {
        return 1;
    }
    if (!bytes_reserve(b, n)) {
        return 0;
    }
    memcpy(b->data + b->len, src, n);
    b->len += n;
    return 1;
}

static void bytes_free(cbf_bytes* b)
{
    free(b->data);
    b->data = NULL;
    b->len = b->cap = 0;
}

static int32_t ok(codebrix_flac_encoder* e)
{
    e->last_state = "";
    return CBF_OK;
}

static int32_t fail(codebrix_flac_encoder* e, int32_t code, const char* text)
{
    size_t n = strlen(text);
    if (n >= sizeof(e->own_state)) {
        n = sizeof(e->own_state) - 1;
    }
    memcpy(e->own_state, text, n);
    e->own_state[n] = '\0';
    e->last_state = e->own_state;
    return code;
}

/* Maps libFLAC's current encoder state to a result code and records its string. */
static int32_t fail_from_flac_state(codebrix_flac_encoder* e)
{
    FLAC__StreamEncoderState st = FLAC__stream_encoder_get_state(e->enc);
    int32_t code;
    e->last_state = FLAC__StreamEncoderStateString[st];
    if (e->io_failed) {
        code = CBF_E_OUT_OF_MEMORY;
    } else if (st == FLAC__STREAM_ENCODER_VERIFY_MISMATCH_IN_AUDIO_DATA) {
        code = CBF_E_VERIFY_MISMATCH;
    } else if (st == FLAC__STREAM_ENCODER_MEMORY_ALLOCATION_ERROR) {
        code = CBF_E_OUT_OF_MEMORY;
    } else {
        code = CBF_E_FLAC_FAULT;
    }
    if (st == FLAC__STREAM_ENCODER_OK) {
        /* libFLAC reported failure without a state (should not happen); keep a useful text */
        fail(e, code, "libFLAC call failed with state FLAC__STREAM_ENCODER_OK");
    }
    return code;
}

/* Records (or extends) a patch and applies it to the header copy where it overlaps. */
static int record_patch(codebrix_flac_encoder* e, uint64_t offset, const uint8_t* src, size_t n)
{
    cbf_patch* last = e->patch_count > 0 ? &e->patches[e->patch_count - 1] : NULL;

    if (offset < (uint64_t)e->header.len) {
        size_t start = (size_t)offset;
        size_t m = n;
        if (m > e->header.len - start) {
            m = e->header.len - start;
        }
        memcpy(e->header.data + start, src, m);
    }

    if (last && last->offset + last->bytes.len == offset && last->bytes.len + n <= (size_t)INT32_MAX) {
        return bytes_append(&last->bytes, src, n);
    }
    if (e->patch_count == e->patch_cap) {
        int32_t cap = e->patch_cap ? e->patch_cap * 2 : 8;
        cbf_patch* p = (cbf_patch*)realloc(e->patches, (size_t)cap * sizeof(cbf_patch));
        if (!p) {
            return 0;
        }
        e->patches = p;
        e->patch_cap = cap;
    }
    memset(&e->patches[e->patch_count], 0, sizeof(cbf_patch));
    e->patches[e->patch_count].offset = offset;
    if (!bytes_append(&e->patches[e->patch_count].bytes, src, n)) {
        bytes_free(&e->patches[e->patch_count].bytes);
        return 0;
    }
    e->patch_count++;
    return 1;
}

static int enqueue_packet(codebrix_flac_encoder* e, const uint8_t* src, size_t n, uint32_t samples)
{
    cbf_packet* p;
    if (n > (size_t)INT32_MAX) {
        return 0;
    }
    if (e->pk_head > 0 && e->pk_head == e->pk_count) {
        e->pk_head = e->pk_count = 0;
    }
    if (e->pk_count == e->pk_cap) {
        if (e->pk_head > 0) {
            memmove(e->packets, e->packets + e->pk_head, (size_t)(e->pk_count - e->pk_head) * sizeof(cbf_packet));
            e->pk_count -= e->pk_head;
            e->pk_head = 0;
        }
        if (e->pk_count == e->pk_cap) {
            int32_t cap = e->pk_cap ? e->pk_cap * 2 : 16;
            cbf_packet* q = (cbf_packet*)realloc(e->packets, (size_t)cap * sizeof(cbf_packet));
            if (!q) {
                return 0;
            }
            e->packets = q;
            e->pk_cap = cap;
        }
    }
    p = &e->packets[e->pk_count];
    p->data = (uint8_t*)malloc(n ? n : 1);
    if (!p->data) {
        return 0;
    }
    memcpy(p->data, src, n);
    p->length = (int32_t)n;
    p->first_sample = e->packet_samples;
    p->sample_count = (int32_t)samples;
    e->packet_samples += samples;
    e->pk_count++;
    return 1;
}

/* ---- libFLAC callbacks ---------------------------------------------------------------------- */

static FLAC__StreamEncoderWriteStatus cb_write(const FLAC__StreamEncoder* encoder, const FLAC__byte buffer[],
                                               size_t bytes, uint32_t samples, uint32_t current_frame,
                                               void* client_data)
{
    codebrix_flac_encoder* e = (codebrix_flac_encoder*)client_data;
    const uint8_t* src = (const uint8_t*)buffer;
    (void)encoder;
    (void)current_frame;

    /* the part that rewrites already-written bytes is a patch */
    if (e->pos < e->end && bytes > 0) {
        uint64_t room = e->end - e->pos;
        size_t n = bytes < room ? bytes : (size_t)room;
        if (!record_patch(e, e->pos, src, n)) {
            e->io_failed = 1;
            return FLAC__STREAM_ENCODER_WRITE_STATUS_FATAL_ERROR;
        }
        e->pos += n;
        src += n;
        bytes -= n;
    }
    if (bytes == 0) {
        return FLAC__STREAM_ENCODER_WRITE_STATUS_OK;
    }

    /* the rest extends the stream */
    if (samples == 0) {
        if (e->header_open && !bytes_append(&e->header, src, bytes)) {
            e->io_failed = 1;
            return FLAC__STREAM_ENCODER_WRITE_STATUS_FATAL_ERROR;
        }
        if (!bytes_append(&e->out, src, bytes)) {
            e->io_failed = 1;
            return FLAC__STREAM_ENCODER_WRITE_STATUS_FATAL_ERROR;
        }
    } else if (e->packet_mode) {
        if (!enqueue_packet(e, src, bytes, samples)) {
            e->io_failed = 1;
            return FLAC__STREAM_ENCODER_WRITE_STATUS_FATAL_ERROR;
        }
    } else {
        if (!bytes_append(&e->out, src, bytes)) {
            e->io_failed = 1;
            return FLAC__STREAM_ENCODER_WRITE_STATUS_FATAL_ERROR;
        }
    }
    e->pos += bytes;
    e->end += bytes;
    return FLAC__STREAM_ENCODER_WRITE_STATUS_OK;
}

static FLAC__StreamEncoderSeekStatus cb_seek(const FLAC__StreamEncoder* encoder, FLAC__uint64 absolute_byte_offset,
                                             void* client_data)
{
    codebrix_flac_encoder* e = (codebrix_flac_encoder*)client_data;
    (void)encoder;
    if (absolute_byte_offset > e->end) {
        return FLAC__STREAM_ENCODER_SEEK_STATUS_ERROR;
    }
    e->pos = absolute_byte_offset;
    return FLAC__STREAM_ENCODER_SEEK_STATUS_OK;
}

static FLAC__StreamEncoderTellStatus cb_tell(const FLAC__StreamEncoder* encoder, FLAC__uint64* absolute_byte_offset,
                                             void* client_data)
{
    codebrix_flac_encoder* e = (codebrix_flac_encoder*)client_data;
    (void)encoder;
    *absolute_byte_offset = e->pos;
    return FLAC__STREAM_ENCODER_TELL_STATUS_OK;
}

/* ---- versions ------------------------------------------------------------------------------- */

CBF_EXPORT int32_t codebrix_flac_api_version(void)
{
    return CODEBRIX_FLAC_API_VERSION;
}

CBF_EXPORT const char* codebrix_flac_library_version(void)
{
    return FLAC__VERSION_STRING;
}

/* ---- lifecycle ------------------------------------------------------------------------------ */

CBF_EXPORT int32_t codebrix_flac_encoder_create(
    int32_t channels,
    int32_t sample_rate_hz,
    int32_t bits_per_sample,
    int32_t compression_level,
    int32_t block_size,
    int32_t verify,
    int32_t streamable_subset,
    int64_t total_samples_estimate,
    codebrix_flac_encoder** out_encoder)
{
    codebrix_flac_encoder* e;

    if (!out_encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    *out_encoder = NULL;
    if (channels < 1 || channels > 8 ||
        sample_rate_hz < 1 || sample_rate_hz > 655350 ||
        bits_per_sample < 4 || bits_per_sample > 32 ||
        compression_level < -1 || compression_level > 8 ||
        (block_size != 0 && (block_size < 16 || block_size > 65535)) ||
        (verify != 0 && verify != 1) ||
        (streamable_subset != 0 && streamable_subset != 1) ||
        total_samples_estimate < 0 || total_samples_estimate > (int64_t)((UINT64_C(1) << 36) - 1)) {
        return CBF_E_INVALID_ARGUMENT;
    }

    e = (codebrix_flac_encoder*)calloc(1, sizeof(*e));
    if (!e) {
        return CBF_E_OUT_OF_MEMORY;
    }
    e->enc = FLAC__stream_encoder_new();
    e->vorbis_comment = FLAC__metadata_object_new(FLAC__METADATA_TYPE_VORBIS_COMMENT);
    if (!e->enc || !e->vorbis_comment) {
        codebrix_flac_encoder_destroy(e);
        return CBF_E_OUT_OF_MEMORY;
    }
    e->channels = (uint32_t)channels;
    e->sample_rate = (uint32_t)sample_rate_hz;
    e->bits_per_sample = (uint32_t)bits_per_sample;
    e->compression_level = compression_level < 0 ? 5 : compression_level;
    e->block_size = (uint32_t)block_size;
    e->verify = verify;
    e->streamable_subset = streamable_subset;
    e->total_samples_estimate = (uint64_t)total_samples_estimate;
    e->phase = CBF_CREATED;
    e->header_open = 0;
    e->last_state = "";
    *out_encoder = e;
    return CBF_OK;
}

CBF_EXPORT int32_t codebrix_flac_encoder_add_comment(
    codebrix_flac_encoder* encoder,
    const char* tag_utf8,
    const char* value_utf8)
{
    FLAC__StreamMetadata_VorbisComment_Entry entry;
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (encoder->phase != CBF_CREATED) {
        return fail(encoder, CBF_E_INVALID_STATE, "add_comment is only valid before start");
    }
    if (!tag_utf8 || !value_utf8 || tag_utf8[0] == '\0') {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "comment tag and value must be non-null and the tag non-empty");
    }
    if (!FLAC__format_vorbiscomment_entry_name_is_legal(tag_utf8)) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "comment tag is not a legal Vorbis comment field name (printable ASCII 0x20-0x7D except '=')");
    }
    if (!FLAC__format_vorbiscomment_entry_value_is_legal((const FLAC__byte*)value_utf8, (uint32_t)-1)) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "comment value is not valid UTF-8");
    }
    memset(&entry, 0, sizeof(entry));
    if (!FLAC__metadata_object_vorbiscomment_entry_from_name_value_pair(&entry, tag_utf8, value_utf8)) {
        return fail(encoder, CBF_E_OUT_OF_MEMORY, "could not build the Vorbis comment entry");
    }
    if (!FLAC__metadata_object_vorbiscomment_append_comment(encoder->vorbis_comment, entry, /*copy=*/false)) {
        free(entry.entry);
        return fail(encoder, CBF_E_OUT_OF_MEMORY, "could not append the Vorbis comment entry");
    }
    return ok(encoder);
}

CBF_EXPORT int32_t codebrix_flac_encoder_set_padding(
    codebrix_flac_encoder* encoder,
    int32_t padding_bytes)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (encoder->phase != CBF_CREATED) {
        return fail(encoder, CBF_E_INVALID_STATE, "set_padding is only valid before start");
    }
    if (padding_bytes < 0 || padding_bytes > (int32_t)((1u << FLAC__STREAM_METADATA_LENGTH_LEN) - 1)) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "padding_bytes must be 0..16777215");
    }
    encoder->padding_bytes = padding_bytes;
    return ok(encoder);
}

CBF_EXPORT int32_t codebrix_flac_encoder_set_seek_table(
    codebrix_flac_encoder* encoder,
    int64_t samples_between_points)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (encoder->phase != CBF_CREATED) {
        return fail(encoder, CBF_E_INVALID_STATE, "set_seek_table is only valid before start");
    }
    if (samples_between_points < 0 || samples_between_points > (int64_t)UINT32_MAX) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "samples_between_points must be 0..4294967295");
    }
    if (samples_between_points > 0 && encoder->total_samples_estimate == 0) {
        return fail(encoder, CBF_E_INVALID_STATE, "a seek table needs total_samples_estimate > 0 at create");
    }
    encoder->seek_spacing = (uint64_t)samples_between_points;
    return ok(encoder);
}

CBF_EXPORT int32_t codebrix_flac_encoder_set_packet_mode(
    codebrix_flac_encoder* encoder,
    int32_t enabled)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (encoder->phase != CBF_CREATED) {
        return fail(encoder, CBF_E_INVALID_STATE, "set_packet_mode is only valid before start");
    }
    if (enabled != 0 && enabled != 1) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "enabled must be 0 or 1");
    }
    encoder->packet_mode = enabled;
    return ok(encoder);
}

CBF_EXPORT int32_t codebrix_flac_encoder_start(codebrix_flac_encoder* encoder)
{
    codebrix_flac_encoder* e = encoder;
    FLAC__StreamEncoderInitStatus st;
    uint32_t n = 0;
    FLAC__bool set_ok = true;

    if (!e) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (e->phase != CBF_CREATED) {
        return fail(e, CBF_E_INVALID_STATE, "start may be called exactly once, before any write");
    }

    /* metadata, in file order after STREAMINFO: SEEKTABLE, VORBIS_COMMENT, PADDING */
    if (e->seek_spacing > 0) {
        e->seek_table = FLAC__metadata_object_new(FLAC__METADATA_TYPE_SEEKTABLE);
        if (!e->seek_table ||
            !FLAC__metadata_object_seektable_template_append_spaced_points_by_samples(
                e->seek_table, (uint32_t)e->seek_spacing, e->total_samples_estimate) ||
            !FLAC__metadata_object_seektable_template_sort(e->seek_table, /*compact=*/true)) {
            e->phase = CBF_FAILED;
            return fail(e, CBF_E_OUT_OF_MEMORY, "could not build the seek table template");
        }
        e->metadata[n++] = e->seek_table;
    }
    e->metadata[n++] = e->vorbis_comment;
    if (e->padding_bytes > 0) {
        e->padding = FLAC__metadata_object_new(FLAC__METADATA_TYPE_PADDING);
        if (!e->padding) {
            e->phase = CBF_FAILED;
            return fail(e, CBF_E_OUT_OF_MEMORY, "could not allocate the PADDING block");
        }
        e->padding->length = (uint32_t)e->padding_bytes;
        e->metadata[n++] = e->padding;
    }

    set_ok &= FLAC__stream_encoder_set_channels(e->enc, e->channels);
    set_ok &= FLAC__stream_encoder_set_bits_per_sample(e->enc, e->bits_per_sample);
    set_ok &= FLAC__stream_encoder_set_sample_rate(e->enc, e->sample_rate);
    set_ok &= FLAC__stream_encoder_set_compression_level(e->enc, (uint32_t)e->compression_level);
    if (e->block_size > 0) {
        /* after the level: the level sets a block size of its own */
        set_ok &= FLAC__stream_encoder_set_blocksize(e->enc, e->block_size);
    }
    set_ok &= FLAC__stream_encoder_set_verify(e->enc, e->verify ? true : false);
    set_ok &= FLAC__stream_encoder_set_streamable_subset(e->enc, e->streamable_subset ? true : false);
    /* MD5 of the input is ON: libFLAC's default (set_defaults_: do_md5 = true). Its only switch,
     * FLAC__stream_encoder_set_do_md5, is undeclared in the public headers (an unsupported test
     * hook), so it is deliberately not called. */
    set_ok &= FLAC__stream_encoder_set_total_samples_estimate(e->enc, e->total_samples_estimate);
    set_ok &= FLAC__stream_encoder_set_metadata(e->enc, e->metadata, n);
    if (!set_ok) {
        e->phase = CBF_FAILED;
        return fail_from_flac_state(e);
    }

    e->header_open = 1;
    st = FLAC__stream_encoder_init_stream(e->enc, cb_write, cb_seek, cb_tell, NULL, e);
    e->header_open = 0;
    if (st != FLAC__STREAM_ENCODER_INIT_STATUS_OK) {
        e->phase = CBF_FAILED;
        if (st == FLAC__STREAM_ENCODER_INIT_STATUS_ENCODER_ERROR) {
            return fail_from_flac_state(e);
        }
        e->last_state = FLAC__StreamEncoderInitStatusString[st];
        return CBF_E_UNSUPPORTED;
    }
    e->phase = CBF_STARTED;
    return ok(e);
}

CBF_EXPORT int32_t codebrix_flac_encoder_write_int32(
    codebrix_flac_encoder* encoder,
    const int32_t* interleaved_samples,
    int32_t frame_count)
{
    codebrix_flac_encoder* e = encoder;
    int64_t i, total;
    int32_t smax, smin;

    if (!e) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (e->phase != CBF_STARTED) {
        return fail(e, CBF_E_INVALID_STATE, e->phase == CBF_CREATED ? "write before start"
                                          : e->phase == CBF_FINISHED ? "write after finish"
                                          : "the encoder failed earlier and cannot accept more input");
    }
    if (frame_count < 0 || (frame_count > 0 && !interleaved_samples)) {
        return fail(e, CBF_E_INVALID_ARGUMENT, "frame_count must be >= 0 and the sample pointer non-null");
    }
    if (frame_count == 0) {
        return ok(e);
    }

    /* libFLAC would turn an out-of-range sample into a fatal CLIENT_ERROR; reject it up front so the
     * encoder stays usable and the caller learns why */
    smax = (int32_t)(INT32_MAX >> (32 - e->bits_per_sample));
    smin = (int32_t)(INT32_MIN >> (32 - e->bits_per_sample));
    total = (int64_t)frame_count * (int64_t)e->channels;
    for (i = 0; i < total; i++) {
        int32_t v = interleaved_samples[i];
        if (v < smin || v > smax) {
            return fail(e, CBF_E_INVALID_ARGUMENT, "a sample is outside the range of bits_per_sample");
        }
    }

    if (!FLAC__stream_encoder_process_interleaved(e->enc, (const FLAC__int32*)interleaved_samples, (uint32_t)frame_count)) {
        e->phase = CBF_FAILED;
        return fail_from_flac_state(e);
    }
    e->samples_accepted += frame_count;
    return ok(e);
}

CBF_EXPORT int32_t codebrix_flac_encoder_finish(codebrix_flac_encoder* encoder)
{
    codebrix_flac_encoder* e = encoder;
    if (!e) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (e->phase != CBF_STARTED) {
        return fail(e, CBF_E_INVALID_STATE, e->phase == CBF_CREATED ? "finish before start"
                                          : e->phase == CBF_FINISHED ? "finish was already called"
                                          : "the encoder failed earlier");
    }
    if (!FLAC__stream_encoder_finish(e->enc)) {
        /* libFLAC keeps the failing state after an unsuccessful finish */
        e->phase = CBF_FAILED;
        return fail_from_flac_state(e);
    }
    e->phase = CBF_FINISHED;
    return ok(e);
}

/* ---- output: stream ------------------------------------------------------------------------- */

CBF_EXPORT int64_t codebrix_flac_encoder_pending_output(codebrix_flac_encoder* encoder)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    return (int64_t)(encoder->out.len - encoder->out_read);
}

CBF_EXPORT int32_t codebrix_flac_encoder_read_output(
    codebrix_flac_encoder* encoder,
    uint8_t* destination,
    int32_t capacity)
{
    codebrix_flac_encoder* e = encoder;
    size_t pending, n;
    if (!e) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (capacity < 0 || (capacity > 0 && !destination)) {
        return fail(e, CBF_E_INVALID_ARGUMENT, "capacity must be >= 0 and the destination non-null");
    }
    pending = e->out.len - e->out_read;
    n = pending < (size_t)capacity ? pending : (size_t)capacity;
    if (n > 0) {
        memcpy(destination, e->out.data + e->out_read, n);
        e->out_read += n;
    }
    if (e->out_read == e->out.len) {
        e->out_read = 0;
        e->out.len = 0;
    } else if (e->out_read > 65536 && e->out_read > e->out.len / 2) {
        memmove(e->out.data, e->out.data + e->out_read, e->out.len - e->out_read);
        e->out.len -= e->out_read;
        e->out_read = 0;
    }
    e->last_state = "";
    return (int32_t)n;
}

/* ---- output: header ------------------------------------------------------------------------- */

CBF_EXPORT int32_t codebrix_flac_encoder_header_length(codebrix_flac_encoder* encoder)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (encoder->phase == CBF_CREATED || encoder->header.len == 0) {
        return fail(encoder, CBF_E_INVALID_STATE, "the header exists only after a successful start");
    }
    return (int32_t)encoder->header.len;
}

CBF_EXPORT int32_t codebrix_flac_encoder_read_header(
    codebrix_flac_encoder* encoder,
    uint8_t* destination,
    int32_t capacity)
{
    codebrix_flac_encoder* e = encoder;
    if (!e) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (e->phase == CBF_CREATED || e->header.len == 0) {
        return fail(e, CBF_E_INVALID_STATE, "the header exists only after a successful start");
    }
    if (!destination || capacity < 0 || (size_t)capacity < e->header.len) {
        return fail(e, CBF_E_INVALID_ARGUMENT, "destination must be non-null with capacity >= header_length");
    }
    memcpy(destination, e->header.data, e->header.len);
    e->last_state = "";
    return (int32_t)e->header.len;
}

/* ---- output: patches ------------------------------------------------------------------------ */

CBF_EXPORT int32_t codebrix_flac_encoder_patch_count(codebrix_flac_encoder* encoder)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    return encoder->patch_count;
}

CBF_EXPORT int32_t codebrix_flac_encoder_patch_info(
    codebrix_flac_encoder* encoder,
    int32_t index,
    int64_t* out_offset,
    int32_t* out_length)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (index < 0 || index >= encoder->patch_count || !out_offset || !out_length) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "patch index out of range, or a null output pointer");
    }
    *out_offset = (int64_t)encoder->patches[index].offset;
    *out_length = (int32_t)encoder->patches[index].bytes.len;
    return ok(encoder);
}

CBF_EXPORT int32_t codebrix_flac_encoder_read_patch(
    codebrix_flac_encoder* encoder,
    int32_t index,
    uint8_t* destination,
    int32_t capacity)
{
    const cbf_patch* p;
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (index < 0 || index >= encoder->patch_count) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "patch index out of range");
    }
    p = &encoder->patches[index];
    if (!destination || capacity < 0 || (size_t)capacity < p->bytes.len) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "destination must be non-null with capacity >= the patch length");
    }
    memcpy(destination, p->bytes.data, p->bytes.len);
    encoder->last_state = "";
    return (int32_t)p->bytes.len;
}

/* ---- output: packets ------------------------------------------------------------------------ */

CBF_EXPORT int32_t codebrix_flac_encoder_pending_packets(codebrix_flac_encoder* encoder)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    return encoder->pk_count - encoder->pk_head;
}

CBF_EXPORT int32_t codebrix_flac_encoder_packet_info(
    codebrix_flac_encoder* encoder,
    int32_t* out_length,
    int64_t* out_first_sample,
    int32_t* out_sample_count)
{
    const cbf_packet* p;
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (!out_length || !out_first_sample || !out_sample_count) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "null output pointer");
    }
    if (encoder->pk_head == encoder->pk_count) {
        return fail(encoder, CBF_E_INVALID_STATE, "no packet is pending");
    }
    p = &encoder->packets[encoder->pk_head];
    *out_length = p->length;
    *out_first_sample = p->first_sample;
    *out_sample_count = p->sample_count;
    return ok(encoder);
}

CBF_EXPORT int32_t codebrix_flac_encoder_read_packet(
    codebrix_flac_encoder* encoder,
    uint8_t* destination,
    int32_t capacity)
{
    cbf_packet* p;
    int32_t n;
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    if (encoder->pk_head == encoder->pk_count) {
        return fail(encoder, CBF_E_INVALID_STATE, "no packet is pending");
    }
    p = &encoder->packets[encoder->pk_head];
    if (!destination || capacity < p->length) {
        return fail(encoder, CBF_E_INVALID_ARGUMENT, "destination must be non-null with capacity >= the packet length");
    }
    memcpy(destination, p->data, (size_t)p->length);
    n = p->length;
    free(p->data);
    p->data = NULL;
    encoder->pk_head++;
    if (encoder->pk_head == encoder->pk_count) {
        encoder->pk_head = encoder->pk_count = 0;
    }
    encoder->last_state = "";
    return n;
}

/* ---- diagnostics ---------------------------------------------------------------------------- */

CBF_EXPORT int64_t codebrix_flac_encoder_samples_written(codebrix_flac_encoder* encoder)
{
    if (!encoder) {
        return CBF_E_INVALID_ARGUMENT;
    }
    return encoder->samples_accepted;
}

CBF_EXPORT const char* codebrix_flac_encoder_last_state(codebrix_flac_encoder* encoder)
{
    if (!encoder || !encoder->last_state) {
        return "";
    }
    return encoder->last_state;
}

CBF_EXPORT void codebrix_flac_encoder_destroy(codebrix_flac_encoder* encoder)
{
    int32_t i;
    if (!encoder) {
        return;
    }
    if (encoder->enc) {
        /* delete finishes an unfinished stream itself; its writes land in buffers freed below */
        FLAC__stream_encoder_delete(encoder->enc);
    }
    if (encoder->vorbis_comment) {
        FLAC__metadata_object_delete(encoder->vorbis_comment);
    }
    if (encoder->seek_table) {
        FLAC__metadata_object_delete(encoder->seek_table);
    }
    if (encoder->padding) {
        FLAC__metadata_object_delete(encoder->padding);
    }
    bytes_free(&encoder->out);
    bytes_free(&encoder->header);
    for (i = 0; i < encoder->patch_count; i++) {
        bytes_free(&encoder->patches[i].bytes);
    }
    free(encoder->patches);
    for (i = encoder->pk_head; i < encoder->pk_count; i++) {
        free(encoder->packets[i].data);
    }
    free(encoder->packets);
    free(encoder);
}
