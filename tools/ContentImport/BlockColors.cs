using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Renesance.Content;
using Renesance.Content.Format;

namespace Renesance.Import;

// Maps voxel colours to registry blocks by the average colour of each block's textures.
public sealed class BlockColors
{
	readonly List<(string Name, float R, float G, float B)> palette = new();
	readonly Dictionary<uint, string> cache = new();

	public BlockColors(BlockRegistry reg, string textureRoot, IReadOnlySet<string> skip)
	{
		foreach (var d in reg.All)
		{
			// Blocks with special rendering never get picked by colour; the per-category rules choose them explicitly.
			if (d.Entry is not { Faces: { } faces, Surface: Surface.Opaque, Tint: null } || d.Light != null || skip.Contains(d.Name)) continue;
			var c = Enum.GetValues<FaceDir>().Select(f => Png.Average(Path.Combine(textureRoot, ContentPaths.TextureFile(faces.Get(f).Texture)))).ToArray();
			palette.Add((d.Name, c.Average(v => v.R), c.Average(v => v.G), c.Average(v => v.B)));
		}
	}

	public string Nearest(uint rgba)
	{
		if (cache.TryGetValue(rgba, out var hit)) return hit;
		var (r, g, b) = Rgb(rgba);
		// "redmean" weighted RGB distance.
		string best = null; float bestD = float.MaxValue;
		foreach (var p in palette)
		{
			float rm = (r + p.R) / 2, dr = r - p.R, dg = g - p.G, db = b - p.B;
			float d = (2 + rm) * dr * dr + 4 * dg * dg + (3 - rm) * db * db;
			if (d < bestD) { bestD = d; best = p.Name; }
		}
		return cache[rgba] = best;
	}

	public static (float R, float G, float B) Rgb(uint rgba) => ((rgba & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f);

	public static (float H, float S, float V) Hsv(uint rgba)
	{
		var (r, g, b) = Rgb(rgba);
		float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
		float h = d == 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
		return ((h * 60 + 360) % 360, max == 0 ? 0 : d / max, max);
	}
}
