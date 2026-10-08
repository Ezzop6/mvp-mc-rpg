using System.Text.RegularExpressions;
using Renesance.Content.Format;
using static Renesance.Content.Format.EntryKind;

static class Rules
{
	public static readonly (Regex Template, EntryKind Kind)[] Kinds = new (string, EntryKind)[]
	{
		(@"block/(cube_mirrored_all|cube_north_west_mirrored_all)$", Cube), (@"block/cube_all$", Cube),
		(@"block/cube_column(_horizontal|_mirrored|_uv_locked_.)?$", Cube), (@"block/cube_bottom_top$", Cube), (@"block/cube_top$", Cube),
		(@"block/orientable(_with_bottom|_vertical)?$", Cube), (@"block/template_glazed_terracotta$", Cube), (@"block/template_command_block$", Cube),
		(@"block/(cube|cube_directional|cube_mirrored|cube_north_west_mirrored|observer)$", Cube), (@"block/leaves$", Cube),
		(@"block/(tinted_)?cross$", Cross), (@"block/(tinted_)?flower_pot_cross$|template_potted_azalea_bush", Potted), (@"block/crop$", Crop),
		(@"block/stem_(growth\d|fruit)$", Stem), (@"block/(inner_|outer_)?stairs$", Stairs), (@"block/slab(_top)?$", Slab),
		(@"block/template_(custom_)?fence_gate", FenceGate), (@"block/(custom_)?fence_", Fence), (@"block/(template_wall_(post|side|side_tall)|wall_inventory)$", Wall),
		(@"block/door_", Door), (@"block/template_(orientable_)?trapdoor_", Trapdoor), (@"block/template_glass_pane_", Pane), (@"block/carpet$", Carpet),
		(@"block/button", Button), (@"block/pressure_plate_", PressurePlate), (@"block/(rail_flat|rail_curved|template_rail_raised_)", Rail),
		(@"block/template_torch", Torch), (@"block/template_fire_", Fire), (@"block/template_(two_|three_|four_)?candles?$", Candle),
		(@"block/template_cake_with_candle$", Custom), (@"block/template_(hanging_)?lantern$", Lantern), (@"block/template_campfire$", Custom),
		(@"block/template_seagrass$", Cross), (@"block/template_single_face$", SingleFace), (@"block/redstone_dust_", RedstoneDust),
		(@"block/(template_piston|piston_extended)", Piston), (@"block/thin_block$", ThinBlock),
	}.Select(r => (new Regex(r.Item1), r.Item2)).ToArray();

	public static readonly CategoryDef[] Categories =
	{
		new("building", "Building"), new("stone", "Stone", "building"), new("soil", "Soil & Sand", "building"),
		new("ores", "Ores & Minerals", "building"), new("wood", "Wood", "building"), new("masonry", "Masonry", "building"),
		new("colored", "Colored", "building"), new("glass", "Glass & Ice", "building"), new("copper", "Copper", "building"),
		new("nether_end", "Nether & End", "building"),
		new("shapes", "Shapes"), new("stairs", "Stairs", "shapes"), new("slabs", "Slabs", "shapes"), new("walls", "Walls", "shapes"),
		new("carpets", "Carpets", "shapes"),
		new("doors_fences", "Doors & Fences"), new("doors", "Doors", "doors_fences"), new("trapdoors", "Trapdoors", "doors_fences"),
		new("fences", "Fences", "doors_fences"), new("gates", "Gates", "doors_fences"), new("panes", "Panes & Bars", "doors_fences"),
		new("plants", "Plants"), new("flowers", "Flowers", "plants"), new("saplings", "Saplings", "plants"), new("grass", "Grass & Ferns", "plants"),
		new("leaves", "Leaves", "plants"), new("mushrooms", "Mushrooms & Fungi", "plants"), new("vines", "Vines & Roots", "plants"),
		new("crops", "Crops & Stems", "plants"), new("tall_plants", "Cactus, Bamboo & Cane", "plants"), new("potted", "Potted", "plants"),
		new("water", "Water"), new("liquids", "Liquids", "water"), new("water_plants", "Water plants", "water"), new("coral", "Coral", "water"),
		new("light", "Light"), new("torches", "Torches", "light"), new("lanterns", "Lanterns", "light"), new("candles", "Candles", "light"),
		new("lamps", "Lamps", "light"), new("fire", "Fire & Campfire", "light"),
		new("functional", "Functional"), new("workstations", "Workstations", "functional"), new("storage", "Storage", "functional"),
		new("beds", "Beds", "functional"),
		new("redstone", "Redstone"), new("wiring", "Wire & Logic", "redstone"), new("buttons", "Buttons", "redstone"),
		new("plates", "Pressure plates", "redstone"), new("rails", "Rails", "redstone"), new("mechanisms", "Mechanisms", "redstone"),
		new("decoration", "Decoration"), new("signs", "Signs", "decoration"), new("banners", "Banners", "decoration"),
		new("heads", "Heads", "decoration"), new("decor", "Other decoration", "decoration"),
		new("technical", "Technical"), new("misc", "Uncategorized"),
	};

