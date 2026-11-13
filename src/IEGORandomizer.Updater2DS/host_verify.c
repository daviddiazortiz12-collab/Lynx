#include "source/ie4p_core.h"

#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    if (argc != 4) {
        fprintf(stderr, "Usage: %s <installed_ie_a.fa> <patch.ie4p> <reconstructed_ie_a.fa>\n", argv[0]);
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
