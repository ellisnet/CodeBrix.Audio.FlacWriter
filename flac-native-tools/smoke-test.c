/*
 * smoke-test.c - the load-and-run verification program for the codebrix_flac native library
 *
 * Part of CodeBrix.Audio.FlacWriter's flac-native-tools. Every platform's gate builds it through
 * shim/CMakeLists.txt (-DCBF_BUILD_SMOKE_TEST=ON -> target codebrix_flac_smoke) and runs it against
 * the STAGED (stripped, about-to-be-shipped) library, so the bytes that are tested are the bytes
 * that ship:
 *
 *     ./codebrix_flac_smoke <path-to-library> [output.flac]
 *
 * It loads the library the way .NET's DllImport does (dlopen / LoadLibrary, by path) and resolves
 * every entry point of codebrix_flac.h by name. The smoke test ALSO links its own static libFLAC,
 * but uses it ONLY as an independent DECODER to verify what the library under test produced
 * (libFLAC's symbols inside codebrix_flac are hidden, so the two copies never meet).
 *
 *   1. codebrix_flac_api_version() must be 1; codebrix_flac_library_version() must be non-empty,
 *   2. STREAM mode, stereo 44100 Hz 16-bit, level 5, two comments (one non-ASCII): 1 s of a 440 Hz
 *      sine written in uneven chunks, output drained as it comes. Checks the "fLaC" magic, that the
 *      header read back before finish carries PLACEHOLDER totals (0 samples, zero MD5), that finish
 *      records patches that all fall inside the header, that applying them gives STREAMINFO total
 *      samples = 44100 + a non-zero MD5, that read_header after finish equals the patched header,
 *      and that libFLAC's own decoder decodes BOTH the patched and the unpatched stream
 *      sample-exactly (MD5 checked on the patched one) and finds the comments + vendor string,
 *   3. PACKET mode, verify=1, seek table + padding: header_length > 0, only the header reaches the
 *      output, N packets whose sample counts sum to 44100 with first_sample == running sum,
 *      header patched after finish, header + packets decode sample-exactly,
 *   4. the same options in STREAM mode: the patched stream == header + packets, byte for byte,
 *   5. 24-bit mono 48000 Hz at levels 0 and 8 (verify=1): decode sample-exact,
 *   6. an empty stream (start + finish): valid, 0 samples,
 *   7. the contract's error paths: null handles, bad create arguments, every set_* / add_comment /
 *      start after start, seek table without an estimate, illegal comment tag / non-UTF-8 value,
 *      write/finish before start and after finish, an out-of-range sample (rejected, encoder still
 *      usable), no pending packet, bad patch index, short header buffer, and a non-subset block
 *      size refused at start with CBF_E_UNSUPPORTED + libFLAC's init-status string,
 *   8. optionally writes the patched stream-mode bytes to output.flac (for ffprobe / listening).
 * Exit code 0 means every step passed; anything else names the step that failed.
 *
 * Function types are declared by hand from codebrix_flac.h (int32_t / int64_t / pointers only,
 * default C calling convention on every platform).
 */

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "FLAC/stream_decoder.h"

#if defined(_WIN32)
#  include <windows.h>
typedef HMODULE lib_handle;
static lib_handle lib_open(const char* path) { return LoadLibraryA(path); }
static void* lib_sym(lib_handle h, const char* name) { return (void*)GetProcAddress(h, name); }
#else
#  include <dlfcn.h>
typedef void* lib_handle;
static lib_handle lib_open(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static void* lib_sym(lib_handle h, const char* name) { return dlsym(h, name); }
#endif

#ifndef M_PI
#  define M_PI 3.14159265358979323846
#endif

/* result codes (codebrix_flac.h) */
#define CBF_OK                   0
#define CBF_E_INVALID_ARGUMENT  -1
#define CBF_E_INVALID_STATE     -3
#define CBF_E_UNSUPPORTED       -4

typedef struct enc enc;
typedef int32_t     (*fn_api_version)(void);
typedef const char* (*fn_library_version)(void);
typedef int32_t     (*fn_create)(int32_t, int32_t, int32_t, int32_t, int32_t, int32_t, int32_t, int64_t, enc**);
typedef int32_t     (*fn_add_comment)(enc*, const char*, const char*);
typedef int32_t     (*fn_set_i32)(enc*, int32_t);
typedef int32_t     (*fn_set_i64)(enc*, int64_t);
typedef int32_t     (*fn_op)(enc*);
typedef int32_t     (*fn_write_int32)(enc*, const int32_t*, int32_t);
typedef int64_t     (*fn_i64)(enc*);
typedef int32_t     (*fn_read)(enc*, uint8_t*, int32_t);
typedef int32_t     (*fn_patch_info)(enc*, int32_t, int64_t*, int32_t*);
typedef int32_t     (*fn_read_patch)(enc*, int32_t, uint8_t*, int32_t);
typedef int32_t     (*fn_packet_info)(enc*, int32_t*, int64_t*, int32_t*);
typedef const char* (*fn_last_state)(enc*);
typedef void        (*fn_destroy)(enc*);

static fn_api_version     p_api_version;
static fn_library_version p_library_version;
static fn_create          p_create;
static fn_add_comment     p_add_comment;
static fn_set_i32         p_set_padding;
static fn_set_i64         p_set_seek_table;
static fn_set_i32         p_set_packet_mode;
static fn_op              p_start;
static fn_write_int32     p_write_int32;
static fn_op              p_finish;
static fn_i64             p_pending_output;
static fn_read            p_read_output;
static fn_op              p_header_length;
static fn_read            p_read_header;
static fn_op              p_patch_count;
static fn_patch_info      p_patch_info;
static fn_read_patch      p_read_patch;
static fn_op              p_pending_packets;
static fn_packet_info     p_packet_info;
static fn_read            p_read_packet;
static fn_i64             p_samples_written;
static fn_last_state      p_last_state;
static fn_destroy         p_destroy;

static int failures = 0;

#define CHECK(cond, what) do { if (!(cond)) { fprintf(stderr, "FAIL: %s\n", what); failures++; } \
                               else { printf("ok:   %s\n", what); } } while (0)

