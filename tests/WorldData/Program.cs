using System.IO.Compression;
using Renesance.Content;
using Renesance.Content.Format;
using Renesance.Save;
using Renesance.Tools;
using Renesance.World;
using System.Text.Json;

// Self-checks for core/data (world, save, content), exits 1 on the first failure. Run: dotnet run --project tests/WorldData
static void Check(bool ok, string what) { if (!ok) { Console.WriteLine("FAIL: " + what); Environment.Exit(1); } Console.WriteLine("ok   " + what); }

string root = ContentPaths.FindRoot();
var reg = BlockRegistry.Load(Path.Combine(root, ContentPaths.Mod(BlockId.BaseMod)));

// --- coordinates ---
Check(new BlockPos(-1, 0, 0).Chunk == new ChunkPos(-1, 0, 0), "block -1 is in chunk -1");
Check(new BlockPos(-Coords.ChunkSize, 0, 0).Chunk == new ChunkPos(-1, 0, 0) && new BlockPos(-Coords.ChunkSize - 1, 0, 0).Chunk == new ChunkPos(-2, 0, 0), "chunk boundary at -ChunkSize / -ChunkSize - 1");
Check(new BlockPos(-1, -1, -1).LocalIndex == Chunk.Index(Coords.ChunkSize - 1, Coords.ChunkSize - 1, Coords.ChunkSize - 1), "local index of -1,-1,-1 is the last of each axis");
Check(new ChunkPos(-1, 0, -6).Region == new RegionPos(-1, 0, -2), "chunk -> region floor division");
var cp = new ChunkPos(-7, 3, 12);
Check(cp.Region.ChunkAt(cp.LocalIndex) == cp, "region local index round-trip");
var rots = Enumerable.Range(0, 24).Select(r => new Rotation24((byte)r)).ToList();
Check(rots.Select(r => r.Perm).Distinct().Count() == 6 && rots.All(r => ((r.Perm.Y - r.Perm.X + 3) % 3 == 1 ? 1 : -1) * r.Signs.X * r.Signs.Y * r.Signs.Z == 1)
	&& Rotation24.Yaw.All(r => r.Perm.Y == 1 && r.Signs.Y == 1), "24 proper rotations, yaw keeps up");

// --- selection box (core/tools) ---
var sel = new Selection();
Check(!sel.Complete && sel.A == null && sel.B == null, "selection: empty is incomplete");
sel.Set(true, new BlockPos(-3, 2, 5));
Check(!sel.Complete, "selection: one corner is incomplete");
sel.Set(false, new BlockPos(1, 8, -2));
Check(sel.Complete && sel.Min == new BlockPos(-3, 2, -2) && sel.Size == (5, 7, 8) && sel.Volume == 280, "selection: min, size and volume");
Check(sel.Contains(new BlockPos(-3, 2, -2)) && sel.Contains(new BlockPos(1, 8, 5))
	&& !sel.Contains(new BlockPos(2, 8, 5)) && !sel.Contains(new BlockPos(-4, 2, -2)), "selection: contains corners, rejects outside");
Check(sel.Cells.Count() == 280 && sel.Cells.First() == new BlockPos(-3, 2, -2) && sel.Cells.Last() == new BlockPos(1, 8, 5), "selection: cells iterate the volume, x fastest");
Check(Selection.Normalized(new BlockPos(4, 0, 0), new BlockPos(4, 0, 0)).Size == (1, 1, 1), "selection: a single block is 1x1x1");
sel.Clear();
Check(!sel.Complete && !sel.Contains(new BlockPos(0, 0, 0)), "selection: clear empties it");

// --- registry ---
Check(reg[BlockRegistry.AirId].Name == BlockRegistry.AirName && reg[BlockRegistry.ObjectName].Invisible, "registry: air and the object anchor are engine blocks");
Check(reg.All.Count(d => d.Placeable) == reg.Content.Entries.Count(BlockLooks.HasLooks) && reg.Content.Entries.Where(e => !BlockLooks.HasLooks(e)).All(e => e.Kind == EntryKind.Entity),
	$"registry: every entry with a look ({reg.All.Count(d => d.Placeable)} blocks), only air-like entities left out");
var entryIds = reg.Content.Entries.Select(e => e.Id).ToHashSet();
Check(reg.All.Count(d => d.Item != null) == reg.Content.Items.Count(i => i.Item.Model != null) && reg.Content.Items.All(i => (i.Item.Block == null) != (i.Item.Model == null))
	&& reg.Content.Items.Where(i => i.Item.Block != null).All(i => entryIds.Contains(i.Item.Block)),
	$"items: {reg.All.Count(d => d.Item != null)} shown by their model, the rest name an existing block");
