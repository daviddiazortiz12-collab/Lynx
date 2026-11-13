#ifndef IE4P_CORE_H
#define IE4P_CORE_H

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    IE4P_OK = 0,
    IE4P_ERR_ARGS = -1,
    IE4P_ERR_IO_READ = -2,
    IE4P_ERR_IO_WRITE = -3,
    IE4P_ERR_BAD_ARC0 = -4,
    IE4P_ERR_BAD_PACK = -5,
    IE4P_ERR_ENTRY_COUNT = -6,
    IE4P_ERR_OOM = -7,
    IE4P_ERR_LZ10 = -8,
    IE4P_ERR_LZ4 = -9,
    IE4P_ERR_CRC = -10,
    IE4P_ERR_SIZE_MISMATCH = -11,
    IE4P_ERR_ABORTED = -12
} ie4p_status_t;

typedef struct {
    uint32_t file_offset_in_data;
    uint32_t file_size;
} ie4p_arc0_slot_t;

/* Abstract random-access byte reader (backed by PxiFS0 on 2DS or FILE* on host/SD). */
typedef int (*ie4p_read_at_fn)(void *ctx, uint64_t offset, void *dst, size_t len);

typedef struct {
    void *ctx;
    uint64_t total_size;
    ie4p_read_at_fn read_at;
} ie4p_reader_t;

/* Return non-zero to abort cleanly (e.g. if aptMainLoop() returns false). */
typedef int (*ie4p_progress_fn)(
    uint32_t current_op,
    uint32_t total_ops,
    uint64_t bytes_written,
    uint64_t total_bytes,
    void *user_data);

void ie4p_init_file_reader(ie4p_reader_t *out_reader, FILE *fp, uint64_t total_size);

void ie4p_crc32_init(void);
uint32_t ie4p_crc32_update(uint32_t crc_inv, const uint8_t *data, size_t len);

ie4p_status_t ie4p_decompress_lz4_raw(
    const uint8_t *src,
    size_t src_len,
    uint8_t *dst,
    size_t dst_len);

ie4p_status_t ie4p_load_arc0_slots(
    const ie4p_reader_t *installed_arc0,
    uint32_t *out_data_offset,
    ie4p_arc0_slot_t **out_slots,
    uint32_t *out_slot_count,
    char *err_detail,
    size_t err_cap);

ie4p_status_t ie4p_apply_patch_reader(
    const ie4p_reader_t *installed_arc0,
    FILE *patch_pack,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap);

ie4p_status_t ie4p_apply_patch_streams(
    FILE *installed_arc0,
    FILE *patch_pack,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap);

const char *ie4p_status_str(ie4p_status_t st);

#ifdef __cplusplus
}
#endif

#endif /* IE4P_CORE_H */
