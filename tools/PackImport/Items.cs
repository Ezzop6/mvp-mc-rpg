using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Renesance.Content;
using Renesance.Content.Format;

sealed class ItemImport
{
	public static readonly CategoryDef[] Categories =
	{
		new("items", "Items"), new("tools", "Tools", "items"), new("weapons", "Weapons", "items"), new("armor", "Armor & Equipment", "items"),
		new("food", "Food", "items"), new("materials", "Materials", "items"), new("seeds", "Seeds", "items"), new("dyes", "Dyes", "items"),
		new("buckets_potions", "Buckets & Potions", "items"), new("vehicles", "Vehicles", "items"),
		new("collectibles", "Music & Collectibles", "items"), new("spawn_eggs", "Spawn eggs", "items"), new("other_items", "Other items", "items"),
	};

	static readonly HashSet<string> NotBlockItems = new(StringComparer.Ordinal) { "wheat" };

	static readonly (Regex Name, string Category)[] CategoryByName = new (string, string)[]
	{
		(@"_spawn_egg$", "spawn_eggs"),
		(@"_(helmet|chestplate|leggings|boots)$|^elytra$|_armor$|saddle$|harness$", "armor"),
		(@"_sword$|_spear$|^bow$|^crossbow$|^trident$|^mace$|arrow$|^wind_charge$|^fire_charge$", "weapons"),
		(@"_(pickaxe|axe|shovel|hoe)$|^shears$|^flint_and_steel$|^brush$|^fishing_rod$|^spyglass$|^lead$|^name_tag$|clock$|compass$|_on_a_stick$|^bundle$|_bundle$|^totem_of_undying$|^ender_pearl$|^ender_eye$|^firework_rocket$", "tools"),
		(@"bucket$|potion$|^experience_bottle$|^ominous_bottle$|^glass_bottle$|^dragon_breath$", "buckets_potions"),
		(@"boat$|raft$|minecart$", "vehicles"),
		(@"^(apple|golden_apple|enchanted_golden_apple|bread|cookie|pumpkin_pie|cake|carrot|golden_carrot|potato|baked_potato|poisonous_potato|beetroot|melon_slice|glistering_melon_slice|sweet_berries|glow_berries|dried_kelp|chorus_fruit|honey_bottle|spider_eye|rotten_flesh|egg|blue_egg|brown_egg)$|stew$|soup$|beef$|porkchop$|mutton$|chicken$|rabbit$|cod$|salmon$|tropical_fish$|pufferfish$", "food"),
		(@"_seeds$|^pitcher_pod$|^cocoa_beans$", "seeds"),
		(@"_dye$|^bone_meal$|ink_sac$", "dyes"),
		(@"music_disc|disc_fragment|_sherd$|banner_pattern$|book$|map$|smithing_template$|goat_horn$|^painting$|^trial_key$|^ominous_trial_key$|^armor_stand$|^firework_star$", "collectibles"),
		(@"ingot$|nugget$|^raw_|^(diamond|emerald|lapis_lazuli|redstone|quartz|amethyst_shard|coal|charcoal|string|bone|slime_ball|blaze_rod|blaze_powder|breeze_rod|leather|rabbit_hide|feather|flint|gunpowder|paper|stick|clay_ball|brick|nether_brick|resin_brick|glowstone_dust|prismarine_shard|prismarine_crystals|turtle_scute|armadillo_scute|honeycomb|phantom_membrane|ghast_tear|magma_cream|nether_star|shulker_shell|echo_shard|heart_of_the_sea|nautilus_shell|netherite_scrap|fermented_spider_eye|sugar|wheat|rabbit_foot|snowball|popped_chorus_fruit|sulfur|cinnabar)$", "materials"),
	}.Select(r => (new Regex(r.Item1), r.Item2)).ToArray();

	public static string Category(string name) => CategoryByName.FirstOrDefault(r => r.Name.IsMatch(name)).Category ?? "other_items";