Check(reg.All.Skip(2).All(d => d.Entry.Kind == EntryKind.Volume || !BlockLooks.Resolve(d, d.Default).Empty), "every block's default state shows something");
Check(reg["base:test_fog_dense"].Entry is { Kind: EntryKind.Volume, Matter: Matter.Gas, Density: > 0, Tint: not null }, "fog: a volume of gas with a density and a colour");
Check(reg["base:water"].Liquid && reg["base:water"].Entry.Tint != null && reg["base:lava"].Liquid, "water and lava are liquids, water tinted");
var log = reg["base:oak_log"];
var axis = log.Param("axis");
Check(log.Orientation == axis && axis.Bits == 2 && axis.Value(log.Default) == "y", "log axis: 2 bits, default y");
var axisZ = axis.With(log.Default, "z");
Check(axisZ.Type == log.Id && axis.Value(axisZ) == "z" && axis.Get(axisZ) == 2, "param pack/unpack");
var furnace = reg["base:furnace"];
var litFurnace = furnace.Param("lit").With(furnace.Default, "true");
Check(!furnace.Lit(furnace.Default) && furnace.Lit(litFurnace) && furnace.Light.Range == 13 * BlockRegistry.LightMetersPerLevel, "furnace lights only when lit");
Check(reg["base:glowstone"].Lit(reg["base:glowstone"].Default) && reg["base:stone"].RenderMask == 0, "unconditional light, a block without states");
var grassBlock = reg["base:grass_block"];
var snowy = grassBlock.Param("snowy").With(grassBlock.Default, "true");
Check(grassBlock.RenderMask == grassBlock.Param("snowy").Mask && BlockLooks.Resolve(grassBlock, snowy).Cube.Side.Texture == "block/grass_block_snow"
	&& BlockLooks.Resolve(grassBlock, grassBlock.Default).Cube.Side.Overlay != null, "look cases put their states in the render mask, cube looks pick the cube");
var gmap = BlockFacing.Map(grassBlock, grassBlock.Default);
var gcube = BlockLooks.Resolve(grassBlock, grassBlock.Default).Cube;
Check(gcube != null && gcube.Get(gmap[3].Face) is { Texture: "block/grass_block_top", Tint: true }
	&& gcube.Get(gmap[2].Face) is { Texture: "block/dirt", Tint: false }
	&& gcube.Get(gmap[4].Face) is { Texture: "block/grass_block_side", Overlay: "block/grass_block_side_overlay", Tint: true },
	"grass cube resolves top tinted, bottom dirt, sides overlay (the ghost shows the real faces, not the fallback box)");
var fence = reg["base:oak_fence"];
var fenceNE = fence.Param("east").With(fence.Param("north").With(fence.Default, "true"), "true");
var fenceLook = BlockLooks.Resolve(fence, fenceNE);
Check(fenceLook.Cube == null && fenceLook.Parts.Length == 3 && fenceLook.Parts[2][0] is { Model: "oak_fence_side", Y: 90 }, "multipart: post and two sides");
Check(BlockLooks.Resolve(reg["base:poppy"], reg["base:poppy"].Default).Parts is [{ Length: 4 }], "variants: one part, a weighted choice");
Check(BlockLooks.Resolve(reg["base:chest"], reg["base:chest"].Default).Cube?.All.Texture == "block/oak_planks", "entity placeholder is a cube");
var wall = reg["base:cobblestone_wall"];
Check(wall.Param("up").Value(wall.Default) == "true", "a wall stands as a post by default");
var water = reg["base:water"];
Check(BlockLooks.LiquidHeight(water, water.Default) == 1 && BlockLooks.LiquidHeight(water, water.Param("level").With(water.Default, "4")) == 4 / 9f
	&& BlockLooks.Resolve(water, water.Default).Cube.Side.Texture == "block/water_flow", "liquid: full source, lowered flow, still top and flowing sides");
bool Rejects(Entry e) { try { BlockRegistry.Define(e); return false; } catch (InvalidDataException) { return true; } }
var wide = new SortedDictionary<string, string[]> { ["a"] = Enumerable.Range(0, 300).Select(i => $"{i}").ToArray(), ["b"] = Enumerable.Range(0, 300).Select(i => $"{i}").ToArray() };
Check(Rejects(new Entry("x:y", EntryKind.Cube, States: wide)), "states over 16 bits are rejected");
Check(Rejects(new Entry("x:y", EntryKind.Cube, Light: new Light(7, "lit=maybe"), States: new() { ["lit"] = new[] { "false", "true" } }))
	&& Rejects(new Entry("x:y", EntryKind.Cube, Orientation: Orientation.Axis)), "bad light condition and orientation without state are rejected");

