#ifdef __3DS__

#include <3ds.h>
#include <3ds/services/fspxi.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#include "ie4p_core.h"
#include "ie4p_seed.h"
#include "pxi_romfs.h"

/* Luma3DS / Rosalina custom SVC 0xB0 (ControlService): op 0 = STEAL_CLIENT_SESSION */
extern Result svcControlService(uint32_t op, Handle *out, const char *service_name);

#define LUMA_DIR_1       "sdmc:/luma"
#define LUMA_DIR_2       "sdmc:/luma/titles"
#define LUMA_DIR_3       "sdmc:/luma/titles/0004000000112F00"
#define LUMA_DIR_4       "sdmc:/luma/titles/0004000000112F00/romfs"
#define OUT_TMP_PATH     "sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa.tmp"
#define OUT_FIN_PATH     "sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa"
#define SEED_TAG_PATH    "sdmc:/luma/titles/0004000000112F00/ultima_seed.txt"
#define LUMA_INI         "sdmc:/luma/config.ini"

#define REPAIR_PACK_PATH "romfs:/patch_repair.ie4p"
#define SEED_RECIPE_PATH "romfs:/seed_recipe.ie4r"

typedef struct {
    PrintConsole *top;
    PrintConsole *bottom;
    int last_pct;
} ui_ctx_t;

static int32_t hal_3ds_steal_pxifs0(void *ctx, uint32_t *out_session)
{
    (void)ctx;
    Handle h = 0;
    Result rc = svcControlService(0U, &h, "PxiFS0");
    *out_session = (uint32_t)h;
    return (int32_t)rc;
}

static int32_t hal_3ds_get_title_seed(void *ctx, uint64_t tid, uint32_t out_seed[4])
{
    (void)ctx;
    memset(out_seed, 0, 16U);
    Handle *fs_h = fsGetSessionHandle();
    if (fs_h == NULL || *fs_h == 0) {
        return PXI_RC_SEED_NOT_FOUND;
    }
    u32 *cmd = getThreadCommandBuffer();
    cmd[0] = IPC_MakeHeader(0x087B, 2, 0); /* 0x087B0080 FSUSER:GetSeed */
    cmd[1] = (u32)(tid & 0xFFFFFFFFULL);
    cmd[2] = (u32)(tid >> 32);
    Result rc = svcSendSyncRequest(*fs_h);
    if (R_FAILED(rc)) {
        return (int32_t)rc;
    }
    rc = (Result)cmd[1];
    if (R_FAILED(rc)) {
        return (int32_t)rc;
    }
    memcpy(out_seed, &cmd[2], 16U);
    return 0;
}

static int hal_3ds_is_card_inserted(void *ctx)
{
    (void)ctx;
    bool inserted = false;
    if (R_SUCCEEDED(FSUSER_CardSlotIsInserted(&inserted)) && inserted) {
        return 1;
    }
    return 0;
}

static int32_t hal_3ds_open_archive(
    void *ctx,
    uint32_t session,
    uint64_t *out_arch,
    uint32_t archive_id,
    const void *path_data,
    uint32_t path_size)
{
    (void)ctx;
    FSPXI_Archive arch = 0ULL;
    FS_Path p = { PATH_BINARY, path_size, path_data };
    Result rc = FSPXI_OpenArchive((Handle)session, &arch, (FS_ArchiveID)archive_id, p);
    *out_arch = (uint64_t)arch;
    return (int32_t)rc;
}

static int32_t hal_3ds_open_file(
    void *ctx,
    uint32_t session,
    uint64_t *out_file,
    uint64_t arch,
    const void *path_data,
    uint32_t path_size,
    uint32_t flags,
    uint32_t attrs)
{
    (void)ctx;
    FSPXI_File f = 0ULL;
    FS_Path p = { PATH_BINARY, path_size, path_data };
    Result rc = FSPXI_OpenFile((Handle)session, &f, (FSPXI_Archive)arch, p, flags, attrs);
    *out_file = (uint64_t)f;
    return (int32_t)rc;
}

static int32_t hal_3ds_get_file_size(
    void *ctx,
    uint32_t session,
    uint64_t file,
    uint64_t *out_size)
{
    (void)ctx;
    u64 sz = 0ULL;
    Result rc = FSPXI_GetFileSize((Handle)session, (FSPXI_File)file, &sz);
    *out_size = (uint64_t)sz;
    return (int32_t)rc;
}

static int32_t hal_3ds_read_file(
    void *ctx,
    uint32_t session,
    uint64_t file,
    uint32_t *out_got,
    uint64_t offset,
    void *buffer,
    uint32_t size)
{
    (void)ctx;
    u32 got = 0U;
    Result rc = FSPXI_ReadFile((Handle)session, (FSPXI_File)file, &got, offset, buffer, size);
    *out_got = (uint32_t)got;
    return (int32_t)rc;
}

