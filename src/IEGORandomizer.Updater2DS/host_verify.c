#define _POSIX_C_SOURCE 200809L
#define _FILE_OFFSET_BITS 64

#include "source/ie4p_core.h"
#include "source/ie4p_seed.h"
#include "source/pxi_romfs.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint32_t hv_le32(const uint8_t *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static void hv_wr32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFFU);
    p[1] = (uint8_t)((v >> 8) & 0xFFU);
    p[2] = (uint8_t)((v >> 16) & 0xFFU);
    p[3] = (uint8_t)((v >> 24) & 0xFFU);
}

static void hv_wr64(uint8_t *p, uint64_t v)
{
    hv_wr32(p, (uint32_t)(v & 0xFFFFFFFFULL));
    hv_wr32(p + 4, (uint32_t)(v >> 32));
}

static uint64_t hv_align64(uint64_t x)
{
    return (x + 0x3FULL) & ~0x3FULL;
}

/* =========================================================================
 * Strict Process9 PxiFS0 Hardware Protocol Simulator
 * Enforces:
 *   1. Archive 0x2345678A 16-byte path
 *   2. Title Access 36-byte (0x24) path with non-zero content-type mask 0x3F
 *      (returns 0xD9004676 on 20-byte path or zero mask, matching real 2DS!)
 *   3. 0x1000 (4 KB page) alignment on buffer pointer, offset, and length
 *      for every FSPXI_ReadFile call (matching Process9 PXI DMA on real 3DS!)
 * ========================================================================= */
typedef struct {
    FILE    *f_cia;
    uint64_t cia_romfs_off;
    uint64_t romfs_size;
    uint32_t installed_media;
    int      card_inserted;
    int      return_fake_seed;
    int      require_zero_seed;
    uint32_t active_arch_media;
    int      active_seed_nonzero;
    uint64_t read_calls;
} p9_sim_t;

static int32_t sim_steal_pxifs0(void *ctx, uint32_t *out_session)
{
    (void)ctx;
    *out_session = 0x50584930U;
    return 0;
}

static int32_t sim_get_title_seed(void *ctx, uint64_t tid, uint32_t out_seed[4])
{
    p9_sim_t *s = (p9_sim_t *)ctx;
    memset(out_seed, 0, 16U);
    if (tid != IEGO_LIGHT_EUR_TID || !s->return_fake_seed) {
        return PXI_RC_SEED_NOT_FOUND;
    }
    out_seed[0] = 0xDEADBEEFU;
    out_seed[1] = 0xCAFEBABEU;
    out_seed[2] = 0x12345678U;
    out_seed[3] = 0x87654321U;
    return 0;
}

static int sim_is_card_inserted(void *ctx)
{
    p9_sim_t *s = (p9_sim_t *)ctx;
    return s->card_inserted;
}

static int32_t sim_open_archive(
    void *ctx,
    uint32_t session,
    uint64_t *out_arch,
    uint32_t archive_id,
    const void *path_data,
    uint32_t path_size)
{
    p9_sim_t *s = (p9_sim_t *)ctx;
    if (session != 0x50584930U || archive_id != PXI_ARCHIVE_SAVEDATA_AND_CONTENT || path_size != 0x10U) {
        return (int32_t)0xD9004676;
    }
    const uint32_t *w = (const uint32_t *)path_data;
    if (w[0] != IEGO_LIGHT_LOW_ID || w[1] != IEGO_LIGHT_HIGH_ID || w[3] != 0U) {
        return (int32_t)0xC8804478;
    }
    s->active_arch_media = w[2];
    *out_arch = 0xA0000000ULL | (uint64_t)w[2];
    return 0;
}