// --- orientation: faces and placement ---
var ew = BlockFacing.Map(log, axis.With(log.Default, "x"));
Check(ew[1].Face == FaceDir.Top && ew[0].Face == FaceDir.Bottom && ew[3].Face is not (FaceDir.Top or FaceDir.Bottom) && ew[3].Turns % 2 == 1,
	"log along x: rings east and west, bark on top turned a quarter");
var east = furnace.Param("facing").With(furnace.Default, "east");
var fm = BlockFacing.Map(furnace, east);
Check(fm[1].Face == FaceDir.Front && fm[0].Face == FaceDir.Back && fm[5].Face == FaceDir.Right && fm[4].Face == FaceDir.Left && fm[3] == (FaceDir.Top, 1),
	"furnace facing east: front east, its right hand south, top turned a quarter");
Check(BlockFacing.Map(furnace, furnace.Default).Select((m, i) => m.Turns == 0 && m.Face == new[] { FaceDir.Left, FaceDir.Right, FaceDir.Bottom, FaceDir.Top, FaceDir.Front, FaceDir.Back }[i]).All(x => x),
	"default orientation is the entry's faces unturned");
Check(axis.Value(BlockFacing.Place(log, (1, 0, 0), (0, -1, 0))) == "x" && axis.Value(BlockFacing.Place(log, (0, 0, 0), (0.2f, 0.1f, -0.9f))) == "z",
	"log placed along the clicked face's normal, else the look");
Check(furnace.Param("facing").Value(BlockFacing.Place(furnace, (0, 1, 0), (0.3f, -0.8f, -0.5f))) == "south", "furnace faces the viewer, horizontally");
var oakStairs = reg["base:oak_stairs"];
Check(oakStairs.Param("facing").Value(BlockFacing.Place(oakStairs, (0, 1, 0), (0.3f, -0.8f, -0.5f))) == "north", "stairs face along the look: the step rises away from the viewer");
var redBed = reg["base:red_bed"];
Check(redBed.Param("facing").Value(BlockFacing.Place(redBed, (0, 1, 0), (0.9f, -0.4f, 0.2f))) == "east", "a bed's head lies away from the viewer");
var observer = reg["base:observer"];
Check(observer.Param("facing").Value(BlockFacing.Place(observer, (0, 1, 0), (0.3f, -0.8f, -0.5f))) == "up", "facing block faces the viewer on all axes");
Check(BlockFacing.Place(reg["base:stone"], (1, 0, 0), (1, 0, 0)) == reg["base:stone"].Default, "no orientation: default state");

// --- model mesher (core/data/content, no Godot) ---
var oneVoxel = new VoxelModelBuilder("one");
oneVoxel.Set(0, 0, 0, VoxelMaterial.FromRgba(255, 0, 0));
var meshLib = new ModelLibrary();
int oneModel = meshLib.Add(oneVoxel);
var meshVerts = new List<MeshVertex>();
Check(ModelMesher.Append(meshVerts, meshLib, oneModel, new Rotation24(0), 0, 0, 0, 1f, 100000) && meshVerts.Count == 36
	&& meshVerts.All(v => v.X is >= 0 and < 0.5f / ModelLibrary.Cell + 1e-6f && v.Rgba == 0xFF0000FF && v.Layer < 0), "mesher: one voxel is 6 outer faces (36 vertices) coloured by its material");
var twoVoxels = new VoxelModelBuilder("two");
twoVoxels.Set(0, 0, 0, VoxelMaterial.FromRgba(255, 0, 0));
twoVoxels.Set(1, 0, 0, VoxelMaterial.FromRgba(0, 255, 0));
int twoModel = meshLib.Add(twoVoxels);
meshVerts.Clear();
Check(ModelMesher.Append(meshVerts, meshLib, twoModel, new Rotation24(0), 0, 0, 0, 1f, 100000) && meshVerts.Count == 60, "mesher: the face between two solid voxels is skipped (10 faces)");
meshVerts.Clear();
Check(!ModelMesher.Append(meshVerts, meshLib, twoModel, new Rotation24(0), 0, 0, 0, 1f, 30) && meshVerts.Count == 30, "mesher: stops at the vertex cap");
meshVerts.Clear();
ModelMesher.Box(meshVerts, 0, 0, 0, 1, 1, 1, 0x3F, 0xFFFFFFFFu);
Check(meshVerts.Count == 36 && meshVerts.Max(v => v.X) == Coords.BlockMeters, "mesher: a full box is 6 faces one block wide");
meshVerts.Clear();
var cubeFaces = Enumerable.Range(0, 6).Select(d => new CubeFace(d + 3, -1, 0, 0xFFFFFFFFu)).ToArray();
ModelMesher.Cube(meshVerts, 0, 0, 0, 0x3F, cubeFaces);
Check(meshVerts.Count == 36 && meshVerts.Take(6).All(v => v.Layer == 3 && v.Overlay == -1)
	&& new[] { (0, 1), (1, 1), (1, 0), (0, 0) }.All(t => meshVerts.Take(6).Any(v => v.U == t.Item1 && v.V == t.Item2)),
	"cube mesher: six textured faces carry their layer, +Z UV has row 0 at the top (blocks.glsli face_uv)");
