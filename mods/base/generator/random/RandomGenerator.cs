using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using Renesance.Content;
using Renesance.World;

// Saved in the world header; the debug menu edits the [Setting] fields (docs/architecture.md, Generátor).
public sealed class RandomParams
{
	[Setting(MenuTab.Generator, "Terrain", 0, 1000, 1)] public int Seed = 1337;
	[Setting(MenuTab.Generator, "Terrain", 0, 800, 1, Label = "Height (m)")] public float Height = 30;
	[Setting(MenuTab.Generator, "Terrain", 20, 2000, 10, Label = "Scale (m)")] public float Scale = 400;
	[Setting(MenuTab.Generator, "Terrain", 0, 1, 0.01f)] public float Roughness = 0.5f;
	[Setting(MenuTab.Generator, "Terrain", -100, 400, 0.5f, Label = "Water level (m)")] public float WaterLevel = -3;
	[Setting(MenuTab.Generator, "Terrain")] public bool Ridged;
	[Setting(MenuTab.Generator, "Terrain", 0, 4, 0.05f, Label = "Domain warp (x Scale, 0 = off)")] public float Warp;
	[Setting(MenuTab.Generator, "Terrain", 0, 50, 0.5f, Label = "Terrace step (m, 0 = off)")] public float Terraces;
	[Setting(MenuTab.Generator, "Shape", 0, 150, 1, Label = "Overhangs (m of 3D noise, 0 = off)")] public float Overhang;
	[Setting(MenuTab.Generator, "Shape", 5, 300, 1, Label = "Overhang scale (m)")] public float OverhangScale = 40;
	[Setting(MenuTab.Generator, "Shape", 0, 1, 0.01f, Label = "Caves (tunnel width, 0 = off)")] public float Caves;
	[Setting(MenuTab.Generator, "Shape", 5, 400, 1, Label = "Cave depth (m below the surface)")] public float CaveDepth = 60;
	[Setting(MenuTab.Generator, "Shape", 0, 1, 0.01f, Label = "Islands (floating, cover, 0 = off)")] public float Islands;
	[Setting(MenuTab.Generator, "Shape", -100, 900, 1, Label = "Island altitude (m)")] public float IslandAltitude = 120;
	[Setting(MenuTab.Generator, "Shape", 10, 400, 1, Label = "Island size (m)")] public float IslandSize = 80;
	[Setting(MenuTab.Generator, "Density", 0, 1, 0.01f, Label = "Trees, props (per 2.5 m cell)")] public float Trees = 0.12f;
	[Setting(MenuTab.Generator, "Density", 0, 1, 0.01f, Label = "Structures (per anchor)")] public float Structures = 0.3f;
	[Setting(MenuTab.Generator, "Density", 0, 1, 0.01f, Label = "Models (per 2.5 m cell)")] public float Models = 0.05f;
	[Setting(MenuTab.Generator, "Density", 0, 0.2f, 0.001f, Label = "Items (per surface block)")] public float Items = 0.002f;
	[Setting(MenuTab.Generator, "Density", 0, 0.2f, 0.001f, Label = "Blocks (any, per surface block)")] public float Blocks = 0.002f;
	[Setting(MenuTab.Generator, "Density", Label = "Categories (block category ids, comma separated, empty = all)")] public string Categories = "";

	// The random-crazy bookmark's look (debug menu preset).
	public static RandomParams Crazy => new()
	{
		Seed = 7, Height = 250, Scale = 700, Roughness = 0.55f, WaterLevel = 0, Ridged = true, Warp = 0.8f, Overhang = 6, OverhangScale = 25,
		Caves = 0.5f, CaveDepth = 10, Trees = 0.25f, Structures = 0.8f, Blocks = 0.01f,
	};
}

// Test world from the current block set: fbm terrain with optional 3D shape, trees and props, catalog structures, models, scattered blocks and items (random.glsli).
public sealed class RandomGenerator : WorldGenerator
{
	const int Octaves = 6, Fine = 5, Coarse = 25, FineReach = 5, CoarseReach = 2, CoarseAnchor = 2, LargeCell = 100, LargeN = 4, SmallHalf = 16, MaxLargeHalf = 110, WeightOne = 65536; // Fine, Coarse: anchor lattices in blocks, independent of the chunk size
	static readonly int FineN = Span(Fine) + 2 * FineReach, CoarseN = Span(Coarse) + 2 * CoarseReach;

