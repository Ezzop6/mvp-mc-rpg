// Converts .vox packs (assets/models/vox) into structures, the model library and mods/base definitions; args: [-- <project root>].
using Renesance.Import;
using Renesance.Content;
using static Renesance.Content.TemplateCategory;

string root = args.Length > 0 ? args[0] : ContentPaths.FindRoot();
string src = Path.Combine(root, ContentPaths.VoxSources), outDir = Path.Combine(root, ContentPaths.Structures), modDir = Path.Combine(root, ContentPaths.Mod(BlockId.BaseMod));
var reg = BlockRegistry.Load(modDir);
var colors = new BlockColors(reg, Path.Combine(root, ContentPaths.Textures), BaseMapping.TwoTone);
Directory.CreateDirectory(outDir);
foreach (var f in Directory.GetFiles(outDir, "*" + Template.Extension)) File.Delete(f);

const string Cc0 = "CC0 1.0", CcBy = "CC BY 4.0", Mit = "MIT";
const TemplateCategory ByContent = (TemplateCategory)(-1);
T? Prefix<T>(string file, params (string Prefix, T Cat)[] map) where T : struct =>
	map.Where(m => Path.GetFileName(file).StartsWith(m.Prefix, StringComparison.OrdinalIgnoreCase)).Select(m => (T?)m.Cat).FirstOrDefault();
Pack mmmm = new("mmmm", "Mini Mike's Metro Minis (Mike Judge)", CcBy), kytric = new("kytric", "Voxel Path And Terrain (Kytric)", Cc0),
	dhil = new("dhil", "Medieval Voxel Pack (Dhil, based on Quaternius, CC0)", Cc0), office = new("office", "Voxel Office Pack (Air Dinghy)", Cc0),
	medievalTheme = new("medieval-theme", "Medieval Theme Voxels (FuzzyManStudios)", Cc0);

// Cat: null = skip, ByContent = classify by content. Scale 0 = auto.
var packs = new (Pack Pack, string Dir, Func<string, TemplateCategory?> Cat, float Scale, float Weight)[]
{
	(new("ephtracy-monument", "ephtracy/voxel-model", Mit), "ephtracy/vox/monument",
		f => f.Contains("without-water") ? null : Monument, 1, 0.3f),
	(new("ephtracy-procedure", "ephtracy/voxel-model", Mit), "ephtracy/vox/procedure",
		f => Path.GetFileName(f) is "nature.vox" or "snow.vox" ? Scene : Path.GetFileName(f).StartsWith("menger") ? Monument : null, 1, 0.3f),
	(new("ephtracy-scan", "ephtracy/voxel-model", Mit), "ephtracy/vox/scan", f => f.Contains("dragon") ? Monument : null, 1, 0.2f),
	(mmmm, "mmmm/vox", f => Prefix(f,
		("obj_house", House), ("scene_house", House), ("obj_store", House), ("obj_story", House), ("scene_store", House),
		("scene_church", House), ("scene_coffe", House), ("scene_theate", House), ("scene_depot", House),
		("obj_tree", Tree), ("obj_planter", Bush), ("obj_mushroom", Bush), ("obj_stlight", Light), ("obj_trlight", Light),
		("obj_campfire", Light), ("obj_candl", Light), ("veh_", Vehicle), ("scene_park", Scene), ("scene_grav", Scene),
		("obj_statue", Monument), ("obj_fountain", Prop), ("obj_bench", Prop), ("obj_table", Prop), ("obj_chair", Prop),
		("obj_fence", Prop), ("obj_grave", Prop), ("obj_container", Prop), ("obj_cart", Prop), ("obj_trashcan", Prop),
		("obj_hydrant", Prop), ("obj_mailbox", Prop), ("obj_newsbox", Prop), ("obj_column", Ruin), ("obj_playgrnd", Prop),
		("obj_bussto", Prop), ("obj_celltowe", Monument), ("obj_arcade", Prop), ("obj_pumpkin", Prop), ("obj_sign", Prop)), 0, 1),
	(kytric, "kytric/vox", f => Path.GetFileName(Path.GetDirectoryName(f)) switch
	{
		"Trees" => Tree, "Bushes" or "Cactus" => Bush, "Stones" => Rock, "Mountains" => Rock, "Buildings" => House,
		"Bones" or "Wood" or "TrackEquip" => Prop, _ => Tile,
	}, 1, 1),
	(dhil, "dhil", f => Path.GetFileName(Path.GetDirectoryName(f)) switch
	{
		"Medieval_house" => Ruin, // modular wall/roof pieces, not whole houses: standalone they read as ruins
		"Nature" => ByContent, "Farm" or "Property" => Prop, _ => null,
	}, 0, 1),
	(office, "office", f => Path.GetFileName(f).StartsWith("lamp") ? Light : Prop, 0.25f, 0.5f),
	(medievalTheme, "medieval_theme", f => Prop, 0, 0.5f),
	(new("voxel-buildings", "Voxel Buildings (mehrasaur)", Cc0), "voxel_buildings", f => House, 1, 0.3f),
	(new("mountainlabs-trees", "Voxel Tree Pack (MountainLabs)", "free use (\"do whatever you want\")"), "trees", f => Tree, 0, 1.5f),
};