static int32_t sim_open_file(
    void *ctx,
    uint32_t session,
    uint64_t *out_file,
    uint64_t arch,
    const void *path_data,
    uint32_t path_size,
    uint32_t flags,
    uint32_t attrs)
{
    p9_sim_t *s = (p9_sim_t *)ctx;
    (void)attrs;
    if (session != 0x50584930U || (arch & 0xFFFFFFFFFFFFFF00ULL) != 0xA0000000ULL) {
        return (int32_t)0xD9004676;
    }
    if (flags != PXI_FS_OPEN_READ) {
        return (int32_t)0xE0E046BE;
    }
    /* Exact check that triggered 0xD9004676 on real 2DS when path_size was 0x14 / mask was 0! */
    if (path_size != PXI_TITLE_ACCESS_PATH_BYTES) {
        return (int32_t)0xD9004676;
    }
    const uint32_t *w = (const uint32_t *)path_data;
    uint64_t allow_mask = (uint64_t)w[3] | ((uint64_t)w[4] << 32);
    if (w[0] != 0U || w[1] != 0U || w[2] != 0U || (allow_mask & 0x3FULL) != 0x3FULL) {
        return (int32_t)0xD9004676;
    }

    uint32_t arch_media = (uint32_t)(arch & 0xFFULL);
    if (arch_media != s->installed_media) {
        return (int32_t)0xC8804478;
    }

    s->active_seed_nonzero = ((w[5] | w[6] | w[7] | w[8]) != 0U) ? 1 : 0;
    *out_file = 0xF0000001ULL;
    return 0;
}

static int32_t sim_get_file_size(void *ctx, uint32_t session, uint64_t file, uint64_t *out_size)
{
    p9_sim_t *s = (p9_sim_t *)ctx;
    if (session != 0x50584930U || file != 0xF0000001ULL) {
        return -1;
    }
    *out_size = s->romfs_size;
    return 0;
}

static int32_t sim_read_file(
    void *ctx,
    uint32_t session,
    uint64_t file,
    uint32_t *out_got,
    uint64_t offset,
    void *buffer,
    uint32_t size)
{
    p9_sim_t *s = (p9_sim_t *)ctx;
    *out_got = 0U;
    if (session != 0x50584930U || file != 0xF0000001ULL) {
        return -1;
    }
    /* Strict Process9 PXI DMA page-alignment enforcement! */
    if ((((uintptr_t)buffer) & 0xFFFU) != 0U ||
        (offset & 0xFFFULL) != 0ULL ||
        (size == 0U) ||
        ((size & 0xFFFU) != 0U)) {
        fprintf(stderr,
            "P9_SIM VIOLATION: unaligned PXI DMA read (buf=%p, off=0x%llX, size=0x%X)\n",
            buffer, (unsigned long long)offset, (unsigned)size);
        return (int32_t)0xE0E046BE;
    }
    /* Simulate NCCH superblock hash mismatch (0xD9004587) if non-zero seed used on NoCrypto NCCH */
    if (s->require_zero_seed && s->active_seed_nonzero) {
        return (int32_t)0xD9004587;
    }
    if (offset >= s->romfs_size) {
        *out_got = 0U;
        return 0;
    }
    uint64_t avail = s->romfs_size - offset;
    uint32_t to_read = (avail < (uint64_t)size) ? (uint32_t)avail : size;
    if (fseeko(s->f_cia, (off_t)(s->cia_romfs_off + offset), SEEK_SET) != 0) {
        return -1;
    }
    size_t rd = fread(buffer, 1U, to_read, s->f_cia);
    *out_got = (uint32_t)rd;
    s->read_calls++;
    return 0;
}

static int32_t sim_close_file(void *ctx, uint32_t session, uint64_t file)
{
    (void)ctx;
    (void)session;
    (void)file;
    return 0;
}

static int32_t sim_close_archive(void *ctx, uint32_t session, uint64_t arch)
{
    (void)ctx;
    (void)session;
    (void)arch;
    return 0;
}

static void sim_close_session(void *ctx, uint32_t session)
{
    (void)ctx;
    (void)session;
}