	static readonly Dictionary<EntryKind, string> CategoryByKind = new()
	{
		[Stairs] = "stairs", [Slab] = "slabs", [Wall] = "walls", [Carpet] = "carpets", [Door] = "doors", [Trapdoor] = "trapdoors",
		[Fence] = "fences", [FenceGate] = "gates", [Pane] = "panes", [Button] = "buttons", [PressurePlate] = "plates", [Rail] = "rails",
		[Torch] = "torches", [Lantern] = "lanterns", [Candle] = "candles", [Fire] = "fire", [Potted] = "potted", [Crop] = "crops",
		[Stem] = "crops", [RedstoneDust] = "wiring", [Piston] = "mechanisms", [Liquid] = "liquids",
	};

	static readonly (Regex Name, string Category)[] CategoryByName = new (string, string)[]
	{
		(@"^(air|cave_air|void_air|barrier|light|structure_block|structure_void|jigsaw|moving_piston|spawner|trial_spawner|vault)$|command_block|^test_", "technical"),
		(@"sign$", "signs"), (@"banner$", "banners"), (@"(head|skull)$", "heads"), (@"_bed$", "beds"), (@"coral", "coral"),
		(@"^bubble_column$", "liquids"), (@"pumpkin|melon$|hay_block", "crops"), (@"^iron_bars$", "panes"), (@"^cobweb$", "decor"),
		(@"kelp|seagrass|sea_pickle|lily_pad|frogspawn", "water_plants"), (@"_leaves$", "leaves"), (@"sapling|propagule", "saplings"),
		(@"glowstone|sea_lantern|froglight|shroomlight|redstone_lamp|jack_o_lantern|end_rod|copper_bulb|beacon|conduit|respawn_anchor", "lamps"),
		(@"poppy|dandelion|tulip|orchid|allium|azure_bluet|oxeye_daisy|cornflower|lily_of_the_valley|wither_rose|sunflower|lilac|rose_bush|peony|torchflower$|pitcher_plant|pink_petals|spore_blossom|eyeblossom|wildflowers|cactus_flower|flowering_azalea$", "flowers"),
		(@"^(short_grass|grass|tall_grass|fern|large_fern|dead_bush|bush|short_dry_grass|tall_dry_grass|leaf_litter|firefly_bush)$", "grass"),
		(@"vine|lichen|sculk_vein|hanging_roots|hanging_moss", "vines"),
		(@"wheat|carrots|potatoes|beetroots|nether_wart$|cocoa|sweet_berry|torchflower_crop|pitcher_crop", "crops"),
		(@"mushroom|fungus|(crimson|warped)_roots$|nether_sprouts", "mushrooms"),
		(@"cactus|bamboo$|bamboo_sapling|sugar_cane|dripleaf|azalea$|chorus_(plant|flower)", "tall_plants"),
		(@"_ore$|^raw_.*_block$|ancient_debris|^(coal|iron|gold|diamond|emerald|lapis|redstone|netherite|amethyst)_block$|amethyst_(cluster|bud)|_amethyst_bud", "ores"),
		(@"copper", "copper"),
		(@"redstone_wire|repeater|comparator|lever", "wiring"),
		(@"redstone|observer|dispenser|dropper|hopper|piston|daylight_detector|target|tripwire|sculk_sensor|sculk_shrieker|crafter|note_block|^tnt$|lightning_rod", "mechanisms"),
		(@"chest|barrel|shulker_box", "storage"),
		(@"fire$|campfire", "fire"),
		(@"crafting_table|smithing_table|cartography_table|fletching_table|loom|stonecutter|grindstone|furnace|smoker|anvil|lectern|enchanting_table|brewing_stand|cauldron|composter|bell$|beehive|bee_nest|jukebox|lodestone|bookshelf", "workstations"),
		(@"frame$|pot$|chain$|ladder|scaffolding|cake|candle|painting|heavy_core|dragon_egg|turtle_egg|sniffer_egg|creaking_heart|dried_ghast|shelf$", "decor"),
		(@"(_log|_wood|_planks|_stem|_hyphae|bamboo_block|bamboo_mosaic|mangrove_roots)$", "wood"),
		(@"wool$|concrete$|terracotta", "colored"),
		(@"dirt|grass_block|podzol|mycelium|mud$|clay$|gravel|sand$|concrete_powder|farmland|snow|moss_block|_path$|soul_soil", "soil"),
		(@"glass|ice$|slime_block|honey_block", "glass"),
		(@"netherrack|nylium|wart_block|end_stone|purpur|chorus|soul|magma|obsidian|nether|basalt|blackstone|end_portal|end_gateway", "nether_end"),
		(@"brick|cobblestone|sandstone|prismarine|quartz|smooth_stone|chiseled|polished|cut_|tiles", "masonry"),
		(@"stone|granite|diorite|andesite|deepslate|tuff|calcite|dripstone|bedrock|amethyst|sculk|resin|cinnabar|sulfur", "stone"),
	}.Select(r => (new Regex(r.Item1), r.Item2)).ToArray();