meshVerts.Clear();
ModelMesher.Cube(meshVerts, 0, 0, 0, 1, new[] { new CubeFace(3, -1, 1, 0xFFFFFFFFu) }.Concat(cubeFaces.Skip(1)).ToArray());
Check(meshVerts.Count == 6 && meshVerts[0] is { U: 1, V: 1, Layer: 3 }, "cube mesher: a quarter turn rotates the face UV");

// --- GPU block ids ---
var variants = new BlockVariants(reg, 8);
var logX = axis.With(log.Default, "x");
Check(variants.Id(BlockState.Air) == 0 && variants.Id(log.Default) == 1 && variants.Id(logX) == 2 && variants.Id(log.Default) == 1, "one GPU id per orientation, air 0");
var grass = reg["base:grass_block"];
Check(variants.Id(grass.Param("snowy").With(grass.Default, "true")) != variants.Id(grass.Default) && variants.State(2) == logX, "look states get their own id, ids map back");
Check(variants.Id(litFurnace) != variants.Id(furnace.Default) && variants.Count == 7, "lit and unlit get their own ids");
bool threw = false;
try { foreach (var d in reg.All.Skip(2).Take(10)) variants.Id(d.Default); } catch (InvalidOperationException) { threw = true; }
Check(threw && variants.Count == 8, "more variants than ids fail loudly");
var reserved = new BlockVariants(reg, 1 << 22);
reserved.ReserveDefaults();
var types = Enumerable.Range(0, reserved.Count).Select(i => (int)reserved.State(i).Type).ToArray();
Check(types.Zip(types.Skip(1)).All(p => p.First < p.Second) && reserved.Id(reg["base:stone"].Default) < reserved.Count && reserved.Id(reg["base:object"].Default) < reserved.Count,
	"reserved default ids in registry order (shader constants don't depend on the worlds loaded before)");
Console.WriteLine($"info variants the registry can have: {new BlockVariants(reg, 1 << 23).MaxCount}");
Check(variants.MaxCount == 8 && new BlockVariants(reg, 1 << 24).MaxCount >= reg.Count, "variant table bounded by the id bits and the registry's looks");

var strip = TextureAnimation.Parse("""{ "animation": { "frametime": 2, "interpolate": true } }""", 3);
Check(strip.Frames.SequenceEqual(new[] { 0, 1, 2 }) && strip.At(5) == (2, 0, 0.5f) && strip.At(6) == (0, 1, 0f), "animation: every tile in order, blend towards the next");
var timed = TextureAnimation.Parse("""{ "animation": { "frametime": 4, "frames": [{ "index": 1, "time": 2 }, 0, 7] } }""", 2);
Check(timed.Frames.SequenceEqual(new[] { 1, 0, 0 }) && timed.FrameTime == 2 && timed.At(3) == (0, 0, 0f), "animation: frame times as repeats, frames outside the strip dropped");
Check(TextureAnimation.Parse("""{ "animation": { "frametime": 5 } }""", 1) == null && TextureAnimation.Parse("""{ "texture": { "blur": true } }""", 4) == null,
	"animation: a single tile or no animation is still");

