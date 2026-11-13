#ifndef PXI_ROMFS_H
#define PXI_ROMFS_H

#include "ie4p_core.h"
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define IEGO_LIGHT_EUR_TID      0x0004000000112F00ULL
#define IEGO_LIGHT_LOW_ID       0x00112F00U
#define IEGO_LIGHT_HIGH_ID      0x00040000U

#define PXI_ARCHIVE_SAVEDATA_AND_CONTENT 0x2345678AU
#define PXI_PATH_BINARY                  2U
#define PXI_FS_OPEN_READ                 1U

#define PXI_MEDIA_NAND                   0U
#define PXI_MEDIA_SD                     1U
#define PXI_MEDIA_GAMECARD               2U

/* Process9 Title Access binary lowpath size (9 x u32 = 0x24 bytes) */
#define PXI_TITLE_ACCESS_PATH_WORDS      9U
#define PXI_TITLE_ACCESS_PATH_BYTES      0x24U
#define PXI_CONTENT_TYPE_ALLOW_ALL_MASK  0x3FU

/* Process9 PXI DMA requires 0x1000-byte page alignment for buffer, offset, and length */
#define PXI_PAGE_SIZE                    0x1000U
#define PXI_BOUNCE_SIZE                  0x20000U /* 128 KB (32 x 4 KB pages) */

#define PXI_RC_SEED_NOT_FOUND            ((int32_t)0xC8804464)

typedef struct {
    void *hal_ctx;
    int32_t (*steal_pxifs0)(void *ctx, uint32_t *out_session);
    int32_t (*get_title_seed)(void *ctx, uint64_t tid, uint32_t out_seed[4]);
    int     (*is_card_inserted)(void *ctx);
    int32_t (*open_archive)(
        void *ctx,
        uint32_t session,
        uint64_t *out_arch,
        uint32_t archive_id,
        const void *path_data,
        uint32_t path_size);
    int32_t (*open_file)(
        void *ctx,
        uint32_t session,
        uint64_t *out_file,
        uint64_t arch,
        const void *path_data,
        uint32_t path_size,
        uint32_t flags,
        uint32_t attrs);
    int32_t (*get_file_size)(
        void *ctx,
        uint32_t session,
        uint64_t file,
        uint64_t *out_size);
    int32_t (*read_file)(
        void *ctx,
        uint32_t session,
        uint64_t file,
        uint32_t *out_got,
        uint64_t offset,
        void *buffer,
        uint32_t size);
    int32_t (*close_file)(void *ctx, uint32_t session, uint64_t file);
    int32_t (*close_archive)(void *ctx, uint32_t session, uint64_t arch);
    void    (*close_session)(void *ctx, uint32_t session);
} pxi_hal_t;

typedef struct {
    pxi_hal_t hal;
    uint32_t  pxi_session;
    uint64_t  pxi_arch;
    uint64_t  pxi_file;
    uint64_t  pxi_romfs_size;
    uint32_t  media;
    int       arch_open;
    int       file_open;
    uint64_t  ie_a_offset_in_pxi;
    uint64_t  ie_a_size;
    uint8_t  *bounce_buf;
    uint64_t  cache_off;
    uint32_t  cache_len;
    int32_t   last_read_rc;
    uint32_t  last_read_got;
} pxi_game_ctx_t;

int pxi_game_open_with_hal(
    pxi_game_ctx_t *g,
    const pxi_hal_t *hal,
    ie4p_reader_t *out_reader,
    int32_t *out_rc_svc,
    int32_t *out_rc_sd,
    int32_t *out_rc_cart);

void pxi_game_close(pxi_game_ctx_t *g);

#ifdef __cplusplus
}
#endif

#endif /* PXI_ROMFS_H */