	public static string Category(string name, EntryKind kind) =>
		CategoryByKind.GetValueOrDefault(kind) ?? CategoryByName.FirstOrDefault(r => r.Name.IsMatch(name)).Category ?? "misc";

	static readonly string[] Woods = { "pale_oak", "dark_oak", "oak", "spruce", "birch", "jungle", "acacia", "mangrove", "cherry", "bamboo", "crimson", "warped", "poplar" };
	static readonly string[] Colors = { "light_blue", "light_gray", "white", "orange", "magenta", "yellow", "lime", "pink", "gray", "cyan", "purple", "blue", "brown", "green", "red", "black" };

	public static IEnumerable<string> NameTags(string name)
	{
		string bare = name.StartsWith("stripped_") ? name["stripped_".Length..] : name;
		if (Woods.FirstOrDefault(w => bare.StartsWith(w + "_")) is { } wood) yield return "wood:" + wood;
		if (Colors.FirstOrDefault(c => Regex.IsMatch(name, $"(^|_){c}_")) is { } color) yield return "color:" + color;
	}

	public static readonly Dictionary<string, (TintSource Source, string Color)> Tints = new()
	{
		["grass_block"] = (TintSource.Grass, null), ["grass"] = (TintSource.Grass, null), ["short_grass"] = (TintSource.Grass, null),
		["tall_grass"] = (TintSource.Grass, null), ["fern"] = (TintSource.Grass, null), ["large_fern"] = (TintSource.Grass, null),
		["potted_fern"] = (TintSource.Grass, null), ["sugar_cane"] = (TintSource.Grass, null), ["bush"] = (TintSource.Grass, null),
		["oak_leaves"] = (TintSource.Foliage, null), ["jungle_leaves"] = (TintSource.Foliage, null), ["acacia_leaves"] = (TintSource.Foliage, null),
		["dark_oak_leaves"] = (TintSource.Foliage, null), ["mangrove_leaves"] = (TintSource.Foliage, null), ["vine"] = (TintSource.Foliage, null),
		["leaf_litter"] = (TintSource.DryFoliage, null), ["water"] = (TintSource.Water, null), ["water_cauldron"] = (TintSource.Water, null),
		["bubble_column"] = (TintSource.Water, null), ["redstone_wire"] = (TintSource.Redstone, null), ["melon_stem"] = (TintSource.Stem, null),
		["pumpkin_stem"] = (TintSource.Stem, null), ["attached_melon_stem"] = (TintSource.Stem, null), ["attached_pumpkin_stem"] = (TintSource.Stem, null),
		["spruce_leaves"] = (TintSource.Constant, "#619961"), ["birch_leaves"] = (TintSource.Constant, "#80a755"), ["lily_pad"] = (TintSource.Constant, "#208030"),
	};