var world = new Dictionary<BlockPos, BlockState>();
BlockState At(BlockPos p) => world.GetValueOrDefault(p);
List<(BlockPos Pos, BlockState State)> Put(params (BlockPos, BlockState)[] sets)
{
	var changes = BlockRules.Apply(reg, At, sets);
	foreach (var (p, s) in changes) world[p] = s;
	return changes;
}
string V(BlockPos p, string state) => reg.Get(At(p)).Param(state).Value(At(p));
var o = new BlockPos(0, 0, 0);
world[new BlockPos(1, 0, 0)] = reg["base:stone"].Default;
Put((o, fence.Default));
Check(V(o, "east") == "true" && V(o, "north") == "false", "fence joins a solid cube");
var changed = Put((new BlockPos(0, 0, 1), fence.Default));
Check(V(o, "south") == "true" && V(new BlockPos(0, 0, 1), "north") == "true" && changed.Count == 2, "a new fence updates its neighbour");
Put((new BlockPos(0, 0, 2), reg["base:nether_brick_fence"].Default));
Check(V(new BlockPos(0, 0, 1), "south") == "false", "wooden and nether brick fences don't join");
Put((new BlockPos(1, 0, 0), BlockState.Air));
Check(V(o, "east") == "false", "breaking the cube disconnects the fence");
var door = reg["base:oak_door"];
var d0 = new BlockPos(5, 0, 0);
Put((d0, door.Default));
Check(At((d0 with { Y = 1 })) == door.Param("half").With(door.Default, "upper"), "placing a door places its upper half");
Put(((d0 with { Y = 1 }), BlockState.Air));
Check(At(d0) == BlockState.Air, "breaking the upper half breaks the door");
world[new BlockPos(6, 1, 0)] = reg["base:stone"].Default;
Put((new BlockPos(6, 0, 0), door.Default));
Check(At(new BlockPos(6, 0, 0)) == BlockState.Air && At(new BlockPos(6, 1, 0)) == reg["base:stone"].Default, "a door under a block is refused");
var bed = reg["base:white_bed"];
Put((new BlockPos(10, 0, 0), bed.Default));
Check(V(new BlockPos(10, 0, -1), "part") == "head" && V(new BlockPos(10, 0, -1), "facing") == "north", "a bed's head lies the way it faces");
Put((new BlockPos(10, 0, -1), BlockState.Air));
Check(At(new BlockPos(10, 0, 0)) == BlockState.Air, "breaking the head breaks the foot");
var stairs = reg["base:oak_stairs"];
var sf = stairs.Param("facing");
Put((new BlockPos(20, 0, 0), stairs.Default), (new BlockPos(20, 0, -1), sf.With(stairs.Default, "east")));
Check(V(new BlockPos(20, 0, 0), "shape") == "outer_right" && V(new BlockPos(20, 0, -1), "shape") == "straight", "stairs: a perpendicular stair in front makes an outer corner");
Put((new BlockPos(22, 0, 0), stairs.Default), (new BlockPos(22, 0, 1), sf.With(stairs.Default, "west")));
Check(V(new BlockPos(22, 0, 0), "shape") == "inner_left", "stairs: a perpendicular stair behind makes an inner corner");
Put((new BlockPos(30, 0, 0), wall.Default), (new BlockPos(30, 0, 1), wall.Default), (new BlockPos(30, 0, 2), wall.Default));
Check(V(new BlockPos(30, 0, 1), "up") == "false" && V(new BlockPos(30, 0, 1), "north") == "low" && V(new BlockPos(30, 0, 0), "up") == "true", "walls: a straight run drops the post");
var wire = reg["base:redstone_wire"];
Put((new BlockPos(40, 0, 0), wire.Default), (new BlockPos(41, 0, 0), wire.Default));
Check(V(new BlockPos(40, 0, 0), "east") == "side" && V(new BlockPos(40, 0, 0), "west") == "side" && V(new BlockPos(40, 0, 0), "north") == "none", "wire: a neighbour wire and the line through");

// --- chunk palette ---
var stone = reg["base:stone"].Default;
var c = new Chunk();
Check(c.IsUniform && c.Get(5, 5, 5) == BlockState.Air, "new chunk is uniform air");
c.Set(1, 2, 3, stone);
Check(c.IndexBits == 1 && c.Get(1, 2, 3) == stone && c.Get(0, 0, 0) == BlockState.Air, "first edit grows to 1 bit");
var rnd = new Random(1);
var expect = new BlockState[Coords.ChunkVolume];
for (int i = 0; i < expect.Length; i++) { expect[i] = new BlockState((ushort)(1 + rnd.Next(10)), (ushort)rnd.Next(30)); c.Set(i, expect[i]); }
Check(c.IndexBits == 16 && Enumerable.Range(0, expect.Length).All(i => c.Get(i) == expect[i]), "300 states grow to 16 bits, all values intact");
for (int i = 0; i < expect.Length; i++) c.Set(i, i % 2 == 0 ? stone : BlockState.Air);
c.Compact();
Check(c.PaletteCount == 2 && c.IndexBits == 1 && c.Get(0) == stone && c.Get(1) == BlockState.Air, "compact shrinks back to 1 bit");
for (int i = 0; i < expect.Length; i++) c.Set(i, stone);
c.Compact();
Check(c.IsUniform && c.Get(123) == stone, "compact to uniform drops the array");

