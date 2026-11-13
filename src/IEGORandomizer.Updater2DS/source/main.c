#ifdef __3DS__

#include <3ds.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <errno.h>

#include "ie4p_core.h"

#define IEGO_LIGHT_EUR_TID 0x0004000000112F00ULL

#define LUMA_DIR_1   "sdmc:/luma"
#define LUMA_DIR_2   "sdmc:/luma/titles"
#define LUMA_DIR_3   "sdmc:/luma/titles/0004000000112F00"
#define LUMA_DIR_4   "sdmc:/luma/titles/0004000000112F00/romfs"
#define OUT_TMP_PATH "sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa.tmp"
#define OUT_FIN_PATH "sdmc:/luma/titles/0004000000112F00/romfs/ie_a.fa"
#define PACK_PATH    "romfs:/patch_v123.ie4p"
#define LUMA_INI     "sdmc:/luma/config.ini"

typedef struct {
    PrintConsole *top;
    PrintConsole *bottom;
    int last_pct;
} ui_ctx_t;

static void mkdir_p_luma(void)
{
    mkdir(LUMA_DIR_1, 0777);
    mkdir(LUMA_DIR_2, 0777);
    mkdir(LUMA_DIR_3, 0777);
    mkdir(LUMA_DIR_4, 0777);
}

