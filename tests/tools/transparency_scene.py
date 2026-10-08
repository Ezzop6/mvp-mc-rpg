# Transparency test scene (docs/testing.md, list transparency): prints the --edits string for --gen=flat; ground top at y = 0.
e = []
def b(a, c, blk): e.append(f"{a[0]},{a[1]},{a[2]}..{c[0]},{c[1]},{c[2]}=base:{blk}")
def p(a, blk): e.append(f"{a[0]},{a[1]},{a[2]}=base:{blk}")
# 1 stained glass blocks and panes in front of a white wall with gold stripes
b((28, 0, 34), (40, 4, 34), "white_concrete")
b((28, 2, 34), (40, 2, 34), "gold_block")
for i, c in enumerate(["red", "lime", "blue", "yellow", "light_blue", "magenta", "orange"]):
    b((29 + i, 0, 38), (29 + i, 2, 38), f"{c}_stained_glass")
for i, c in enumerate(["red", "lime", "blue", "yellow"]):
    p((29 + 2 * i, 0, 41), f"{c}_stained_glass_pane")
    p((29 + 2 * i, 1, 41), f"{c}_stained_glass_pane")
# 2 glass roof on stone pillars over a white floor
b((43, 0, 28), (51, 0, 36), "white_concrete")
for x, z in [(43, 28), (51, 28), (43, 36), (51, 36)]:
    b((x, 1, z), (x, 4, z), "stone_bricks")
for i, c in enumerate(["red", "yellow", "lime", "light_blue", "blue", "purple", "magenta", "orange", "red"]):
    b((43 + i, 5, 28), (43 + i, 5, 36), f"{c}_stained_glass")
# 3 torch in a closed stone room behind a blue glass window, white floor outside
b((55, 0, 28), (59, 4, 32), "stone_bricks")
b((56, 1, 29), (58, 3, 31), "air")
p((57, 3, 30), "torch")
b((56, 1, 32), (56, 2, 32), "red_stained_glass")
b((57, 1, 32), (58, 2, 32), "blue_stained_glass")
b((54, 0, 33), (60, 0, 38), "white_concrete")
# 4 ice in front of gold
b((63, 0, 33), (66, 3, 33), "gold_block")
b((63, 0, 36), (63, 2, 36), "ice")
b((64, 0, 36), (64, 2, 36), "packed_ice")
b((65, 0, 36), (65, 2, 36), "blue_ice")
# 5 fog room: stone room with a doorway, coloured fog inside, a gold block and a torch; a dense fog cube outside
b((69, 0, 24), (77, 5, 32), "stone_bricks")
b((70, 1, 25), (76, 4, 31), "test_fog_colored")
b((72, 1, 32), (74, 3, 32), "air")
b((70, 1, 25), (71, 2, 26), "gold_block")
p((75, 1, 26), "torch")
b((80, 0, 35), (82, 2, 37), "test_fog_dense")
# 6 lanterns and torches as models at night
for x in (40, 44, 48):
    p((x, 0, 44), "oak_fence")
    p((x, 1, 44), "lantern")
p((42, 0, 45), "torch")
# 7 water pool with objects at the bottom, glass over part of it, a torch at its edge
b((14, -4, 28), (24, -1, 38), "water")
b((14, -5, 28), (24, -5, 38), "white_concrete")
b((16, -4, 30), (17, -3, 31), "gold_block")
b((20, -4, 34), (21, -4, 35), "red_concrete")
p((22, -4, 30), "glowstone")
p((13, 0, 33), "torch")
b((15, 3, 34), (18, 3, 37), "lime_stained_glass")
for x in range(19, 23):
    p((x, 0, 37), "red_stained_glass_pane")
print(";".join(e))
# The water-* bookmarks: the same world plus this (applied offline to a copy of the saved world).
LAKE = "-20,-20,10..5,-1,40=base:water;-10,-20,20..-9,-8,21=base:gold_block;-5,-20,30..-3,-18,32=base:red_concrete;-12,-20,30=base:glowstone;-2,-20,15..-2,-14,15=base:white_concrete;6,0,25=base:torch;-14,-3,28..-12,-3,28=base:light_blue_stained_glass_pane;-14,0,28..-12,1,28=base:light_blue_stained_glass_pane"