// --- edits ---
var edits = new WorldEdits();
var ep = new BlockPos(-1, 30, -26);
edits.Set(ep, stone);
edits.Set(ep, BlockState.Air);
edits.Set(new BlockPos(0, 0, 0), stone);
Check(edits.Count == 2 && edits.TryGet(ep, out var es) && es == BlockState.Air && !edits.TryGet(new BlockPos(1, 0, 0), out _), "edit store: last write wins");
Check(edits.InChunk(ep.Chunk).Single() == (ep, BlockState.Air) && edits.Chunks.Count() == 2, "edit store: positions round-trip through the chunk");
var fluid = new FluidState(reg["base:water"].Id, 3);
edits.SetFluid(new BlockPos(0, 0, 0), fluid);
edits.SetFluid(new BlockPos(0, 100, 0), FluidState.None);
Check(edits.FluidCount == 2 && edits.Count == 2 && edits.Chunks.Count() == 3 && edits.TryGetFluid(new BlockPos(0, 0, 0), out var ef) && ef == fluid && edits.TryGet(new BlockPos(0, 0, 0), out var eb) && eb == stone
	&& !edits.TryGetFluid(ep, out _) && edits.FluidsInChunk(new BlockPos(0, 100, 0).Chunk).Single() == (new BlockPos(0, 100, 0), FluidState.None), "edit store: fluid beside the block, fluid-only chunk listed");

// --- chunk serialization ---
var typical = TypicalSurfaceChunk(reg);
var lit = new Chunk(stone);
lit.Set(7, stone with { Raw = log.Default.Raw });
lit.Amount = new byte[Coords.ChunkVolume]; lit.Amount[7] = 200;
lit.Entities = new() { [7] = new byte[] { 1, 2, 3 } };
var chunkBytes = new MemoryStream();
using (var cw = new BinaryWriter(chunkBytes, System.Text.Encoding.UTF8, leaveOpen: true)) { typical.WriteTo(cw); lit.WriteTo(cw); }
chunkBytes.Position = 0;
using (var cr = new BinaryReader(chunkBytes))
{
	Check(SameBlocks(Chunk.ReadFrom(cr), typical), "typical chunk equal after read");
	var l0 = Chunk.ReadFrom(cr);
	Check(SameBlocks(l0, lit) && l0.Amount[7] == 200 && l0.Entities[7].SequenceEqual(new byte[] { 1, 2, 3 }), "layers equal after read");
}

// --- region save/load ---
string dir = Path.Combine(Path.GetTempPath(), "renesance-worlddata-test");
if (Directory.Exists(dir)) Directory.Delete(dir, true);
Directory.CreateDirectory(dir);
var region = new Region(new RegionPos(-1, 0, 2));
int typicalChunk = new ChunkPos(-3, 1, 11).LocalIndex;
region.Edits[typicalChunk] = new() { [0] = stone, [Coords.ChunkVolume - 1] = BlockState.Air };
region.Edits[0] = new() { [7] = log.Default };
region.Instances.Add(new ObjectInstance(42, new(17), new BlockPos(-120, 30, 260)));
region.Instances.Add(new ObjectInstance(7, new(0), new BlockPos(-100, 31, 255), new byte[] { 9, 9 }));
region.Fluids[typicalChunk] = new() { [0] = fluid, [5] = FluidState.None };
region.Fluids[3] = new() { [9] = fluid };
var futureChunk = new Section((SectionTag)0x31585858, 7, new byte[] { 1, 2, 3 });
var futureRegion = new Section((SectionTag)0x32585858, 1, Array.Empty<byte>());
region.UnknownChunkSections[3] = new() { futureChunk };
region.UnknownSections.Add(futureRegion);
string path = Path.Combine(dir, Region.FileName(region.Pos));
region.Save(path);
var loaded = Region.Load(path);
Check(loaded.Pos == region.Pos && loaded.Edits.Count == 2 && loaded.Instances.Count == 2, "region header and counts");
Check(loaded.Edits[typicalChunk][0] == stone && loaded.Edits[typicalChunk][Coords.ChunkVolume - 1] == BlockState.Air && loaded.Edits[0][7] == log.Default, "sparse edits equal after load, air edits kept");
Check(loaded.Instances[0] with { Data = null } == region.Instances[0] && loaded.Instances[1].Data.SequenceEqual(new byte[] { 9, 9 }), "instances equal after load");
Check(loaded.Fluids.Count == 2 && loaded.Fluids[typicalChunk][0] == fluid && loaded.Fluids[typicalChunk][5] == FluidState.None && loaded.Fluids[3][9] == fluid && !loaded.Edits.ContainsKey(3), "v3: fluids equal after load, fluid-only chunk");
Check(loaded.UnknownChunkSections[3].Single() is { Version: 7 } u && u.Tag == futureChunk.Tag && u.Payload.SequenceEqual(futureChunk.Payload)
	&& loaded.UnknownSections.Single().Tag == futureRegion.Tag, "v3: unknown chunk and region sections skipped and kept");