/* ---- growable byte buffer ------------------------------------------------------------------- */

typedef struct buf { uint8_t* p; size_t n, cap; } buf;

static void buf_add(buf* b, const uint8_t* src, size_t n)
{
    if (b->n + n > b->cap) {
        size_t cap = b->cap ? b->cap : 4096;
        while (cap < b->n + n) cap *= 2;
        b->p = (uint8_t*)realloc(b->p, cap);
        if (!b->p) { fprintf(stderr, "out of memory\n"); exit(9); }
        b->cap = cap;
    }
    if (n) memcpy(b->p + b->n, src, n);
    b->n += n;
}

static void drain_output(enc* e, buf* b)
{
    uint8_t chunk[1000];   /* small on purpose: exercises partial reads */
    int32_t n;
    while ((n = p_read_output(e, chunk, (int32_t)sizeof chunk)) > 0) buf_add(b, chunk, (size_t)n);
    if (n < 0) { fprintf(stderr, "FAIL: read_output returned %d\n", n); failures++; }
}

/* ---- STREAMINFO parsing (at offset 0 of a FLAC stream / header) ------------------------------ */

typedef struct streaminfo {
    int ok;
    uint32_t rate, channels, bps;
    uint64_t total;
    uint8_t md5[16];
} streaminfo;

static streaminfo parse_streaminfo(const uint8_t* d, size_t n)
{
    streaminfo si;
    const uint8_t* s;
    memset(&si, 0, sizeof si);
    if (n < 42 || memcmp(d, "fLaC", 4) != 0) return si;
    if ((d[4] & 0x7f) != 0 || d[5] != 0 || d[6] != 0 || d[7] != 34) return si;  /* type 0, length 34 */
    s = d + 8;
    si.rate = ((uint32_t)s[10] << 12) | ((uint32_t)s[11] << 4) | ((uint32_t)s[12] >> 4);
    si.channels = ((s[12] >> 1) & 7) + 1;
    si.bps = (((uint32_t)(s[12] & 1) << 4) | ((uint32_t)s[13] >> 4)) + 1;
    si.total = ((uint64_t)(s[13] & 0x0f) << 32) | ((uint64_t)s[14] << 24) | ((uint64_t)s[15] << 16) |
               ((uint64_t)s[16] << 8) | (uint64_t)s[17];
    memcpy(si.md5, s + 18, 16);
    si.ok = 1;
    return si;
}

static int md5_is_zero(const uint8_t* m) { int i; for (i = 0; i < 16; i++) if (m[i]) return 0; return 1; }

/* ---- verifying decoder (libFLAC's own FLAC__StreamDecoder, statically linked HERE only) ------ */

typedef struct dec_ctx {
    const uint8_t* data; size_t len, pos;
    int32_t* pcm; uint64_t frames, cap;
    uint32_t channels, bps, rate;
    int found_comment; const char* want_comment;
    char vendor[128];
    int error;
} dec_ctx;

static FLAC__StreamDecoderReadStatus d_read(const FLAC__StreamDecoder* d, FLAC__byte buffer[], size_t* bytes, void* cd)
{
    dec_ctx* c = (dec_ctx*)cd; size_t n = c->len - c->pos; (void)d;
    if (n == 0) { *bytes = 0; return FLAC__STREAM_DECODER_READ_STATUS_END_OF_STREAM; }
    if (n > *bytes) n = *bytes;
    memcpy(buffer, c->data + c->pos, n); c->pos += n; *bytes = n;
    return FLAC__STREAM_DECODER_READ_STATUS_CONTINUE;
}

static FLAC__StreamDecoderWriteStatus d_write(const FLAC__StreamDecoder* d, const FLAC__Frame* f, const FLAC__int32* const b[], void* cd)
{
    dec_ctx* c = (dec_ctx*)cd; uint32_t i, ch; (void)d;
    c->channels = f->header.channels; c->bps = f->header.bits_per_sample; c->rate = f->header.sample_rate;
    if ((c->frames + f->header.blocksize) * c->channels > c->cap) {
        c->cap = (c->frames + f->header.blocksize) * c->channels * 2;
        c->pcm = (int32_t*)realloc(c->pcm, (size_t)c->cap * sizeof(int32_t));
        if (!c->pcm) return FLAC__STREAM_DECODER_WRITE_STATUS_ABORT;
    }
    for (i = 0; i < f->header.blocksize; i++)
        for (ch = 0; ch < c->channels; ch++)
            c->pcm[(c->frames + i) * c->channels + ch] = b[ch][i];
    c->frames += f->header.blocksize;
    return FLAC__STREAM_DECODER_WRITE_STATUS_CONTINUE;
}

