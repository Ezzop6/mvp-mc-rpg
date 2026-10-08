using System;
using System.Collections.Generic;
using System.Text.Json;
using Renesance.Content;
using Renesance.Save;
using Renesance.World;

public static class Voxel
{
	public const float BlockSize = Coords.BlockMeters;
	public const float ChunkMeters = Coords.ChunkSize * BlockSize;   // 25^3 blocks
}

// Improved Perlin; gen.compute gets the same permutation table (World.Gpu header).
public sealed class Perlin
{
	public const int TableSize = 512;
	readonly int[] p = new int[TableSize];
	public int[] Table => p;

	public Perlin(int seed)
	{
		var a = new int[256];
		for (int i = 0; i < 256; i++) a[i] = i;
		var r = new Random(seed);
		for (int i = 255; i > 0; i--) { int j = r.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
		for (int i = 0; i < TableSize; i++) p[i] = a[i & 255];
	}

	static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
	static float Lerp(float t, float a, float b) => a + t * (b - a);

	static float Grad(int h, float x, float y, float z)
	{
		h &= 15;
		float u = h < 8 ? x : y;
		float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
		return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
	}

	public float Noise(float x, float y, float z)
	{
		int X = (int)MathF.Floor(x), Y = (int)MathF.Floor(y), Z = (int)MathF.Floor(z);
		x -= X; y -= Y; z -= Z;
		X &= 255; Y &= 255; Z &= 255;
		float u = Fade(x), v = Fade(y), w = Fade(z);
		int A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
		int B = p[X + 1] + Y, BA = p[B] + Z, BB = p[B + 1] + Z;
		return Lerp(w,
			Lerp(v, Lerp(u, Grad(p[AA], x, y, z), Grad(p[BA], x - 1, y, z)),
					Lerp(u, Grad(p[AB], x, y - 1, z), Grad(p[BB], x - 1, y - 1, z))),
			Lerp(v, Lerp(u, Grad(p[AA + 1], x, y, z - 1), Grad(p[BA + 1], x - 1, y, z - 1)),
					Lerp(u, Grad(p[AB + 1], x, y - 1, z - 1), Grad(p[BB + 1], x - 1, y - 1, z - 1))));
	}
}

public sealed record Climate(float Temperature, float Humidity)
{
	public static readonly Climate Plains = new(0.8f, 0.4f);
}

// The world generator as core sees it (docs/architecture.md, generator interface); one instance per seed and parameters.
public abstract class WorldGenerator
{
	public Perlin Perlin { get; }

	protected WorldGenerator(int seed) => Perlin = new Perlin(seed);

	static readonly Dictionary<string, Func<JsonElement?, WorldGenerator>> factories = new()
	{
		["flat"] = p => new FlatGenerator(Params<FlatParams>(p)),
		["showcase"] = _ => new ShowcaseGenerator(),
		["random"] = p => new RandomGenerator(Params<RandomParams>(p)),
	};

	public static WorldGenerator Create(string id, JsonElement? parameters) =>
		factories.TryGetValue(id, out var f) ? f(parameters) : throw new ArgumentException($"unknown generator '{id}' (known: {string.Join(", ", factories.Keys)})");

	static T Params<T>(JsonElement? p) where T : new() => p is JsonElement e ? e.Deserialize<T>(WorldHeader.Json) : new T();

	public virtual int Version => 1; // bump when the same seed and parameters give other terrain
	public virtual object Settings => null; // the parameters object, its [Setting] fields show in the debug menu
	public virtual string BufferKey => null; // LoadBuffers depends on it: a change rebuilds the GPU side
	public abstract string ShaderModule { get; }
	public abstract IEnumerable<string> Blocks { get; } // block names the shader code uses as B_* constants
	public abstract int FeatureCandidates { get; }
	public abstract string ShaderConstants();
	public abstract byte[] ShaderParams();
	public abstract byte[][] LoadBuffers(BlockVariants variants);
	public abstract (int y0, int y1) ChunkYRange();
	public abstract float SurfaceHeight(float x, float z);
	public virtual string Describe(BlockRegistry registry, BlockPos p) => null;
	public virtual Climate Climate => Climate.Plains;
	public virtual string ClimateShader() =>
		FormattableString.Invariant($"vec2 gen_climate(ivec2 column) {{ return vec2({Climate.Temperature:0.0###}, {Climate.Humidity:0.0###}); }}\n");
}
