#ifdef __3DS__

#include <3ds.h>
#include <3ds/services/fspxi.h>
#include <3ds/applets/swkbd.h>
#include <malloc.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <errno.h>

#include "ie4p_core.h"
#include "ie4p_seed.h"

#define IEGO_LIGHT_EUR_TID  0x0004000000112F00ULL
#define IEGO_LIGHT_LOW_ID   0x00112F00U
#define IEGO_LIGHT_HIGH_ID  0x00040000U

#define LUMA_DIR_1     "sdmc:/luma"
#define LUMA_DIR_2     "sdmc:/luma/titles"
#define LUMA_DIR_3     "sdmc:/luma/titles/0004000000112F00"
#define LUMA_DIR_4     "sdmc:/luma/titles/0004000000112F00/romfs"
#define OUT_TMP_PATH   "sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa.tmp"
#define OUT_FIN_PATH   "sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa"
#define LAST_SEED_PATH "sdmc:/luma/titles/0004000000112F00/ultima_seed.txt"
#define LUMA_INI       "sdmc:/luma/config.ini"

#define REPAIR_PACK_PATH "romfs:/patch_repair.ie4p"
#define SEED_RECIPE_PATH "romfs:/seed_recipe.ie4r"

#define PXI_BOUNCE_SIZE  65536U

/* Provided by source/csvc.s (Luma3DS custom SVC 0xB0) */
extern Result svcControlService(u32 op, Handle *outHandle, const char *serviceName);

typedef struct {
    Handle pxi_handle;
    FSPXI_Archive pxi_arch;
    FSPXI_File pxi_file;
    FS_MediaType media;
    uint64_t ie_a_offset_in_pxi;
    uint64_t ie_a_size;
    uint8_t *bounce_buf;
    bool arch_open;
    bool file_open;
} pxi_game_ctx_t;

typedef struct {
    PrintConsole *top;
    PrintConsole *bottom;
    int last_pct;
} ui_ctx_t;

