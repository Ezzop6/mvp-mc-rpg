using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Renesance.Content;

public sealed record TextureAnimation(int[] Frames, int FrameTime, bool Interpolate)
{
	public const float TicksPerSecond = 20f;

	public static TextureAnimation Parse(string mcmeta, int tiles)
	{
		if (JsonNode.Parse(mcmeta)?["animation"] is not JsonObject a) return null;
		int time = Math.Max(1, a["frametime"]?.GetValue<int>() ?? 1);
		var frames = a["frames"] is JsonArray list
			? list.Select(f => f is JsonObject o ? (o["index"].GetValue<int>(), Math.Max(1, o["time"]?.GetValue<int>() ?? time)) : (f.GetValue<int>(), time))
			: Enumerable.Range(0, tiles).Select(i => (i, time));
		var valid = frames.Where(f => f.Item1 >= 0 && f.Item1 < tiles).ToList();
		if (valid.Select(f => f.Item1).Distinct().Count() < 2) return null;
		int unit = valid.Aggregate(time, (g, f) => Gcd(g, f.Item2));
		return new TextureAnimation(valid.SelectMany(f => Enumerable.Repeat(f.Item1, f.Item2 / unit)).ToArray(), unit, a["interpolate"]?.GetValue<bool>() == true);
	}

	public (int Frame, int Next, float Blend) At(double ticks)
	{
		long slot = (long)Math.Floor(ticks / FrameTime);
		int i = (int)(((slot % Frames.Length) + Frames.Length) % Frames.Length);
		return (Frames[i], Frames[(i + 1) % Frames.Length], Interpolate ? (float)(ticks / FrameTime - slot) : 0f);
	}

	static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