/*
 * Returns:
 *   1 = already enabled ('enable_game_patching = 1')
 *   2 = was '0', automatically flipped to '1' (reboot needed so Luma reloads config.ini)
 *   0 = config.ini not found or key not present (old Luma < v11 using config.bin)
 */
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
    while (*p == ' ' || *p == '\t') {
        p++;
    }
    if (*p != '=') {
        free(buf);
        fclose(f);
        return 0;
    }
    p++;
    while (*p == ' ' || *p == '\t') {
        p++;
    }

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
    int pct = total_bytes > 0
        ? (int)((bytes_written * 100ULL) / total_bytes)
        : (total_ops > 0 ? (int)((current_op * 100U) / total_ops) : 0);
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

        printf("\x1b[14;2HProgreso: [%s] %3d%%   ", bar, pct);
        printf("\x1b[15;2HDatos:    %u MB / %u MB (%u/%u archivos)   ",
               mb_done, mb_tot, (unsigned)current_op, (unsigned)total_ops);

        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    return 0;
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

    consoleSelect(&bottomScreen);
    printf("\x1b[36;1m=== REGISTRO TECNICO v1.2.3 ===\x1b[0m\n");
    printf("- Fix crash post-formacion Cap.1\n");
    printf("  (Caballeros Templarios btl0002)\n");
    printf("- Coherencia total sprites:\n");
    printf("  cinematicas + mapa + partido\n");
    printf("- Fix uniforme 903 Victor Blade\n");
    printf("- Partida guardada 100%% intacta\n\n");

    consoleSelect(&topScreen);
    printf("\x1b[36;1m================================================\n");
    printf(" INAZUMA ELEVEN GO: LUZ - ACTUALIZADOR v1.2.3\n");
    printf(" Parche Ligero Independiente para Nintendo 2DS\n");
    printf("================================================\x1b[0m\n\n");
    printf(" Tu juego de 3 GB y tu partida guardada NO se\n");
    printf(" borran ni se reescriben enteros.\n\n");

    Result rc_romfs = romfsInit();
    if (R_FAILED(rc_romfs)) {
        printf("\x1b[31;1m[ERROR] No se pudo abrir RomFS del actualizador\n");
        printf("Codigo: 0x%08lX\x1b[0m\n", (unsigned long)rc_romfs);
        goto wait_exit;
    }

    FS_MediaType game_media = MEDIATYPE_SD;
    Result rc_game = romfsMountFromTitle(IEGO_LIGHT_EUR_TID, MEDIATYPE_SD, "ieri");
    if (R_FAILED(rc_game)) {
        rc_game = romfsMountFromTitle(IEGO_LIGHT_EUR_TID, MEDIATYPE_GAME_CARD, "ieri");
        game_media = MEDIATYPE_GAME_CARD;
    }

    if (R_FAILED(rc_game)) {
        printf("\x1b[31;1m[ERROR] No se encontro Inazuma Eleven GO: Luz\n");
        printf("(0004000000112F00) instalado en esta 2DS.\n");
        printf("Codigo: 0x%08lX\x1b[0m\n", (unsigned long)rc_game);
        romfsExit();
        goto wait_exit;
    }

    printf("\x1b[32;1m[OK]\x1b[0m Juego base detectado (%s).\n",
           game_media == MEDIATYPE_SD ? "Tarjeta SD" : "Cartucho");
    printf("Pulsa \x1b[33;1m[A]\x1b[0m para aplicar la actualizacion v1.2.3\n");
    printf("o \x1b[33;1m[START]\x1b[0m para cancelar y salir.\n");

    bool start_patch = false;
    while (aptMainLoop()) {
        hidScanInput();
        u32 kDown = hidKeysDown();
        if (kDown & KEY_A) {
            start_patch = true;
            break;
        }
        if (kDown & KEY_START) {
            romfsUnmount("ieri");
            romfsExit();
            gfxExit();
            return 0;
        }
        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    if (!start_patch) {
        romfsUnmount("ieri");
        romfsExit();
        gfxExit();
        return 0;
    }

    consoleSelect(&bottomScreen);
    printf("[1/4] Preparando carpetas Luma...\n");
    mkdir_p_luma();

    FILE *f_inst = fopen("ieri:/ie_a.fa", "rb");
    FILE *f_pack = fopen(PACK_PATH, "rb");
    if (!f_inst || !f_pack) {
        consoleSelect(&topScreen);
        printf("\n\x1b[31;1m[ERROR] No se pudo abrir ie_a.fa o patch_v123.ie4p\x1b[0m\n");
        if (f_inst) fclose(f_inst);
        if (f_pack) fclose(f_pack);
        romfsUnmount("ieri");
        romfsExit();
        goto wait_exit;
    }

    remove(OUT_TMP_PATH);
    FILE *f_out = fopen(OUT_TMP_PATH, "wb");
    if (!f_out) {
        consoleSelect(&topScreen);
        printf("\n\x1b[31;1m[ERROR] No se pudo crear archivo en SD (%d)\x1b[0m\n", errno);
        fclose(f_inst);
        fclose(f_pack);
        romfsUnmount("ieri");
        romfsExit();
        goto wait_exit;
    }

    /* Use 128 KB stdio buffers for fast SD throughput on Old 2DS */
    setvbuf(f_inst, NULL, _IOFBF, 131072);
    setvbuf(f_pack, NULL, _IOFBF, 131072);
    setvbuf(f_out,  NULL, _IOFBF, 131072);

    consoleSelect(&bottomScreen);
    printf("[2/4] Aplicando delta v1.2.3...\n");

    consoleSelect(&topScreen);
    printf("\nAplicando parche v1.2.3 (no apagues la 2DS)...\n");

    ui_ctx_t ui = { &topScreen, &bottomScreen, -1 };
    char err_msg[160];
    err_msg[0] = '\0';

    ie4p_status_t st = ie4p_apply_patch_streams(
        f_inst, f_pack, f_out, on_patch_progress, &ui, err_msg, sizeof(err_msg));

    fclose(f_out);
    fclose(f_pack);
    fclose(f_inst);
    romfsUnmount("ieri");
    romfsExit();

    if (st != IE4P_OK) {
        remove(OUT_TMP_PATH);
        consoleSelect(&topScreen);
        printf("\n\n\x1b[31;1m[ERROR] Fallo al aplicar parche (%s):\n%s\x1b[0m\n",
               ie4p_status_str(st), err_msg);
        goto wait_exit;
    }

    consoleSelect(&bottomScreen);
    printf("[3/4] Activando archivo final...\n");
    remove(OUT_FIN_PATH);
    if (rename(OUT_TMP_PATH, OUT_FIN_PATH) != 0) {
        consoleSelect(&topScreen);
        printf("\n\n\x1b[31;1m[ERROR] No se pudo renombrar ie_a.fa en SD.\x1b[0m\n");
        goto wait_exit;
    }

    consoleSelect(&bottomScreen);
    printf("[4/4] Verificando Luma3DS config.ini...\n");
    int luma_state = ensure_luma_game_patching_enabled();
    printf("[OK] Actualizacion completada!\n");

    consoleSelect(&topScreen);
    printf("\n\n\x1b[32;1m================================================\n");
    printf("   ACTUALIZACION v1.2.3 APLICADA CON EXITO!\n");
    printf("================================================\x1b[0m\n");

    if (luma_state == 2) {
        printf("\x1b[33;1m[AVISO]\x1b[0m Se activo automaticamente\n");
        printf("'enable_game_patching = 1' en Luma3DS.\n");
        printf("Pulsa \x1b[32;1m[START]\x1b[0m para reiniciar tu 2DS y jugar!\n");

        while (aptMainLoop()) {
            hidScanInput();
            u32 kDown = hidKeysDown();
            if (kDown & KEY_START) {
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
        if (luma_state == 0) {
            printf("\x1b[33;1mNota:\x1b[0m Si tienes Luma antiguo, verifica tener\n");
            printf("marcado 'Enable game patching' (SELECT al encender).\n");
        }
        printf("Pulsa \x1b[32;1m[A]\x1b[0m para iniciar Inazuma Eleven GO Luz!\n");
        printf("Pulsa \x1b[33;1m[START]\x1b[0m para salir al Menu HOME.\n");

        while (aptMainLoop()) {
            hidScanInput();
            u32 kDown = hidKeysDown();
            if (kDown & KEY_A) {
                u8 param[0x300];
                u8 hmac[0x20];
                memset(param, 0, sizeof(param));
                memset(hmac, 0, sizeof(hmac));
                if (R_SUCCEEDED(APT_PrepareToDoApplicationJump(0, IEGO_LIGHT_EUR_TID, game_media))) {
                    APT_DoApplicationJump(param, sizeof(param), hmac);
                    break;
                }
            }
            if (kDown & KEY_START) {
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
