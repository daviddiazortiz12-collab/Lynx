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

/* Return non-zero to abort cleanly (e.g. if aptMainLoop() returns false). */
typedef int (*ie4p_progress_fn)(
    uint32_t current_op,
    uint32_t total_ops,
    uint64_t bytes_written,
    uint64_t total_bytes,
    void *user_data);

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