var catalog = new List<CatalogEntry<TemplateCategory>>();
var counts = new Dictionary<TemplateCategory, int>();
foreach (var p in packs)
	foreach (var file in Directory.GetFiles(Path.Combine(src, p.Dir), "*.vox", SearchOption.AllDirectories).Order())
	{
		var cat0 = p.Cat(file);
		if (cat0 == null) continue;
		List<VoxObject> objects;
		try { objects = VoxScene.Load(file); }
		catch (Exception e) { Console.WriteLine($"skip {file}: {e.Message}"); continue; }
		foreach (var o in objects.Where(o => o.Voxels.Count > 0))
		{
			var cat = cat0 == ByContent ? Classify(o) : cat0.Value;
			float scale = p.Scale > 0 ? p.Scale : VoxConverter.AutoScale(o, cat);
			var t = VoxConverter.Convert(o, scale, BaseMapping.Mapper(colors, cat));
			if (t.Cells.All(c => c == 0)) continue;
			foreach (var n in t.Names) _ = reg[n];
			string name = $"{p.Pack.Id}_{Slug(o.Name)}";
			t.Save(Path.Combine(outDir, name + Template.Extension));
			catalog.Add(new()
			{
				Name = name, File = name + Template.Extension, Category = cat, Weight = p.Weight, Size = new[] { t.Sx, t.Sy, t.Sz },
				Anchor = new[] { t.Sx / 2, 0, t.Sz / 2 }, Source = $"{p.Pack.Credit}: {Path.GetRelativePath(src, file)}", License = p.Pack.License,
			});
			counts[cat] = counts.GetValueOrDefault(cat) + 1;
		}
	}
Catalog.Save(Path.Combine(modDir, Catalog.StructuresFile), catalog);
foreach (var (c, n) in counts.OrderBy(e => e.Key.ToString())) Console.WriteLine($"{c.ToString().ToLowerInvariant(),-10} {n}");
Console.WriteLine($"total {catalog.Count}");