static void sim_fill_hal(p9_sim_t *sim, pxi_hal_t *hal)
{
    memset(hal, 0, sizeof(*hal));
    hal->hal_ctx          = sim;
    hal->steal_pxifs0     = sim_steal_pxifs0;
    hal->get_title_seed   = sim_get_title_seed;
    hal->is_card_inserted = sim_is_card_inserted;
    hal->open_archive     = sim_open_archive;
    hal->open_file        = sim_open_file;
    hal->get_file_size    = sim_get_file_size;
    hal->read_file        = sim_read_file;
    hal->close_file       = sim_close_file;
    hal->close_archive    = sim_close_archive;
    hal->close_session    = sim_close_session;
}

static int locate_cia_romfs(FILE *f_cia, uint64_t *out_romfs_off, uint64_t *out_romfs_size)
{
    uint8_t cia_hdr[0x20];
    if (fseeko(f_cia, 0, SEEK_SET) != 0 || fread(cia_hdr, 1, sizeof(cia_hdr), f_cia) != sizeof(cia_hdr)) {
        return -1;
    }
    uint32_t hdr_sz  = hv_le32(cia_hdr + 0x00);
    uint32_t cert_sz = hv_le32(cia_hdr + 0x08);
    uint32_t tik_sz  = hv_le32(cia_hdr + 0x0C);
    uint32_t tmd_sz  = hv_le32(cia_hdr + 0x10);
    if (hdr_sz != 0x2020U) {
        return -2;
    }
    uint64_t cert_off    = hv_align64((uint64_t)hdr_sz);
    uint64_t tik_off     = hv_align64(cert_off + (uint64_t)cert_sz);
    uint64_t tmd_off     = hv_align64(tik_off + (uint64_t)tik_sz);
    uint64_t content_off = hv_align64(tmd_off + (uint64_t)tmd_sz);

    uint8_t ncch_hdr[0x200];
    if (fseeko(f_cia, (off_t)content_off, SEEK_SET) != 0 ||
        fread(ncch_hdr, 1, sizeof(ncch_hdr), f_cia) != sizeof(ncch_hdr)) {
        return -3;
    }
    if (hv_le32(ncch_hdr + 0x100) != 0x4843434EU) { /* "NCCH" */
        return -4;
    }
    uint64_t romfs_rel  = (uint64_t)hv_le32(ncch_hdr + 0x1B0) * 0x200ULL;
    uint64_t romfs_size = (uint64_t)hv_le32(ncch_hdr + 0x1B4) * 0x200ULL;
    if (romfs_rel == 0ULL || romfs_size < 0x2000ULL) {
        return -5;
    }
    *out_romfs_off  = content_off + romfs_rel;
    *out_romfs_size = romfs_size;
    return 0;
}

