using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Content;
using static Renesance.Content.TemplateCategory;

namespace Renesance.Import;

// .vox object -> block template ("structure" mode: one voxel becomes Scale blocks; Scale < 1 merges voxels).
public static class VoxConverter
{
	// Target heights in blocks (0.5 m).
	public static float? TargetHeight(TemplateCategory category) => category switch
	{
		Tree => 24, Bush => 4, Rock => 6, House => 20, Ruin => 16, Monument => 100,
		Prop => 4, Light => 10, Vehicle => 6, Scene => 40, _ => null,
	};

	static readonly float[] scales = { 0.25f, 0.5f, 1, 2, 3 };

	public static float AutoScale(VoxObject o, TemplateCategory category)
	{
		if (TargetHeight(category) is not float target) return 1;
		int h = o.Voxels.Max(v => v.Y) - o.Voxels.Min(v => v.Y) + 1;
		return scales.OrderBy(s => Math.Abs(Math.Log(h * s / target))).First();
	}

	public static Template Convert(VoxObject o, float scale, Func<uint, string> block)
	{
		int minX = o.Voxels.Min(v => v.X), minY = o.Voxels.Min(v => v.Y), minZ = o.Voxels.Min(v => v.Z);
		int maxX = o.Voxels.Max(v => v.X), maxY = o.Voxels.Max(v => v.Y), maxZ = o.Voxels.Max(v => v.Z);
		if (scale >= 1)
		{
			int k = (int)scale;
			var t = new Template((maxX - minX + 1) * k, (maxY - minY + 1) * k, (maxZ - minZ + 1) * k);
			foreach (var v in o.Voxels)
			{
				ushort id = t.NameIndex(block(v.Rgba));
				for (int z = 0; z < k; z++) for (int y = 0; y < k; y++) for (int x = 0; x < k; x++)
					t[(v.X - minX) * k + x, (v.Y - minY) * k + y, (v.Z - minZ) * k + z] = id;
			}
			return t;
		}
		// Merge d^3 voxels: a block is solid when at least 3/8 of its voxels are (keeps thin parts), colour = most common.
		int d = (int)Math.Round(1 / scale);
		var cells = new Dictionary<(int, int, int), Dictionary<uint, int>>();
		foreach (var v in o.Voxels)
		{
			var key = ((v.X - minX) / d, (v.Y - minY) / d, (v.Z - minZ) / d);
			if (!cells.TryGetValue(key, out var c)) cells[key] = c = new();
			c[v.Rgba] = c.GetValueOrDefault(v.Rgba) + 1;
		}
		var tm = new Template((maxX - minX) / d + 1, (maxY - minY) / d + 1, (maxZ - minZ) / d + 1);
		foreach (var (key, c) in cells)
			if (c.Values.Sum() * 8 >= 3 * d * d * d)
				tm[key.Item1, key.Item2, key.Item3] = tm.NameIndex(block(c.MaxBy(e => e.Value).Key));
		return tm;
	}
}
