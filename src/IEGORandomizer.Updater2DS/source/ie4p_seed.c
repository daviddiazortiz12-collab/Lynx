#include "ie4p_seed.h"

#include <ctype.h>
#include <stdlib.h>
#include <string.h>

#define IE4R_SRC_INSTALLED_SLOT   0U
#define IE4R_SRC_EMBEDDED_BLOB    1U
#define IE4R_SRC_MUTABLE_TEMPLATE 2U

#define PFLAG_SWAPPABLE        (1U << 0)
#define PFLAG_PROTECTED_STORY  (1U << 1)
#define PFLAG_PROTECTED_AVATAR (1U << 2)
#define PFLAG_IS_ARION         (1U << 3)
#define PFLAG_IS_JP            (1U << 4)

#define SEED_STREAM_CHUNK (256U * 1024U)

static uint32_t rd_u32(const uint8_t *p)
{
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static int32_t rd_i32(const uint8_t *p)
{
    return (int32_t)rd_u32(p);
}

static void wr_u32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFFU);
    p[1] = (uint8_t)((v >> 8) & 0xFFU);
    p[2] = (uint8_t)((v >> 16) & 0xFFU);
    p[3] = (uint8_t)((v >> 24) & 0xFFU);
}

static void wr_i32(uint8_t *p, int32_t v)
{
    wr_u32(p, (uint32_t)v);
}

static void set_seed_err(char *buf, size_t cap, const char *msg)
{
    if (buf != NULL && cap > 0U) {
        snprintf(buf, cap, "%s", msg != NULL ? msg : "");
    }
}

static int f_read_exact(FILE *f, void *dst, size_t len)
{
    uint8_t *p = (uint8_t *)dst;
    size_t done = 0U;
    while (done < len) {
        size_t r = fread(p + done, 1U, len - done, f);
        if (r == 0U) {
            return -1;
        }
        done += r;
    }
    return 0;
}

static int f_write_exact(FILE *f, const void *src, size_t len)
{
    const uint8_t *p = (const uint8_t *)src;
    size_t done = 0U;
    while (done < len) {
        size_t w = fwrite(p + done, 1U, len - done, f);
        if (w == 0U) {
            return -1;
        }
        done += w;
    }
    return 0;
}

static int f_write_zeros(FILE *f, size_t len)
{
    uint8_t z[256];
    memset(z, 0, sizeof(z));
    while (len > 0U) {
        size_t c = len > sizeof(z) ? sizeof(z) : len;
        if (f_write_exact(f, z, c) != 0) {
            return -1;
        }
        len -= c;
    }
    return 0;
}

/* ---------------- SplitMix64 Deterministic RNG ---------------- */

typedef struct {
    uint64_t state;
} sm64_rng_t;

static void rng_seed(sm64_rng_t *rng, const char *seed_str, const char *subsystem)
{
    uint64_t h = 14695981039346656037ULL; /* FNV-1a 64-bit */
    if (seed_str != NULL) {
        for (const char *p = seed_str; *p != '\0'; p++) {
            unsigned char ch = (unsigned char)*p;
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') {
                continue;
            }
            if (ch >= 'a' && ch <= 'z') {
                ch = (unsigned char)(ch - 'a' + 'A');
            }
            h ^= (uint64_t)ch;
            h *= 1099511628211ULL;
        }
    }
    h ^= (uint64_t)':';
    h *= 1099511628211ULL;
    if (subsystem != NULL) {
        for (const char *p = subsystem; *p != '\0'; p++) {
            h ^= (uint64_t)(unsigned char)*p;
            h *= 1099511628211ULL;
        }
    }
    rng->state = (h == 0ULL) ? 0x9E3779B97F4A7C15ULL : h;
}

