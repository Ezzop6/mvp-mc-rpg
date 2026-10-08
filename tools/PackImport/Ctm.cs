static class Ctm
{
	static readonly string[] SixFaces = { "top", "bottom", "north", "south", "east", "west" };
	static readonly string[] Conditions = { "matchBlocks", "biomes", "heights", "minHeight", "maxHeight", "name" };
	static readonly string[] Suffixes = { ".png", "_n.png", "_s.png", ".png.mcmeta" };

	public sealed record Replacement(string Texture, string Source);

	// Swaps each listed placeholder a CTM rule hides for its CTM tile, else for the base layer's texture.
	public static List<Replacement> ReplacePlaceholders(Assets pack, Assets vanilla, IEnumerable<string> placeholders, List<string> warnings, out int hiddenCount)
	{
		var hidden = HiddenBases(pack);
		hiddenCount = hidden.Count;
		var replaced = new List<Replacement>();
		foreach (var texture in placeholders.Where(t => pack.Layer($"textures/{t}.png") > 0))
		{
			if (!hidden.TryGetValue(texture, out var tile)) { warnings.Add($"{texture} is listed as a CTM placeholder but no CTM rule hides it"); continue; }
			string stem = $"textures/{texture}";
			foreach (var s in Suffixes)
				if (tile != null) pack.Use(stem + s, pack, tile[..^4] + s);
				else pack.Use(stem + s, vanilla, stem + s);
			replaced.Add(new(texture, tile ?? "base layer"));
		}
		return replaced;
	}

	// Pack block textures that unconditional CTM rules without <default> cover on all six faces, with the tile to show instead.
	static SortedDictionary<string, string> HiddenBases(Assets pack)
	{
		var hidden = new SortedDictionary<string, (HashSet<string> Faces, string Tile, int Covers)>(StringComparer.Ordinal);
		foreach (var rule in pack.Under("optifine/ctm/", ".properties"))
		{
			var p = new Dictionary<string, string>();
			foreach (var (k, v) in Mc.Properties(pack.Text(rule))) p[k] = v;
			if (Conditions.Any(p.ContainsKey) || p.GetValueOrDefault("method", "").StartsWith("overlay")) continue;
			var tiles = Tiles(p.GetValueOrDefault("tiles", ""), rule[..(rule.LastIndexOf('/') + 1)]).ToList();
			if (tiles.Contains("<default>")) continue;
			int first = p.GetValueOrDefault("method") is "horizontal" or "vertical" or "horizontal+vertical" or "vertical+horizontal" && tiles.Count > 3 ? 3 : 0;
			string tile = tiles.Skip(first).Concat(tiles).FirstOrDefault(t => t != "<skip>" && pack.Has(t));
			var faces = Faces(p.GetValueOrDefault("faces", "all"));
			foreach (var texture in p.GetValueOrDefault("matchTiles", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(BlockTexture))
			{
				if (texture == null || pack.Layer($"textures/{texture}.png") <= 0) continue;
				var h = hidden.TryGetValue(texture, out var old) ? old : (new HashSet<string>(), null, 0);
				h.Faces.UnionWith(faces);
				if (tile != null && faces.Count > h.Covers) h = (h.Faces, tile, faces.Count);
				hidden[texture] = h;
			}
		}
		return new(hidden.Where(h => h.Value.Faces.Count == SixFaces.Length).ToDictionary(h => h.Key, h => h.Value.Tile), StringComparer.Ordinal);
	}

	static string BlockTexture(string match)
	{
		match = match.Replace("minecraft:", "");
		if (match.StartsWith("textures/")) match = match["textures/".Length..];
		if (match.EndsWith(".png")) match = match[..^4];
		if (!match.Contains('/')) match = "block/" + match;
		return match.StartsWith("block/") ? match : null;
	}

	static IEnumerable<string> Tiles(string list, string dir)
	{
		foreach (var t in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			if (t is "<default>" or "<skip>") yield return t;
			else if (t.Split('-') is [var a, var b] && int.TryParse(a, out int from) && int.TryParse(b, out int to))
				for (int i = from; i <= to; i++) yield return $"{dir}{i}.png";
			else
			{
				string path = t.Replace("minecraft:", "");
				path = path.StartsWith("~/") ? "optifine/" + path[2..] : path.Contains('/') ? path : dir + path;
				yield return path.EndsWith(".png") ? path : path + ".png";
			}
	}

	static HashSet<string> Faces(string list) => list.Split(' ', StringSplitOptions.RemoveEmptyEntries).SelectMany(f => f switch
	{
		"all" => SixFaces,
		"sides" => SixFaces[2..],
		"up" => new[] { "top" },
		"down" => new[] { "bottom" },
		_ => new[] { f },
	}).ToHashSet();
}