	// Lattice cells a chunk column [col * ChunkSize, +ChunkSize) can touch.
	static int Span(int cell) => Coords.ChunkSize % cell == 0 ? Coords.ChunkSize / cell : (Coords.ChunkSize + cell - 2) / cell + 1;
	const float NoiseY = 0.37f, OctaveY = 1.7f, NoiseBound = 1.1f, FeatureRoom = 70f, WarpY = 11.3f, WarpY2 = 17.9f, IslandTop = 0.25f, IslandDepth = 0.25f, TerraceEdge = 0.7f, CaveScale = 24f;
	static readonly string[] Used =
	{
		"base:stone", "base:dirt", "base:grass_block", "base:sand", "base:snow_block", "base:water", "base:oak_log", "base:oak_leaves", "base:dark_oak_log", "base:dark_oak_leaves",
		"base:birch_log", "base:birch_leaves", "base:spruce_log", "base:spruce_leaves", "base:cobblestone", "base:mossy_cobblestone", "base:stone_bricks", "base:glowstone", "base:oak_planks", "base:cactus",
	};

	enum Buffer { Ids, TemplDir, TemplData }

	[StructLayout(LayoutKind.Sequential)]
	struct ParamsGpu
	{
		public float Height, Scale, Roughness, Water, Trees, Models, Structures, Items, Blocks, Ridged, Warp, Terraces;
		public float Overhang, OverhangScale, Caves, CaveDepth, Islands, IslandAltitude, IslandSize, Pad0;
	}

	readonly RandomParams p;

	public RandomGenerator(RandomParams p) : base(p.Seed) => this.p = p;

	public override object Settings => p;
	public override string BufferKey => p.Categories;
	public override string ShaderModule => "res://mods/base/generator/random/random.glsli";
	public override IEnumerable<string> Blocks => Used;
	public override int FeatureCandidates => FineN * FineN + CoarseN * CoarseN + LargeN * LargeN;

	public override string ShaderConstants()
	{
		const int chunk = Coords.ChunkSize;
		if (CoarseReach * Coarse + CoarseAnchor * Fine < SmallHalf || (CoarseReach + 1) * Coarse - CoarseAnchor * Fine <= SmallHalf)
			throw new InvalidOperationException("coarse feature candidates don't reach SmallHalf");
		if ((LargeN - 1) * LargeCell < 2 * (MaxLargeHalf + chunk - 1)) throw new InvalidOperationException("large feature candidates don't reach MaxLargeHalf");
		return FormattableString.Invariant($"const int RG_OCTAVES = {Octaves};\nconst float RG_NOISE_Y = {NoiseY:R}, RG_OCTAVE_Y = {OctaveY:R}, RG_WARP_Y = {WarpY:R}, RG_WARP_Y2 = {WarpY2:R}, RG_ISLAND_TOP = {IslandTop:R}, RG_ISLAND_DEPTH = {IslandDepth:R}, RG_TERRACE_EDGE = {TerraceEdge:R}, RG_CAVE_SCALE = {CaveScale:R}, RG_NOISE_BOUND = {NoiseBound:R};\n") +
			$"const int RG_FINE = {Fine}, RG_COARSE = {Coarse}, RG_FINE_REACH = {FineReach}, RG_FINE_N = {FineN}, RG_COARSE_REACH = {CoarseReach}, RG_COARSE_N = {CoarseN}, RG_COARSE_ANCHOR = {CoarseAnchor}, RG_LARGE_CELL = {LargeCell}, RG_LARGE_N = {LargeN}, RG_WEIGHT_ONE = {WeightOne};\n" +
			string.Concat(Enum.GetValues<Buffer>().Select(b => $"const uint GB_{Gpu.Snake(b.ToString())} = WB_GEN_BUFFERS + {(int)b}u;\n"));
	}

	public override byte[] ShaderParams()
	{
		var g = new ParamsGpu
		{
			Height = p.Height, Scale = p.Scale, Roughness = p.Roughness, Water = p.WaterLevel,
			Trees = p.Trees, Models = p.Models, Structures = p.Structures, Items = p.Items, Blocks = p.Blocks,
			Ridged = p.Ridged ? 1 : 0, Warp = p.Warp, Terraces = p.Terraces, Overhang = p.Overhang, OverhangScale = p.OverhangScale,
			Caves = p.Caves, CaveDepth = p.CaveDepth, Islands = p.Islands, IslandAltitude = p.IslandAltitude, IslandSize = p.IslandSize,
		};
		return MemoryMarshal.AsBytes(new ReadOnlySpan<ParamsGpu>(in g)).ToArray();
	}

