#!/usr/bin/env python3
# lod_seam.py <out dir of a run> <list> <shot>...: the transition rows of the levels of detail (docs/v2.md §5.17). Per shot, every pixel whose Lod view level differs from the
# pixel below it is a seam pixel; its luminance (Rec. 709 of sRGB) against the mean of the pixels 2-4 rows above and below in the same column (same level each side).
# Prints per level boundary the mean signed difference and the share of seam pixels darker or brighter by more than 8/255, then the same for rows without a seam (the texture's noise floor);
# a flat surface should show a mean ~0 and shares at the floor.
import sys, os
import numpy as np
from PIL import Image
d, lst, shots = sys.argv[1], sys.argv[2], sys.argv[3:]
LOD_RGB = [(0.1, 0.8, 0.2), (0.9, 0.85, 0.1), (0.95, 0.45, 0.05), (0.85, 0.1, 0.1)]
Y = np.array([0.2126, 0.7152, 0.0722], np.float32)

def img(name): return np.asarray(Image.open(os.path.join(d, lst, name)).convert('RGB'), dtype=np.float32)

def levels(name):
    v = img(name) / 255.0
    out = np.full(v.shape[:2], -1, np.int8)
    best = np.full(out.shape, 1e9)
    for l, c in enumerate(LOD_RGB):
        for f in (1.0, 0.4):
            e = ((v - np.power(np.array(c) * f, 1 / 2.2)) ** 2).sum(axis=2)
            m = e < best
            best[m], out[m] = e[m], l
    out[v.sum(axis=2) < 0.03] = -1
    return out

for s in shots:
    lv, lum = levels(f'{s}.Lod.png'), img(f'{s}.png') @ Y
    H = lv.shape[0]
    for b in range(3):
        diffs = []
        for y in range(4, H - 5):
            m = (lv[y] == b) & (lv[y + 1] == b + 1) | (lv[y] == b + 1) & (lv[y + 1] == b)
            for k in range(2, 5): m &= (lv[y - k] == lv[y]) & (lv[y + 1 + k] == lv[y + 1])
            if not m.any(): continue
            ref = (lum[y - 4:y - 1] + lum[y + 3:y + 6]).mean(axis=0) / 2
            diffs.append(np.concatenate([lum[y][m] - ref[m], lum[y + 1][m] - ref[m]]))
        if not diffs: continue
        a = np.concatenate(diffs)
        print(f'{s} L{b}/L{b + 1}: {a.size} seam px, mean {a.mean():+.2f}, darker > 8 {100 * (a < -8).mean():.1f} %, brighter > 8 {100 * (a > 8).mean():.1f} %')
    # noise floor: the same measure on rows 6+ rows away from any level change (texture alone)
    calm = [y for y in range(10, H - 10, 3) if (lv[y - 5:y + 7] == lv[y]).all(axis=0).mean() > 0.99 and (lv[y] >= 0).mean() > 0.99]
    if calm:
        a = np.concatenate([np.concatenate([lum[y] - (lum[y - 4:y - 1] + lum[y + 3:y + 6]).mean(axis=0) / 2, lum[y + 1] - (lum[y - 4:y - 1] + lum[y + 3:y + 6]).mean(axis=0) / 2]) for y in calm])
        print(f'{s} rows without a seam: mean {a.mean():+.2f}, darker > 8 {100 * (a < -8).mean():.1f} %, brighter > 8 {100 * (a > 8).mean():.1f} %')