// Model packs: one .vox voxel = Scale^3 micro-voxels; Split = every object of the file is a model; prop = placed, item = thrown.
var modelPacks = new (Pack Pack, string Dir, Func<string, ModelCategory?> Cat, int Scale, bool Split)[]
{
	(office, "office", f => ModelCategory.Prop, 1, false),
	(dhil, "dhil/3D Voxel Medieval Pack/Source/Property", f => ModelCategory.Prop, 3, false),
	(dhil, "dhil/3D Voxel Medieval Pack/Source/Weapon", f => ModelCategory.Item, 1, false),
	(medievalTheme, "medieval_theme", f => ModelCategory.Prop, 2, true),
	(kytric, "kytric/vox", f => Path.GetFileName(Path.GetDirectoryName(f)) switch
	{
		"Stones" or "Bones" or "Wood" => ModelCategory.Item, _ => null,
	}, 3, false),
	(mmmm, "mmmm/vox", f => Prefix(f,
		("obj_bench", ModelCategory.Prop), ("obj_chair", ModelCategory.Prop), ("obj_table", ModelCategory.Prop),
		("obj_trashcan", ModelCategory.Prop), ("obj_mailbox", ModelCategory.Prop), ("obj_newsbox", ModelCategory.Prop),
		("obj_hydrant", ModelCategory.Prop), ("obj_grave", ModelCategory.Prop), ("obj_pumpkin", ModelCategory.Item),
		("obj_cart", ModelCategory.Prop), ("obj_campfire", ModelCategory.Prop), ("obj_grill", ModelCategory.Prop),
		("obj_planter", ModelCategory.Prop), ("obj_fountain", ModelCategory.Prop), ("obj_statue", ModelCategory.Prop),
		("obj_sign", ModelCategory.Prop), ("obj_cone", ModelCategory.Item), ("obj_arcade", ModelCategory.Prop),
		("obj_dogstand", ModelCategory.Prop), ("obj_stretcher", ModelCategory.Prop), ("obj_trellis", ModelCategory.Prop),
		("obj_potty", ModelCategory.Prop), ("obj_column", ModelCategory.Prop), ("obj_guitarcase", ModelCategory.Item)), 5, false),
};
string modelDir = Path.Combine(root, ContentPaths.ModelLibrary);
Directory.CreateDirectory(modelDir);
var lib = new ModelLibrary();
var models = new List<CatalogEntry<ModelCategory>>();
foreach (var p in modelPacks)
	foreach (var file in Directory.GetFiles(Path.Combine(src, p.Dir), "*.vox", SearchOption.AllDirectories).Order())
	{
		var cat = p.Cat(file);
		if (cat == null) continue;
		var objects = VoxScene.Load(file).Where(o => o.Voxels.Count >= 12).ToList();
		foreach (var group in p.Split ? objects.Select(o => new[] { o }) : new[] { objects.ToArray() })
		{
			if (group.Length == 0) continue;
			var b = VoxScene.ToModel(p.Split ? group[0].Name : Path.GetFileNameWithoutExtension(file), group, p.Scale);
			int max = Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ));
			if (max < ModelLibrary.Cell / 4 || max > 4 * ModelLibrary.Cell) continue; // 0.25 to 4 blocks
			lib.Add(b);
			models.Add(new()
			{
				Name = $"{p.Pack.Id}_{Slug(b.Name)}", File = Catalog.ModelLibraryFile, Category = cat.Value, Size = new[] { b.SizeX, b.SizeY, b.SizeZ },
				Source = $"{p.Pack.Credit}: {Path.GetRelativePath(src, file)}", License = p.Pack.License,
			});
		}
	}
using (var f = File.Create(Path.Combine(modelDir, Catalog.ModelLibraryFile))) lib.Save(f);
Catalog.Save(Path.Combine(modDir, Catalog.ModelsFile), models);
Console.WriteLine($"models {models.Count} ({models.Count(m => m.Category == ModelCategory.Prop)} props), {lib.Pools.Sum(p => p.Bytes) / 1024} kB");

// Unlabelled nature packs: mostly leaves -> tree/bush by height, otherwise rock.
TemplateCategory Classify(VoxObject o)
{
	var map = BaseMapping.Mapper(colors, Tree);
	int leaves = o.Voxels.Count(v => map(v.Rgba) is BaseBlocks.Leaves or BaseBlocks.AutumnLeaves);
	int h = o.Voxels.Max(v => v.Y) - o.Voxels.Min(v => v.Y);
	return leaves * 4 < o.Voxels.Count ? Rock : h > 12 ? Tree : Bush;
}

static string Slug(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray()).Trim('_');

record Pack(string Id, string Credit, string License);
