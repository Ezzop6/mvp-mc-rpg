using System;
using Renesance.World;

// Levels of detail (docs/v2.md §5.17): level L has cells of 2^L blocks; it is drawn from SwitchM[L] (pixel footprint, never inside the mid sphere) and its ring reaches Radius[L] of its own chunks; Godot-free.
public sealed record LodLayout(float PixelAngle, float LodPixels, float HorizonM, float[] SwitchM, int[] Radius)
{
	public int Levels => Radius.Length;
	public float SwitchBlocks(int level) => level < Levels ? SwitchM[level] / Coords.BlockMeters : float.MaxValue;
	public float RadiusM(int level) => Radius[level] * (Coords.ChunkSize * Coords.BlockMeters) * (1 << level);
}

public static class Lod
{
	public const int MaxLevels = 4, Margin = 2, Samples = 8, MaxRadius = 160; // MaxLevels: HitId bits 3-4; MaxRadius: chunks of a level (a ring of 324^3 slots, 0.27 GB of directory)
	public const float LastShare = 0.9f; // no further level when the last one starts this close to the horizon

	// d_1 = max(2 BS / (K a), mid floor), d_L = max(2^L BS / (K a), 2 d_{L-1}); a level per band until the horizon. refLevel > 0: that level's band is drawn one level finer (the lod-vs-finer reference).
	public static LodLayout Plan(float pixelAngle, float lodPixels, float horizonM, float midFloorM, int maxLevels = MaxLevels, int refLevel = 0)
	{
		maxLevels = Math.Clamp(maxLevels, 1, MaxLevels);
		horizonM = Math.Max(horizonM, midFloorM);
		var d = new float[MaxLevels + 1];
		for (int l = 1; l <= MaxLevels; l++)
		{
			float fp = (1 << l) * Coords.BlockMeters / (lodPixels * pixelAngle);
			d[l] = l == 1 ? Math.Max(fp, midFloorM) : Math.Max(fp, 2 * d[l - 1]);
		}
		int n = 1;
		while (n < maxLevels && d[n] < LastShare * horizonM) n++;
		var sw = new float[n];
		var r = new int[n];
		for (int l = 0; l < n; l++)
		{
			sw[l] = l == 0 ? 0 : d[l];
			float reach = l == n - 1 ? horizonM : d[l + 1];
			r[l] = Math.Min((int)MathF.Ceiling(reach / ((Coords.ChunkSize * Coords.BlockMeters) * (1 << l))) + Margin, MaxRadius);
		}
		if (refLevel > 0 && refLevel < n)
		{
			float end = refLevel == n - 1 ? horizonM : d[refLevel + 1];
			sw[refLevel] = refLevel == n - 1 ? float.MaxValue : d[refLevel + 1];
			r[refLevel - 1] = Math.Min((int)MathF.Ceiling(end / ((Coords.ChunkSize * Coords.BlockMeters) * (1 << (refLevel - 1)))) + Margin, MaxRadius);
		}
		return new LodLayout(pixelAngle, lodPixels, horizonM, sw, r);
	}

	// The level-L chunk holding L0 chunk c.
	public static ChunkPos Parent(ChunkPos c, int level) => level == 0 ? c : new(c.X >> level, c.Y >> level, c.Z >> level);

	// Sample offset k (0-7) within a cell of 2^L blocks: per axis floor(2^L / 4) or floor(3 2^L / 4) (L1: both blocks).
	public static (int X, int Y, int Z) SampleOffset(int level, int k)
	{
		int s = 1 << level, a = s / 4, b = 3 * s / 4;
		return ((k & 1) != 0 ? b : a, (k & 2) != 0 ? b : a, (k & 4) != 0 ? b : a);
	}

	// The sample index of a block's offset within its cell, -1 when it is no sample.
	public static int SampleOf(int level, int ox, int oy, int oz)
	{
		int s = 1 << level, a = s / 4, b = 3 * s / 4;
		int Axis(int o) => o == b ? 1 : o == a ? 0 : -1;
		int x = Axis(ox), y = Axis(oy), z = Axis(oz);
		return x < 0 || y < 0 || z < 0 ? -1 : x | y << 1 | z << 2;
	}
}
