using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Content.Format;
using Renesance.World;

namespace Renesance.Content;

public sealed record VariantLook(Faces Cube, Look[][] Parts)
{
	public bool Empty => Cube == null && Parts.Length == 0;
}

public static class BlockLooks
{
	public const string LevelState = "level", ParticleTexture = "particle", StillTexture = "still", FlowTexture = "flow";

	public static bool HasLooks(Entry e) => e.Kind is EntryKind.Cube or EntryKind.Liquid or EntryKind.Volume || e.Variants?.Length > 0 || e.Multipart?.Length > 0;

	public static string Particle(Entry e) => e.Textures?.GetValueOrDefault(ParticleTexture) ?? e.Textures?.Values.FirstOrDefault();

	public static Faces LiquidFaces(Entry e)
	{
		string still = e.Textures?.GetValueOrDefault(StillTexture) ?? Particle(e), flow = e.Textures?.GetValueOrDefault(FlowTexture) ?? still;
		Face F(string t) => t == null ? null : new Face(t, Tint: e.Tint != null, Emission: e.Emission);
		return new Faces(Top: F(still), Bottom: F(still), Side: F(flow));
	}

	public static IEnumerable<Faces> AllFaces(Entry e) =>
		new[] { e.Faces, e.Kind == EntryKind.Liquid ? LiquidFaces(e) : null }.Concat(e.Cubes?.Values ?? Enumerable.Empty<Faces>()).Where(f => f != null);

	public static IEnumerable<string> StatesUsed(Entry e) =>
		(e.Variants ?? Array.Empty<LookCase>()).SelectMany(c => c.When?.Keys ?? Enumerable.Empty<string>())
			.Concat((e.Multipart ?? Array.Empty<LookPart>()).SelectMany(p => (p.When ?? Array.Empty<SortedDictionary<string, string>>()).SelectMany(w => w.Keys)))
			.Concat(e.Kind == EntryKind.Liquid ? new[] { LevelState } : Array.Empty<string>()).Distinct();

	static bool Holds(BlockDef d, BlockState s, SortedDictionary<string, string> when) =>
		when == null || when.All(c => d.Params.FirstOrDefault(p => p.Name == c.Key) is { } p && c.Value.Split('|').Contains(p.Value(s)));

	public static VariantLook Resolve(BlockDef d, BlockState s)
	{
		var e = d.Entry;
		if (e == null) return new VariantLook(null, Array.Empty<Look[]>());
		if (e.Kind == EntryKind.Liquid) return new VariantLook(LiquidFaces(e), Array.Empty<Look[]>());
		var parts = e.Variants?.FirstOrDefault(c => Holds(d, s, c.When)) is { } match ? new[] { match.Looks }
			: (e.Multipart ?? Array.Empty<LookPart>()).Where(p => p.When == null || p.When.Any(w => Holds(d, s, w))).Select(p => p.Looks).ToArray();
		var cube = parts.SelectMany(p => p).FirstOrDefault(l => l.Cube != null);
		if (cube != null || e.Kind == EntryKind.Cube)
			return new VariantLook(cube?.Cube is { } name && e.Cubes?.GetValueOrDefault(name) is { } f ? f : e.Faces, Array.Empty<Look[]>());
		return new VariantLook(null, parts.Select(p => p.Where(l => l.Model != null).ToArray()).Where(p => p.Length > 0).ToArray());
	}

	public static float LiquidHeight(BlockDef d, BlockState s) =>
		d.Liquid && d.Params.FirstOrDefault(p => p.Name == LevelState) is { } p && int.TryParse(p.Value(s), out int level) && level is >= 1 and <= 7 ? (8 - level) / 9f : 1f;
}
