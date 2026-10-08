using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using Renesance.Content;
using Renesance.World;

public sealed class ShowcaseGenerator : WorldGenerator
{
	public const int BlockRow = 32, ModelRow = 24, DepthChunks = 4;
	const string FloorBlock = "base:smooth_stone", AxisBlock = "base:red_concrete";

	sealed record Shown(BlockDef Def, string Id, string Category, int InCategory, int CategoryCount, int Index);
	sealed record Quadrant(string Name, Shown[][] Rows, int Total);

	public static readonly (string Name, Func<BlockDef, bool> Shown)[] Sets = { ("blocks +X+Z", d => d.Placeable), ("items +X-Z", d => d.Item != null) };
	Quadrant[] layout;

	public ShowcaseGenerator() : base(0) { }

	static Quadrant[] Layout(BlockRegistry reg)
	{
		var order = reg.Content.Categories.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
		Quadrant Make(string name, Func<BlockDef, bool> shown)
		{
			int index = 0;
			var rows = reg.All.Where(shown)
				.GroupBy(d => d.Category)
				.OrderBy(g => g.Key != null && order.TryGetValue(g.Key, out var i) ? i : int.MaxValue)
				.SelectMany(g =>
				{
					var defs = g.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
					return defs.Select((d, i) => new Shown(d, d.Item?.Id ?? d.Name, g.Key, i + 1, defs.Length, ++index)).Chunk(BlockRow);
				})
				.ToArray();
			return new Quadrant(name, rows, index);
		}
		return Sets.Select(s => Make(s.Name, s.Shown)).ToArray();
	}

	public override string ShaderModule => "res://mods/base/generator/showcase/showcase.glsli";
	public override IEnumerable<string> Blocks => new[] { FloorBlock, AxisBlock };
	const int Cell = 5; // model lattice in blocks, independent of chunk and group size
	static readonly int Span = Coords.ChunkSize % Cell == 0 ? Coords.ChunkSize / Cell : (Coords.ChunkSize + Cell - 2) / Cell + 1; // lattice cells a chunk column can touch
	public override int FeatureCandidates => Span * Span;

	public override string ShaderConstants() =>
		$"const int SC_BLOCK_ROW = {BlockRow}, SC_MODEL_ROW = {ModelRow}, SC_CELL = {Cell}, SC_SPAN = {Span};\n" +
		$"const uint SC_FLOOR = {Gpu.BlockConstant(FloorBlock)}, SC_AXIS = {Gpu.BlockConstant(AxisBlock)}, GB_SHOWCASE_BLOCKS = WB_GEN_BUFFERS;\n";

	public override byte[] ShaderParams() => new byte[16];

	public override byte[][] LoadBuffers(BlockVariants variants)
	{
		var quadrants = layout = Layout(variants.Registry);
		var words = new List<uint> { 0 };
		foreach (var q in quadrants)
		{
			if (q != quadrants[0]) words[0] = (uint)words.Count;
			uint first = (uint)(words.Count + q.Rows.Length + 2);
			words.Add((uint)q.Rows.Length);
			foreach (var r in q.Rows) { words.Add(first); first += (uint)r.Length; }
			words.Add(first);
			words.AddRange(q.Rows.SelectMany(r => r.Select(s => (uint)variants.Id(s.Def.Default))));
		}
		return new[] { MemoryMarshal.AsBytes(words.ToArray().AsSpan()).ToArray() };
	}

	public override string Describe(BlockRegistry registry, BlockPos p)
	{
		if (p.Y != 0 || p.Z == 0) return null;
		var q = (layout ??= Layout(registry))[p.Z > 0 ? 0 : 1];
		int z = Math.Abs(p.Z), r = z / 2 - 1, c = p.X / 2 - 1;
		if (p.X < 2 || z < 2 || ((p.X | z) & 1) != 0 || r >= q.Rows.Length || c >= q.Rows[r].Length) return null;
		var s = q.Rows[r][c];
		return $"{s.Id} — {s.Category} {s.InCategory}/{s.CategoryCount} — {s.Index}/{q.Total}";
	}

	public override (int y0, int y1) ChunkYRange() => (-DepthChunks, 1);

	public override float SurfaceHeight(float x, float z) => 0;
}