string resaved = Path.Combine(dir, "resaved.bin");
loaded.Save(resaved);
Check(Region.Load(resaved).UnknownChunkSections[3].Single().Payload.SequenceEqual(futureChunk.Payload), "v3: unknown sections written back");
var v2 = Region.Load(Path.Combine(root, "tests/WorldData/region-v2.bin")); // written with 25^3 chunks
Check(v2.Pos == region.Pos && v2.Edits[typicalChunk][0] == new BlockState(5, 0) && v2.Edits[typicalChunk][25 * 25 * 25 - 1] == BlockState.Air && v2.Edits[0][7] == new BlockState(9, 3)
	&& v2.Instances.Count == 2 && v2.Instances[1].Data.SequenceEqual(new byte[] { 9, 9 }) && v2.Fluids.Count == 0, "v2 region fixture reads");

// --- name -> id remap ---
var header = new WorldHeader { Blocks = reg.NameTable() };
header.Dimensions[WorldHeader.DefaultDimension] = new DimensionInfo { Generator = "flat", GeneratorVersion = 1, Params = JsonSerializer.SerializeToElement(new { Surface = 42 }) };
header.Save(Path.Combine(dir, WorldHeader.FileName));
var mod = BlockRegistry.From(reg.Content with { Files = reg.Content.Files.Append(new EntryFile("test", new[] { new Entry("aaa:first", EntryKind.Cube) })).ToArray() }); // shifts every base id by one
var savedHeader = WorldHeader.Load(Path.Combine(dir, WorldHeader.FileName));
var remapped = Region.Load(path, mod.RemapFrom(savedHeader.Blocks));
var dim = savedHeader.Dimensions[WorldHeader.DefaultDimension];
Check(savedHeader.Dimensions.Count == 1 && dim.Generator == "flat" && dim.GeneratorVersion == 1 && dim.Params.Value.GetProperty("Surface").GetInt32() == 42, "world header round-trip");
string oldPath = Path.Combine(dir, "old-" + WorldHeader.FileName);
File.WriteAllText(oldPath, """{ "GeneratorVersion": 3, "GeneratorParams": { "Seed": 7 }, "Blocks": { "base:stone": 5 } }""");
var old = WorldHeader.Load(oldPath);
var oldDim = old.Dimensions[WorldHeader.DefaultDimension];
Check(oldDim.Generator == "base" && oldDim.GeneratorVersion == 3 && oldDim.Params.Value.GetProperty("Seed").GetInt32() == 7 && old.Blocks["base:stone"] == 5, "header without dimensions loads as one base dimension");
Check(mod.Get(remapped.Edits[typicalChunk][0]).Name == "base:stone" && mod.Get(remapped.Edits[0][7]).Name == "base:oak_log" && mod["base:stone"].Id == reg["base:stone"].Id + 1, "ids remapped by name");

// --- world save: sparse edits, remap, player state, bookmarks ---
string savesRoot = Path.Combine(dir, "saves");
var worldEdits = new WorldEdits();
worldEdits.Set(new BlockPos(-1, 30, -26), BlockState.Air);
worldEdits.Set(new BlockPos(300, -5, 7), furnace.Param("facing").With(furnace.Default, "east"));
worldEdits.Set(new BlockPos(301, -5, 7), stone);
worldEdits.SetFluid(new BlockPos(301, -5, 7), fluid);
worldEdits.SetFluid(new BlockPos(-1000, 0, 0), FluidState.None);
var ws = WorldSave.Create(savesRoot, "round trip");
ws.Header.Dimensions[WorldHeader.DefaultDimension] = new DimensionInfo { Generator = "flat", GeneratorVersion = 1 };
ws.Header.Player = new View(WorldHeader.DefaultDimension, new Vec3(1.5f, 20.25f, -3), 45, -10);
ws.Header.TimeOfDay = 18.5f; ws.Header.Day = 3;
ws.Header.Bookmarks.Add(new Bookmark { Name = "gi off", View = new View(WorldHeader.DefaultDimension, new Vec3(150, 3, 3.5f), -90, -30), TimeOfDay = 12, Day = 1, Preset = "dusk", Off = new[] { "GI" }, Test = true, Resolution = "1080p", Note = "shadow under glass" });
ws.Save(reg, worldEdits);
var stale = Path.Combine(ws.Dir, WorldHeader.DefaultDimension, Region.FileName(new RegionPos(9, 9, 9)));
File.WriteAllBytes(stale, new byte[] { 1 });
var keptFile = Path.Combine(ws.Dir, WorldHeader.DefaultDimension, Region.FileName(new BlockPos(301, -5, 7).Chunk.Region));
var withFuture = Region.Load(keptFile);
withFuture.UnknownChunkSections[0] = new() { futureChunk };
withFuture.Save(keptFile);
ws.Save(reg, worldEdits);
Check(Region.Load(keptFile).UnknownChunkSections[0].Single().Payload.SequenceEqual(futureChunk.Payload), "world save keeps unknown sections of the replaced region");
Check(!File.Exists(stale) && WorldSave.List(savesRoot).SequenceEqual(new[] { "round trip" }) && !WorldSave.ValidName("../x") && !WorldSave.ValidName(" x"), "world folder: listed, stale regions removed, names checked");
var opened = WorldSave.Open(ws.Dir);
var back = new WorldEdits();
opened.Load(mod, back);
Check(back.Count == 3 && back.TryGet(new BlockPos(-1, 30, -26), out var air) && air == BlockState.Air
	&& back.TryGet(new BlockPos(300, -5, 7), out var f) && mod.Get(f).Name == "base:furnace" && furnace.Param("facing").Value(f) == "east"
	&& back.TryGet(new BlockPos(301, -5, 7), out var st) && st.Type == stone.Type + 1, "world edits round-trip with remap");