	public override byte[][] LoadBuffers(BlockVariants variants)
	{
		var reg = variants.Registry;
		var inCategories = InCategories(reg);
		var shown = ShowcaseGenerator.Sets.Select(s => reg.All.Where(d => s.Shown(d) && inCategories(d)).Select(d => (uint)variants.Id(d.Default)).ToArray()).ToArray();
		var ids = new List<uint> { (uint)shown[0].Length, (uint)shown[1].Length };
		ids.AddRange(shown.SelectMany(s => s));
		var (dir, data) = Templates(variants, ids);
		var buffers = new byte[Enum.GetValues<Buffer>().Length][];
		buffers[(int)Buffer.Ids] = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(ids)).ToArray();
		buffers[(int)Buffer.TemplDir] = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(dir)).ToArray();
		buffers[(int)Buffer.TemplData] = data;
		return buffers;
	}

	Func<BlockDef, bool> InCategories(BlockRegistry reg)
	{
		var want = p.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
		if (want.Count == 0) return _ => true;
		var parent = reg.Content.Categories.ToDictionary(c => c.Id, c => c.Parent);
		return d =>
		{
			for (var c = d.Category; c != null; c = parent.GetValueOrDefault(c)) if (want.Contains(c)) return true;
			return false;
		};
	}

	// Catalog structures in two tiers (small on the Coarse grid, large on the LargeCell grid), palettes appended to ids.
	static (List<Vector4I> dir, byte[] data) Templates(BlockVariants variants, List<uint> ids)
	{
		string catalog = ProjectSettings.GlobalizePath($"res://{ContentPaths.Mod(BlockId.BaseMod)}/{Catalog.StructuresFile}"), dir = ProjectSettings.GlobalizePath("res://" + ContentPaths.Structures);
		var all = new List<(Template T, uint[] Palette, float Weight)>();
		foreach (var c in File.Exists(catalog) && Directory.Exists(dir) ? Catalog.Load<TemplateCategory>(catalog) : new())
		{
			if (Math.Max(c.Size[0], c.Size[2]) / 2 > MaxLargeHalf) continue;
			try
			{
				var t = Template.Load(Path.Combine(dir, c.File));
				if (t.Names.Count > byte.MaxValue) throw new InvalidDataException("more than 255 block names");
				all.Add((t, t.Names.Select(n => (uint)variants.Id(variants.Registry[n].Default)).ToArray(), c.Weight));
			}
			catch (Exception e) { GD.PushWarning($"[RandomGenerator] structure {c.Name} skipped: {e.Message}"); }
		}
		var small = all.Where(e => Math.Max(e.T.Sx, e.T.Sz) / 2 <= SmallHalf).ToList();
		var large = all.Except(small).ToList();
		var d = new List<Vector4I> { new(small.Count, large.Count, large.Count == 0 ? 0 : large.Max(e => Math.Max(e.T.Sx, e.T.Sz) / 2), small.Count == 0 ? 0 : small.Max(e => e.T.Sy)) };
		var data = new byte[(all.Sum(e => (long)e.T.Cells.Length) + 3) / 4 * 4 + 4];
		int at = 0;
		foreach (var tier in new[] { small, large })
		{
			float total = tier.Sum(e => e.Weight), acc = 0;
			foreach (var (t, palette, weight) in tier)
			{
				acc += weight;
				d.Add(new(at, t.Sx, t.Sy, t.Sz));
				d.Add(new((int)Math.Ceiling(acc / total * WeightOne), ids.Count, 0, 0));
				ids.AddRange(palette);
				foreach (var cell in t.Cells) data[at++] = (byte)cell;
			}
		}
		GD.Print($"[RandomGenerator] structures: {small.Count} small, {large.Count} large, {data.Length / 1048576} MB");
		return (d, data);
	}

	float MaxHeight => NoiseBound * p.Height + p.Terraces;
	float Bottom => -MaxHeight - NoiseBound * p.Overhang - (p.Caves > 0 ? p.CaveDepth : 0);
	float Top => Math.Max(MaxHeight + NoiseBound * p.Overhang, p.Islands > 0 ? p.IslandAltitude + IslandTop * IslandDepth * p.IslandSize : float.MinValue);

	public override (int y0, int y1) ChunkYRange() => (
		Mathf.FloorToInt((Math.Min(Bottom, p.WaterLevel) - 8) / Voxel.ChunkMeters),
		Mathf.CeilToInt((Math.Max(Top, p.WaterLevel) + FeatureRoom) / Voxel.ChunkMeters));

	// CPU copy of random.glsli height_m (the heightfield, without the 3D shape).
	public override float SurfaceHeight(float x, float z)
	{
		x /= p.Scale; z /= p.Scale;
		if (p.Warp > 0) (x, z) = (x + p.Warp * Perlin.Noise(x, WarpY, z), z + p.Warp * Perlin.Noise(x, WarpY2, z));
		float sum = 0, amp = 1, norm = 0;
		for (int i = 0; i < Octaves; i++)
		{
			float n = Perlin.Noise(x, NoiseY + i * OctaveY, z);
			if (p.Ridged) n = 2 * (1 - Math.Abs(n)) * (1 - Math.Abs(n)) - 1;
			sum += n * amp;
			norm += amp; amp *= p.Roughness; x *= 2; z *= 2;
		}
		float h = p.Height * sum / norm;
		if (p.Terraces > 0) h = p.Terraces * (MathF.Floor(h / p.Terraces) + Mathf.SmoothStep(TerraceEdge, 1f, h / p.Terraces - MathF.Floor(h / p.Terraces)));
		return Math.Max(h, p.WaterLevel);
	}
}
