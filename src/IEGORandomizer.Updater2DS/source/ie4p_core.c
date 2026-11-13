#include "ie4p_core.h"

#include <stdlib.h>
#include <string.h>

#define ARC0_MAGIC_LE 0x30435241U /* "ARC0" */
#define IE4P_OP_COPY_INSTALLED 0U
#define IE4P_OP_EMBEDDED_RAW   1U
#define IE4P_OP_EMBEDDED_LZ4   2U

#define STREAM_CHUNK_SIZE (256U * 1024U)

typedef struct {
    uint32_t file_offset_in_data;
    uint32_t file_size;
} arc0_slot_t;

static uint16_t read_u16_le(const uint8_t *p)
{
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t read_u32_le(const uint8_t *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static uint64_t read_u64_le(const uint8_t *p)
{
    return (uint64_t)read_u32_le(p) | ((uint64_t)read_u32_le(p + 4) << 32);
}

static void set_err(char *buf, size_t cap, const char *msg)
{
    if (buf != NULL && cap > 0) {
        snprintf(buf, cap, "%s", msg != NULL ? msg : "");
    }
}

static uint32_t crc32_table[256];
static int crc32_ready = 0;

static void init_crc32_table(void)
{
    if (crc32_ready) {
        return;
    }
    for (uint32_t i = 0; i < 256U; i++) {
        uint32_t c = i;
        for (int k = 0; k < 8; k++) {
            c = (c & 1U) ? (0xEDB88320U ^ (c >> 1)) : (c >> 1);
        }
        crc32_table[i] = c;
    }
    crc32_ready = 1;
}

static uint32_t crc32_update(uint32_t crc_inv, const uint8_t *data, size_t len)
{
    uint32_t c = crc_inv;
    for (size_t i = 0; i < len; i++) {
        c = crc32_table[(c ^ data[i]) & 0xFFU] ^ (c >> 8);
    }
    return c;
}

static int read_exact(FILE *f, void *buf, size_t len)
{
    uint8_t *p = (uint8_t *)buf;
    size_t done = 0;
    while (done < len) {
        size_t r = fread(p + done, 1, len - done, f);
        if (r == 0) {
            return -1;
        }
        done += r;
    }
    return 0;
}

static int write_exact(FILE *f, const void *buf, size_t len)
{
    const uint8_t *p = (const uint8_t *)buf;
    size_t done = 0;
    while (done < len) {
        size_t w = fwrite(p + done, 1, len - done, f);
        if (w == 0) {
            return -1;
        }
        done += w;
    }
    return 0;
}

/* Decompress Level-5 table container (method 0 = None, method 1 = LZ10). */
static ie4p_status_t decompress_level5_table(
    const uint8_t *src,
    size_t src_len,
    uint8_t **out_buf,
    size_t *out_len)
{
    if (src_len < 4) {
        return IE4P_ERR_LZ10;
    }

    uint32_t hdr = read_u32_le(src);
    uint32_t method = hdr & 0x7U;
    size_t dst_size = (size_t)(hdr >> 3);

    uint8_t *dst = (uint8_t *)malloc(dst_size > 0 ? dst_size : 1U);
    if (dst == NULL) {
        return IE4P_ERR_OOM;
    }

    if (method == 0U) {
        if (src_len - 4U < dst_size) {
            free(dst);
            return IE4P_ERR_LZ10;
        }
        memcpy(dst, src + 4U, dst_size);
        *out_buf = dst;
        *out_len = dst_size;
        return IE4P_OK;
    }

    if (method != 1U) {
        free(dst);
        return IE4P_ERR_LZ10;
    }

    size_t sp = 4U;
    size_t dp = 0U;
    uint8_t mask = 0U;
    uint8_t flag = 0U;

    while (sp < src_len && dp < dst_size) {
        if (mask == 0U) {
            flag = src[sp++];
            mask = 0x80U;
        }

        if ((flag & mask) == 0U) {
            if (sp >= src_len) {
                break;
            }
            dst[dp++] = src[sp++];
        } else {
            if (sp + 2U > src_len) {
                break;
            }
            uint32_t dat = ((uint32_t)src[sp] << 8) | (uint32_t)src[sp + 1U];
            sp += 2U;
            size_t disp = (size_t)(dat & 0x0FFFU) + 1U;
            size_t length = (size_t)(dat >> 12) + 3U;

            for (size_t i = 0; i < length && dp < dst_size; i++) {
                if (dp < disp) {
                    free(dst);
                    return IE4P_ERR_LZ10;
                }
                dst[dp] = dst[dp - disp];
                dp++;
            }
        }

        mask >>= 1;
    }

    if (dp != dst_size) {
        free(dst);
        return IE4P_ERR_LZ10;
    }

    *out_buf = dst;
    *out_len = dst_size;
    return IE4P_OK;
}

static ie4p_status_t decompress_lz4_raw(
    const uint8_t *src,
    size_t src_len,
    uint8_t *dst,
    size_t dst_len)
{
    if (dst_len == 0U) {
        return src_len == 0U ? IE4P_OK : IE4P_ERR_LZ4;
    }

    size_t sp = 0U;
    size_t dp = 0U;

    while (sp < src_len) {
        uint8_t token = src[sp++];
        size_t lit_len = (size_t)(token >> 4);
        if (lit_len == 15U) {
            uint8_t s;
            do {
                if (sp >= src_len) {
                    return IE4P_ERR_LZ4;
                }
                s = src[sp++];
                lit_len += s;
            } while (s == 255U);
        }

        if (sp + lit_len > src_len || dp + lit_len > dst_len) {
            return IE4P_ERR_LZ4;
        }

        memcpy(dst + dp, src + sp, lit_len);
        sp += lit_len;
        dp += lit_len;

        if (sp >= src_len) {
            break;
        }

        if (sp + 2U > src_len) {
            return IE4P_ERR_LZ4;
        }
        size_t offset = (size_t)read_u16_le(src + sp);
        sp += 2U;
        if (offset == 0U || offset > dp) {
            return IE4P_ERR_LZ4;
        }

        size_t match_len = (size_t)(token & 0x0FU) + 4U;
        if ((token & 0x0FU) == 15U) {
            uint8_t s;
            do {
                if (sp >= src_len) {
                    return IE4P_ERR_LZ4;
                }
                s = src[sp++];
                match_len += s;
            } while (s == 255U);
        }

        if (dp + match_len > dst_len) {
            return IE4P_ERR_LZ4;
        }

        size_t match_src = dp - offset;
        for (size_t i = 0; i < match_len; i++) {
            dst[dp++] = dst[match_src + i];
        }
    }

    return (dp == dst_len) ? IE4P_OK : IE4P_ERR_LZ4;
}

static ie4p_status_t load_installed_arc0_slots(
    FILE *installed_arc0,
    uint32_t *out_data_offset,
    arc0_slot_t **out_slots,
    uint32_t *out_slot_count,
    char *err_detail,
    size_t err_cap)
{
    uint8_t hdr[72];
    if (fseek(installed_arc0, 0L, SEEK_SET) != 0 || read_exact(installed_arc0, hdr, sizeof(hdr)) != 0) {
        set_err(err_detail, err_cap, "No se pudo leer la cabecera de ie_a.fa instalado.");
        return IE4P_ERR_IO_READ;
    }

    uint32_t magic = read_u32_le(hdr + 0x00);
    if (magic != ARC0_MAGIC_LE) {
        set_err(err_detail, err_cap, "ie_a.fa instalado no tiene cabecera ARC0 valida.");
        return IE4P_ERR_BAD_ARC0;
    }

    uint32_t file_entries_off = read_u32_le(hdr + 0x0C);
    uint32_t name_off         = read_u32_le(hdr + 0x10);
    uint32_t data_off         = read_u32_le(hdr + 0x14);
    uint32_t file_entries_cnt = read_u32_le(hdr + 0x1C);

    if (name_off <= file_entries_off || data_off < name_off || file_entries_cnt == 0U || file_entries_cnt > 200000U) {
        set_err(err_detail, err_cap, "Tabla FileEntries invalida en ie_a.fa instalado.");
        return IE4P_ERR_BAD_ARC0;
    }

    size_t comp_len = (size_t)(name_off - file_entries_off);
    uint8_t *comp_buf = (uint8_t *)malloc(comp_len);
    if (comp_buf == NULL) {
        set_err(err_detail, err_cap, "Memoria insuficiente al leer indice ARC0.");
        return IE4P_ERR_OOM;
    }

    if (fseek(installed_arc0, (long)file_entries_off, SEEK_SET) != 0 ||
        read_exact(installed_arc0, comp_buf, comp_len) != 0) {
        free(comp_buf);
        set_err(err_detail, err_cap, "Error leyendo tabla FileEntries del juego instalado.");
        return IE4P_ERR_IO_READ;
    }

    uint8_t *raw_buf = NULL;
    size_t raw_len = 0U;
    ie4p_status_t st = decompress_level5_table(comp_buf, comp_len, &raw_buf, &raw_len);
    free(comp_buf);
    if (st != IE4P_OK) {
        set_err(err_detail, err_cap, "Error descomprimiendo tabla LZ10 de ie_a.fa instalado.");
        return st;
    }

    if (raw_len < (size_t)file_entries_cnt * 16U) {
        free(raw_buf);
        set_err(err_detail, err_cap, "Tamano inesperado en FileEntries descomprimido.");
        return IE4P_ERR_BAD_ARC0;
    }

    arc0_slot_t *slots = (arc0_slot_t *)malloc((size_t)file_entries_cnt * sizeof(arc0_slot_t));
    if (slots == NULL) {
        free(raw_buf);
        set_err(err_detail, err_cap, "Memoria insuficiente para indice de archivos.");
        return IE4P_ERR_OOM;
    }

    for (uint32_t i = 0; i < file_entries_cnt; i++) {
        const uint8_t *fe = raw_buf + ((size_t)i * 16U);
        slots[i].file_offset_in_data = read_u32_le(fe + 0x08);
        slots[i].file_size           = read_u32_le(fe + 0x0C);
    }

    free(raw_buf);
    *out_data_offset = data_off;
    *out_slots = slots;
    *out_slot_count = file_entries_cnt;
    return IE4P_OK;
}

static int ensure_capacity(uint8_t **buf, size_t *cap, size_t needed)
{
    if (needed <= *cap) {
        return 0;
    }
    size_t next_cap = needed < 65536U ? 65536U : needed;
    uint8_t *nb = (uint8_t *)realloc(*buf, next_cap);
    if (nb == NULL) {
        return -1;
    }
    *buf = nb;
    *cap = next_cap;
    return 0;
}

ie4p_status_t ie4p_apply_patch_streams(
    FILE *installed_arc0,
    FILE *patch_pack,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap)
{
    if (installed_arc0 == NULL || patch_pack == NULL || out_arc0 == NULL) {
        set_err(err_detail, err_detail_cap, "Punteros de archivo nulos.");
        return IE4P_ERR_ARGS;
    }

    init_crc32_table();

    uint32_t installed_data_off = 0U;
    arc0_slot_t *slots = NULL;
    uint32_t slot_count = 0U;

    ie4p_status_t st = load_installed_arc0_slots(
        installed_arc0, &installed_data_off, &slots, &slot_count, err_detail, err_detail_cap);
    if (st != IE4P_OK) {
        return st;
    }

    uint8_t pack_hdr[48];
    if (fseek(patch_pack, 0L, SEEK_SET) != 0 || read_exact(patch_pack, pack_hdr, sizeof(pack_hdr)) != 0) {
        free(slots);
        set_err(err_detail, err_detail_cap, "No se pudo leer cabecera del parche IE4P.");
        return IE4P_ERR_IO_READ;
    }

    if (memcmp(pack_hdr, "IE4PV100", 8) != 0) {
        free(slots);
        set_err(err_detail, err_detail_cap, "Formato de parche IE4P no reconocido.");
        return IE4P_ERR_BAD_PACK;
    }

    uint64_t expected_final_size = read_u64_le(pack_hdr + 0x08);
    uint32_t pack_total_entries  = read_u32_le(pack_hdr + 0x10);
    uint32_t op_count            = read_u32_le(pack_hdr + 0x14);
    uint32_t prefix_raw_len      = read_u32_le(pack_hdr + 0x18);
    uint32_t prefix_stored_len   = read_u32_le(pack_hdr + 0x1C);
    uint32_t prefix_crc32        = read_u32_le(pack_hdr + 0x20);
    uint8_t  prefix_codec        = pack_hdr[0x24];

    if (pack_total_entries != slot_count) {
        free(slots);
        set_err(err_detail, err_detail_cap,
            "El juego instalado no coincide con Inazuma Eleven GO Luz EUR (distinto num. de entradas).");
        return IE4P_ERR_ENTRY_COUNT;
    }

    uint8_t *io_buf = (uint8_t *)malloc(STREAM_CHUNK_SIZE);
    uint8_t *comp_buf = NULL;
    size_t comp_cap = 0U;
    uint8_t *decomp_buf = NULL;
    size_t decomp_cap = 0U;
    uint8_t zero_pad[512];
    memset(zero_pad, 0, sizeof(zero_pad));

    if (io_buf == NULL) {
        free(slots);
        set_err(err_detail, err_detail_cap, "Memoria insuficiente para buffer de streaming.");
        return IE4P_ERR_OOM;
    }

    /* 1. Reconstruct ARC0 header + tables prefix */
    if (ensure_capacity(&comp_buf, &comp_cap, prefix_stored_len) != 0 ||
        ensure_capacity(&decomp_buf, &decomp_cap, prefix_raw_len) != 0) {
        st = IE4P_ERR_OOM;
        set_err(err_detail, err_detail_cap, "Memoria insuficiente al descomprimir cabecera ARC0.");
        goto cleanup;
    }

    if (read_exact(patch_pack, comp_buf, prefix_stored_len) != 0) {
        st = IE4P_ERR_IO_READ;
        set_err(err_detail, err_detail_cap, "Error leyendo cabecera ARC0 del parche.");
        goto cleanup;
    }

    const uint8_t *prefix_ptr = NULL;
    if (prefix_codec == IE4P_OP_EMBEDDED_LZ4) {
        st = decompress_lz4_raw(comp_buf, prefix_stored_len, decomp_buf, prefix_raw_len);
        if (st != IE4P_OK) {
            set_err(err_detail, err_detail_cap, "Error LZ4 en cabecera ARC0 del parche.");
            goto cleanup;
        }
        prefix_ptr = decomp_buf;
    } else if (prefix_codec == IE4P_OP_EMBEDDED_RAW && prefix_stored_len == prefix_raw_len) {
        prefix_ptr = comp_buf;
    } else {
        st = IE4P_ERR_BAD_PACK;
        set_err(err_detail, err_detail_cap, "Codec invalido en cabecera del parche.");
        goto cleanup;
    }

    uint32_t actual_prefix_crc = crc32_update(0xFFFFFFFFU, prefix_ptr, prefix_raw_len) ^ 0xFFFFFFFFU;
    if (actual_prefix_crc != prefix_crc32) {
        st = IE4P_ERR_CRC;
        set_err(err_detail, err_detail_cap, "CRC32 invalido en cabecera ARC0 del parche.");
        goto cleanup;
    }

    if (write_exact(out_arc0, prefix_ptr, prefix_raw_len) != 0) {
        st = IE4P_ERR_IO_WRITE;
        set_err(err_detail, err_detail_cap, "Error escribiendo en la tarjeta SD (espacio lleno?).");
        goto cleanup;
    }

    uint64_t bytes_written = prefix_raw_len;

    /* 2. Stream all operations */
    for (uint32_t op = 0; op < op_count; op++) {
        if ((op & 0x3FU) == 0U && progress_cb != NULL) {
            if (progress_cb(op, op_count, bytes_written, expected_final_size, user_data) != 0) {
                st = IE4P_ERR_ABORTED;
                set_err(err_detail, err_detail_cap, "Actualizacion cancelada por el usuario.");
                goto cleanup;
            }
        }

        uint8_t op_hdr[24];
        if (read_exact(patch_pack, op_hdr, sizeof(op_hdr)) != 0) {
            st = IE4P_ERR_IO_READ;
            set_err(err_detail, err_detail_cap, "Lectura incompleta del paquete de actualizacion.");
            goto cleanup;
        }

        uint8_t  op_type      = op_hdr[0];
        uint32_t file_idx     = read_u32_le(op_hdr + 0x04);
        uint32_t raw_len      = read_u32_le(op_hdr + 0x08);
        uint32_t stored_len   = read_u32_le(op_hdr + 0x0C);
        uint32_t pad_len      = read_u32_le(op_hdr + 0x10);
        uint32_t expected_crc = read_u32_le(op_hdr + 0x14);

        if (file_idx >= slot_count) {
            st = IE4P_ERR_BAD_PACK;
            set_err(err_detail, err_detail_cap, "Indice de archivo fuera de rango en parche.");
            goto cleanup;
        }

        uint32_t crc_state = 0xFFFFFFFFU;

        if (op_type == IE4P_OP_COPY_INSTALLED) {
            const arc0_slot_t *slot = &slots[file_idx];
            if (slot->file_size != raw_len) {
                st = IE4P_ERR_SIZE_MISMATCH;
                if (err_detail != NULL && err_detail_cap > 0) {
                    snprintf(err_detail, err_detail_cap,
                        "Tamano distinto en archivo base #%u (%u != %u).",
                        (unsigned)file_idx, (unsigned)slot->file_size, (unsigned)raw_len);
                }
                goto cleanup;
            }

            uint64_t abs_off = (uint64_t)installed_data_off + (uint64_t)slot->file_offset_in_data;
            if (abs_off > 0x7FFFFFFFULL || fseek(installed_arc0, (long)abs_off, SEEK_SET) != 0) {
                st = IE4P_ERR_IO_READ;
                set_err(err_detail, err_detail_cap, "Error de posicionamiento en ie_a.fa instalado.");
                goto cleanup;
            }

            uint32_t rem = raw_len;
            while (rem > 0U) {
                size_t chunk = rem > STREAM_CHUNK_SIZE ? STREAM_CHUNK_SIZE : (size_t)rem;
                if (read_exact(installed_arc0, io_buf, chunk) != 0) {
                    st = IE4P_ERR_IO_READ;
                    set_err(err_detail, err_detail_cap, "Error leyendo datos del juego instalado.");
                    goto cleanup;
                }
                crc_state = crc32_update(crc_state, io_buf, chunk);
                if (write_exact(out_arc0, io_buf, chunk) != 0) {
                    st = IE4P_ERR_IO_WRITE;
                    set_err(err_detail, err_detail_cap, "Error escribiendo en SD (revisa espacio libre ~900 MB).");
                    goto cleanup;
                }
                rem -= (uint32_t)chunk;
            }
        } else if (op_type == IE4P_OP_EMBEDDED_RAW) {
            if (stored_len != raw_len) {
                st = IE4P_ERR_BAD_PACK;
                set_err(err_detail, err_detail_cap, "Longitud inconsistente en bloque RAW.");
                goto cleanup;
            }

            uint32_t rem = raw_len;
            while (rem > 0U) {
                size_t chunk = rem > STREAM_CHUNK_SIZE ? STREAM_CHUNK_SIZE : (size_t)rem;
                if (read_exact(patch_pack, io_buf, chunk) != 0) {
                    st = IE4P_ERR_IO_READ;
                    set_err(err_detail, err_detail_cap, "Error leyendo bloque RAW del parche.");
                    goto cleanup;
                }
                crc_state = crc32_update(crc_state, io_buf, chunk);
                if (write_exact(out_arc0, io_buf, chunk) != 0) {
                    st = IE4P_ERR_IO_WRITE;
                    set_err(err_detail, err_detail_cap, "Error escribiendo en SD (revisa espacio libre ~900 MB).");
                    goto cleanup;
                }
                rem -= (uint32_t)chunk;
            }
        } else if (op_type == IE4P_OP_EMBEDDED_LZ4) {
            if (ensure_capacity(&comp_buf, &comp_cap, stored_len) != 0 ||
                ensure_capacity(&decomp_buf, &decomp_cap, raw_len) != 0) {
                st = IE4P_ERR_OOM;
                set_err(err_detail, err_detail_cap, "Memoria insuficiente en bloque LZ4.");
                goto cleanup;
            }

            if (read_exact(patch_pack, comp_buf, stored_len) != 0) {
                st = IE4P_ERR_IO_READ;
                set_err(err_detail, err_detail_cap, "Error leyendo bloque LZ4 del parche.");
                goto cleanup;
            }

            st = decompress_lz4_raw(comp_buf, stored_len, decomp_buf, raw_len);
            if (st != IE4P_OK) {
                set_err(err_detail, err_detail_cap, "Datos corruptos en bloque LZ4 del parche.");
                goto cleanup;
            }

            crc_state = crc32_update(crc_state, decomp_buf, raw_len);
            if (write_exact(out_arc0, decomp_buf, raw_len) != 0) {
                st = IE4P_ERR_IO_WRITE;
                set_err(err_detail, err_detail_cap, "Error escribiendo bloque actualizado en SD.");
                goto cleanup;
            }
        } else {
            st = IE4P_ERR_BAD_PACK;
            set_err(err_detail, err_detail_cap, "Opcode desconocido en paquete IE4P.");
            goto cleanup;
        }

        uint32_t actual_crc = crc_state ^ 0xFFFFFFFFU;
        if (actual_crc != expected_crc) {
            st = IE4P_ERR_CRC;
            if (err_detail != NULL && err_detail_cap > 0) {
                snprintf(err_detail, err_detail_cap,
                    "Fallo CRC32 en entrada #%u (op=%u): 0x%08X != 0x%08X.",
                    (unsigned)file_idx, (unsigned)op_type, (unsigned)actual_crc, (unsigned)expected_crc);
            }
            goto cleanup;
        }

        uint32_t rem_pad = pad_len;
        while (rem_pad > 0U) {
            size_t chunk = rem_pad > sizeof(zero_pad) ? sizeof(zero_pad) : (size_t)rem_pad;
            if (write_exact(out_arc0, zero_pad, chunk) != 0) {
                st = IE4P_ERR_IO_WRITE;
                set_err(err_detail, err_detail_cap, "Error escribiendo alineacion en SD.");
                goto cleanup;
            }
            rem_pad -= (uint32_t)chunk;
        }

        bytes_written += (uint64_t)raw_len + (uint64_t)pad_len;
    }

    if (fflush(out_arc0) != 0) {
        st = IE4P_ERR_IO_WRITE;
        set_err(err_detail, err_detail_cap, "Error al vaciar cache de escritura en SD.");
        goto cleanup;
    }

    if (bytes_written != expected_final_size) {
        st = IE4P_ERR_SIZE_MISMATCH;
        set_err(err_detail, err_detail_cap, "El tamano final de ie_a.fa no coincide con el esperado.");
        goto cleanup;
    }

    if (progress_cb != NULL) {
        progress_cb(op_count, op_count, bytes_written, expected_final_size, user_data);
    }

    st = IE4P_OK;

cleanup:
    free(decomp_buf);
    free(comp_buf);
    free(io_buf);
    free(slots);
    return st;
}

const char *ie4p_status_str(ie4p_status_t st)
{
    switch (st) {
        case IE4P_OK:                return "OK";
        case IE4P_ERR_ARGS:          return "IE4P_ERR_ARGS";
        case IE4P_ERR_IO_READ:       return "IE4P_ERR_IO_READ";
        case IE4P_ERR_IO_WRITE:      return "IE4P_ERR_IO_WRITE";
        case IE4P_ERR_BAD_ARC0:      return "IE4P_ERR_BAD_ARC0";
        case IE4P_ERR_BAD_PACK:      return "IE4P_ERR_BAD_PACK";
        case IE4P_ERR_ENTRY_COUNT:   return "IE4P_ERR_ENTRY_COUNT";
        case IE4P_ERR_OOM:           return "IE4P_ERR_OOM";
        case IE4P_ERR_LZ10:          return "IE4P_ERR_LZ10";
        case IE4P_ERR_LZ4:           return "IE4P_ERR_LZ4";
        case IE4P_ERR_CRC:           return "IE4P_ERR_CRC";
        case IE4P_ERR_SIZE_MISMATCH: return "IE4P_ERR_SIZE_MISMATCH";
        case IE4P_ERR_ABORTED:       return "IE4P_ERR_ABORTED";
        default:                     return "IE4P_ERR_UNKNOWN";
    }
}