Check(back.FluidCount == 2 && back.TryGetFluid(new BlockPos(301, -5, 7), out var bf) && mod[bf.Type].Name == "base:water" && bf.Level == 3
	&& back.TryGetFluid(new BlockPos(-1000, 0, 0), out var none) && none == FluidState.None, "fluid edits round-trip with remap");
var bm = opened.Header.Bookmarks.Single();
Check(opened.Header.Name == "round trip" && opened.Header.Player.Position == new Vec3(1.5f, 20.25f, -3) && opened.Header.Player.Yaw == 45 && opened.Header.TimeOfDay == 18.5f && opened.Header.Day == 3
	&& opened.Header.Blocks.Count == 4, "player state, time and used block names round-trip");
Check(bm.Name == "gi off" && bm.View.Position.X == 150 && bm.View.Pitch == -30 && bm.Preset == "dusk" && bm.Off.SequenceEqual(new[] { "GI" }) && bm.On == null && bm.TimeOfDay == 12 && bm.Day == 1
	&& bm.Test && Bookmark.Resolutions[bm.Resolution] == (1920, 1080) && bm.Note == "shadow under glass", "bookmark round-trip");
var renamed = WorldSave.Rename(savesRoot, "round trip", "renamed");
Check(WorldSave.List(savesRoot).SequenceEqual(new[] { "renamed" }) && WorldSave.Open(renamed.Dir).Header.Name == "renamed", "world renamed");
WorldSave.Delete(savesRoot, "renamed");
Check(!WorldSave.List(savesRoot).Any(), "world deleted");
threw = false;
try { new BlockRegistry().RemapFrom(savedHeader.Blocks); } catch (InvalidDataException) { threw = true; }
Check(threw, "unknown block names refuse to load");

// --- size of a typical modified chunk ---
long raw = Coords.ChunkVolume * 4L;
using var ms = new MemoryStream();
using (var b = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
using (var w = new BinaryWriter(b)) typical.WriteTo(w);
Console.WriteLine($"info typical surface chunk: palette {typical.PaletteCount}, {typical.IndexBits} bit, " +
	$"raw 32-bit {raw / 1024} KB, compressed {ms.Length} B; region file {new FileInfo(path).Length} B");
Check(ms.Length < 4096, "typical chunk compresses under 4 KB");
Console.WriteLine("all checks passed");

static bool SameBlocks(Chunk a, Chunk b) => Enumerable.Range(0, Coords.ChunkVolume).All(i => a.Get(i) == b.Get(i));

// Hilly surface through the chunk: stone below, dirt, grass on top, air above, plus a small player-built wall.
static Chunk TypicalSurfaceChunk(BlockRegistry reg)
{
	var c = new Chunk();
	for (int z = 0; z < Coords.ChunkSize; z++)
		for (int x = 0; x < Coords.ChunkSize; x++)
		{
			int h = 12 + (int)(4 * Math.Sin(x * 0.3) + 3 * Math.Cos(z * 0.25));
			for (int y = 0; y < Coords.ChunkSize; y++)
				c.Set(x, y, z, y > h ? BlockState.Air : y == h ? reg["base:grass_block"].Default : y > h - 3 ? reg["base:dirt"].Default : reg["base:stone"].Default);
			if (x == 10 && z < 10) for (int y = h + 1; y < h + 4 && y < Coords.ChunkSize; y++) c.Set(x, y, z, reg["base:bricks"].Default);
		}
	return c;
}