static void d_meta(const FLAC__StreamDecoder* d, const FLAC__StreamMetadata* m, void* cd)
{
    dec_ctx* c = (dec_ctx*)cd; uint32_t i; (void)d;
    if (m->type == FLAC__METADATA_TYPE_VORBIS_COMMENT) {
        size_t vl = m->data.vorbis_comment.vendor_string.length;
        if (vl >= sizeof c->vendor) vl = sizeof c->vendor - 1;
        memcpy(c->vendor, m->data.vorbis_comment.vendor_string.entry, vl); c->vendor[vl] = '\0';
        for (i = 0; i < m->data.vorbis_comment.num_comments; i++) {
            const FLAC__StreamMetadata_VorbisComment_Entry* en = &m->data.vorbis_comment.comments[i];
            if (c->want_comment && en->length == strlen(c->want_comment) &&
                memcmp(en->entry, c->want_comment, en->length) == 0) c->found_comment = 1;
        }
    }
}

static void d_error(const FLAC__StreamDecoder* d, FLAC__StreamDecoderErrorStatus s, void* cd)
{
    (void)d; (void)s; ((dec_ctx*)cd)->error = 1;
}

/* Decodes `data`; returns 1 when libFLAC decoded it cleanly (and, when md5 is set, the MD5 matched). */
static int decode(const uint8_t* data, size_t len, dec_ctx* c, int md5, const char* want_comment)
{
    FLAC__StreamDecoder* d = FLAC__stream_decoder_new();
    int good;
    memset(c, 0, sizeof *c);
    c->data = data; c->len = len; c->want_comment = want_comment;
    if (!d) return 0;
    FLAC__stream_decoder_set_md5_checking(d, md5 ? true : false);
    FLAC__stream_decoder_set_metadata_respond(d, FLAC__METADATA_TYPE_VORBIS_COMMENT);
    if (FLAC__stream_decoder_init_stream(d, d_read, NULL, NULL, NULL, NULL, d_write, d_meta, d_error, c)
            != FLAC__STREAM_DECODER_INIT_STATUS_OK) { FLAC__stream_decoder_delete(d); return 0; }
    good = FLAC__stream_decoder_process_until_end_of_stream(d) ? 1 : 0;
    if (!FLAC__stream_decoder_finish(d)) good = 0;   /* false = MD5 mismatch */
    FLAC__stream_decoder_delete(d);
    return good && !c->error;
}

static int pcm_equal(const dec_ctx* c, const int32_t* want, uint64_t frames, uint32_t channels)
{
    return c->frames == frames && (frames == 0 || c->channels == channels) &&
           (frames == 0 || memcmp(c->pcm, want, (size_t)(frames * channels) * sizeof(int32_t)) == 0);
}

/* ---- signals -------------------------------------------------------------------------------- */

#define FRAMES 44100

static int32_t* make_sine(uint32_t frames, uint32_t channels, uint32_t bps, uint32_t rate)
{
    int32_t* s = (int32_t*)malloc((size_t)frames * channels * sizeof(int32_t));
    double peak = (double)((1u << (bps - 1)) - 1);
    uint32_t i, ch;
    if (!s) exit(9);
    for (i = 0; i < frames; i++)
        for (ch = 0; ch < channels; ch++)
            s[i * channels + ch] = (int32_t)lrint((ch == 0 ? 0.5 : 0.25) * peak *
                                                  sin(2.0 * M_PI * (ch == 0 ? 440.0 : 660.0) * i / rate));
    return s;
}

static const int32_t CHUNKS[] = { 1, 7, 1000, 333, 4097, 2, 12345, 511 };

/* writes all frames in uneven chunks; drains output into `out` (if not NULL) after every chunk */
static int write_all(enc* e, const int32_t* s, uint32_t frames, uint32_t channels, buf* out)
{
    uint32_t done = 0; size_t k = 0;
    while (done < frames) {
        int32_t n = CHUNKS[k++ % (sizeof CHUNKS / sizeof CHUNKS[0])];
        int32_t r;
        if ((uint32_t)n > frames - done) n = (int32_t)(frames - done);
        r = p_write_int32(e, s + (size_t)done * channels, n);
        if (r != CBF_OK) { fprintf(stderr, "write_int32 -> %d (%s)\n", r, p_last_state(e)); return 0; }
        done += (uint32_t)n;
        if (out) drain_output(e, out);
    }
    return 1;
}

