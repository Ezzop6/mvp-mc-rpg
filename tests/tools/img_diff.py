#!/usr/bin/env python3
# img_diff.py <run dir a> <run dir b> <list/shot.png>...: pixels that differ between two runs' shots, share and max channel difference.
import sys, os
import numpy as np
from PIL import Image
a, b = sys.argv[1], sys.argv[2]
for s in sys.argv[3:]:
    x = np.asarray(Image.open(os.path.join(a, s)).convert('RGB'), dtype=np.int16)
    y = np.asarray(Image.open(os.path.join(b, s)).convert('RGB'), dtype=np.int16)
    d = np.abs(x - y).max(axis=2)
    print(f'{s}: {100 * (d > 0).mean():.4f} % pixels differ, over 8/255 {100 * (d > 8).mean():.4f} %, max {d.max()}')
