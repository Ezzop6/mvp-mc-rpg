#!/usr/bin/env python3
# lod_vs_finer.py <out dir of a run> [K] [view]: lod-vs-finer per level band (docs/v2.md §5.17): <view> (default lod-default) against <view>-ref<r> inside the pixels its Lod view
# shows at level r, after a K x K box blur of both (both are one sample per pixel); RMSE in % of the range, the share of band pixels over 24/255 and the band's mean luminance (Rec. 709 of sRGB) LOD / finer.
import sys, os
import numpy as np
from PIL import Image
d, K = sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 2
v = sys.argv[3] if len(sys.argv) > 3 else 'lod-default'
Y = np.array([0.2126, 0.7152, 0.0722], np.float32)
LOD_RGB = [(0.1, 0.8, 0.2), (0.9, 0.85, 0.1), (0.95, 0.45, 0.05), (0.85, 0.1, 0.1)]

def img(name): return np.asarray(Image.open(os.path.join(d, 'lod', name)).convert('RGB'), dtype=np.float32)

def levels(lod_view):
    # nearest of the four level colours (gamma 1/2.2 as albedo_encode), -1 for black (sky); fallback (x 0.4) maps to the same level
    out = np.full(lod_view.shape[:2], -1, np.int8)
    v = lod_view / 255.0
    best = np.full(out.shape, 1e9)
    for l, c in enumerate(LOD_RGB):
        for f in (1.0, 0.4):
            e = ((v - np.power(np.array(c) * f, 1 / 2.2)) ** 2).sum(axis=2)
            m = e < best
            best[m], out[m] = e[m], l
    out[v.sum(axis=2) < 0.03] = -1
    return out

def blur(a):
    k = np.ones(K) / K
    a = np.apply_along_axis(lambda r: np.convolve(r, k, 'same'), 1, a)
    return np.apply_along_axis(lambda c: np.convolve(c, k, 'same'), 0, a)

lv = levels(img(f'{v}.Lod.png'))
base = img(f'{v}.png')
for r in (1, 2, 3):
    name = f'{v}-ref{r}.png'
    if not os.path.exists(os.path.join(d, 'lod', name)): continue
    ref = img(name)
    m = lv == r
    if m.sum() == 0: print(f'L{r}: no pixels'); continue
    a, b = blur(base.mean(axis=2)), blur(ref.mean(axis=2))
    diff = np.abs(a - b)[m]
    la, lb = (base @ Y)[m].mean(), (ref @ Y)[m].mean()
    print(f'L{r} band ({100 * m.mean():.2f} % of pixels): RMSE {100 * np.sqrt((diff ** 2).mean()) / 255:.2f} %, over 24/255 {100 * (diff > 24).mean():.3f} %, mean {diff.mean():.2f}, luminance {la:.1f} / {lb:.1f} ({100 * (la / lb - 1):+.1f} %)')
print('pixels per level:', ' '.join(f'L{l} {100 * (lv == l).mean():.1f} %' for l in range(4)), f'sky {100 * (lv == -1).mean():.1f} %')
