/*
 * layout-check.c - compile-time checks of the codebrix_flac.h ABI the managed binding assumes.
 *
 * Cross-compiled (never executed) for EVERY Android ABI by build.sh, so the checks run on the
 * target's data model, not the build host's. codebrix_flac.h deliberately has no struct, no
 * callback and no C `long`; what the managed LibraryImport declarations rely on is the width of
 * the scalar types, pointers, and the numeric values of the result constants. Any drift fails the
 * build here instead of corrupting a call on a device.
 */
#include <stddef.h>
#include <stdint.h>
#include "codebrix_flac.h"

#define SIZE(type, size) _Static_assert(sizeof(type) == (size), #type " size")
#define VALUE(name, value) _Static_assert((name) == (value), #name " value")

SIZE(int32_t, 4);
SIZE(int64_t, 8);
SIZE(uint8_t, 1);
SIZE(void*, 8);                       /* both shipped Android ABIs are 64-bit */
SIZE(codebrix_flac_encoder*, 8);
SIZE(const char*, 8);

VALUE(CODEBRIX_FLAC_API_VERSION, 1);
VALUE(CBF_OK, 0);
VALUE(CBF_E_INVALID_ARGUMENT, -1);
VALUE(CBF_E_OUT_OF_MEMORY, -2);
VALUE(CBF_E_INVALID_STATE, -3);
VALUE(CBF_E_UNSUPPORTED, -4);
VALUE(CBF_E_VERIFY_MISMATCH, -5);
VALUE(CBF_E_FLAC_FAULT, -6);

/* The function types, exactly as the managed side declares them. A changed signature in the
 * header makes one of these initialisations a constraint violation (an error with -Werror). */
typedef codebrix_flac_encoder enc;
static int32_t     (*const p_api)(void) = codebrix_flac_api_version;
static const char* (*const p_libver)(void) = codebrix_flac_library_version;
static int32_t     (*const p_create)(int32_t, int32_t, int32_t, int32_t, int32_t, int32_t, int32_t, int64_t,
                                     enc**) = codebrix_flac_encoder_create;
static int32_t     (*const p_comment)(enc*, const char*, const char*) = codebrix_flac_encoder_add_comment;
static int32_t     (*const p_padding)(enc*, int32_t) = codebrix_flac_encoder_set_padding;
static int32_t     (*const p_seektable)(enc*, int64_t) = codebrix_flac_encoder_set_seek_table;
static int32_t     (*const p_packetmode)(enc*, int32_t) = codebrix_flac_encoder_set_packet_mode;
static int32_t     (*const p_start)(enc*) = codebrix_flac_encoder_start;
static int32_t     (*const p_write)(enc*, const int32_t*, int32_t) = codebrix_flac_encoder_write_int32;
static int32_t     (*const p_finish)(enc*) = codebrix_flac_encoder_finish;
static int64_t     (*const p_pending)(enc*) = codebrix_flac_encoder_pending_output;
static int32_t     (*const p_read)(enc*, uint8_t*, int32_t) = codebrix_flac_encoder_read_output;
static int32_t     (*const p_hlen)(enc*) = codebrix_flac_encoder_header_length;
static int32_t     (*const p_header)(enc*, uint8_t*, int32_t) = codebrix_flac_encoder_read_header;
static int32_t     (*const p_pcount)(enc*) = codebrix_flac_encoder_patch_count;
static int32_t     (*const p_pinfo)(enc*, int32_t, int64_t*, int32_t*) = codebrix_flac_encoder_patch_info;
static int32_t     (*const p_patch)(enc*, int32_t, uint8_t*, int32_t) = codebrix_flac_encoder_read_patch;
static int32_t     (*const p_pkpend)(enc*) = codebrix_flac_encoder_pending_packets;
static int32_t     (*const p_pkinfo)(enc*, int32_t*, int64_t*, int32_t*) = codebrix_flac_encoder_packet_info;
static int32_t     (*const p_packet)(enc*, uint8_t*, int32_t) = codebrix_flac_encoder_read_packet;
static int64_t     (*const p_samples)(enc*) = codebrix_flac_encoder_samples_written;
static const char* (*const p_state)(enc*) = codebrix_flac_encoder_last_state;
static void        (*const p_destroy)(enc*) = codebrix_flac_encoder_destroy;

/* Referenced so -Wunused does not complain; never called. */
const void* codebrix_flac_layout_check_refs[] = {
    (const void*)&p_api, (const void*)&p_libver, (const void*)&p_create, (const void*)&p_comment,
    (const void*)&p_padding, (const void*)&p_seektable, (const void*)&p_packetmode, (const void*)&p_start,
    (const void*)&p_write, (const void*)&p_finish, (const void*)&p_pending, (const void*)&p_read,
    (const void*)&p_hlen, (const void*)&p_header, (const void*)&p_pcount, (const void*)&p_pinfo,
    (const void*)&p_patch, (const void*)&p_pkpend, (const void*)&p_pkinfo, (const void*)&p_packet,
    (const void*)&p_samples, (const void*)&p_state, (const void*)&p_destroy
};
