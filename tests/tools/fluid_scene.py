# Fluid test scene (docs/testing.md, list fluid): prints the --edits string for --gen=flat; ground top at y = 0.
e = []
def b(a, c, blk): e.append(f"{a[0]},{a[1]},{a[2]}..{c[0]},{c[1]},{c[2]}=base:{blk}")
def p(a, blk): e.append(f"{a[0]},{a[1]},{a[2]}=base:{blk}")
def pool(x, z, half, name, depth):
    b((x - half, -depth - 1, z - half), (x + half - 1, -depth - 1, z + half), "white_concrete")
    b((x - half, -depth, z - half), (x + half - 1, -1, z + half), name)
# Deep tanks (14 blocks) with objects on the bottom: water, honey, ink (see-through and glow from below).
DEEP = [("water", 0), ("honey", -34), ("ink", 34)]
for name, x in DEEP:
    pool(x, 0, 6, name, 14)
    b((x - 4, -14, -3), (x - 3, -12, -2), "gold_block")
    b((x + 1, -14, 1), (x + 2, -14, 2), "red_concrete")
    p((x - 1, -14, 3), "glowstone")
    p((x + 3, -14, -4), "torch")
# A shallow pool next to stained glass and a fog block (Transparency unchanged with Fluid:fluid).
pool(17, -1, 6, "water", 4)
b((12, 3, -4), (17, 3, 4), "lime_stained_glass")
b((19, 0, 3), (21, 2, 3), "test_fog_dense")
p((11, 0, 0), "torch")
# A row of water pools at increasing distance from a camera on the +x side: spans FluidFadeStart..FluidFadeEnd.
for z in [-20, -60, -120, -260, -500]:
    pool(80, z, 8, "water", 4)
    p((88, 0, z), "gold_block")
# Three rows receding from the overview camera: a farther row is wider (distance fade).
COLUMNS = [
    (-100, 14, ["water", "oil", "milk"]),
    (-320, 32, ["lava", "blood", "honey"]),
    (-700, 60, ["acid", "slime", "ink"]),
]
for z, half, names in COLUMNS:
    for i, name in enumerate(names):
        x = round(2.6 * half) * (i - 1)
        pool(x, z, half, name, 2)
print(";".join(e))