/* collects every patch; applies them to `stream` (when not NULL); returns 1 when all lie inside [0, limit) */
static int apply_patches(enc* e, uint8_t* stream, size_t stream_len, size_t limit, int32_t* count_out)
{
    int32_t count = p_patch_count(e), i, inside = 1;
    *count_out = count;
    for (i = 0; i < count; i++) {
        int64_t off; int32_t len; uint8_t tmp[65536];
        if (p_patch_info(e, i, &off, &len) != CBF_OK || len <= 0 || len > (int32_t)sizeof tmp) return 0;
        if (p_read_patch(e, i, tmp, (int32_t)sizeof tmp) != len) return 0;
        if ((uint64_t)off + (uint64_t)len > limit) inside = 0;
        if (stream && (uint64_t)off + (uint64_t)len <= stream_len) memcpy(stream + off, tmp, (size_t)len);
    }
    return inside;
}

/* ---- main ----------------------------------------------------------------------------------- */

#define RESOLVE(var, type, name) do { var = (type)lib_sym(lib, name); \
    if (!var) { fprintf(stderr, "FAIL: symbol %s not exported\n", name); return 3; } } while (0)

int main(int argc, char** argv)
{
    lib_handle lib;
    int32_t* stereo;
    enc* e = NULL;
    char what[256];

    if (argc < 2) {
        fprintf(stderr, "usage: %s <path-to-codebrix_flac-library> [output.flac]\n", argv[0]);
        return 2;
    }
    lib = lib_open(argv[1]);
    if (!lib) {
#if defined(_WIN32)
        fprintf(stderr, "FAIL: cannot load %s (error %lu)\n", argv[1], (unsigned long)GetLastError());
#else
        fprintf(stderr, "FAIL: cannot load %s (%s)\n", argv[1], dlerror());
#endif
        return 3;
    }
    RESOLVE(p_api_version,     fn_api_version,     "codebrix_flac_api_version");
    RESOLVE(p_library_version, fn_library_version, "codebrix_flac_library_version");
    RESOLVE(p_create,          fn_create,          "codebrix_flac_encoder_create");
    RESOLVE(p_add_comment,     fn_add_comment,     "codebrix_flac_encoder_add_comment");
    RESOLVE(p_set_padding,     fn_set_i32,         "codebrix_flac_encoder_set_padding");
    RESOLVE(p_set_seek_table,  fn_set_i64,         "codebrix_flac_encoder_set_seek_table");
    RESOLVE(p_set_packet_mode, fn_set_i32,         "codebrix_flac_encoder_set_packet_mode");
    RESOLVE(p_start,           fn_op,              "codebrix_flac_encoder_start");
    RESOLVE(p_write_int32,     fn_write_int32,     "codebrix_flac_encoder_write_int32");
    RESOLVE(p_finish,          fn_op,              "codebrix_flac_encoder_finish");
    RESOLVE(p_pending_output,  fn_i64,             "codebrix_flac_encoder_pending_output");
    RESOLVE(p_read_output,     fn_read,            "codebrix_flac_encoder_read_output");
    RESOLVE(p_header_length,   fn_op,              "codebrix_flac_encoder_header_length");
    RESOLVE(p_read_header,     fn_read,            "codebrix_flac_encoder_read_header");
    RESOLVE(p_patch_count,     fn_op,              "codebrix_flac_encoder_patch_count");
    RESOLVE(p_patch_info,      fn_patch_info,      "codebrix_flac_encoder_patch_info");
    RESOLVE(p_read_patch,      fn_read_patch,      "codebrix_flac_encoder_read_patch");
    RESOLVE(p_pending_packets, fn_op,              "codebrix_flac_encoder_pending_packets");
    RESOLVE(p_packet_info,     fn_packet_info,     "codebrix_flac_encoder_packet_info");
    RESOLVE(p_read_packet,     fn_read,            "codebrix_flac_encoder_read_packet");
    RESOLVE(p_samples_written, fn_i64,             "codebrix_flac_encoder_samples_written");
    RESOLVE(p_last_state,      fn_last_state,      "codebrix_flac_encoder_last_state");
    RESOLVE(p_destroy,         fn_destroy,         "codebrix_flac_encoder_destroy");
    printf("ok:   all 23 entry points resolved\n");

    /* ---- 1. versions ---- */
    CHECK(p_api_version() == 1, "codebrix_flac_api_version() == 1");
    printf("      libFLAC version: %s\n", p_library_version());
    CHECK(p_library_version() && p_library_version()[0] != '\0', "codebrix_flac_library_version() is non-empty");

    stereo = make_sine(FRAMES, 2, 16, 44100);

    /* ---- 2. stream mode ---- */
    {
        buf out = { 0 };
        uint8_t hdr_before[8192], hdr_after[8192];
        int32_t hlen, hb, ha, npatch = 0;
        uint8_t* unpatched;
        streaminfo si0, si1, sihb;
        dec_ctx dc;

        CHECK(p_create(2, 44100, 16, 5, 0, 0, 1, 0, &e) == CBF_OK && e, "stream: create stereo 44100 16-bit level 5");
        CHECK(p_add_comment(e, "ENCODER", "codebrix_flac smoke test") == CBF_OK, "stream: add_comment ENCODER");
        CHECK(p_add_comment(e, "TITLE", "Gr\xC3\xBC\xC3\x9F" "e \xE2\x80\x93 440 Hz") == CBF_OK, "stream: add_comment TITLE (non-ASCII UTF-8)");
        CHECK(p_header_length(e) == CBF_E_INVALID_STATE, "stream: header_length before start = CBF_E_INVALID_STATE");
        CHECK(p_start(e) == CBF_OK, "stream: start");
        CHECK(p_last_state(e)[0] == '\0', "stream: last_state is empty after a successful call");
        hlen = p_header_length(e);
        CHECK(hlen > 42, "stream: header_length > 42");
        CHECK(p_pending_output(e) == hlen, "stream: pending_output == header_length after start");
        hb = p_read_header(e, hdr_before, (int32_t)sizeof hdr_before);
        CHECK(hb == hlen, "stream: read_header before finish");
        CHECK(memcmp(hdr_before, "fLaC", 4) == 0, "stream: header starts with \"fLaC\"");
        sihb = parse_streaminfo(hdr_before, (size_t)hb);
        CHECK(sihb.ok && sihb.total == 0 && md5_is_zero(sihb.md5), "stream: header before finish has placeholder totals (0 samples, zero MD5)");
        CHECK(sihb.rate == 44100 && sihb.channels == 2 && sihb.bps == 16, "stream: STREAMINFO rate/channels/bps");
        drain_output(e, &out);
        CHECK(out.n == (size_t)hlen && memcmp(out.p, hdr_before, (size_t)hlen) == 0, "stream: first output bytes == the header");
        CHECK(write_all(e, stereo, FRAMES, 2, &out), "stream: 1 s of 440 Hz in uneven chunks");
        CHECK(p_samples_written(e) == FRAMES, "stream: samples_written == 44100");
        CHECK(p_patch_count(e) == 0, "stream: no patches before finish");
        CHECK(p_finish(e) == CBF_OK, "stream: finish");
        drain_output(e, &out);
        CHECK(p_pending_output(e) == 0, "stream: everything drained");
        CHECK(p_pending_packets(e) == 0, "stream: no packets in stream mode");

        unpatched = (uint8_t*)malloc(out.n);
        memcpy(unpatched, out.p, out.n);
        CHECK(apply_patches(e, out.p, out.n, (size_t)hlen, &npatch), "stream: every patch lies inside the header");
        snprintf(what, sizeof what, "stream: finish recorded patches (%d)", npatch);
        CHECK(npatch > 0, what);
        si0 = parse_streaminfo(unpatched, out.n);
        si1 = parse_streaminfo(out.p, out.n);
        CHECK(si0.ok && si0.total == 0, "stream: unpatched output keeps the placeholder total");
        CHECK(si1.ok && si1.total == FRAMES, "stream: patched STREAMINFO total samples == 44100");
        CHECK(si1.ok && !md5_is_zero(si1.md5), "stream: patched STREAMINFO MD5 is non-zero");
        ha = p_read_header(e, hdr_after, (int32_t)sizeof hdr_after);
        CHECK(ha == hlen && memcmp(hdr_after, out.p, (size_t)hlen) == 0, "stream: read_header after finish == the patched header");
        CHECK(p_write_int32(e, stereo, 1) == CBF_E_INVALID_STATE, "stream: write after finish = CBF_E_INVALID_STATE");
        CHECK(p_last_state(e)[0] != '\0', "stream: last_state explains the failure");
        CHECK(p_finish(e) == CBF_E_INVALID_STATE, "stream: second finish = CBF_E_INVALID_STATE");

        CHECK(decode(out.p, out.n, &dc, 1, "TITLE=Gr\xC3\xBC\xC3\x9F" "e \xE2\x80\x93 440 Hz"), "stream: libFLAC decodes the patched stream (MD5 checked)");
        CHECK(pcm_equal(&dc, stereo, FRAMES, 2), "stream: decoded samples are sample-exact (44100 frames)");
        CHECK(dc.rate == 44100 && dc.bps == 16, "stream: decoded rate/bps");
        CHECK(dc.found_comment, "stream: the non-ASCII TITLE comment round-trips");
        printf("      vendor string: %s\n", dc.vendor);
        CHECK(strncmp(dc.vendor, "reference libFLAC ", 18) == 0, "stream: vendor string is libFLAC's");
        free(dc.pcm);
        CHECK(decode(unpatched, out.n, &dc, 0, NULL) && pcm_equal(&dc, stereo, FRAMES, 2),
              "stream: the UNPATCHED stream (forward-only writer) also decodes sample-exact");
        free(dc.pcm);

        if (argc >= 3) {
            FILE* f = fopen(argv[2], "wb");
            if (f) { fwrite(out.p, 1, out.n, f); fclose(f); printf("      wrote %s (%zu bytes)\n", argv[2], out.n); }
            else { fprintf(stderr, "FAIL: cannot write %s\n", argv[2]); failures++; }
        }
        p_destroy(e); e = NULL;
        free(unpatched); free(out.p);
    }

    /* ---- 3 + 4. packet mode, then the same options in stream mode ---- */
    {
        buf hdr_out = { 0 }, joined = { 0 }, stream = { 0 };
        uint8_t hdr[16384], pk[65536];
        int32_t hlen, npk = 0, npatch = 0, mono_ok = 1, len, cnt, r;
        int64_t first, sum = 0;
        streaminfo si;
        dec_ctx dc;

        CHECK(p_create(2, 44100, 16, 5, 0, 1, 1, FRAMES, &e) == CBF_OK, "packet: create (verify=1, estimate 44100)");
        CHECK(p_set_seek_table(e, 4096) == CBF_OK, "packet: set_seek_table every 4096 samples");
        CHECK(p_set_padding(e, 512) == CBF_OK, "packet: set_padding 512");
        CHECK(p_set_packet_mode(e, 1) == CBF_OK, "packet: set_packet_mode 1");
        CHECK(p_start(e) == CBF_OK, "packet: start");
        hlen = p_header_length(e);
        CHECK(hlen > 42 + 512, "packet: header_length > 0 (includes seek table + padding)");
        drain_output(e, &hdr_out);
        CHECK(hdr_out.n == (size_t)hlen, "packet: the output carries the header only");
        {
            uint32_t done = 0; size_t k = 0;
            while (done < FRAMES) {
                int32_t n = CHUNKS[k++ % (sizeof CHUNKS / sizeof CHUNKS[0])];
                if ((uint32_t)n > FRAMES - done) n = (int32_t)(FRAMES - done);
                if (p_write_int32(e, stereo + (size_t)done * 2, n) != CBF_OK) { mono_ok = 0; break; }
                done += (uint32_t)n;
                while (p_pending_packets(e) > 0) {
                    if (p_packet_info(e, &len, &first, &cnt) != CBF_OK || first != sum || cnt <= 0) mono_ok = 0;
                    r = p_read_packet(e, pk, (int32_t)sizeof pk);
                    if (r != len) mono_ok = 0; else buf_add(&joined, pk, (size_t)r);
                    sum += cnt; npk++;
                }
                if (p_pending_output(e) != 0) mono_ok = 0;
            }
        }
        CHECK(p_finish(e) == CBF_OK, "packet: finish (verify on)");
        while (p_pending_packets(e) > 0) {
            if (p_packet_info(e, &len, &first, &cnt) != CBF_OK || first != sum || cnt <= 0) mono_ok = 0;
            r = p_read_packet(e, pk, (int32_t)sizeof pk);
            if (r != len) mono_ok = 0; else buf_add(&joined, pk, (size_t)r);
            sum += cnt; npk++;
        }
        CHECK(mono_ok, "packet: every packet's first_sample == running sum, lengths match, no audio in the output");
        snprintf(what, sizeof what, "packet: %d packets whose sample counts sum to 44100", npk);
        CHECK(npk > 1 && sum == FRAMES, what);
        CHECK(p_pending_output(e) == 0, "packet: output still empty after finish");
        CHECK(p_packet_info(e, &len, &first, &cnt) == CBF_E_INVALID_STATE, "packet: packet_info with none pending = CBF_E_INVALID_STATE");
        CHECK(p_read_packet(e, pk, (int32_t)sizeof pk) == CBF_E_INVALID_STATE, "packet: read_packet with none pending = CBF_E_INVALID_STATE");
        CHECK(apply_patches(e, NULL, 0, (size_t)hlen, &npatch) && npatch > 0, "packet: patches recorded, all inside the header");
        CHECK(p_read_header(e, hdr, (int32_t)sizeof hdr) == hlen, "packet: read_header after finish");
        si = parse_streaminfo(hdr, (size_t)hlen);
        CHECK(si.ok && si.total == FRAMES && !md5_is_zero(si.md5), "packet: header patched after finish (total 44100, MD5 set)");
        CHECK(memcmp(hdr, hdr_out.p, (size_t)hlen) != 0, "packet: patched header differs from the placeholder header");
        p_destroy(e); e = NULL;

        {
            buf full = { 0 };
            buf_add(&full, hdr, (size_t)hlen);
            buf_add(&full, joined.p, joined.n);
            CHECK(decode(full.p, full.n, &dc, 1, NULL) && pcm_equal(&dc, stereo, FRAMES, 2),
                  "packet: header + packets decode sample-exact (MD5 checked)");
            free(dc.pcm);

            /* 4. identical options, stream mode */
            CHECK(p_create(2, 44100, 16, 5, 0, 1, 1, FRAMES, &e) == CBF_OK &&
                  p_set_seek_table(e, 4096) == CBF_OK && p_set_padding(e, 512) == CBF_OK && p_start(e) == CBF_OK,
                  "stream-twin: create + seek table + padding + start");
            CHECK(write_all(e, stereo, FRAMES, 2, &stream) && p_finish(e) == CBF_OK, "stream-twin: encode + finish (verify on)");
            drain_output(e, &stream);
            CHECK(apply_patches(e, stream.p, stream.n, (size_t)hlen, &npatch), "stream-twin: patches applied");
            CHECK(stream.n == full.n && memcmp(stream.p, full.p, full.n) == 0,
                  "stream-twin: patched stream == header + packets, byte for byte");
            p_destroy(e); e = NULL;
            free(full.p);
        }
        free(hdr_out.p); free(joined.p); free(stream.p);
    }

    /* ---- 5. 24-bit mono 48 kHz at levels 0 and 8 ---- */
    {
        int32_t* mono24 = make_sine(48000, 1, 24, 48000);
        int levels[2] = { 0, 8 }, li;
        for (li = 0; li < 2; li++) {
            buf out = { 0 }; int32_t np; dec_ctx dc; streaminfo si;
            snprintf(what, sizeof what, "24-bit mono 48000 Hz level %d (verify=1): encode, patch, decode sample-exact", levels[li]);
            if (p_create(1, 48000, 24, levels[li], 0, 1, 1, 0, &e) != CBF_OK || p_start(e) != CBF_OK ||
                !write_all(e, mono24, 48000, 1, &out) || p_finish(e) != CBF_OK) {
                CHECK(0, what);
            } else {
                drain_output(e, &out);
                apply_patches(e, out.p, out.n, out.n, &np);
                si = parse_streaminfo(out.p, out.n);
                CHECK(si.ok && si.total == 48000 && si.bps == 24 && si.channels == 1 &&
                      decode(out.p, out.n, &dc, 1, NULL) && pcm_equal(&dc, mono24, 48000, 1), what);
                free(dc.pcm);
            }
            p_destroy(e); e = NULL; free(out.p);
        }
        free(mono24);
    }

    /* ---- 6. empty stream ---- */
    {
        buf out = { 0 }; int32_t np; dec_ctx dc;
        CHECK(p_create(2, 44100, 16, -1, 0, 0, 1, 0, &e) == CBF_OK && p_start(e) == CBF_OK && p_finish(e) == CBF_OK,
              "empty: start + finish with no samples (level -1 = default)");
        drain_output(e, &out);
        apply_patches(e, out.p, out.n, out.n, &np);
        CHECK(decode(out.p, out.n, &dc, 0, NULL) && dc.frames == 0, "empty: a valid FLAC stream with 0 samples");
        free(dc.pcm);
        p_destroy(e); e = NULL; free(out.p);
    }

    /* ---- 7. error paths ---- */
    {
        uint8_t small[8];
        int64_t off; int32_t len, cnt; int64_t first;
        int32_t bad[4] = { 0, 0, 40000, 0 };

        CHECK(p_create(2, 44100, 16, 5, 0, 0, 1, 0, NULL) == CBF_E_INVALID_ARGUMENT, "create(out=NULL) = CBF_E_INVALID_ARGUMENT");
        CHECK(p_create(0, 44100, 16, 5, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT && e == NULL, "create(channels=0) = CBF_E_INVALID_ARGUMENT, handle NULL");
        CHECK(p_create(9, 44100, 16, 5, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(channels=9)");
        CHECK(p_create(2, 0, 16, 5, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(rate=0)");
        CHECK(p_create(2, 655351, 16, 5, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(rate=655351)");
        CHECK(p_create(2, 44100, 3, 5, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(bps=3)");
        CHECK(p_create(2, 44100, 33, 5, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(bps=33)");
        CHECK(p_create(2, 44100, 16, 9, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(level=9)");
        CHECK(p_create(2, 44100, 16, -2, 0, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(level=-2)");
        CHECK(p_create(2, 44100, 16, 5, 15, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(block_size=15)");
        CHECK(p_create(2, 44100, 16, 5, 65536, 0, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(block_size=65536)");
        CHECK(p_create(2, 44100, 16, 5, 0, 2, 1, 0, &e) == CBF_E_INVALID_ARGUMENT, "create(verify=2)");
        CHECK(p_create(2, 44100, 16, 5, 0, 0, 1, -1, &e) == CBF_E_INVALID_ARGUMENT, "create(estimate=-1)");

        CHECK(p_add_comment(NULL, "A", "B") == CBF_E_INVALID_ARGUMENT, "add_comment(NULL handle)");
        CHECK(p_start(NULL) == CBF_E_INVALID_ARGUMENT, "start(NULL)");
        CHECK(p_write_int32(NULL, bad, 1) == CBF_E_INVALID_ARGUMENT, "write_int32(NULL)");
        CHECK(p_finish(NULL) == CBF_E_INVALID_ARGUMENT, "finish(NULL)");
        CHECK(p_pending_output(NULL) < 0, "pending_output(NULL) < 0");
        CHECK(p_read_output(NULL, small, 8) < 0, "read_output(NULL) < 0");
        CHECK(p_header_length(NULL) < 0 && p_read_header(NULL, small, 8) < 0, "header_length/read_header(NULL) < 0");
        CHECK(p_patch_count(NULL) < 0 && p_pending_packets(NULL) < 0 && p_samples_written(NULL) < 0, "counts(NULL) < 0");
        CHECK(p_last_state(NULL) != NULL, "last_state(NULL) is a valid string");
        p_destroy(NULL);
        printf("ok:   destroy(NULL) is ignored\n");

        CHECK(p_create(2, 44100, 16, 5, 0, 0, 1, 0, &e) == CBF_OK, "errors: create");
        CHECK(p_set_seek_table(e, 4096) == CBF_E_INVALID_STATE, "set_seek_table without an estimate = CBF_E_INVALID_STATE");
        CHECK(p_add_comment(e, "A=B", "x") == CBF_E_INVALID_ARGUMENT, "add_comment with '=' in the tag = CBF_E_INVALID_ARGUMENT");
        CHECK(p_add_comment(e, "TITLE", "\xff\xfe") == CBF_E_INVALID_ARGUMENT, "add_comment with a non-UTF-8 value = CBF_E_INVALID_ARGUMENT");
        CHECK(p_set_padding(e, -1) == CBF_E_INVALID_ARGUMENT, "set_padding(-1) = CBF_E_INVALID_ARGUMENT");
        CHECK(p_set_packet_mode(e, 2) == CBF_E_INVALID_ARGUMENT, "set_packet_mode(2) = CBF_E_INVALID_ARGUMENT");
        CHECK(p_write_int32(e, bad, 1) == CBF_E_INVALID_STATE, "write before start = CBF_E_INVALID_STATE");
        CHECK(p_finish(e) == CBF_E_INVALID_STATE, "finish before start = CBF_E_INVALID_STATE");
        CHECK(p_read_header(e, small, 8) == CBF_E_INVALID_STATE, "read_header before start = CBF_E_INVALID_STATE");
        CHECK(p_start(e) == CBF_OK, "errors: start");
        CHECK(p_start(e) == CBF_E_INVALID_STATE, "start twice = CBF_E_INVALID_STATE");
        CHECK(p_add_comment(e, "A", "B") == CBF_E_INVALID_STATE, "add_comment after start = CBF_E_INVALID_STATE");
        CHECK(p_set_padding(e, 16) == CBF_E_INVALID_STATE, "set_padding after start = CBF_E_INVALID_STATE");
        CHECK(p_set_seek_table(e, 0) == CBF_E_INVALID_STATE, "set_seek_table after start = CBF_E_INVALID_STATE");
        CHECK(p_set_packet_mode(e, 0) == CBF_E_INVALID_STATE, "set_packet_mode after start = CBF_E_INVALID_STATE");
        CHECK(p_read_header(e, small, (int32_t)sizeof small) == CBF_E_INVALID_ARGUMENT, "read_header into a too-small buffer = CBF_E_INVALID_ARGUMENT");
        CHECK(p_write_int32(e, bad, 2) == CBF_E_INVALID_ARGUMENT, "16-bit sample 40000 = CBF_E_INVALID_ARGUMENT");
        printf("      last_state: %s\n", p_last_state(e));
        CHECK(p_last_state(e)[0] != '\0', "last_state names the out-of-range sample");
        CHECK(p_write_int32(e, bad, 1) == CBF_OK, "encoder still usable after the rejected write");
        CHECK(p_write_int32(e, NULL, 1) == CBF_E_INVALID_ARGUMENT, "write_int32(samples=NULL) = CBF_E_INVALID_ARGUMENT");
        CHECK(p_write_int32(e, bad, -1) == CBF_E_INVALID_ARGUMENT, "write_int32(frames=-1) = CBF_E_INVALID_ARGUMENT");
        CHECK(p_write_int32(e, bad, 0) == CBF_OK, "write_int32(frames=0) is a no-op");
        CHECK(p_samples_written(e) == 1, "samples_written counts only accepted frames");
        CHECK(p_patch_info(e, 0, &off, &len) == CBF_E_INVALID_ARGUMENT, "patch_info before finish (index 0) = CBF_E_INVALID_ARGUMENT");
        CHECK(p_packet_info(e, &len, &first, &cnt) == CBF_E_INVALID_STATE, "packet_info in stream mode = CBF_E_INVALID_STATE");
        CHECK(p_read_output(e, small, -1) == CBF_E_INVALID_ARGUMENT, "read_output(capacity=-1) = CBF_E_INVALID_ARGUMENT");
        p_destroy(e); e = NULL;
        printf("ok:   destroy of a started, unfinished encoder\n");

        CHECK(p_create(2, 44100, 16, 5, 8192, 0, 1, 0, &e) == CBF_OK, "subset: create block_size 8192 at 44100 Hz, streamable_subset=1");
        CHECK(p_start(e) == CBF_E_UNSUPPORTED, "subset: start refuses it = CBF_E_UNSUPPORTED");
        printf("      last_state: %s\n", p_last_state(e));
        CHECK(strstr(p_last_state(e), "NOT_STREAMABLE") != NULL, "subset: last_state is libFLAC's NOT_STREAMABLE init status");
        CHECK(p_write_int32(e, bad, 1) == CBF_E_INVALID_STATE, "subset: write after a failed start = CBF_E_INVALID_STATE");
        p_destroy(e); e = NULL;
        CHECK(p_create(2, 44100, 16, 5, 8192, 0, 0, 0, &e) == CBF_OK && p_start(e) == CBF_OK && p_finish(e) == CBF_OK,
              "subset: the same block size with streamable_subset=0 is accepted");
        p_destroy(e); e = NULL;
    }

    free(stereo);
    if (failures) {
        fprintf(stderr, "\nSMOKE TEST FAILED: %d check(s)\n", failures);
        return 1;
    }
    printf("\nSMOKE TEST PASSED\n");
    return 0;
}
