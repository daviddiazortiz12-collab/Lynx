#ifndef __3DS__
#define _POSIX_C_SOURCE 200809L
#endif

#include "pxi_romfs.h"

#ifdef __3DS__
#include <malloc.h>
#endif
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

static uint32_t s_arch_path[4] __attribute__((aligned(64)));
static uint32_t s_file_path[PXI_TITLE_ACCESS_PATH_WORDS] __attribute__((aligned(64)));

static uint32_t pxi_le32(const uint8_t *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static uint64_t pxi_le64(const uint8_t *p)
{
    return (uint64_t)pxi_le32(p) | ((uint64_t)pxi_le32(p + 4) << 32);
}

static uint8_t *pxi_page_alloc(size_t size)
{
#ifdef __3DS__
    return (uint8_t *)memalign(PXI_PAGE_SIZE, size);
#else
    void *ptr = NULL;
    if (posix_memalign(&ptr, PXI_PAGE_SIZE, size) != 0) {
        return NULL;
    }
    return (uint8_t *)ptr;
#endif
}

static int pxi_raw_read(pxi_game_ctx_t *g, uint64_t raw_off, void *dst, size_t len)
{
    uint8_t *out = (uint8_t *)dst;
    while (len > 0U) {
        if (g->cache_len > 0U &&
            raw_off >= g->cache_off &&
            raw_off < g->cache_off + (uint64_t)g->cache_len) {
            size_t avail = (size_t)((g->cache_off + (uint64_t)g->cache_len) - raw_off);
            size_t take = (len < avail) ? len : avail;
            memcpy(out, g->bounce_buf + (size_t)(raw_off - g->cache_off), take);
            out += take;
            raw_off += (uint64_t)take;
            len -= take;
            continue;
        }

        uint64_t aligned_start = raw_off & ~((uint64_t)PXI_PAGE_SIZE - 1ULL);
        uint32_t req_len = PXI_BOUNCE_SIZE;
        if (g->pxi_romfs_size > 0ULL && aligned_start < g->pxi_romfs_size) {
            uint64_t rem = g->pxi_romfs_size - aligned_start;
            if (rem < (uint64_t)req_len) {
                uint64_t page_rem = (rem + ((uint64_t)PXI_PAGE_SIZE - 1ULL)) & ~((uint64_t)PXI_PAGE_SIZE - 1ULL);
                if (page_rem == 0ULL) {
                    page_rem = (uint64_t)PXI_PAGE_SIZE;
                }
                if (page_rem < (uint64_t)req_len) {
                    req_len = (uint32_t)page_rem;
                }
            }
        }

        uint32_t got = 0U;
        int32_t rc = g->hal.read_file(
            g->hal.hal_ctx,
            g->pxi_session,
            g->pxi_file,
            &got,
            aligned_start,
            g->bounce_buf,
            req_len);

        if ((rc < 0 || got == 0U) &&
            g->pxi_romfs_size > aligned_start &&
            (g->pxi_romfs_size - aligned_start) < (uint64_t)req_len) {
            uint32_t exact_rem = (uint32_t)(g->pxi_romfs_size - aligned_start);
            got = 0U;
            rc = g->hal.read_file(
                g->hal.hal_ctx,
                g->pxi_session,
                g->pxi_file,
                &got,
                aligned_start,
                g->bounce_buf,
                exact_rem);
        }

        g->last_read_rc = rc;
        g->last_read_got = got;
        if (rc < 0 || got <= (uint32_t)(raw_off - aligned_start)) {
            g->cache_len = 0U;
            return -1;
        }

        g->cache_off = aligned_start;
        g->cache_len = got;
    }
    return 0;
}

static int pxi_ie_a_read_at(void *ctx, uint64_t offset, void *dst, size_t len)
{
    pxi_game_ctx_t *g = (pxi_game_ctx_t *)ctx;
    if (offset > g->ie_a_size || len > (size_t)(g->ie_a_size - offset)) {
        return -1;
    }
    return pxi_raw_read(g, g->ie_a_offset_in_pxi + offset, dst, len);
}

static bool utf16le_equals_ie_a_fa(const uint8_t *utf16_bytes, uint32_t byte_len)
{
    static const char target[7] = { 'i', 'e', '_', 'a', '.', 'f', 'a' };
    if (byte_len != 14U) {
        return false;
    }
    for (int i = 0; i < 7; i++) {
        uint16_t ch = (uint16_t)utf16_bytes[i * 2] | ((uint16_t)utf16_bytes[i * 2 + 1] << 8);
        if (ch >= 'A' && ch <= 'Z') {
            ch = (uint16_t)(ch - 'A' + 'a');
        }
        if (ch != (uint16_t)target[i]) {
            return false;
        }
    }
    return true;
}

static int pxi_try_open_media(
    pxi_game_ctx_t *g,
    uint32_t media,
    int32_t *out_rc_media,
    uint8_t out_ivfc_hdr[0x60])
{
    uint32_t seed_candidates[2][4];
    int seed_count = 1;
    memset(seed_candidates, 0, sizeof(seed_candidates));

    if (media == PXI_MEDIA_SD && g->hal.get_title_seed != NULL) {
        uint32_t fetched[4] = { 0U, 0U, 0U, 0U };
        int32_t rc_seed = g->hal.get_title_seed(g->hal.hal_ctx, IEGO_LIGHT_EUR_TID, fetched);
        if (rc_seed >= 0 && (fetched[0] | fetched[1] | fetched[2] | fetched[3]) != 0U) {
            memcpy(seed_candidates[0], fetched, 16U);
            /* Keep zero-seed as second candidate for decrypted NoCrypto CIAs */
            seed_count = 2;
        }
    }

    s_arch_path[0] = IEGO_LIGHT_LOW_ID;
    s_arch_path[1] = IEGO_LIGHT_HIGH_ID;
    s_arch_path[2] = media;
    s_arch_path[3] = 0U;

    int32_t rc = g->hal.open_archive(
        g->hal.hal_ctx,
        g->pxi_session,
        &g->pxi_arch,
        PXI_ARCHIVE_SAVEDATA_AND_CONTENT,
        s_arch_path,
        sizeof(s_arch_path));
    if (rc < 0) {
        *out_rc_media = rc;
        return -3;
    }
    g->arch_open = 1;

    bool opened_file_once = false;
    for (int s = 0; s < seed_count; s++) {
        /* Process9 36-byte (0x24) Title Access binary file lowpath:
         * [0] = 0 (NCCH Title Access)
         * [1] = 0 (TMD content index 0 = main CXI)
         * [2] = 0 (NCCH section 0 = RomFS)
         * [3..4] = 0x3FULL (u64 bitmask allowing all 6 TMD content-type combinations)
         * [5..8] = 16-byte NCCH seed (all zeros when unseeded / NoCrypto)
         */
        s_file_path[0] = 0U;
        s_file_path[1] = 0U;
        s_file_path[2] = 0U;
        s_file_path[3] = PXI_CONTENT_TYPE_ALLOW_ALL_MASK;
        s_file_path[4] = 0U;
        s_file_path[5] = seed_candidates[s][0];
        s_file_path[6] = seed_candidates[s][1];
        s_file_path[7] = seed_candidates[s][2];
        s_file_path[8] = seed_candidates[s][3];

        rc = g->hal.open_file(
            g->hal.hal_ctx,
            g->pxi_session,
            &g->pxi_file,
            g->pxi_arch,
            s_file_path,
            PXI_TITLE_ACCESS_PATH_BYTES,
            PXI_FS_OPEN_READ,
            0U);
        if (rc < 0) {
            *out_rc_media = rc;
            continue;
        }

        opened_file_once = true;
        g->file_open = 1;
        g->pxi_romfs_size = 0ULL;
        if (g->hal.get_file_size != NULL) {
            uint64_t fsz = 0ULL;
            if (g->hal.get_file_size(g->hal.hal_ctx, g->pxi_session, g->pxi_file, &fsz) >= 0) {
                g->pxi_romfs_size = fsz;
            }
        }

        g->cache_off = 0ULL;
        g->cache_len = 0U;
        g->last_read_rc = 0;
        g->last_read_got = 0U;

        if (pxi_raw_read(g, 0ULL, out_ivfc_hdr, 0x60U) == 0) {
            g->media = media;
            *out_rc_media = 0;
            return 0;
        }

        *out_rc_media = (g->last_read_rc < 0) ? g->last_read_rc : -5;
        g->hal.close_file(g->hal.hal_ctx, g->pxi_session, g->pxi_file);
        g->file_open = 0;
    }

    g->hal.close_archive(g->hal.hal_ctx, g->pxi_session, g->pxi_arch);
    g->arch_open = 0;
    return opened_file_once ? -5 : -4;
}

void pxi_game_close(pxi_game_ctx_t *g)
{
    if (g == NULL) {
        return;
    }
    if (g->file_open && g->hal.close_file != NULL) {
        g->hal.close_file(g->hal.hal_ctx, g->pxi_session, g->pxi_file);
        g->file_open = 0;
    }
    if (g->arch_open && g->hal.close_archive != NULL) {
        g->hal.close_archive(g->hal.hal_ctx, g->pxi_session, g->pxi_arch);
        g->arch_open = 0;
    }
    if (g->pxi_session != 0U && g->hal.close_session != NULL) {
        g->hal.close_session(g->hal.hal_ctx, g->pxi_session);
        g->pxi_session = 0U;
    }
    if (g->bounce_buf != NULL) {
        free(g->bounce_buf);
        g->bounce_buf = NULL;
    }
    g->cache_off = 0ULL;
    g->cache_len = 0U;
}

int pxi_game_open_with_hal(
    pxi_game_ctx_t *g,
    const pxi_hal_t *hal,
    ie4p_reader_t *out_reader,
    int32_t *out_rc_svc,
    int32_t *out_rc_sd,
    int32_t *out_rc_cart)
{
    memset(g, 0, sizeof(*g));
    g->hal = *hal;
    *out_rc_svc = 0;
    *out_rc_sd = 0;
    *out_rc_cart = 0;

    g->bounce_buf = pxi_page_alloc(PXI_BOUNCE_SIZE);
    if (g->bounce_buf == NULL) {
        return -1;
    }

    int32_t rc = g->hal.steal_pxifs0(g->hal.hal_ctx, &g->pxi_session);
    *out_rc_svc = rc;
    if (rc != 0 || g->pxi_session == 0U) {
        pxi_game_close(g);
        return -2;
    }

    uint8_t ivfc_hdr[0x60];
    int step_sd = pxi_try_open_media(g, PXI_MEDIA_SD, out_rc_sd, ivfc_hdr);
    if (step_sd != 0) {
        int step_gc = step_sd;
        if (g->hal.is_card_inserted != NULL && g->hal.is_card_inserted(g->hal.hal_ctx) != 0) {
            step_gc = pxi_try_open_media(g, PXI_MEDIA_GAMECARD, out_rc_cart, ivfc_hdr);
            if (step_gc == 0) {
                step_sd = 0;
            }
        }
        if (step_sd != 0) {
            pxi_game_close(g);
            return (step_gc < step_sd) ? step_gc : step_sd;
        }
    }

    uint64_t candidates[4] = { 0x1000ULL, 0x2000ULL, 0ULL, 0x4000ULL };
    int cand_cnt = 3;
    if (pxi_le32(ivfc_hdr + 0x00) == 0x43465649U) { /* "IVFC" */
        uint32_t master_hash_sz = pxi_le32(ivfc_hdr + 0x08);
        uint32_t l3_blk_log2 = pxi_le32(ivfc_hdr + 0x4C) & 31U;
        uint64_t blk = (l3_blk_log2 >= 9U && l3_blk_log2 <= 20U) ? (1ULL << l3_blk_log2) : 0x1000ULL;
        uint64_t computed_l3 = ((uint64_t)(0x60U + master_hash_sz) + (blk - 1ULL)) & ~(blk - 1ULL);
        candidates[0] = computed_l3;
        candidates[1] = 0x1000ULL;
        candidates[2] = 0x2000ULL;
        candidates[3] = 0ULL;
        cand_cnt = 4;
    }

    uint64_t l3_off = 0ULL;
    uint8_t l3_hdr[0x28];
    bool found_l3 = false;
    for (int c = 0; c < cand_cnt; c++) {
        if (pxi_raw_read(g, candidates[c], l3_hdr, sizeof(l3_hdr)) == 0 &&
            pxi_le32(l3_hdr + 0x00) == 0x28U &&
            pxi_le32(l3_hdr + 0x04) >= 0x28U) {
            l3_off = candidates[c];
            found_l3 = true;
            break;
        }
    }
    if (!found_l3) {
        if (g->last_read_rc < 0) {
            if (g->media == PXI_MEDIA_SD) *out_rc_sd = g->last_read_rc;
            else *out_rc_cart = g->last_read_rc;
        }
        pxi_game_close(g);
        return -6;
    }

    uint32_t file_meta_off = pxi_le32(l3_hdr + 0x1C);
    uint32_t file_meta_len = pxi_le32(l3_hdr + 0x20);
    uint32_t file_data_off = pxi_le32(l3_hdr + 0x24);
    if (file_meta_len == 0U || file_meta_len > 8U * 1024U * 1024U) {
        pxi_game_close(g);
        return -7;
    }

    uint8_t *ftab = (uint8_t *)malloc(file_meta_len);
    if (ftab == NULL) {
        pxi_game_close(g);
        return -8;
    }
    if (pxi_raw_read(g, l3_off + (uint64_t)file_meta_off, ftab, file_meta_len) != 0) {
        if (g->last_read_rc < 0) {
            if (g->media == PXI_MEDIA_SD) *out_rc_sd = g->last_read_rc;
            else *out_rc_cart = g->last_read_rc;
        }
        free(ftab);
        pxi_game_close(g);
        return -9;
    }

    bool found_ie_a = false;
    uint32_t pos = 0U;
    while (pos + 0x20U <= file_meta_len) {
        uint32_t parent_off = pxi_le32(ftab + pos + 0x00U);
        uint64_t data_off   = pxi_le64(ftab + pos + 0x08U);
        uint64_t data_len   = pxi_le64(ftab + pos + 0x10U);
        uint32_t name_bytes = pxi_le32(ftab + pos + 0x1CU);
        if (pos + 0x20U + name_bytes > file_meta_len) {
            break;
        }
        if (parent_off == 0U && utf16le_equals_ie_a_fa(ftab + pos + 0x20U, name_bytes)) {
            g->ie_a_offset_in_pxi = l3_off + (uint64_t)file_data_off + data_off;
            g->ie_a_size = data_len;
            found_ie_a = true;
            break;
        }
        uint32_t step_name = (name_bytes + 3U) & ~3U;
        pos += 0x20U + step_name;
    }
    free(ftab);

    if (!found_ie_a || g->ie_a_size < 72ULL) {
        pxi_game_close(g);
        return -10;
    }

    uint8_t arc_magic[4];
    if (pxi_raw_read(g, g->ie_a_offset_in_pxi, arc_magic, 4U) != 0 ||
        pxi_le32(arc_magic) != 0x30435241U) { /* "ARC0" */
        if (g->last_read_rc < 0) {
            if (g->media == PXI_MEDIA_SD) *out_rc_sd = g->last_read_rc;
            else *out_rc_cart = g->last_read_rc;
        }
        pxi_game_close(g);
        return -11;
    }

    out_reader->ctx = g;
    out_reader->total_size = g->ie_a_size;
    out_reader->read_at = pxi_ie_a_read_at;
    return 0;
}