static uint64_t rng_next_u64(sm64_rng_t *rng)
{
    uint64_t z = (rng->state += 0x9E3779B97F4A7C15ULL);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

static uint32_t rng_bound(sm64_rng_t *rng, uint32_t bound)
{
    if (bound <= 1U) {
        return 0U;
    }
    uint32_t threshold = (uint32_t)(0ULL - (uint64_t)bound) % bound;
    for (;;) {
        uint32_t r = (uint32_t)(rng_next_u64(rng) >> 32);
        if (r >= threshold) {
            return r % bound;
        }
    }
}

static int32_t rng_range_incl(sm64_rng_t *rng, int32_t lo, int32_t hi)
{
    if (hi <= lo) {
        return lo;
    }
    uint32_t span = (uint32_t)((int64_t)hi - (int64_t)lo + 1LL);
    return lo + (int32_t)rng_bound(rng, span);
}

static void build_derangement(uint32_t *perm, uint32_t n, sm64_rng_t *rng)
{
    for (uint32_t i = 0U; i < n; i++) {
        perm[i] = i;
    }
    if (n <= 1U) {
        return;
    }
    /* Sattolo's algorithm produces a uniform cyclic derangement (perm[i] != i for all i)! */
    for (uint32_t i = n - 1U; i > 0U; i--) {
        uint32_t j = rng_bound(rng, i); /* 0 <= j < i */
        uint32_t tmp = perm[i];
        perm[i] = perm[j];
        perm[j] = tmp;
    }
}

/* ---------------- Binary Cursor Reader ---------------- */

typedef struct {
    uint8_t *base;
    size_t len;
    size_t pos;
} buf_cursor_t;

static int cur_u32(buf_cursor_t *c, uint32_t *out)
{
    if (c->pos + 4U > c->len) {
        return -1;
    }
    *out = rd_u32(c->base + c->pos);
    c->pos += 4U;
    return 0;
}

static int cur_bytes(buf_cursor_t *c, uint8_t **out_ptr, size_t n)
{
    if (c->pos + n > c->len) {
        return -1;
    }
    *out_ptr = c->base + c->pos;
    c->pos += n;
    return 0;
}

static int cur_blob(buf_cursor_t *c, uint8_t **out_ptr, uint32_t *out_len)
{
    uint32_t blen = 0U;
    if (cur_u32(c, &blen) != 0) {
        return -1;
    }
    size_t aligned = ((size_t)blen + 3U) & ~(size_t)3U;
    if (c->pos + aligned > c->len) {
        return -1;
    }
    *out_ptr = c->base + c->pos;
    *out_len = blen;
    c->pos += aligned;
    return 0;
}

typedef struct {
    const uint8_t *ptr;
    uint32_t count;
} int_pool_view_t;

static int cur_int_pool(buf_cursor_t *c, int_pool_view_t *out)
{
    uint32_t cnt = 0U;
    if (cur_u32(c, &cnt) != 0) {
        return -1;
    }
    size_t byte_len = (size_t)cnt * 4U;
    if (c->pos + byte_len > c->len) {
        return -1;
    }
    out->ptr = c->base + c->pos;
    out->count = cnt;
    c->pos += byte_len;
    return 0;
}

static int32_t pool_at(const int_pool_view_t *p, uint32_t idx)
{
    return rd_i32(p->ptr + ((size_t)idx * 4U));
}

static int32_t pool_pick(const int_pool_view_t *p, sm64_rng_t *rng, int32_t fallback)
{
    if (p->count == 0U) {
        return fallback;
    }
    return pool_at(p, rng_bound(rng, p->count));
}

static int32_t pool_pick_diff(const int_pool_view_t *p, int32_t cur_val, sm64_rng_t *rng)
{
    if (p->count == 0U) {
        return cur_val;
    }
    if (p->count == 1U) {
        return pool_at(p, 0U);
    }
    for (int tries = 0; tries < 8; tries++) {
        int32_t cand = pool_at(p, rng_bound(rng, p->count));
        if (cand != cur_val) {
            return cand;
        }
    }
    for (uint32_t i = 0; i < p->count; i++) {
        int32_t cand = pool_at(p, i);
        if (cand != cur_val) return cand;
    }
    return cur_val;
}

typedef struct {
    uint8_t *bytes;
    uint32_t len;
    uint32_t arc_file_idx;
} tpl_view_t;

static int patch_tpl_i32(tpl_view_t *tpls, uint32_t tpl_cnt, uint32_t tpl_idx, uint32_t off, int32_t val)
{
    if (off == 0xFFFFFFFFU) {
        return 0;
    }
    if (tpl_idx >= tpl_cnt || tpls[tpl_idx].len < 4U || off > tpls[tpl_idx].len - 4U) {
        return -1;
    }
    wr_i32(tpls[tpl_idx].bytes + off, val);
    return 0;
}

typedef struct {
    int32_t param_hash;
    int32_t base_hash;
    int32_t body;
    int32_t gender;
    int32_t orig_pos;
    uint32_t flags;
    int32_t move_off;
    int32_t move_cnt;
    int32_t fp, tp, kick, dribble, tech, block, speed, stamina, catch_stat, luck, freedom;
    int32_t avatar_hash;
    uint32_t offs[14];
    int32_t mapped_param_hash;
    uint8_t is_11v11_gk;
    uint8_t is_pachanga_goal;
    uint8_t has_final_avatar;
} player_work_t;

static int find_player_by_param(const player_work_t *arr, uint32_t cnt, int32_t param_hash)
{
    for (uint32_t i = 0U; i < cnt; i++) {
        if (arr[i].param_hash == param_hash) {
            return (int)i;
        }
    }
    return -1;
}

static int find_player_by_base(const player_work_t *arr, uint32_t cnt, int32_t base_hash)
{
    for (uint32_t i = 0U; i < cnt; i++) {
        if (arr[i].base_hash == base_hash) {
            return (int)i;
        }
    }
    return -1;
}

static int32_t scale_clamp(int32_t base_val, int32_t min_pct, int32_t max_pct, int32_t lo, int32_t hi, sm64_rng_t *rng)
{
    int32_t pct = rng_range_incl(rng, min_pct, max_pct);
    int64_t scaled = ((int64_t)base_val * (int64_t)pct + 50LL) / 100LL;
    if (scaled < (int64_t)lo) {
        scaled = (int64_t)lo;
    }
    if (scaled > (int64_t)hi) {
        scaled = (int64_t)hi;
    }
    return (int32_t)scaled;
}

/* ---------------- Main On-Console Seed Execution ---------------- */

ie4p_status_t ie4p_apply_onconsole_seed_reader(
    const ie4p_reader_t *installed_arc0,
    FILE *recipe_file,
    const char *seed_text,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap)
{
    if (installed_arc0 == NULL || installed_arc0->read_at == NULL ||
        recipe_file == NULL || seed_text == NULL || out_arc0 == NULL) {
        set_seed_err(err_detail, err_detail_cap, "Argumentos nulos al motor de semillas.");
        return IE4P_ERR_ARGS;
    }

    ie4p_crc32_init();

    uint32_t inst_data_off = 0U;
    ie4p_arc0_slot_t *inst_slots = NULL;
    uint32_t inst_slot_count = 0U;
    ie4p_status_t st = ie4p_load_arc0_slots(
        installed_arc0, &inst_data_off, &inst_slots, &inst_slot_count, err_detail, err_detail_cap);
    if (st != IE4P_OK) {
        return st;
    }

    uint8_t outer_hdr[24];
    if (fseek(recipe_file, 0L, SEEK_SET) != 0 || f_read_exact(recipe_file, outer_hdr, sizeof(outer_hdr)) != 0) {
        free(inst_slots);
        set_seed_err(err_detail, err_detail_cap, "No se pudo leer cabecera de seed_recipe.ie4r.");
        return IE4P_ERR_IO_READ;
    }

    if (memcmp(outer_hdr, "IE4RV100", 8) != 0) {
        free(inst_slots);
        set_seed_err(err_detail, err_detail_cap, "Cabecera IE4RV100 invalida.");
        return IE4P_ERR_BAD_PACK;
    }

    uint32_t raw_len    = rd_u32(outer_hdr + 0x08);
    uint32_t stored_len = rd_u32(outer_hdr + 0x0C);
    uint32_t exp_crc    = rd_u32(outer_hdr + 0x10);
    uint8_t  codec      = outer_hdr[0x14];

    if (raw_len == 0U || raw_len > 32U * 1024U * 1024U || stored_len == 0U) {
        free(inst_slots);
        set_seed_err(err_detail, err_detail_cap, "Tamano invalido en seed_recipe.ie4r.");
        return IE4P_ERR_BAD_PACK;
    }

    uint8_t *recipe_buf = (uint8_t *)malloc(raw_len);
    if (recipe_buf == NULL) {
        free(inst_slots);
        set_seed_err(err_detail, err_detail_cap, "Memoria insuficiente para receta de semilla.");
        return IE4P_ERR_OOM;
    }

    if (codec == 2U) {
        uint8_t *comp = (uint8_t *)malloc(stored_len);
        if (comp == NULL) {
            free(recipe_buf);
            free(inst_slots);
            set_seed_err(err_detail, err_detail_cap, "Memoria insuficiente al descomprimir receta.");
            return IE4P_ERR_OOM;
        }
        if (f_read_exact(recipe_file, comp, stored_len) != 0) {
            free(comp);
            free(recipe_buf);
            free(inst_slots);
            set_seed_err(err_detail, err_detail_cap, "Error leyendo seed_recipe.ie4r.");
            return IE4P_ERR_IO_READ;
        }
        st = ie4p_decompress_lz4_raw(comp, stored_len, recipe_buf, raw_len);
        free(comp);
        if (st != IE4P_OK) {
            free(recipe_buf);
            free(inst_slots);
            set_seed_err(err_detail, err_detail_cap, "Error LZ4 descomprimiendo seed_recipe.ie4r.");
            return st;
        }
    } else if (codec == 1U && stored_len == raw_len) {
        if (f_read_exact(recipe_file, recipe_buf, raw_len) != 0) {
            free(recipe_buf);
            free(inst_slots);
            set_seed_err(err_detail, err_detail_cap, "Error leyendo seed_recipe.ie4r.");
            return IE4P_ERR_IO_READ;
        }
    } else {
        free(recipe_buf);
        free(inst_slots);
        set_seed_err(err_detail, err_detail_cap, "Codec desconocido en seed_recipe.ie4r.");
        return IE4P_ERR_BAD_PACK;
    }

    uint32_t actual_recipe_crc = ie4p_crc32_update(0xFFFFFFFFU, recipe_buf, raw_len) ^ 0xFFFFFFFFU;
    if (actual_recipe_crc != exp_crc) {
        free(recipe_buf);
        free(inst_slots);
        set_seed_err(err_detail, err_detail_cap, "CRC32 invalido en seed_recipe.ie4r.");
        return IE4P_ERR_CRC;
    }

    /* Workspace allocations freed under cleanup: */
    tpl_view_t *blobs = NULL;
    tpl_view_t *tpls = NULL;
    player_work_t *players = NULL;
    int32_t *enc_final_players = NULL; /* encount_cnt * 5 */
    uint8_t *file_entries_sec = NULL;
    uint8_t *io_buf = NULL;
    uint32_t *perm_tmp = NULL;

    buf_cursor_t cur = { recipe_buf, raw_len, 0U };

    uint32_t slot_count = 0U, align = 0U, data_offset = 0U;
    uint8_t *hdr72 = NULL;
    uint8_t *dir_comp = NULL, *hash_comp = NULL, *name_comp = NULL;
    uint32_t dir_comp_len = 0U, hash_comp_len = 0U, name_comp_len = 0U;
    uint8_t *phys_order_raw = NULL;
    uint8_t *slot_descs_raw = NULL;

    if (cur_u32(&cur, &slot_count) != 0 ||
        cur_u32(&cur, &align) != 0 ||
        cur_u32(&cur, &data_offset) != 0 ||
        cur_bytes(&cur, &hdr72, 72U) != 0 ||
        cur_blob(&cur, &dir_comp, &dir_comp_len) != 0 ||
        cur_blob(&cur, &hash_comp, &hash_comp_len) != 0 ||
        cur_blob(&cur, &name_comp, &name_comp_len) != 0) {
        st = IE4P_ERR_BAD_PACK;
        set_seed_err(err_detail, err_detail_cap, "Seccion ARC0 truncada en receta.");
        goto cleanup;
    }

    if (slot_count != inst_slot_count || align == 0U) {
        st = IE4P_ERR_ENTRY_COUNT;
        set_seed_err(err_detail, err_detail_cap, "El juego instalado no tiene el numero esperado de archivos ARC0.");
        goto cleanup;
    }

    if (cur_bytes(&cur, &phys_order_raw, (size_t)slot_count * 4U) != 0 ||
        cur_bytes(&cur, &slot_descs_raw, (size_t)slot_count * 24U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        set_seed_err(err_detail, err_detail_cap, "Tabla de slots truncada en receta.");
        goto cleanup;
    }

    uint32_t blob_cnt = 0U;
    if (cur_u32(&cur, &blob_cnt) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }
    blobs = (tpl_view_t *)calloc(blob_cnt > 0U ? blob_cnt : 1U, sizeof(tpl_view_t));
    if (blobs == NULL) {
        st = IE4P_ERR_OOM;
        goto cleanup;
    }
    for (uint32_t i = 0U; i < blob_cnt; i++) {
        if (cur_blob(&cur, &blobs[i].bytes, &blobs[i].len) != 0) {
            st = IE4P_ERR_BAD_PACK;
            goto cleanup;
        }
    }

    uint32_t tpl_cnt = 0U;
    if (cur_u32(&cur, &tpl_cnt) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }
    tpls = (tpl_view_t *)calloc(tpl_cnt > 0U ? tpl_cnt : 1U, sizeof(tpl_view_t));
    if (tpls == NULL) {
        st = IE4P_ERR_OOM;
        goto cleanup;
    }
    for (uint32_t i = 0U; i < tpl_cnt; i++) {
        if (cur_u32(&cur, &tpls[i].arc_file_idx) != 0 ||
            cur_blob(&cur, &tpls[i].bytes, &tpls[i].len) != 0) {
            st = IE4P_ERR_BAD_PACK;
            goto cleanup;
        }
    }

    perm_tmp = (uint32_t *)malloc(4096U * sizeof(uint32_t));
    if (perm_tmp == NULL) {
        st = IE4P_ERR_OOM;
        goto cleanup;
    }

    /* -------- PASS 1: Atomic Character Visual Identity Swaps (Rig-Matched) -------- */
    uint32_t vgrp_cnt = 0U;
    if (cur_u32(&cur, &vgrp_cnt) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }
    sm64_rng_t vis_rng;
    rng_seed(&vis_rng, seed_text, "players.visual");

    for (uint32_t g = 0U; g < vgrp_cnt; g++) {
        uint32_t mcnt = 0U;
        uint8_t *mraw = NULL;
        if (cur_u32(&cur, &mcnt) != 0 ||
            cur_bytes(&cur, &mraw, (size_t)mcnt * 108U) != 0) {
            st = IE4P_ERR_BAD_PACK;
            goto cleanup;
        }
        if (mcnt < 2U || mcnt > 4096U) {
            continue;
        }

        build_derangement(perm_tmp, mcnt, &vis_rng);
        for (uint32_t i = 0U; i < mcnt; i++) {
            uint32_t d = perm_tmp[i];
            const uint8_t *mi = mraw + ((size_t)i * 108U);
            const uint8_t *md = mraw + ((size_t)d * 108U);

            uint32_t bust_arc = rd_u32(mi + 0U);
            uint32_t rpg_arc  = rd_u32(mi + 4U);
            uint32_t waza_arc = rd_u32(mi + 8U);

            uint32_t d_bust_inst = rd_u32(md + 12U);
            uint32_t d_bust_sz   = rd_u32(md + 16U);
            uint32_t d_bust_crc  = rd_u32(md + 20U);
            uint32_t d_rpg_inst  = rd_u32(md + 24U);
            uint32_t d_rpg_sz    = rd_u32(md + 28U);
            uint32_t d_rpg_crc   = rd_u32(md + 32U);
            uint32_t d_waza_inst = rd_u32(md + 36U);
            uint32_t d_waza_sz   = rd_u32(md + 40U);
            uint32_t d_waza_crc  = rd_u32(md + 44U);

            if (bust_arc < slot_count) {
                uint8_t *sd = slot_descs_raw + ((size_t)bust_arc * 24U);
                wr_u32(sd + 8U, d_bust_sz);
                wr_u32(sd + 12U, d_bust_crc);
                wr_u32(sd + 16U, IE4R_SRC_INSTALLED_SLOT);
                wr_u32(sd + 20U, d_bust_inst);
            }
            if (rpg_arc < slot_count) {
                uint8_t *sd = slot_descs_raw + ((size_t)rpg_arc * 24U);
                wr_u32(sd + 8U, d_rpg_sz);
                wr_u32(sd + 12U, d_rpg_crc);
                wr_u32(sd + 16U, IE4R_SRC_INSTALLED_SLOT);
                wr_u32(sd + 20U, d_rpg_inst);
            }
            if (waza_arc < slot_count) {
                uint8_t *sd = slot_descs_raw + ((size_t)waza_arc * 24U);
                wr_u32(sd + 8U, d_waza_sz);
                wr_u32(sd + 12U, d_waza_crc);
                wr_u32(sd + 16U, IE4R_SRC_INSTALLED_SLOT);
                wr_u32(sd + 20U, d_waza_inst);
            }

            uint32_t cb_tpl = rd_u32(mi + 76U);
            if (patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 80U), rd_i32(md + 48U)) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 84U), rd_i32(md + 52U)) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 88U), rd_i32(md + 60U)) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 92U), rd_i32(md + 64U)) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 96U), rd_i32(md + 68U)) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 100U), rd_i32(md + 72U)) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, cb_tpl, rd_u32(mi + 104U), rd_i32(md + 56U)) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
    }

    /* -------- PASS 2: Load Playable Players & Build GK / Field Derangements -------- */
    uint32_t player_cnt = 0U, cp_tpl_idx = 0U;
    uint8_t *players_raw = NULL;
    if (cur_u32(&cur, &player_cnt) != 0 ||
        cur_u32(&cur, &cp_tpl_idx) != 0 ||
        cur_bytes(&cur, &players_raw, (size_t)player_cnt * 136U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    players = (player_work_t *)calloc(player_cnt > 0U ? player_cnt : 1U, sizeof(player_work_t));
    if (players == NULL) {
        st = IE4P_ERR_OOM;
        goto cleanup;
    }

    uint32_t gk_pool_idx[1024];
    uint32_t fld_pool_idx[3072];
    uint32_t gk_cnt = 0U, fld_cnt = 0U;

    for (uint32_t i = 0U; i < player_cnt; i++) {
        const uint8_t *pr = players_raw + ((size_t)i * 136U);
        player_work_t *pw = &players[i];
        pw->param_hash = rd_i32(pr + 0U);
        pw->base_hash  = rd_i32(pr + 4U);
        pw->body       = rd_i32(pr + 8U);
        pw->gender     = rd_i32(pr + 12U);
        pw->orig_pos   = rd_i32(pr + 16U);
        pw->flags      = rd_u32(pr + 20U);
        pw->move_off   = rd_i32(pr + 24U);
        pw->move_cnt   = rd_i32(pr + 28U);
        pw->fp         = rd_i32(pr + 32U);
        pw->tp         = rd_i32(pr + 36U);
        pw->kick       = rd_i32(pr + 40U);
        pw->dribble    = rd_i32(pr + 44U);
        pw->tech       = rd_i32(pr + 48U);
        pw->block      = rd_i32(pr + 52U);
        pw->speed      = rd_i32(pr + 56U);
        pw->stamina    = rd_i32(pr + 60U);
        pw->catch_stat = rd_i32(pr + 64U);
        pw->luck       = rd_i32(pr + 68U);
        pw->freedom    = rd_i32(pr + 72U);
        pw->avatar_hash = rd_i32(pr + 76U);
        for (int c = 0; c < 14; c++) {
            pw->offs[c] = rd_u32(pr + 80U + ((size_t)c * 4U));
        }
        pw->mapped_param_hash = pw->param_hash;

        if ((pw->flags & PFLAG_SWAPPABLE) != 0U) {
            if (pw->orig_pos == 1 && gk_cnt < 1024U) {
                gk_pool_idx[gk_cnt++] = i;
            } else if (pw->orig_pos != 1 && fld_cnt < 3072U) {
                fld_pool_idx[fld_cnt++] = i;
            }
        }
    }

    sm64_rng_t roster_rng;
    rng_seed(&roster_rng, seed_text, "players.rosters");

    if (gk_cnt >= 2U) {
        build_derangement(perm_tmp, gk_cnt, &roster_rng);
        for (uint32_t k = 0U; k < gk_cnt; k++) {
            players[gk_pool_idx[k]].mapped_param_hash = players[gk_pool_idx[perm_tmp[k]]].param_hash;
        }
    }
    if (fld_cnt >= 2U) {
        build_derangement(perm_tmp, fld_cnt, &roster_rng);
        for (uint32_t k = 0U; k < fld_cnt; k++) {
            players[fld_pool_idx[k]].mapped_param_hash = players[fld_pool_idx[perm_tmp[k]]].param_hash;
        }
    }

    /* -------- PASS 3: Story Teams, Encounter Teams, Map Spawns & PalPack -------- */
    uint32_t tc_tpl_idx = 0U, story_cnt = 0U;
    uint8_t *story_raw = NULL;
    if (cur_u32(&cur, &tc_tpl_idx) != 0 ||
        cur_u32(&cur, &story_cnt) != 0 ||
        cur_bytes(&cur, &story_raw, (size_t)story_cnt * 208U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t encount_cnt = 0U;
    uint8_t *encount_raw = NULL;
    if (cur_u32(&cur, &encount_cnt) != 0 ||
        cur_bytes(&cur, &encount_raw, (size_t)encount_cnt * 76U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t spawn_cnt = 0U;
    uint8_t *spawn_raw = NULL;
    if (cur_u32(&cur, &spawn_cnt) != 0 ||
        cur_bytes(&cur, &spawn_raw, (size_t)spawn_cnt * 72U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t item_tpl_idx = 0U, palpack_cnt = 0U;
    uint8_t *palpack_raw = NULL;
    if (cur_u32(&cur, &item_tpl_idx) != 0 ||
        cur_u32(&cur, &palpack_cnt) != 0 ||
        cur_bytes(&cur, &palpack_raw, (size_t)palpack_cnt * 60U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t sk_tpl_idx = 0U, sk_param_cnt = 0U;
    uint8_t *sk_param_raw = NULL;
    if (cur_u32(&cur, &sk_tpl_idx) != 0 ||
        cur_u32(&cur, &sk_param_cnt) != 0 ||
        cur_bytes(&cur, &sk_param_raw, (size_t)sk_param_cnt * 24U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t stbl_tpl_idx = 0U, stbl_cnt = 0U;
    uint8_t *stbl_raw = NULL;
    if (cur_u32(&cur, &stbl_tpl_idx) != 0 ||
        cur_u32(&cur, &stbl_cnt) != 0 ||
        cur_bytes(&cur, &stbl_raw, (size_t)stbl_cnt * 20U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t tp_tpl_idx = 0U, tp_cnt = 0U;
    uint8_t *tp_raw = NULL;
    if (cur_u32(&cur, &tp_tpl_idx) != 0 ||
        cur_u32(&cur, &tp_cnt) != 0 ||
        cur_bytes(&cur, &tp_raw, (size_t)tp_cnt * 24U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    uint32_t chest_cnt = 0U;
    uint8_t *chest_raw = NULL;
    if (cur_u32(&cur, &chest_cnt) != 0 ||
        cur_bytes(&cur, &chest_raw, (size_t)chest_cnt * 12U) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    /* Read all 15 candidate pools */
    int_pool_view_t p_shot, p_solo_shot, p_dribble, p_block, p_save, p_talent;
    int_pool_view_t p_avatars, p_nonsave_avatars, p_emblems;
    int_pool_view_t p_match_forms, p_mini_forms, p_coaches, p_tactics, p_uniforms, p_chests;

    if (cur_int_pool(&cur, &p_shot) != 0 ||
        cur_int_pool(&cur, &p_solo_shot) != 0 ||
        cur_int_pool(&cur, &p_dribble) != 0 ||
        cur_int_pool(&cur, &p_block) != 0 ||
        cur_int_pool(&cur, &p_save) != 0 ||
        cur_int_pool(&cur, &p_talent) != 0 ||
        cur_int_pool(&cur, &p_avatars) != 0 ||
        cur_int_pool(&cur, &p_nonsave_avatars) != 0 ||
        cur_int_pool(&cur, &p_emblems) != 0 ||
        cur_int_pool(&cur, &p_match_forms) != 0 ||
        cur_int_pool(&cur, &p_mini_forms) != 0 ||
        cur_int_pool(&cur, &p_coaches) != 0 ||
        cur_int_pool(&cur, &p_tactics) != 0 ||
        cur_int_pool(&cur, &p_uniforms) != 0 ||
        cur_int_pool(&cur, &p_chests) != 0) {
        st = IE4P_ERR_BAD_PACK;
        goto cleanup;
    }

    /* Randomize Story Teams (11v11) */
    sm64_rng_t kitnum_rng, emb_rng;
    rng_seed(&kitnum_rng, seed_text, "teams.kit_numbers");
    rng_seed(&emb_rng, seed_text, "teams.emblems");

    for (uint32_t t = 0U; t < story_cnt; t++) {
        const uint8_t *sr = story_raw + ((size_t)t * 208U);
        uint32_t flags = rd_u32(sr + 4U);
        int32_t clean_emb = rd_i32(sr + 8U);
        uint32_t emb_off = rd_u32(sr + 12U);

        int32_t team_players[16];
        int32_t used_bases[16];
        int used_cnt = 0;

        for (int s = 0; s < 16; s++) {
            team_players[s] = rd_i32(sr + 16U + ((size_t)s * 4U));
        }

        if ((flags & 1U) == 0U) { /* Not scripted */
            /* Pre-seed used_bases with protected members on this team */
            for (int s = 0; s < 16; s++) {
                int pi = find_player_by_param(players, player_cnt, team_players[s]);
                if (pi >= 0 && (players[pi].flags & PFLAG_SWAPPABLE) == 0U) {
                    used_bases[used_cnt++] = players[pi].base_hash;
                }
            }

            for (int s = 0; s < 16; s++) {
                int32_t cur_p = team_players[s];
                if (cur_p == 0) {
                    continue;
                }
                int pi = find_player_by_param(players, player_cnt, cur_p);
                if (pi < 0 || (players[pi].flags & PFLAG_SWAPPABLE) == 0U) {
                    continue;
                }

                int32_t cand_p = players[pi].mapped_param_hash;
                int cpi = find_player_by_param(players, player_cnt, cand_p);
                int dup = 0;
                if (cpi >= 0) {
                    for (int u = 0; u < used_cnt; u++) {
                        if (used_bases[u] == players[cpi].base_hash) {
                            dup = 1;
                            break;
                        }
                    }
                }
                if (dup || cpi < 0) {
                    /* Pick fresh GK (if s == 0) or field player without duplicate BaseHash */
                    const uint32_t *pool = (s == 0 && gk_cnt > 0U) ? gk_pool_idx : fld_pool_idx;
                    uint32_t pcnt = (s == 0 && gk_cnt > 0U) ? gk_cnt : fld_cnt;
                    if (pcnt > 0U) {
                        for (int tries = 0; tries < 24; tries++) {
                            uint32_t pick = pool[rng_bound(&roster_rng, pcnt)];
                            int clash = 0;
                            for (int u = 0; u < used_cnt; u++) {
                                if (used_bases[u] == players[pick].base_hash) {
                                    clash = 1;
                                    break;
                                }
                            }
                            if (!clash) {
                                cand_p = players[pick].param_hash;
                                cpi = (int)pick;
                                break;
                            }
                        }
                    }
                }
                team_players[s] = cand_p;
                if (cpi >= 0 && used_cnt < 16) {
                    used_bases[used_cnt++] = players[cpi].base_hash;
                }
                uint32_t poff = rd_u32(sr + 80U + ((size_t)s * 4U));
                if (patch_tpl_i32(tpls, tpl_cnt, tc_tpl_idx, poff, cand_p) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }

            /* Randomize shirt numbers (slot 0 keeps #1, slots 1..15 unique 2..99) */
            uint8_t used_nums[100];
            memset(used_nums, 0, sizeof(used_nums));
            used_nums[1] = 1U;
            for (int s = 0; s < 16; s++) {
                if (team_players[s] == 0) {
                    continue;
                }
                int32_t num = 1;
                if (s > 0) {
                    do {
                        num = rng_range_incl(&kitnum_rng, 2, 99);
                    } while (used_nums[num]);
                    used_nums[num] = 1U;
                }
                uint32_t noff = rd_u32(sr + 144U + ((size_t)s * 4U));
                if (patch_tpl_i32(tpls, tpl_cnt, tc_tpl_idx, noff, num) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }

            if ((flags & 4U) == 0U && clean_emb > 0 && p_emblems.count > 1U) {
                int32_t nemb = pool_pick_diff(&p_emblems, clean_emb, &emb_rng);
                if (patch_tpl_i32(tpls, tpl_cnt, tc_tpl_idx, emb_off, nemb) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }
        }

        /* Mark active 11v11 slot-0 GK */
        if ((flags & 2U) != 0U && team_players[0] != 0) {
            int gki = find_player_by_param(players, player_cnt, team_players[0]);
            if (gki >= 0) {
                players[gki].is_11v11_gk = 1U;
            }
        }
    }

    /* Randomize Encounter Teams (5v5 Pachangas) */
    enc_final_players = (int32_t *)calloc((encount_cnt > 0U ? encount_cnt : 1U) * 5U, sizeof(int32_t));
    if (enc_final_players == NULL) {
        st = IE4P_ERR_OOM;
        goto cleanup;
    }

    for (uint32_t e = 0U; e < encount_cnt; e++) {
        const uint8_t *er = encount_raw + ((size_t)e * 76U);
        uint32_t flags = rd_u32(er + 4U);
        int32_t clean_emb = rd_i32(er + 8U);
        uint32_t emb_off = rd_u32(er + 12U);

        int32_t *ef = enc_final_players + ((size_t)e * 5U);
        int32_t used_bases[5];
        int used_cnt = 0;

        for (int s = 0; s < 5; s++) {
            ef[s] = rd_i32(er + 16U + ((size_t)s * 4U));
        }

        if ((flags & 1U) == 0U && fld_cnt > 0U) {
            for (int s = 0; s < 5; s++) {
                if (ef[s] == 0) {
                    continue;
                }
                int pi = find_player_by_param(players, player_cnt, ef[s]);
                if (pi < 0 || (players[pi].flags & PFLAG_SWAPPABLE) == 0U) {
                    if (pi >= 0 && used_cnt < 5) {
                        used_bases[used_cnt++] = players[pi].base_hash;
                    }
                    continue;
                }

                /* Slot 0 in 5v5 pachangas MUST NOT be a dedicated GK (anti-wall rule)! */
                int32_t cand_p = players[pi].mapped_param_hash;
                int cpi = find_player_by_param(players, player_cnt, cand_p);
                int bad = (cpi < 0) || (s == 0 && players[cpi].orig_pos == 1);
                if (!bad) {
                    for (int u = 0; u < used_cnt; u++) {
                        if (used_bases[u] == players[cpi].base_hash) {
                            bad = 1;
                            break;
                        }
                    }
                }
                if (bad) {
                    for (int tries = 0; tries < 24; tries++) {
                        uint32_t pick = fld_pool_idx[rng_bound(&roster_rng, fld_cnt)];
                        int clash = 0;
                        for (int u = 0; u < used_cnt; u++) {
                            if (used_bases[u] == players[pick].base_hash) {
                                clash = 1;
                                break;
                            }
                        }
                        if (!clash) {
                            cand_p = players[pick].param_hash;
                            cpi = (int)pick;
                            break;
                        }
                    }
                }
                ef[s] = cand_p;
                if (cpi >= 0 && used_cnt < 5) {
                    used_bases[used_cnt++] = players[cpi].base_hash;
                }
                uint32_t poff = rd_u32(er + 36U + ((size_t)s * 4U));
                if (patch_tpl_i32(tpls, tpl_cnt, tc_tpl_idx, poff, cand_p) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }

            uint8_t used_nums[100];
            memset(used_nums, 0, sizeof(used_nums));
            for (int s = 0; s < 5; s++) {
                if (ef[s] == 0) {
                    continue;
                }
                int32_t num;
                do {
                    num = rng_range_incl(&kitnum_rng, 1, 99);
                } while (used_nums[num]);
                used_nums[num] = 1U;
                uint32_t noff = rd_u32(er + 56U + ((size_t)s * 4U));
                if (patch_tpl_i32(tpls, tpl_cnt, tc_tpl_idx, noff, num) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }

            if (clean_emb > 0 && p_emblems.count > 1U) {
                int32_t nemb = pool_pick_diff(&p_emblems, clean_emb, &emb_rng);
                if (patch_tpl_i32(tpls, tpl_cnt, tc_tpl_idx, emb_off, nemb) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }
        }
    }

    /* Mark pachanga-only slot-0 goal defenders (never an 11v11 GK, not protected story) */
    for (uint32_t e = 0U; e < encount_cnt; e++) {
        int32_t p0 = enc_final_players[(size_t)e * 5U];
        if (p0 == 0) {
            continue;
        }
        int pi = find_player_by_param(players, player_cnt, p0);
        if (pi >= 0 && !players[pi].is_11v11_gk && (players[pi].flags & PFLAG_SWAPPABLE) != 0U) {
            players[pi].is_pachanga_goal = 1U;
        }
    }

    /* Sync Overworld Map Encounter Spawns (.enc.bin) */
    for (uint32_t m = 0U; m < spawn_cnt; m++) {
        const uint8_t *mr = spawn_raw + ((size_t)m * 72U);
        uint32_t mtpl = rd_u32(mr + 0U);
        int32_t linked_enc = rd_i32(mr + 4U);
        int32_t uniform = rd_i32(mr + 8U);

        for (int s = 0; s < 5; s++) {
            uint32_t dom = rd_u32(mr + 12U + ((size_t)s * 4U));
            int32_t orig_val = rd_i32(mr + 32U + ((size_t)s * 4U));
            uint32_t soff = rd_u32(mr + 52U + ((size_t)s * 4U));
            if (dom != 1U && dom != 2U) {
                continue; /* 0 = empty/unknown, 3 = protected story */
            }

            int orig_pi = (dom == 1U)
                ? find_player_by_param(players, player_cnt, orig_val)
                : find_player_by_base(players, player_cnt, orig_val);

            int target_pi = -1;
            if (linked_enc >= 0 && (uint32_t)linked_enc < encount_cnt) {
                int32_t linked_p = enc_final_players[((size_t)linked_enc * 5U) + (size_t)s];
                target_pi = find_player_by_param(players, player_cnt, linked_p);
            } else if (orig_pi >= 0) {
                target_pi = find_player_by_param(players, player_cnt, players[orig_pi].mapped_param_hash);
            }

            if (target_pi < 0) {
                continue;
            }

            /* When uniform == 0 (street clothes), require identical (body, gender) rig! */
            if (uniform == 0 && orig_pi >= 0 &&
                (players[target_pi].body != players[orig_pi].body ||
                 players[target_pi].gender != players[orig_pi].gender)) {
                for (int tries = 0; tries < 32 && fld_cnt > 0U; tries++) {
                    uint32_t pick = fld_pool_idx[rng_bound(&roster_rng, fld_cnt)];
                    if (players[pick].body == players[orig_pi].body &&
                        players[pick].gender == players[orig_pi].gender) {
                        target_pi = (int)pick;
                        break;
                    }
                }
                if (players[target_pi].body != players[orig_pi].body ||
                    players[target_pi].gender != players[orig_pi].gender) {
                    continue;
                }
            }

            int32_t write_val = (dom == 1U) ? players[target_pi].param_hash : players[target_pi].base_hash;
            if (patch_tpl_i32(tpls, tpl_cnt, mtpl, soff, write_val) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
    }

    /* Randomize PalPack (Fichatron / ITEM_KIZUNAX) & prevent self-prerequisite cycles */
    sm64_rng_t pal_rng;
    rng_seed(&pal_rng, seed_text, "players.palpack");
    uint32_t elig_kiz[2048];
    uint32_t elig_cnt = 0U;
    for (uint32_t k = 0U; k < palpack_cnt; k++) {
        const uint8_t *kr = palpack_raw + ((size_t)k * 60U);
        if (rd_u32(kr + 0U) != 0U && elig_cnt < 2048U) {
            elig_kiz[elig_cnt++] = k;
        }
    }

    if (elig_cnt >= 2U) {
        build_derangement(perm_tmp, elig_cnt, &pal_rng);
        for (uint32_t e = 0U; e < elig_cnt; e++) {
            uint32_t k = elig_kiz[e];
            uint32_t dk = elig_kiz[perm_tmp[e]];
            const uint8_t *kr = palpack_raw + ((size_t)k * 60U);
            const uint8_t *dkr = palpack_raw + ((size_t)dk * 60U);

            int32_t new_rec = rd_i32(dkr + 4U);
            uint32_t rec_off = rd_u32(kr + 8U);
            if (patch_tpl_i32(tpls, tpl_cnt, item_tpl_idx, rec_off, new_rec) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }

            for (int p = 0; p < 4; p++) {
                int32_t ptype = rd_i32(kr + 12U + ((size_t)p * 4U));
                int32_t pid   = rd_i32(kr + 28U + ((size_t)p * 4U));
                uint32_t poff = rd_u32(kr + 44U + ((size_t)p * 4U));
                if (ptype == 2 && pid != 0) {
                    int pi = find_player_by_param(players, player_cnt, pid);
                    if (pi >= 0 && players[pi].mapped_param_hash != new_rec) {
                        if (patch_tpl_i32(tpls, tpl_cnt, item_tpl_idx, poff, players[pi].mapped_param_hash) != 0) {
                            st = IE4P_ERR_BAD_PACK;
                            goto cleanup;
                        }
                    }
                }
            }
        }
    }

    /* -------- PASS 4: Player Stats, Elements, Positions & Fighting Spirits -------- */
    sm64_rng_t pos_rng, elem_rng, stat_rng, av_rng;
    rng_seed(&pos_rng, seed_text, "players.positions");
    rng_seed(&elem_rng, seed_text, "players.elements");
    rng_seed(&stat_rng, seed_text, "players.stats");
    rng_seed(&av_rng, seed_text, "players.avatars");

    for (uint32_t i = 0U; i < player_cnt; i++) {
        player_work_t *pw = &players[i];

        int32_t new_pos = pw->orig_pos;
        if (pw->is_11v11_gk || (pw->flags & PFLAG_IS_JP) != 0U) {
            new_pos = pw->is_11v11_gk ? 1 : pw->orig_pos;
        } else if (pw->is_pachanga_goal) {
            new_pos = rng_range_incl(&pos_rng, 2, 4);
        } else if ((pw->flags & PFLAG_IS_ARION) == 0U && rng_bound(&pos_rng, 100U) < 80U) {
            new_pos = rng_range_incl(&pos_rng, 1, 4);
        }

        int32_t new_elem = rng_range_incl(&elem_rng, 1, 4);
        /* Draw each stat independently across its supported range, not around the clean value.
         * Safety minima for goalkeepers and Arion still apply below. */
        int32_t n_fp   = rng_range_incl(&stat_rng, 75, 220);
        int32_t n_tp   = rng_range_incl(&stat_rng, 90, 210);
        int32_t n_kick = rng_range_incl(&stat_rng, 12, 180);
        int32_t n_drib = rng_range_incl(&stat_rng, 12, 180);
        int32_t n_tech = rng_range_incl(&stat_rng, 12, 180);
        int32_t n_blk  = rng_range_incl(&stat_rng, 12, 180);
        int32_t n_spd  = rng_range_incl(&stat_rng, 12, 180);
        int32_t n_sta  = rng_range_incl(&stat_rng, 12, 180);
        int32_t n_cat  = rng_range_incl(&stat_rng, 10, 180);
        int32_t n_lck  = rng_range_incl(&stat_rng, 10, 160);
        int32_t n_fre  = rng_range_incl(&stat_rng, 15, 120);

        if (new_pos == 1 && n_cat < 24) {
            n_cat = rng_range_incl(&stat_rng, 24, 34);
        }
        if (pw->is_pachanga_goal && n_cat > 24) {
            n_cat = rng_range_incl(&stat_rng, 12, 24);
        }
        if ((pw->flags & PFLAG_IS_ARION) != 0U) {
            if (n_kick < 28) n_kick = 28;
            if (n_tp < 100)  n_tp = 100;
        }

        int32_t n_av = pw->avatar_hash;
        if ((pw->flags & PFLAG_PROTECTED_AVATAR) == 0U && p_avatars.count > 0U) {
            const int_pool_view_t *av_pool = pw->is_pachanga_goal ? &p_nonsave_avatars : &p_avatars;
            if (rng_bound(&av_rng, 100U) < 35U) {
                n_av = pool_pick(av_pool, &av_rng, 0);
            } else {
                n_av = 0;
            }
        }
        pw->has_final_avatar = (n_av != 0) ? 1U : 0U;
        pw->orig_pos = new_pos;

        int32_t vals[14] = {
            n_fp, n_tp, n_kick, n_drib, n_tech, n_blk, n_spd, n_sta,
            n_cat, n_lck, new_elem, new_pos, n_fre, n_av
        };
        for (int c = 0; c < 14; c++) {
            if (patch_tpl_i32(tpls, tpl_cnt, cp_tpl_idx, pw->offs[c], vals[c]) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
    }

    /* -------- PASS 5: Skill Parameters & Player Learnsets -------- */
    sm64_rng_t skp_rng, skl_rng;
    rng_seed(&skp_rng, seed_text, "skills.parameters");
    rng_seed(&skl_rng, seed_text, "skills.learnsets");

    for (uint32_t s = 0U; s < sk_param_cnt; s++) {
        const uint8_t *sr = sk_param_raw + ((size_t)s * 24U);
        int32_t c_tp  = rd_i32(sr + 4U);
        int32_t c_pow = rd_i32(sr + 8U);

        int32_t n_el  = rng_range_incl(&skp_rng, 1, 4);
        int32_t n_tp  = scale_clamp(c_tp, 80, 120, 10, 75, &skp_rng);
        int32_t n_pow = scale_clamp(c_pow, 80, 125, 20, 220, &skp_rng);

        if (patch_tpl_i32(tpls, tpl_cnt, sk_tpl_idx, rd_u32(sr + 12U), n_el) != 0 ||
            patch_tpl_i32(tpls, tpl_cnt, sk_tpl_idx, rd_u32(sr + 16U), n_tp) != 0 ||
            patch_tpl_i32(tpls, tpl_cnt, sk_tpl_idx, rd_u32(sr + 20U), n_pow) != 0) {
            st = IE4P_ERR_BAD_PACK;
            goto cleanup;
        }
    }

    for (uint32_t i = 0U; i < player_cnt; i++) {
        const player_work_t *pw = &players[i];
        if (pw->move_off < 0 || pw->move_cnt <= 0 ||
            (uint32_t)(pw->move_off + pw->move_cnt) > stbl_cnt) {
            continue;
        }

        int32_t used_skills[16];
        int used_sk_cnt = 0;

        for (int s = 0; s < pw->move_cnt; s++) {
            uint32_t idx = (uint32_t)(pw->move_off + s);
            const uint8_t *sl = stbl_raw + ((size_t)idx * 20U);
            if ((rd_u32(sl + 8U) & 1U) != 0U && used_sk_cnt < 16) {
                used_skills[used_sk_cnt++] = rd_i32(sl + 0U);
            }
        }

        for (int s = 0; s < pw->move_cnt; s++) {
            uint32_t idx = (uint32_t)(pw->move_off + s);
            const uint8_t *sl = stbl_raw + ((size_t)idx * 20U);
            int32_t c_hash = rd_i32(sl + 0U);
            int32_t c_lvl  = rd_i32(sl + 4U);
            uint32_t sflags = rd_u32(sl + 8U);
            uint32_t loff  = rd_u32(sl + 12U);
            uint32_t hoff  = rd_u32(sl + 16U);

            if ((sflags & 1U) != 0U || c_hash == 0) {
                continue; /* Frozen story-scripted move */
            }

            const int_pool_view_t *chosen_pool = &p_dribble;
            if (s == 0 && (pw->flags & PFLAG_IS_ARION) != 0U) {
                chosen_pool = &p_solo_shot;
            } else if ((sflags & 2U) != 0U && s > 0 && p_talent.count > 0U) {
                chosen_pool = &p_talent;
            } else {
                /* Randomize active techniques independently of player position.
                 * Saves remain excluded for street-only goal defenders. */
                const int_pool_view_t *pools[4] = { &p_shot, &p_dribble, &p_block, &p_save };
                uint32_t total = p_shot.count + p_dribble.count + p_block.count +
                                 (pw->is_pachanga_goal ? 0U : p_save.count);
                if (total > 0U) {
                    uint32_t roll = rng_bound(&skl_rng, total);
                    for (int category = 0; category < 4; category++) {
                        uint32_t count = (category == 3 && pw->is_pachanga_goal)
                            ? 0U : pools[category]->count;
                        if (roll < count) {
                            chosen_pool = pools[category];
                            break;
                        }
                        roll -= count;
                    }
                }
            }

            int32_t new_sk = c_hash;
            for (int tries = 0; tries < 16 && chosen_pool->count > 0U; tries++) {
                int32_t cand = pool_pick(chosen_pool, &skl_rng, c_hash);
                int clash = 0;
                for (int u = 0; u < used_sk_cnt; u++) {
                    if (used_skills[u] == cand) {
                        clash = 1;
                        break;
                    }
                }
                if (!clash) {
                    new_sk = cand;
                    break;
                }
            }
            if (used_sk_cnt < 16) {
                used_skills[used_sk_cnt++] = new_sk;
            }

            int32_t new_lvl = c_lvl;
            if (s == 0 && ((pw->flags & PFLAG_IS_ARION) != 0U || !pw->has_final_avatar)) {
                new_lvl = 1; /* Guaranteed Lv.1 starter move! */
            }

            if (patch_tpl_i32(tpls, tpl_cnt, stbl_tpl_idx, hoff, new_sk) != 0 ||
                patch_tpl_i32(tpls, tpl_cnt, stbl_tpl_idx, loff, new_lvl) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
    }

    /* -------- PASS 6: Team Formations, Coaches, Tactics, Uniforms & Map Chests -------- */
    sm64_rng_t tp_rng, ch_rng;
    rng_seed(&tp_rng, seed_text, "teams.params");
    rng_seed(&ch_rng, seed_text, "rewards.map_chests");

    for (uint32_t t = 0U; t < tp_cnt; t++) {
        const uint8_t *tr = tp_raw + ((size_t)t * 24U);
        uint32_t mflags = rd_u32(tr + 0U);
        int32_t c_uni   = rd_i32(tr + 4U);
        uint32_t u_off  = rd_u32(tr + 8U);
        uint32_t f_off  = rd_u32(tr + 12U);
        uint32_t c_off  = rd_u32(tr + 16U);
        uint32_t t_off  = rd_u32(tr + 20U);

        const int_pool_view_t *fpool = ((mflags & 2U) != 0U) ? &p_mini_forms : &p_match_forms;
        if (fpool->count > 0U) {
            if (patch_tpl_i32(tpls, tpl_cnt, tp_tpl_idx, f_off, pool_pick(fpool, &tp_rng, 0)) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
        if ((mflags & 4U) != 0U && p_uniforms.count > 1U) {
            if (patch_tpl_i32(tpls, tpl_cnt, tp_tpl_idx, u_off, pool_pick_diff(&p_uniforms, c_uni, &tp_rng)) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
        if ((mflags & 8U) != 0U && p_coaches.count > 0U) {
            if (patch_tpl_i32(tpls, tpl_cnt, tp_tpl_idx, c_off, pool_pick(&p_coaches, &tp_rng, 0)) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
        if ((mflags & 16U) != 0U && p_tactics.count > 0U) {
            if (patch_tpl_i32(tpls, tpl_cnt, tp_tpl_idx, t_off, pool_pick(&p_tactics, &tp_rng, 0)) != 0) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
        }
    }

    if (p_chests.count > 0U) {
        for (uint32_t c = 0U; c < chest_cnt; c++) {
            const uint8_t *cr = chest_raw + ((size_t)c * 12U);
            uint32_t ctpl = rd_u32(cr + 0U);
            uint32_t loff = rd_u32(cr + 4U);
            uint32_t soff = rd_u32(cr + 8U);
            const uint32_t offsets[2] = { loff, soff };
            for (int side = 0; side < 2; side++) {
                uint32_t off = offsets[side];
                if (off == 0xFFFFFFFFU) continue;
                if (ctpl >= tpl_cnt || tpls[ctpl].len < 4U || off > tpls[ctpl].len - 4U) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
                int32_t old_item = rd_i32(tpls[ctpl].bytes + off);
                int32_t item = pool_pick_diff(&p_chests, old_item, &ch_rng);
                if (patch_tpl_i32(tpls, tpl_cnt, ctpl, off, item) != 0) {
                    st = IE4P_ERR_BAD_PACK;
                    goto cleanup;
                }
            }
        }
    }

    /* -------- PASS 7: Build Uncompressed FileEntries & Stream ARC0 Output -------- */
    size_t fe_sec_len = 4U + ((size_t)slot_count * 16U);
    file_entries_sec = (uint8_t *)malloc(fe_sec_len);
    io_buf = (uint8_t *)malloc(SEED_STREAM_CHUNK);
    if (file_entries_sec == NULL || io_buf == NULL) {
        st = IE4P_ERR_OOM;
        set_seed_err(err_detail, err_detail_cap, "Memoria insuficiente al construir tabla ARC0.");
        goto cleanup;
    }

    /* Level-5 NoCompression header: (decompressed_size << 3) | 0 */
    wr_u32(file_entries_sec, (uint32_t)((slot_count * 16U) << 3));

    uint64_t rel_data_cursor = 0ULL;
    for (uint32_t ord = 0U; ord < slot_count; ord++) {
        uint32_t fidx = rd_u32(phys_order_raw + ((size_t)ord * 4U));
        if (fidx >= slot_count) {
            st = IE4P_ERR_BAD_PACK;
            goto cleanup;
        }
        const uint8_t *sd = slot_descs_raw + ((size_t)fidx * 24U);
        uint32_t name_crc = rd_u32(sd + 0U);
        uint32_t name_off = rd_u32(sd + 4U);
        uint32_t fsize    = rd_u32(sd + 8U);

        uint8_t *fe = file_entries_sec + 4U + ((size_t)fidx * 16U);
        wr_u32(fe + 0U, name_crc);
        wr_u32(fe + 4U, name_off);
        wr_u32(fe + 8U, (uint32_t)rel_data_cursor);
        wr_u32(fe + 12U, fsize);

        uint64_t after = rel_data_cursor + (uint64_t)fsize;
        uint64_t rem_al = after % (uint64_t)align;
        if (rem_al != 0ULL) {
            after += (uint64_t)align - rem_al;
        }
        rel_data_cursor = after;
    }

    uint64_t total_expected_bytes = (uint64_t)data_offset + rel_data_cursor;

    /* Write 72-byte ARC0 header + 4 aligned metadata sections */
    size_t pos_out = 0U;
    if (f_write_exact(out_arc0, hdr72, 72U) != 0) {
        st = IE4P_ERR_IO_WRITE;
        goto io_write_err;
    }
    pos_out += 72U;

    if (f_write_exact(out_arc0, dir_comp, dir_comp_len) != 0) goto io_write_err;
    pos_out += dir_comp_len;
    if ((pos_out & 3U) != 0U) {
        size_t p = 4U - (pos_out & 3U);
        if (f_write_zeros(out_arc0, p) != 0) goto io_write_err;
        pos_out += p;
    }

    if (f_write_exact(out_arc0, hash_comp, hash_comp_len) != 0) goto io_write_err;
    pos_out += hash_comp_len;
    if ((pos_out & 3U) != 0U) {
        size_t p = 4U - (pos_out & 3U);
        if (f_write_zeros(out_arc0, p) != 0) goto io_write_err;
        pos_out += p;
    }

    if (f_write_exact(out_arc0, file_entries_sec, fe_sec_len) != 0) goto io_write_err;
    pos_out += fe_sec_len;
    if ((pos_out & 3U) != 0U) {
        size_t p = 4U - (pos_out & 3U);
        if (f_write_zeros(out_arc0, p) != 0) goto io_write_err;
        pos_out += p;
    }

    if (f_write_exact(out_arc0, name_comp, name_comp_len) != 0) goto io_write_err;
    pos_out += name_comp_len;
    if ((pos_out % (size_t)align) != 0U) {
        size_t p = (size_t)align - (pos_out % (size_t)align);
        if (f_write_zeros(out_arc0, p) != 0) goto io_write_err;
        pos_out += p;
    }

    if (pos_out != (size_t)data_offset) {
        st = IE4P_ERR_SIZE_MISMATCH;
        set_seed_err(err_detail, err_detail_cap, "Desalineacion interna en DataOffset ARC0.");
        goto cleanup;
    }

    uint64_t bytes_written = (uint64_t)pos_out;

    /* Stream all 29,969 files in physical order */
    for (uint32_t ord = 0U; ord < slot_count; ord++) {
        if ((ord & 0x3FU) == 0U && progress_cb != NULL) {
            if (progress_cb(ord, slot_count, bytes_written, total_expected_bytes, user_data) != 0) {
                st = IE4P_ERR_ABORTED;
                set_seed_err(err_detail, err_detail_cap, "Generacion cancelada por el usuario.");
                goto cleanup;
            }
        }

        uint32_t fidx = rd_u32(phys_order_raw + ((size_t)ord * 4U));
        const uint8_t *sd = slot_descs_raw + ((size_t)fidx * 24U);
        uint32_t fsize    = rd_u32(sd + 8U);
        uint32_t exp_fcrc = rd_u32(sd + 12U);
        uint32_t skind    = rd_u32(sd + 16U);
        uint32_t sidx     = rd_u32(sd + 20U);

        if (skind == IE4R_SRC_INSTALLED_SLOT) {
            if (sidx >= inst_slot_count || inst_slots[sidx].file_size != fsize) {
                st = IE4P_ERR_SIZE_MISMATCH;
                set_seed_err(err_detail, err_detail_cap, "Discrepancia de tamano en slot instalado.");
                goto cleanup;
            }
            uint64_t abs_off = (uint64_t)inst_data_off + (uint64_t)inst_slots[sidx].file_offset_in_data;
            uint32_t crc_st = 0xFFFFFFFFU;
            uint32_t rem = fsize;
            while (rem > 0U) {
                size_t chunk = rem > SEED_STREAM_CHUNK ? SEED_STREAM_CHUNK : (size_t)rem;
                if (installed_arc0->read_at(installed_arc0->ctx, abs_off, io_buf, chunk) != 0) {
                    st = IE4P_ERR_IO_READ;
                    set_seed_err(err_detail, err_detail_cap, "Error leyendo archivo base desde el juego instalado.");
                    goto cleanup;
                }
                crc_st = ie4p_crc32_update(crc_st, io_buf, chunk);
                if (f_write_exact(out_arc0, io_buf, chunk) != 0) goto io_write_err;
                abs_off += (uint64_t)chunk;
                rem -= (uint32_t)chunk;
            }
            if ((crc_st ^ 0xFFFFFFFFU) != exp_fcrc) {
                st = IE4P_ERR_CRC;
                set_seed_err(err_detail, err_detail_cap, "Fallo CRC32 al copiar recurso del juego instalado.");
                goto cleanup;
            }
        } else if (skind == IE4R_SRC_EMBEDDED_BLOB) {
            if (sidx >= blob_cnt || blobs[sidx].len != fsize) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
            if (f_write_exact(out_arc0, blobs[sidx].bytes, fsize) != 0) goto io_write_err;
        } else if (skind == IE4R_SRC_MUTABLE_TEMPLATE) {
            if (sidx >= tpl_cnt || tpls[sidx].len != fsize) {
                st = IE4P_ERR_BAD_PACK;
                goto cleanup;
            }
            if (f_write_exact(out_arc0, tpls[sidx].bytes, fsize) != 0) goto io_write_err;
        } else {
            st = IE4P_ERR_BAD_PACK;
            goto cleanup;
        }

        bytes_written += (uint64_t)fsize;
        uint64_t mod = bytes_written % (uint64_t)align;
        if (mod != 0ULL) {
            size_t pad = (size_t)((uint64_t)align - mod);
            if (f_write_zeros(out_arc0, pad) != 0) goto io_write_err;
            bytes_written += (uint64_t)pad;
        }
    }

    if (fflush(out_arc0) != 0) {
        goto io_write_err;
    }

    if (progress_cb != NULL) {
        progress_cb(slot_count, slot_count, bytes_written, total_expected_bytes, user_data);
    }

    st = IE4P_OK;
    goto cleanup;

io_write_err:
    st = IE4P_ERR_IO_WRITE;
    set_seed_err(err_detail, err_detail_cap, "Error escribiendo en la tarjeta SD (revisa ~900 MB libres).");

cleanup:
    free(perm_tmp);
    free(io_buf);
    free(file_entries_sec);
    free(enc_final_players);
    free(players);
    free(tpls);
    free(blobs);
    free(recipe_buf);
    free(inst_slots);
    return st;
}

ie4p_status_t ie4p_apply_onconsole_seed_streams(
    FILE *installed_arc0,
    FILE *recipe_file,
    const char *seed_text,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap)
{
    if (installed_arc0 == NULL) {
        set_seed_err(err_detail, err_detail_cap, "Archivo ie_a.fa nulo.");
        return IE4P_ERR_ARGS;
    }
    ie4p_reader_t reader;
    ie4p_init_file_reader(&reader, installed_arc0, 0ULL);
    return ie4p_apply_onconsole_seed_reader(
        &reader, recipe_file, seed_text, out_arc0, progress_cb, user_data, err_detail, err_detail_cap);
}