	public static readonly string[] CtmPlaceholders = { "block/grass_block_top", "block/stone" };

	public static readonly Dictionary<string, (string State, int Value)> UnlistedStateValues = new()
	{
		["composter"] = ("level", 0), ["pink_petals"] = ("flower_amount", 1), ["wildflowers"] = ("flower_amount", 1),
	};

	public static readonly Dictionary<TintSource, string> FixedTintColors = new()
	{
		[TintSource.Water] = "#3f76e4", [TintSource.Redstone] = "#ff3300", [TintSource.Stem] = "#e0c71c",
	};

	public static readonly (Regex Name, int Level, string When)[] Lights = new (string, int, string)[]
	{
		(@"^(beacon|conduit|end_gateway|end_portal|fire|glowstone|jack_o_lantern|lantern|lava|lava_cauldron|sea_lantern|shroomlight|\w+_froglight|light)$", 15, null),
		(@"^(\w+_)?copper_lantern$", 15, null), (@"^(campfire|redstone_lamp)$", 15, "lit=true"), (@"^respawn_anchor$", 15, "charges=4"),
		(@"^(end_rod|torch|wall_torch|(\w+_)?copper_(wall_)?torch)$", 14, null), (@"^cave_vines(_plant)?$", 14, "berries=true"),
		(@"^(waxed_)?copper_bulb$", 15, "lit=true"), (@"^(waxed_)?exposed_copper_bulb$", 12, "lit=true"),
		(@"^(waxed_)?weathered_copper_bulb$", 8, "lit=true"), (@"^(waxed_)?oxidized_copper_bulb$", 4, "lit=true"),
		(@"^(furnace|smoker|blast_furnace)$", 13, "lit=true"), (@"^nether_portal$", 11, null),
		(@"^(crying_obsidian|soul_fire|soul_lantern|soul_torch|soul_wall_torch)$", 10, null), (@"^soul_campfire$", 10, "lit=true"),
		(@"^(deepslate_)?redstone_ore$", 9, "lit=true"), (@"^(enchanting_table|ender_chest|glow_lichen)$", 7, null),
		(@"^redstone_(wall_)?torch$", 7, "lit=true"), (@"^sea_pickle$", 15, "pickles=4"), (@"^(sculk_catalyst|vault)$", 6, null),
		(@"^amethyst_cluster$", 5, null), (@"^large_amethyst_bud$", 4, null), (@"^trial_spawner$", 4, null), (@"^magma_block$", 3, null),
		(@"^((\w+_)?candle|(\w+_)?candle_cake)$", 3, "lit=true"), (@"^(medium_amethyst_bud|firefly_bush)$", 2, null),
		(@"^(small_amethyst_bud|brewing_stand|brown_mushroom|dragon_egg|end_portal_frame|sculk_sensor|calibrated_sculk_sensor)$", 1, null),
	}.Select(r => (new Regex(r.Item1), r.Item2, r.Item3)).ToArray();
}