static int run_pxi_self_test(void)
{
    /* Build a synthetic 200 KB RomFS image (IVFC at 0x0000, Level-3 at 0x1000,
     * root file "ie_a.fa" spanning >128 KB + non-page-multiple tail to exercise:
     *   - 0x24 Title Access path + 0x3F content-type mask
     *   - 0x1000 PXI DMA page alignment across unaligned offsets & 128 KB window boundary
     *   - EOF partial-page read at end of RomFS
     *   - Seed -> zero-seed automatic retry on 0xD9004587
     *   - SD -> GameCard automatic fallback when card_inserted == 1)
     */
    const size_t ie_a_len = 150000U; /* crosses 128 KB window + unaligned size */
    const size_t l3_off   = 0x1000U;
    const size_t fdata_rel = 0x100U;
    const size_t total_sz = l3_off + fdata_rel + ie_a_len + 0x123U; /* unaligned RomFS tail */

    uint8_t *img = (uint8_t *)calloc(1U, total_sz);
    if (!img) {
        return 1;
    }

    /* IVFC header at 0x0000 */
    memcpy(img + 0x00, "IVFC", 4U);
    hv_wr32(img + 0x04, 0x00010000U);
    hv_wr32(img + 0x08, 0x40U); /* master hash size */
    hv_wr32(img + 0x4C, 12U);   /* block_size_log2 = 12 (0x1000) */

    /* Level-3 header at 0x1000 */
    uint8_t *l3 = img + l3_off;
    hv_wr32(l3 + 0x00, 0x28U);  /* header size */
    hv_wr32(l3 + 0x04, 0x28U);  /* dirHashOff */
    hv_wr32(l3 + 0x08, 0x04U);  /* dirHashLen */
    hv_wr32(l3 + 0x0C, 0x2CU);  /* dirMetaOff */
    hv_wr32(l3 + 0x10, 0x18U);  /* dirMetaLen */
    hv_wr32(l3 + 0x14, 0x44U);  /* fileHashOff */
    hv_wr32(l3 + 0x18, 0x04U);  /* fileHashLen */
    hv_wr32(l3 + 0x1C, 0x48U);  /* fileMetaOff */
    hv_wr32(l3 + 0x20, 0x30U);  /* fileMetaLen (1 root file entry "ie_a.fa") */
    hv_wr32(l3 + 0x24, (uint32_t)fdata_rel);

    /* File metadata entry at l3 + 0x48 */
    uint8_t *fe = l3 + 0x48U;
    hv_wr32(fe + 0x00, 0U);                 /* parent_off = 0 (root) */
    hv_wr32(fe + 0x04, 0xFFFFFFFFU);
    hv_wr64(fe + 0x08, 0ULL);               /* data_off = 0 */
    hv_wr64(fe + 0x10, (uint64_t)ie_a_len); /* data_len */
    hv_wr32(fe + 0x18, 0xFFFFFFFFU);
    hv_wr32(fe + 0x1C, 14U);                /* UTF-16LE("ie_a.fa") = 14 bytes */
    const char *nm = "ie_a.fa";
    for (int i = 0; i < 7; i++) {
        fe[0x20 + i * 2]     = (uint8_t)nm[i];
        fe[0x20 + i * 2 + 1] = 0U;
    }

    /* Fake ie_a.fa payload starting with "ARC0" + deterministic pattern */
    uint8_t *fa = l3 + fdata_rel;
    memcpy(fa, "ARC0", 4U);
    for (size_t i = 4U; i < ie_a_len; i++) {
        fa[i] = (uint8_t)((i * 131U + 17U) & 0xFFU);
    }

    FILE *tmp = tmpfile();
    if (!tmp || fwrite(img, 1U, total_sz, tmp) != total_sz) {
        free(img);
        if (tmp) fclose(tmp);
        return 1;
    }

    /* Case 1: SD NoCrypto + stale seed in SeedDB (tests 0xD9004587 auto-retry + 128 KB window + EOF) */
    p9_sim_t sim;
    memset(&sim, 0, sizeof(sim));
    sim.f_cia = tmp;
    sim.cia_romfs_off = 0ULL;
    sim.romfs_size = (uint64_t)total_sz;
    sim.installed_media = PXI_MEDIA_SD;
    sim.card_inserted = 0;
    sim.return_fake_seed = 1;
    sim.require_zero_seed = 1;

    pxi_hal_t hal;
    sim_fill_hal(&sim, &hal);

    pxi_game_ctx_t g;
    ie4p_reader_t reader;
    int32_t rc_svc = 0, rc_sd = 0, rc_gc = 0;
    int st = pxi_game_open_with_hal(&g, &hal, &reader, &rc_svc, &rc_sd, &rc_gc);
    if (st != 0 || g.media != PXI_MEDIA_SD || reader.total_size != ie_a_len) {
        fprintf(stderr, "Self-test Case 1 open failed: st=%d rc_sd=0x%08X\n", st, (unsigned)rc_sd);
        free(img);
        fclose(tmp);
        return 1;
    }

    /* Read unaligned slices across the entire 150 KB archive including the final byte */
    uint8_t *check = (uint8_t *)malloc(ie_a_len);
    size_t cur = 0U;
    size_t step = 37U;
    while (cur < ie_a_len) {
        size_t n = (ie_a_len - cur < step) ? (ie_a_len - cur) : step;
        if (reader.read_at(reader.ctx, (uint64_t)cur, check + cur, n) != 0) {
            fprintf(stderr, "Self-test Case 1 read failed at cur=%zu\n", cur);
            pxi_game_close(&g);
            free(check);
            free(img);
            fclose(tmp);
            return 1;
        }
        cur += n;
        step = (step * 3U + 19U) % 4099U + 1U;
    }
    if (memcmp(check, fa, ie_a_len) != 0) {
        fprintf(stderr, "Self-test Case 1 payload mismatch!\n");
        pxi_game_close(&g);
        free(check);
        free(img);
        fclose(tmp);
        return 1;
    }
    pxi_game_close(&g);
    free(check);

    /* Case 2: GameCard fallback when SD not installed and cartridge IS inserted */
    memset(&sim, 0, sizeof(sim));
    sim.f_cia = tmp;
    sim.cia_romfs_off = 0ULL;
    sim.romfs_size = (uint64_t)total_sz;
    sim.installed_media = PXI_MEDIA_GAMECARD;
    sim.card_inserted = 1;
    sim_fill_hal(&sim, &hal);

    st = pxi_game_open_with_hal(&g, &hal, &reader, &rc_svc, &rc_sd, &rc_gc);
    if (st != 0 || g.media != PXI_MEDIA_GAMECARD) {
        fprintf(stderr, "Self-test Case 2 GameCard fallback failed: st=%d\n", st);
        pxi_game_close(&g);
        free(img);
        fclose(tmp);
        return 1;
    }
    pxi_game_close(&g);

    free(img);
    fclose(tmp);
    printf("PxiFS0 Process9 hardware-protocol self-test PASSED (0x24 path + 0x3F mask + 0x1000 DMA + seed/cart fallbacks).\n");
    return 0;
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--pxi-self-test") == 0) {
        return run_pxi_self_test();
    }

    if (argc == 5 && strcmp(argv[1], "--pxi-cia") == 0) {
        FILE *f_cia = fopen(argv[2], "rb");
        if (!f_cia) {
            fprintf(stderr, "Cannot open installed .cia: %s\n", argv[2]);
            return 1;
        }
        uint64_t romfs_off = 0ULL, romfs_sz = 0ULL;
        int loc = locate_cia_romfs(f_cia, &romfs_off, &romfs_sz);
        if (loc != 0) {
            fprintf(stderr, "Failed to locate NCCH RomFS inside .cia (%d)\n", loc);
            fclose(f_cia);
            return 1;
        }

        p9_sim_t sim;
        memset(&sim, 0, sizeof(sim));
        sim.f_cia = f_cia;
        sim.cia_romfs_off = romfs_off;
        sim.romfs_size = romfs_sz;
        sim.installed_media = PXI_MEDIA_SD;
        sim.card_inserted = 0;
        sim.return_fake_seed = 0;
        sim.require_zero_seed = 1;

        pxi_hal_t hal;
        sim_fill_hal(&sim, &hal);

        pxi_game_ctx_t g;
        ie4p_reader_t reader;
        int32_t rc_svc = 0, rc_sd = 0, rc_gc = 0;
        int open_st = pxi_game_open_with_hal(&g, &hal, &reader, &rc_svc, &rc_sd, &rc_gc);
        if (open_st != 0) {
            fprintf(stderr,
                "Simulated PxiFS0 open failed: step=%d rc_svc=0x%08X rc_sd=0x%08X rc_gc=0x%08X\n",
                open_st, (unsigned)rc_svc, (unsigned)rc_sd, (unsigned)rc_gc);
            fclose(f_cia);
            return 1;
        }

        FILE *f_pack = fopen(argv[3], "rb");
        if (!f_pack) {
            fprintf(stderr, "Cannot open patch pack: %s\n", argv[3]);
            pxi_game_close(&g);
            fclose(f_cia);
            return 1;
        }
        FILE *f_out = fopen(argv[4], "wb");
        if (!f_out) {
            fprintf(stderr, "Cannot open output: %s\n", argv[4]);
            fclose(f_pack);
            pxi_game_close(&g);
            fclose(f_cia);
            return 1;
        }

        char err[256];
        err[0] = '\0';
        ie4p_status_t st = ie4p_apply_patch_reader(&reader, f_pack, f_out, NULL, NULL, err, sizeof(err));
        uint64_t pxi_reads = sim.read_calls;

        fclose(f_out);
        fclose(f_pack);
        pxi_game_close(&g);
        fclose(f_cia);

        if (st != IE4P_OK) {
            fprintf(stderr, "Simulated PxiFS0 patch failed (%s): %s\n", ie4p_status_str(st), err);
            return 1;
        }

        printf("PxiFS0 -> IE4P end-to-end CIA reconstruction succeeded (%llu aligned 4KB-DMA PXI reads): %s\n",
               (unsigned long long)pxi_reads, argv[4]);
        return 0;
    }

    if (argc == 6 && strcmp(argv[1], "--seed") == 0) {
        FILE *f_inst = fopen(argv[2], "rb");
        if (!f_inst) {
            fprintf(stderr, "Cannot open installed archive: %s\n", argv[2]);
            return 1;
        }
        FILE *f_rec = fopen(argv[3], "rb");
        if (!f_rec) {
            fclose(f_inst);
            fprintf(stderr, "Cannot open seed recipe: %s\n", argv[3]);
            return 1;
        }
        FILE *f_out = fopen(argv[5], "wb");
        if (!f_out) {
            fclose(f_rec);
            fclose(f_inst);
            fprintf(stderr, "Cannot create output archive: %s\n", argv[5]);
            return 1;
        }

        char err[256];
        err[0] = '\0';
        ie4p_status_t st = ie4p_apply_onconsole_seed_streams(
            f_inst, f_rec, argv[4], f_out, NULL, NULL, err, sizeof(err));

        fclose(f_out);
        fclose(f_rec);
        fclose(f_inst);

        if (st != IE4P_OK) {
            fprintf(stderr, "On-console seed failed (%s): %s\n", ie4p_status_str(st), err);
            return 1;
        }

        printf("IE4R C99 on-console seed '%s' succeeded: %s\n", argv[4], argv[5]);
        return 0;
    }

    if (argc != 4) {
        fprintf(stderr,
            "Usage:\n"
            "  %s --pxi-self-test\n"
            "  %s --pxi-cia <installed.cia> <patch.ie4p> <reconstructed_ie_a.fa>\n"
            "  %s <installed_ie_a.fa> <patch.ie4p> <reconstructed_ie_a.fa>\n"
            "  %s --seed <installed_ie_a.fa> <seed_recipe.ie4r> <seed_text> <output_ie_a.fa>\n",
            argv[0], argv[0], argv[0], argv[0]);
        return 2;
    }

    FILE *f_inst = fopen(argv[1], "rb");
    if (!f_inst) {
        fprintf(stderr, "Cannot open installed archive: %s\n", argv[1]);
        return 1;
    }

    FILE *f_pack = fopen(argv[2], "rb");
    if (!f_pack) {
        fclose(f_inst);
        fprintf(stderr, "Cannot open patch pack: %s\n", argv[2]);
        return 1;
    }

    FILE *f_out = fopen(argv[3], "wb");
    if (!f_out) {
        fclose(f_pack);
        fclose(f_inst);
        fprintf(stderr, "Cannot create output archive: %s\n", argv[3]);
        return 1;
    }

    char err[256];
    err[0] = '\0';
    ie4p_status_t st = ie4p_apply_patch_streams(f_inst, f_pack, f_out, NULL, NULL, err, sizeof(err));

    fclose(f_out);
    fclose(f_pack);
    fclose(f_inst);

    if (st != IE4P_OK) {
        fprintf(stderr, "Patch failed (%s): %s\n", ie4p_status_str(st), err);
        return 1;
    }

    printf("IE4P C engine reconstruction succeeded: %s\n", argv[3]);
    return 0;
}
