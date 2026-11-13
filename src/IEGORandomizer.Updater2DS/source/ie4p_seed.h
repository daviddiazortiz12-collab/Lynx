#ifndef IE4P_SEED_H
#define IE4P_SEED_H

#include "ie4p_core.h"

#ifdef __cplusplus
extern "C" {
#endif

ie4p_status_t ie4p_apply_onconsole_seed_reader(
    const ie4p_reader_t *installed_arc0,
    FILE *recipe_file,
    const char *seed_text,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap);

ie4p_status_t ie4p_apply_onconsole_seed_streams(
    FILE *installed_arc0,
    FILE *recipe_file,
    const char *seed_text,
    FILE *out_arc0,
    ie4p_progress_fn progress_cb,
    void *user_data,
    char *err_detail,
    size_t err_detail_cap);

#ifdef __cplusplus
}
#endif

#endif /* IE4P_SEED_H */
