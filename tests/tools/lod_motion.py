#!/usr/bin/env python3
# motion.py <out dir of a run>: lod-fly-* and lod-turn-* (60 fps poses): |f(i+1) - 2 f(i) + f(i-1)| of luma per pixel, mean over the frames, by level band of
# lod-default.Lod.png and in the transition zones (pixels within W px of another level), docs/v2-mereni.md "Pohyb a blikání".
import sys, os, glob
import numpy as np
from PIL import Image
d = sys.argv[1]
W = 6
LOD_RGB = [(0.1, 0.8, 0.2), (0.9, 0.85, 0.1), (0.95, 0.45, 0.05), (0.85, 0.1, 0.1)]

def luma(p):
    a = np.asarray(Image.open(p).convert('RGB'), dtype=np.float32)
    return 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]

def levels(p):
    v = np.asarray(Image.open(p).convert('RGB'), dtype=np.float32) / 255.0
    out = np.full(v.shape[:2], -1, np.int8)
    best = np.full(out.shape, 1e9)
    for l, c in enumerate(LOD_RGB):
        for f in (1.0, 0.4):
            e = ((v - np.power(np.array(c) * f, 1 / 2.2)) ** 2).sum(axis=2)
            m = e < best
            best[m], out[m] = e[m], l
    out[v.sum(axis=2) < 0.03] = -1
    return out

lv = levels(os.path.join(d, 'lod', 'lod-default.Lod.png'))
edge = np.zeros(lv.shape, bool)
for dy in range(-W, W + 1):  # vertical neighbourhood: the bands are horizontal
    s = np.roll(lv, dy, axis=0)
    edge |= (s != lv) & (s >= 0) & (lv >= 0)
for seq in ('fly', 'turn'):
    files = sorted(glob.glob(os.path.join(d, 'lod-motion', f'lod-{seq}-*.png')))
    files = [f for f in files if not f.endswith(('.diff.png', '.shimmer.png'))]
    if len(files) < 3: continue
    f = [luma(p) for p in files]
    if f[0].shape != lv.shape: print(seq, 'size differs from lod-default'); continue
    acc = sum(np.abs(f[i + 1] - 2 * f[i] + f[i - 1]) for i in range(1, len(f) - 1)) / (len(f) - 2)
    parts = [f'L{l} {acc[(lv == l) & ~edge].mean():.2f}' for l in range(4) if ((lv == l) & ~edge).any()]
    parts += [f'edge L{l}/L{l + 1} {acc[edge & ((lv == l) | (lv == l + 1)) & band].mean():.2f}' for l in range(3)
              for band in [np.isin(np.roll(lv, W, 0), [l, l + 1]) & np.isin(np.roll(lv, -W, 0), [l, l + 1])] if (edge & ((lv == l) | (lv == l + 1)) & band).any()]
    print(f'{seq} ({len(files)} frames): ' + '  '.join(parts) + f'  all terrain {acc[lv >= 0].mean():.2f}')
