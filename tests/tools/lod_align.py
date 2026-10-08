#!/usr/bin/env python3
# lod_align.py <out dir of a run>: alignment of the levels of detail (docs/v2.md §5.17). lod-default against lod-default-ref<r> (level r's band drawn one level finer)
# in the pixels its Lod view shows at level r where both rays hit: depth difference in blocks (Depth view, t * 2 as 16 bits in r, g). Median = a shift of the coarse
# surface along the ray, the share over 2 coarse cells = missing or misplaced content (a structure that is there in one and not in the other).
import sys, os
import numpy as np
from PIL import Image
d = sys.argv[1]
LOD_RGB = [(0.1, 0.8, 0.2), (0.9, 0.85, 0.1), (0.95, 0.45, 0.05), (0.85, 0.1, 0.1)]

def img(name): return np.asarray(Image.open(os.path.join(d, 'lod', name)).convert('RGB'), dtype=np.int32)

def depth(name):
    a = img(name)
    return np.where(a[..., 2] > 128, (a[..., 0] * 256 + a[..., 1]) / 2.0, np.nan)

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

lv, base = levels('lod-default.Lod.png'), depth('lod-default.Depth.png')
for r in (1, 2, 3):
    if not os.path.exists(os.path.join(d, 'lod', f'lod-default-ref{r}.Depth.png')): continue
    ref = depth(f'lod-default-ref{r}.Depth.png')
    m = (lv == r) & ~np.isnan(base) & ~np.isnan(ref)
    if not m.any(): print(f'L{r}: no pixels'); continue
    dt = (base - ref)[m]
    cell = 2 ** r
    print(f'L{r} band ({m.sum()} px): depth LOD - finer median {np.median(dt):+.2f} blocks, mean |d| {np.abs(dt).mean():.2f}, '
          f'|d| > 2 cells ({2 * cell} blocks) {100 * (np.abs(dt) > 2 * cell).mean():.2f} %, nearer {100 * (dt < -2 * cell).mean():.2f} %, farther {100 * (dt > 2 * cell).mean():.2f} %')