static int32_t hal_3ds_close_file(void *ctx, uint32_t session, uint64_t file)
{
    (void)ctx;
    return (int32_t)FSPXI_CloseFile((Handle)session, (FSPXI_File)file);
}

static int32_t hal_3ds_close_archive(void *ctx, uint32_t session, uint64_t arch)
{
    (void)ctx;
    return (int32_t)FSPXI_CloseArchive((Handle)session, (FSPXI_Archive)arch);
}

static void hal_3ds_close_session(void *ctx, uint32_t session)
{
    (void)ctx;
    if (session != 0U) {
        svcCloseHandle((Handle)session);
    }
}

static int pxi_game_open_3ds(
    pxi_game_ctx_t *g,
    ie4p_reader_t *out_reader,
    Result *out_rc_svc,
    Result *out_rc_sd,
    Result *out_rc_cart)
{
    pxi_hal_t hal;
    memset(&hal, 0, sizeof(hal));
    hal.steal_pxifs0     = hal_3ds_steal_pxifs0;
    hal.get_title_seed   = hal_3ds_get_title_seed;
    hal.is_card_inserted = hal_3ds_is_card_inserted;
    hal.open_archive     = hal_3ds_open_archive;
    hal.open_file        = hal_3ds_open_file;
    hal.get_file_size    = hal_3ds_get_file_size;
    hal.read_file        = hal_3ds_read_file;
    hal.close_file       = hal_3ds_close_file;
    hal.close_archive    = hal_3ds_close_archive;
    hal.close_session    = hal_3ds_close_session;

    int32_t rc_svc = 0, rc_sd = 0, rc_cart = 0;
    int st = pxi_game_open_with_hal(g, &hal, out_reader, &rc_svc, &rc_sd, &rc_cart);
    *out_rc_svc  = (Result)rc_svc;
    *out_rc_sd   = (Result)rc_sd;
    *out_rc_cart = (Result)rc_cart;
    return st;
}

static void mkdir_p_luma(void)
{
    mkdir(LUMA_DIR_1, 0777);
    mkdir(LUMA_DIR_2, 0777);
    mkdir(LUMA_DIR_3, 0777);
    mkdir(LUMA_DIR_4, 0777);
}

static void read_last_seed_label(char *out, size_t cap)
{
    out[0] = '\0';
    FILE *f = fopen(SEED_TAG_PATH, "rb");
    if (!f) {
        return;
    }
    size_t n = fread(out, 1, cap - 1U, f);
    fclose(f);
    out[n] = '\0';
    for (size_t i = 0; i < n; i++) {
        if (out[i] == '\r' || out[i] == '\n') {
            out[i] = '\0';
            break;
        }
    }
}

static void save_last_seed_label(const char *label)
{
    mkdir_p_luma();
    FILE *f = fopen(SEED_TAG_PATH, "wb");
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
    printf("\x1b[36;1m=== PARCELA TECNICA v1.2.6 (TODO EN 1) ===\x1b[0m\n");
    printf("- Fix 0xD9004676 (PxiFS0 0x24 + DMA 4KB)\n");
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
    printf(" INAZUMA ELEVEN GO: LUZ - RANDOMIZER 2DS v1.2.6\n");
    printf(" Reparador + Generador de Seeds en Consola\n");
    printf("================================================\x1b[0m\n");
    printf(" \x1b[32;1m[OK]\x1b[0m Juego detectado (%s, %u MB)\n\n",
           media == MEDIATYPE_SD ? "Tarjeta SD" : "Cartucho",
           (unsigned)(ie_a_bytes / (1024ULL * 1024ULL)));
    printf(" Elige que quieres hacer:\n\n");
    printf(" \x1b[32;1m[A]\x1b[0m REPARAR PARTIDA ACTUAL (Mantener mi Seed)\n");
    printf("     Repara la partida instalada; conserva tu save.\n\n");
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
    int open_err = pxi_game_open_3ds(&game_ctx, &game_reader, &rc_svc, &rc_sd, &rc_cart);
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

    FS_MediaType detected_media = (game_ctx.media == PXI_MEDIA_SD) ? MEDIATYPE_SD : MEDIATYPE_GAME_CARD;
    char last_seed[64];
    read_last_seed_label(last_seed, sizeof(last_seed));
    draw_menu_screens(&topScreen, &bottomScreen, detected_media, game_ctx.ie_a_size, last_seed);

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
            draw_menu_screens(&topScreen, &bottomScreen, detected_media, game_ctx.ie_a_size, last_seed);
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
            draw_menu_screens(&topScreen, &bottomScreen, detected_media, game_ctx.ie_a_size, last_seed);
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
        printf(" Modo        : Reparacion Quirurgica v1.2.6\n");
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

    FS_MediaType launch_media = detected_media;
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
        printf("  PARTIDA REPARADA CON EXITO! (v1.2.6)\n");
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