	public readonly List<(string Category, ItemEntry Item)> Items = new();
	public readonly List<string> ImportFiles = new();
	public readonly ModelLibrary Library = new();
	public readonly SortedSet<string> Textures = new(StringComparer.Ordinal), NoSprite = new(StringComparer.Ordinal);
	public ItemFile[] Files => Items.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.Ordinal)
		.Select(g => new ItemFile(g.Key, g.Select(i => i.Item).ToArray(), EntrySource.Import)).ToArray();

	public ItemImport(Assets pack, Dictionary<string, JsonObject> models, string modDir, IEnumerable<(string Id, string Category)> importedBlocks,
		Voxelizer voxelizer, string ns, List<string> warnings)
	{
		static IEnumerable<(string Path, T File)> Read<T>(string dir) =>
			Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(f => (f, ContentSet.Read<T>(f))) : Enumerable.Empty<(string, T)>();
		var blockCategories = importedBlocks.Concat(Read<EntryFile>(Path.Combine(modDir, ContentSet.EntriesDir)).Where(f => f.File.Source == EntrySource.Hand)
			.SelectMany(f => f.File.Entries.Select(e => (e.Id, f.File.Category)))).ToDictionary(b => b.Id, b => b.Category, StringComparer.Ordinal);
		var handIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (path, file) in Read<ItemFile>(Path.Combine(modDir, ContentSet.ItemsDir)))
			if (file.Source == EntrySource.Import) ImportFiles.Add(path);
			else handIds.UnionWith(file.Entries.Select(e => e.Id));

		var built = new SortedDictionary<string, List<(string, uint)>>(StringComparer.Ordinal);
		foreach (var (name, def) in Mc.Load(pack, "items/", warnings).OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			string id = $"{ns}:{name}";
			if (handIds.Contains(id)) continue;
			if (blockCategories.TryGetValue(id, out var blockCategory) && !NotBlockItems.Contains(name))
			{
				Items.Add((blockCategory, new ItemEntry(id, Block: id)));
				continue;
			}
			var sprite = Candidates(def).Select(c => (c.Model, Layers: Layers(c.Model, c.Tints, models, pack))).FirstOrDefault(c => c.Layers.Count > 0);
			if (sprite.Model == null) { NoSprite.Add(name); continue; }
			built.TryAdd(sprite.Model, sprite.Layers);
			Textures.UnionWith(sprite.Layers.Select(l => l.Item1));
			Items.Add((Category(name), new ItemEntry(id, Model: sprite.Model, Layers: sprite.Layers.Select(l => l.Item1).ToArray())));
		}
		foreach (var (model, layers) in built) Library.Add(voxelizer.Extrude(model, layers));
		CheckExtrusion(pack, "item/stick");
		var taken = Files.Select(f => Path.Combine(modDir, ContentSet.ItemsDir, f.Category + ".json")).FirstOrDefault(p => File.Exists(p) && !ImportFiles.Contains(p));
		if (taken != null) throw new InvalidDataException($"{taken} is hand-written and has the name of an imported item file; rename it");
	}

	public string Summary(Assets pack, int topLayer, string libraryFile)
	{
		var sprites = Items.Where(i => i.Item.Model != null).ToList();
		var unused = pack.Under("textures/item/", ".png").Where(p => pack.Layer(p) == topLayer).Select(p => p["textures/".Length..^4])
			.Where(t => !t.EndsWith("_n") && !t.EndsWith("_s") && !Textures.Contains(t)).ToList();
		return $"\nItems {Items.Count}: block items {Items.Count - sprites.Count}, extruded {sprites.Count} ("
			+ string.Join(", ", sprites.GroupBy(i => i.Category).OrderBy(g => Array.FindIndex(Categories, c => c.Id == g.Key)).Select(g => $"{g.Key} {g.Count()}")) + ")\n"
			+ $"  Item model library {Catalog.ItemModelLibraryFile}: {Library.Models.Count} models, {Library.Materials.Count} materials, "
			+ $"pools {Library.Pools.Sum(p => p.Bytes) / 1024} kB, file {new FileInfo(libraryFile).Length / 1024} kB\n"
			+ $"  Without a sprite model {NoSprite.Count}: {string.Join(", ", NoSprite)}\n"
			+ $"  Top-layer item sprites no item uses: {unused.Count}\n";
	}

	void CheckExtrusion(Assets pack, string model)
	{
		int m = Library.Models.FindIndex(i => i.Name == model), n = ModelLibrary.Cell, voxels = 0;
		if (m < 0) return;
		for (int z = 0; z < n; z++) for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (Library.GetVoxel(m, x, y, z) != 0) voxels++;
		var (w, h, px) = Png.Decode(pack.Bytes($"textures/{model}.png"));
		int opaque = Enumerable.Range(0, w * h).Count(i => px[i * 4 + 3] >= 128) * (n / w) * (n / h);
		if (voxels != opaque) throw new InvalidDataException($"{model} extruded to {voxels} voxels, its sprite has {opaque} opaque texels at {n}x{n}");
	}

	static IEnumerable<(string Model, JsonArray Tints)> Candidates(JsonNode n)
	{
		if (n is JsonObject o)
		{
			if (o["type"]?.GetValue<string>() is "minecraft:model" or "model" && o["model"] is JsonValue m) yield return (Mc.Norm(m.GetValue<string>()), o["tints"] as JsonArray);
			foreach (var (_, v) in o) foreach (var c in Candidates(v)) yield return c;
		}
		else if (n is JsonArray a)
			foreach (var v in a) foreach (var c in Candidates(v)) yield return c;
	}

	static List<(string, uint)> Layers(string model, JsonArray tints, Dictionary<string, JsonObject> models, Assets pack)
	{
		var layers = new List<(string, uint)>();
		if (Mc.Elements(model, models) != null) return layers;
		var vars = Mc.TextureVars(model, models);
		for (int i = 0; vars.ContainsKey($"layer{i}"); i++)
			if (Mc.Resolve($"#layer{i}", vars) is { } t && pack.Has($"textures/{t}.png")) layers.Add((t, Tint(tints?.ElementAtOrDefault(i))));
		return layers;
	}

	static uint Tint(JsonNode t)
	{
		var v = t?["value"] ?? t?["default"];
		if (v is JsonArray rgb) return rgb.Select((c, i) => (uint)Math.Clamp(Math.Round(c.GetValue<double>() * 255), 0, 255) << 8 * i).Aggregate(0u, (a, b) => a | b);
		if (v is not JsonValue n || !n.TryGetValue(out int argb)) return 0xFFFFFF;
		return (uint)(argb >> 16 & 255 | argb & 0xFF00 | (argb & 255) << 16);
	}
}