static uint32_t le32(const uint8_t *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static uint64_t le64(const uint8_t *p)
{
    return (uint64_t)le32(p) | ((uint64_t)le32(p + 4) << 32);
}

static int pxi_raw_read(pxi_game_ctx_t *g, uint64_t raw_off, void *dst, size_t len)
{
    uint8_t *out = (uint8_t *)dst;
    size_t done = 0U;
    while (done < len) {
        u32 chunk = (len - done) > PXI_BOUNCE_SIZE ? PXI_BOUNCE_SIZE : (u32)(len - done);
        u32 got = 0U;
        Result rc = FSPXI_ReadFile(g->pxi_handle, g->pxi_file, &got, raw_off + (uint64_t)done, g->bounce_buf, chunk);
        if (R_FAILED(rc) || got != chunk) {
            return -1;
        }
        memcpy(out + done, g->bounce_buf, chunk);
        done += chunk;
    }
    return 0;
}

static int pxi_ie_a_read_at(void *ctx, uint64_t offset, void *dst, size_t len)
{
    if (len == 0U) {
        return 0;
    }
    pxi_game_ctx_t *g = (pxi_game_ctx_t *)ctx;
    if (g == NULL || offset + (uint64_t)len > g->ie_a_size) {
        return -1;
    }
    return pxi_raw_read(g, g->ie_a_offset_in_pxi + offset, dst, len);
}

static void pxi_game_close(pxi_game_ctx_t *g)
{
    if (g == NULL) {
        return;
    }
    if (g->file_open) {
        FSPXI_CloseFile(g->pxi_handle, g->pxi_file);
        g->file_open = false;
    }
    if (g->arch_open) {
        FSPXI_CloseArchive(g->pxi_handle, g->pxi_arch);
        g->arch_open = false;
    }
    if (g->pxi_handle != 0) {
        svcCloseHandle(g->pxi_handle);
        g->pxi_handle = 0;
    }
    if (g->bounce_buf != NULL) {
        free(g->bounce_buf);
        g->bounce_buf = NULL;
    }
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

static int pxi_game_open(
    pxi_game_ctx_t *g,
    ie4p_reader_t *out_reader,
    Result *out_rc_svc,
    Result *out_rc_sd,
    Result *out_rc_cart)
{
    memset(g, 0, sizeof(*g));
    *out_rc_svc = 0;
    *out_rc_sd = 0;
    *out_rc_cart = 0;

    g->bounce_buf = (uint8_t *)memalign(16, PXI_BOUNCE_SIZE);
    if (g->bounce_buf == NULL) {
        return -1;
    }

    /* Steal PxiFS0 client session from ARM9 Process9 via Luma3DS custom SVC 0xB0 */
    Result rc = svcControlService(0U, &g->pxi_handle, "PxiFS0");
    *out_rc_svc = rc;
    if (R_FAILED(rc) || g->pxi_handle == 0) {
        pxi_game_close(g);
        return -2;
    }

    /* Try SD card title first (0004000000112F00), then Game Card if SD fails */
    u32 arch_sd[4] = { IEGO_LIGHT_LOW_ID, IEGO_LIGHT_HIGH_ID, (u32)MEDIATYPE_SD, 0U };
    FS_Path path_sd = { PATH_BINARY, sizeof(arch_sd), arch_sd };
    *out_rc_sd = FSPXI_OpenArchive(g->pxi_handle, &g->pxi_arch, ARCHIVE_SAVEDATA_AND_CONTENT, path_sd);
    if (R_SUCCEEDED(*out_rc_sd)) {
        g->arch_open = true;
        g->media = MEDIATYPE_SD;
    } else {
        u32 arch_gc[4] = { IEGO_LIGHT_LOW_ID, IEGO_LIGHT_HIGH_ID, (u32)MEDIATYPE_GAME_CARD, 0U };
        FS_Path path_gc = { PATH_BINARY, sizeof(arch_gc), arch_gc };
        *out_rc_cart = FSPXI_OpenArchive(g->pxi_handle, &g->pxi_arch, ARCHIVE_SAVEDATA_AND_CONTENT, path_gc);
        if (R_SUCCEEDED(*out_rc_cart)) {
            g->arch_open = true;
            g->media = MEDIATYPE_GAME_CARD;
        } else {
            pxi_game_close(g);
            return -3;
        }
    }

    /* Open raw NCCH RomFS stream (binary path 20 zero bytes => RomFS) */
    u32 romfs_bin[5] = { 0U, 0U, 0U, 0U, 0U };
    FS_Path romfs_path = { PATH_BINARY, sizeof(romfs_bin), romfs_bin };
    rc = FSPXI_OpenFile(g->pxi_handle, &g->pxi_file, g->pxi_arch, romfs_path, FS_OPEN_READ, 0U);
    if (R_FAILED(rc)) {
        if (g->media == MEDIATYPE_SD) *out_rc_sd = rc;
        else *out_rc_cart = rc;
        pxi_game_close(g);
        return -4;
    }
    g->file_open = true;

    /* Probe Level-3 RomFS header (raw PxiFS0 starts with IVFC at 0x0000 and Level-3 at 0x1000) */
    uint8_t ivfc_hdr[0x60];
    if (pxi_raw_read(g, 0ULL, ivfc_hdr, sizeof(ivfc_hdr)) != 0) {
        pxi_game_close(g);
        return -5;
    }

    uint64_t candidates[3] = { 0x1000ULL, 0ULL, 0x2000ULL };
    int cand_cnt = 2;
    if (le32(ivfc_hdr + 0x00) == 0x43465649U) { /* "IVFC" */
        uint32_t master_hash_sz = le32(ivfc_hdr + 0x08);
        uint64_t computed_l3 = ((uint64_t)(0x60U + master_hash_sz) + 0xFFFULL) & ~0xFFFULL;
        candidates[0] = computed_l3;
        candidates[1] = 0x1000ULL;
        candidates[2] = 0ULL;
        cand_cnt = 3;
    }

    uint64_t l3_off = 0ULL;
    uint8_t l3_hdr[0x28];
    bool found_l3 = false;
    for (int c = 0; c < cand_cnt; c++) {
        if (pxi_raw_read(g, candidates[c], l3_hdr, sizeof(l3_hdr)) == 0 &&
            le32(l3_hdr + 0x00) == 0x28U &&
            le32(l3_hdr + 0x04) == 0x28U) {
            l3_off = candidates[c];
            found_l3 = true;
            break;
        }
    }
    if (!found_l3) {
        pxi_game_close(g);
        return -6;
    }

    uint32_t file_meta_off = le32(l3_hdr + 0x1C);
    uint32_t file_meta_len = le32(l3_hdr + 0x20);
    uint32_t file_data_off = le32(l3_hdr + 0x24);
    if (file_meta_len == 0U || file_meta_len > 2U * 1024U * 1024U) {
        pxi_game_close(g);
        return -7;
    }

    uint8_t *ftab = (uint8_t *)malloc(file_meta_len);
    if (ftab == NULL) {
        pxi_game_close(g);
        return -8;
    }
    if (pxi_raw_read(g, l3_off + (uint64_t)file_meta_off, ftab, file_meta_len) != 0) {
        free(ftab);
        pxi_game_close(g);
        return -9;
    }

    bool found_ie_a = false;
    uint32_t pos = 0U;
    while (pos + 0x20U <= file_meta_len) {
        uint32_t parent_off = le32(ftab + pos + 0x00U);
        uint64_t data_off   = le64(ftab + pos + 0x08U);
        uint64_t data_len   = le64(ftab + pos + 0x10U);
        uint32_t name_bytes = le32(ftab + pos + 0x1CU);
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
        le32(arc_magic) != 0x30435241U) { /* "ARC0" */
        pxi_game_close(g);
        return -11;
    }

    out_reader->ctx = g;
    out_reader->total_size = g->ie_a_size;
    out_reader->read_at = pxi_ie_a_read_at;
    return 0;
}

static void mkdir_p_luma(void)
{
    mkdir(LUMA_DIR_1, 0777);
    mkdir(LUMA_DIR_2, 0777);
    mkdir(LUMA_DIR_3, 0777);
    mkdir(LUMA_DIR_4, 0777);
}

static void read_last_seed_label(char *buf, size_t cap)
{
    buf[0] = '\0';
    FILE *f = fopen(LAST_SEED_PATH, "rb");
    if (!f) {
        return;
    }
    size_t n = fread(buf, 1U, cap - 1U, f);
    fclose(f);
    buf[n] = '\0';
    for (size_t i = 0U; i < n; i++) {
        if (buf[i] == '\r' || buf[i] == '\n') {
            buf[i] = '\0';
            break;
        }
    }
}

static void save_last_seed_label(const char *label)
{
    mkdir_p_luma();
    FILE *f = fopen(LAST_SEED_PATH, "wb");
    if (!f) {
        return;
    }
    fprintf(f, "%s\n", label);
    fclose(f);
}

static void generate_random_seed_code(char *out, size_t cap)
{
    static const char alphabet[] = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    uint64_t x = (uint64_t)svcGetSystemTick() ^ ((uint64_t)osGetTime() * 0x9E3779B97F4A7C15ULL);
    char p1[5], p2[5];
    for (int i = 0; i < 4; i++) {
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        uint64_t r = x * 2685821657736338717ULL;
        p1[i] = alphabet[(r >> 32) & 31U];
        p2[i] = alphabet[(r >> 48) & 31U];
    }
    p1[4] = '\0';
    p2[4] = '\0';
    snprintf(out, cap, "LUZ-%s-%s", p1, p2);
}

static int ensure_luma_game_patching_enabled(void)
{
    FILE *f = fopen(LUMA_INI, "rb+");
    if (!f) {
        return 0;
    }
    if (fseek(f, 0, SEEK_END) != 0) {
        fclose(f);
        return 0;
    }
    long sz = ftell(f);
    if (sz <= 0 || sz > 65536L) {
        fclose(f);
        return 0;
    }
    rewind(f);

    char *buf = (char *)malloc((size_t)sz + 1U);
    if (!buf) {
        fclose(f);
        return 0;
    }
    size_t rd = fread(buf, 1, (size_t)sz, f);
    buf[rd] = '\0';

    const char *key = "enable_game_patching";
    char *pos = strstr(buf, key);
    if (!pos) {
        free(buf);
        fclose(f);
        return 0;
    }

    char *p = pos + strlen(key);
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '=') {
        free(buf);
        fclose(f);
        return 0;
    }
    p++;
    while (*p == ' ' || *p == '\t') p++;

    int result = 0;
    if (*p == '1') {
        result = 1;
    } else if (*p == '0') {
        long byte_offset = (long)(p - buf);
        if (fseek(f, byte_offset, SEEK_SET) == 0) {
            char one = '1';
            if (fwrite(&one, 1, 1, f) == 1 && fflush(f) == 0) {
                result = 2;
            }
        }
    }

    free(buf);
    fclose(f);
    return result;
}

static int on_patch_progress(
    uint32_t current_op,
    uint32_t total_ops,
    uint64_t bytes_written,
    uint64_t total_bytes,
    void *user_data)
{
    if (!aptMainLoop()) {
        return 1;
    }

    ui_ctx_t *ui = (ui_ctx_t *)user_data;
    int pct = total_bytes > 0ULL
        ? (int)((bytes_written * 100ULL) / total_bytes)
        : (total_ops > 0U ? (int)((current_op * 100U) / total_ops) : 0);
    if (pct > 100) {
        pct = 100;
    }

    if (pct != ui->last_pct || current_op == total_ops) {
        ui->last_pct = pct;
        consoleSelect(ui->top);

        char bar[26];
        int filled = (pct * 24) / 100;
        for (int i = 0; i < 24; i++) {
            bar[i] = (i < filled) ? '#' : '.';
        }
        bar[24] = '\0';

        unsigned mb_done = (unsigned)(bytes_written / (1024ULL * 1024ULL));
        unsigned mb_tot  = (unsigned)(total_bytes / (1024ULL * 1024ULL));

        printf("\x1b[19;2HProgreso: [%s] %3d%%   ", bar, pct);
        printf("\x1b[20;2HDatos:    %u MB / %u MB (%u/%u)   ",
               mb_done, mb_tot, (unsigned)current_op, (unsigned)total_ops);

        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    return 0;
}

static void draw_menu_screens(
    PrintConsole *topScreen,
    PrintConsole *bottomScreen,
    FS_MediaType media,
    uint64_t ie_a_bytes,
    const char *last_seed)
{
    consoleSelect(bottomScreen);
    consoleClear();
    printf("\x1b[36;1m=== PARCELA TECNICA v1.2.4 (TODO EN 1) ===\x1b[0m\n");
    printf("- Fix 0xC880448D (lector directo PxiFS0)\n");
    printf("- Fix crash Cap.1 Caballeros Templarios\n");
    printf("- Sprites 100%% coherentes (mapa/video)\n");
    printf("- Fix uniforme 903 Victor Blade\n");
    printf("- Pachangas Cap.1 ganables + tiro Nv.1\n");
    printf("- Items y textos espanol restaurados\n");
    printf("- Generador de seeds 'Todo Activado'\n");
    printf("  integrado en la propia 2DS sin PC!\n\n");
    if (last_seed[0] != '\0') {
        printf("\x1b[33;1mUltima seed en SD:\x1b[0m\n %s\n", last_seed);
    }

    consoleSelect(topScreen);
    consoleClear();
    printf("\x1b[36;1m================================================\n");
    printf(" INAZUMA ELEVEN GO: LUZ - RANDOMIZER 2DS v1.2.4\n");
    printf(" Reparador + Generador de Seeds en Consola\n");
    printf("================================================\x1b[0m\n");
    printf(" \x1b[32;1m[OK]\x1b[0m Juego detectado (%s, %u MB)\n\n",
           media == MEDIATYPE_SD ? "Tarjeta SD" : "Cartucho",
           (unsigned)(ie_a_bytes / (1024ULL * 1024ULL)));

    printf(" Elige que quieres hacer:\n\n");
    printf(" \x1b[32;1m[A]\x1b[0m REPARAR PARTIDA ACTUAL (Mantener mi Seed)\n");
    printf("     Arregla todos los bugs sin perder tu save.\n\n");
    printf(" \x1b[33;1m[X]\x1b[0m NUEVA SEED MANUAL (Teclado tactil 2DS)\n");
    printf("     Escribe la seed que quieras ('Todo Activado').\n\n");
    printf(" \x1b[35;1m[Y]\x1b[0m NUEVA SEED ALEATORIA (1 boton)\n");
    printf("     Genera una seed sorpresa al instante.\n\n");
    printf(" \x1b[37;1m[START]\x1b[0m Salir al Menu HOME\n");
}

int main(int argc, char **argv)
{
    (void)argc;
    (void)argv;

    gfxInitDefault();
    osSetSpeedupEnable(true);

    PrintConsole topScreen, bottomScreen;
    consoleInit(GFX_TOP, &topScreen);
    consoleInit(GFX_BOTTOM, &bottomScreen);

    Result rc_romfs = romfsInit();
    if (R_FAILED(rc_romfs)) {
        consoleSelect(&topScreen);
        printf("\x1b[31;1m[ERROR] No se pudo abrir RomFS interno.\n");
        printf("Codigo: 0x%08lX\x1b[0m\n", (unsigned long)rc_romfs);
        goto wait_exit;
    }

    pxi_game_ctx_t game_ctx;
    ie4p_reader_t game_reader;
    Result rc_svc = 0, rc_sd = 0, rc_cart = 0;
    int open_err = pxi_game_open(&game_ctx, &game_reader, &rc_svc, &rc_sd, &rc_cart);
    if (open_err != 0) {
        consoleSelect(&topScreen);
        printf("\x1b[31;1m[ERROR] No se pudo abrir Inazuma Eleven GO: Luz\n");
        printf("(TitleID 0004000000112F00) mediante PxiFS0.\x1b[0m\n\n");
        printf("Paso fallo: %d\n", open_err);
        printf("rc_svc0xB0: 0x%08lX\n", (unsigned long)rc_svc);
        printf("rc_pxi_sd : 0x%08lX\n", (unsigned long)rc_sd);
        printf("rc_pxi_gc : 0x%08lX\n", (unsigned long)rc_cart);
        romfsExit();
        goto wait_exit;
    }

    char last_seed[64];
    read_last_seed_label(last_seed, sizeof(last_seed));
    draw_menu_screens(&topScreen, &bottomScreen, game_ctx.media, game_ctx.ie_a_size, last_seed);

    int selected_mode = 0; /* 1 = repair, 2 = seed */
    char chosen_seed[64];
    chosen_seed[0] = '\0';

    while (aptMainLoop()) {
        hidScanInput();
        u32 kDown = hidKeysDown();

        if (kDown & KEY_START) {
            pxi_game_close(&game_ctx);
            romfsExit();
            gfxExit();
            return 0;
        }

        if (kDown & KEY_A) {
            selected_mode = 1;
            snprintf(chosen_seed, sizeof(chosen_seed), "%s", "PARTIDA-ACTUAL-REPARADA");
            break;
        }

        if (kDown & KEY_X) {
            SwkbdState swkbd;
            char input_buf[32];
            memset(input_buf, 0, sizeof(input_buf));
            swkbdInit(&swkbd, SWKBD_TYPE_NORMAL, 2, 24);
            swkbdSetValidation(&swkbd, SWKBD_NOTEMPTY_NOTBLANK, 0, 0);
            swkbdSetHintText(&swkbd, "Escribe tu seed (ej: DAVID2026)");
            if (last_seed[0] != '\0' && strcmp(last_seed, "PARTIDA-ACTUAL-REPARADA") != 0) {
                swkbdSetInitialText(&swkbd, last_seed);
            }
            SwkbdButton btn = swkbdInputText(&swkbd, input_buf, sizeof(input_buf));
            if (btn == SWKBD_BUTTON_RIGHT && input_buf[0] != '\0') {
                selected_mode = 2;
                snprintf(chosen_seed, sizeof(chosen_seed), "%s", input_buf);
                break;
            }
            draw_menu_screens(&topScreen, &bottomScreen, game_ctx.media, game_ctx.ie_a_size, last_seed);
        }

        if (kDown & KEY_Y) {
            char rnd_seed[32];
            generate_random_seed_code(rnd_seed, sizeof(rnd_seed));

            consoleSelect(&topScreen);
            consoleClear();
            printf("\x1b[35;1m=== NUEVA SEED ALEATORIA ('TODO ACTIVADO') ===\x1b[0m\n\n");
            printf(" Seed generada: \x1b[33;1m%s\x1b[0m\n\n", rnd_seed);
            printf(" Esto generara una randomizacion completa nueva\n");
            printf(" (jugadores, caras, tecnicas, espiritus, cofres,\n");
            printf(" entrenadores y uniformes) con todos los fixes.\n\n");
            printf(" Pulsa \x1b[32;1m[A]\x1b[0m para APLICAR esta nueva seed.\n");
            printf(" Pulsa \x1b[33;1m[B]\x1b[0m para volver al menu sin cambios.\n");

            bool confirmed = false;
            while (aptMainLoop()) {
                hidScanInput();
                u32 kd2 = hidKeysDown();
                if (kd2 & KEY_A) {
                    confirmed = true;
                    break;
                }
                if (kd2 & KEY_B) {
                    break;
                }
                gfxFlushBuffers();
                gfxSwapBuffers();
                gspWaitForVBlank();
            }
            if (confirmed) {
                selected_mode = 2;
                snprintf(chosen_seed, sizeof(chosen_seed), "%s", rnd_seed);
                break;
            }
            draw_menu_screens(&topScreen, &bottomScreen, game_ctx.media, game_ctx.ie_a_size, last_seed);
        }

        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    if (selected_mode == 0) {
        pxi_game_close(&game_ctx);
        romfsExit();
        gfxExit();
        return 0;
    }

    consoleSelect(&bottomScreen);
    printf("\n[1/4] Preparando carpetas Luma...\n");
    mkdir_p_luma();

    const char *src_pack_path = (selected_mode == 1) ? REPAIR_PACK_PATH : SEED_RECIPE_PATH;
    FILE *f_pack = fopen(src_pack_path, "rb");
    if (!f_pack) {
        consoleSelect(&topScreen);
        printf("\n\x1b[31;1m[ERROR] No se pudo abrir %s\x1b[0m\n", src_pack_path);
        pxi_game_close(&game_ctx);
        romfsExit();
        goto wait_exit;
    }

    remove(OUT_TMP_PATH);
    FILE *f_out = fopen(OUT_TMP_PATH, "wb");
    if (!f_out) {
        consoleSelect(&topScreen);
        printf("\n\x1b[31;1m[ERROR] No se pudo crear archivo en SD (%d)\x1b[0m\n", errno);
        fclose(f_pack);
        pxi_game_close(&game_ctx);
        romfsExit();
        goto wait_exit;
    }

    setvbuf(f_pack, NULL, _IOFBF, 131072);
    setvbuf(f_out,  NULL, _IOFBF, 262144);

    consoleSelect(&topScreen);
    consoleClear();
    printf("\x1b[36;1m================================================\n");
    if (selected_mode == 1) {
        printf(" REPARANDO PARTIDA ACTUAL (MANTENIENDO SEED)\n");
    } else {
        printf(" GENERANDO NUEVA SEED EN CONSOLA 2DS\n");
    }
    printf("================================================\x1b[0m\n\n");
    if (selected_mode == 2) {
        printf(" Seed activa : \x1b[33;1m%s\x1b[0m\n", chosen_seed);
        printf(" Modo        : Todo Activado + Todos los Fixes\n\n");
    } else {
        printf(" Modo        : Reparacion Quirurgica v1.2.4\n");
        printf(" Partida/Seed: 100%% conservadas\n\n");
    }
    printf(" Construyendo ie_a.fa en tarjeta SD...\n");
    printf(" (No apagues la consola, tarda ~2-4 min)\n");

    consoleSelect(&bottomScreen);
    printf("[2/4] Construyendo ie_a.fa...\n");

    ui_ctx_t ui = { &topScreen, &bottomScreen, -1 };
    char err_msg[160];
    err_msg[0] = '\0';

    ie4p_status_t st;
    if (selected_mode == 1) {
        st = ie4p_apply_patch_reader(
            &game_reader, f_pack, f_out, on_patch_progress, &ui, err_msg, sizeof(err_msg));
    } else {
        st = ie4p_apply_onconsole_seed_reader(
            &game_reader, f_pack, chosen_seed, f_out, on_patch_progress, &ui, err_msg, sizeof(err_msg));
    }

    FS_MediaType launch_media = game_ctx.media;
    fclose(f_out);
    fclose(f_pack);
    pxi_game_close(&game_ctx);
    romfsExit();

    if (st != IE4P_OK) {
        remove(OUT_TMP_PATH);
        consoleSelect(&topScreen);
        printf("\n\n\x1b[31;1m[ERROR] Fallo (%s):\n%s\x1b[0m\n",
               ie4p_status_str(st), err_msg);
        goto wait_exit;
    }

    consoleSelect(&bottomScreen);
    printf("[3/4] Activando ie_a.fa final...\n");
    remove(OUT_FIN_PATH);
    if (rename(OUT_TMP_PATH, OUT_FIN_PATH) != 0) {
        consoleSelect(&topScreen);
        printf("\n\n\x1b[31;1m[ERROR] No se pudo renombrar ie_a.fa en SD.\x1b[0m\n");
        goto wait_exit;
    }

    save_last_seed_label(chosen_seed);

    consoleSelect(&bottomScreen);
    printf("[4/4] Verificando Luma3DS config.ini...\n");
    int luma_state = ensure_luma_game_patching_enabled();
    printf("[OK] Proceso completado!\n");

    consoleSelect(&topScreen);
    printf("\x1b[22;1H\x1b[32;1m================================================\n");
    if (selected_mode == 1) {
        printf("  PARTIDA REPARADA CON EXITO! (v1.2.4)\n");
    } else {
        printf("  SEED '%s' LISTA PARA JUGAR!\n", chosen_seed);
    }
    printf("================================================\x1b[0m\n");

    if (luma_state == 2) {
        printf("\x1b[33;1m[AVISO]\x1b[0m Se activo 'enable_game_patching = 1'.\n");
        printf("Pulsa \x1b[32;1m[START]\x1b[0m para reiniciar tu 2DS y jugar!\n");
        while (aptMainLoop()) {
            hidScanInput();
            if (hidKeysDown() & KEY_START) {
                APT_HardwareResetAsync();
                break;
            }
            gfxFlushBuffers();
            gfxSwapBuffers();
            gspWaitForVBlank();
        }
        gfxExit();
        return 0;
    } else {
        printf("Pulsa \x1b[32;1m[A]\x1b[0m para iniciar Inazuma Eleven GO: Luz!\n");
        printf("Pulsa \x1b[33;1m[START]\x1b[0m para salir al Menu HOME.\n");
        while (aptMainLoop()) {
            hidScanInput();
            u32 kd = hidKeysDown();
            if (kd & KEY_A) {
                u8 param[0x300];
                u8 hmac[0x20];
                memset(param, 0, sizeof(param));
                memset(hmac, 0, sizeof(hmac));
                if (R_SUCCEEDED(APT_PrepareToDoApplicationJump(0, IEGO_LIGHT_EUR_TID, launch_media))) {
                    APT_DoApplicationJump(param, sizeof(param), hmac);
                    break;
                }
            }
            if (kd & KEY_START) {
                break;
            }
            gfxFlushBuffers();
            gfxSwapBuffers();
            gspWaitForVBlank();
        }
        gfxExit();
        return 0;
    }

wait_exit:
    consoleSelect(&topScreen);
    printf("\nPulsa [START] para salir.\n");
    while (aptMainLoop()) {
        hidScanInput();
        if (hidKeysDown() & KEY_START) {
            break;
        }
        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }
    gfxExit();
    return 0;
}

#endif /* __3DS__ */
