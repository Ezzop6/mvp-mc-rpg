using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Content;
using Renesance.World;

public sealed record FlatLayer(string Block, int Thickness);

public sealed class FlatParams
{
	public FlatLayer[] Layers = { new("base:stone", 60), new("base:dirt", 3), new("base:grass_block", 1) };
	public int Surface = 0;
	public int? Water;
	public Climate Climate = Climate.Plains;
}

public sealed class FlatGenerator : WorldGenerator
{
	const string WaterBlock = "base:water";
	readonly FlatParams p;
	readonly int[] tops;

	public FlatGenerator(FlatParams p) : base(0)
	{
		if (p.Layers.Length == 0 || p.Layers.Any(l => l.Thickness <= 0)) throw new ArgumentException("flat generator: needs layers with a positive thickness");
		this.p = p;
		tops = new int[p.Layers.Length + 1];
		tops[^2] = p.Surface;
		for (int i = p.Layers.Length - 2; i >= 0; i--) tops[i] = tops[i + 1] - p.Layers[i + 1].Thickness;
		tops[^1] = Math.Max(p.Surface, p.Water ?? p.Surface);
	}

	public override string ShaderModule => "res://mods/base/generator/flat/flat.glsli";
	public override IEnumerable<string> Blocks => p.Layers.Select(l => l.Block).Append(WaterBlock);
	public override int FeatureCandidates => 0;

	public override string ShaderConstants()
	{
		var blocks = p.Layers.Select(l => Gpu.BlockConstant(l.Block)).Append(Gpu.BlockConstant(WaterBlock)).Append("0u");
		return $"const int FLAT_LAYERS = {p.Layers.Length};\n" +
			$"const int FLAT_TOP[FLAT_LAYERS + 1] = int[]({string.Join(", ", tops)});\n" +
			$"const uint FLAT_BLOCK[FLAT_LAYERS + 2] = uint[]({string.Join(", ", blocks)});\n";
	}

	public override byte[] ShaderParams() => new byte[16];
	public override byte[][] LoadBuffers(BlockVariants variants) => Array.Empty<byte[]>();

	public override (int y0, int y1) ChunkYRange() =>
		(Coords.FloorDiv(tops[0] - p.Layers[0].Thickness, Coords.ChunkSize), Coords.FloorDiv(tops[^1] - 1, Coords.ChunkSize) + 1);

	public override float SurfaceHeight(float x, float z) => tops[^1] * Voxel.BlockSize;
	public override Climate Climate => p.Climate ?? Climate.Plains;
}
