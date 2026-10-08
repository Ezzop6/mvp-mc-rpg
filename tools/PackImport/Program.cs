using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Renesance.Content.Format;
using Renesance.Content;
using Renesance.World;

string[] defaultLayers = { $"{ContentPaths.Packs}/minecraft-26.3-client.jar", $"{ContentPaths.Packs}/Patrix_26.3_32x_basic.zip" };
const string Namespace = "base";

string root = ContentPaths.FindRoot();
var layerPaths = (args.Length > 0 ? args : defaultLayers).Select(p => Path.GetFullPath(p, root)).ToArray();
string outDir = Path.Combine(root, ContentPaths.Mod(Namespace)), texDir = Path.Combine(root, ContentPaths.Textures);

var zips = layerPaths.Select(ZipFile.OpenRead).ToArray();
var pack = new Assets(zips);
var vanilla = new Assets(zips[..1]);
var warnings = new List<string>();
var placeholders = Ctm.ReplacePlaceholders(pack, vanilla, Rules.CtmPlaceholders, warnings, out int ctmHidden);

var vanillaModels = Mc.Load(vanilla, "models/", warnings);
var models = Mc.Load(pack, "models/", warnings);
var vanillaStates = Mc.Load(vanilla, "blockstates/", warnings);
var states = new SortedDictionary<string, JsonObject>(Mc.Load(pack, "blockstates/", warnings), StringComparer.Ordinal);

var layers = new Dictionary<string, Surface>();
if (pack.Has("optifine/block.properties"))
	foreach (var (key, value) in Mc.Properties(pack.Text("optifine/block.properties")))
		if (key.StartsWith("layer."))
			foreach (var n in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				layers[n.Replace("minecraft:", "")] = key[6..] == "translucent" ? Surface.Translucent : key[6..].StartsWith("cutout") ? Surface.Cutout : Surface.Opaque;
string lilyPad = pack.Has("optifine/color.properties") ? Mc.Properties(pack.Text("optifine/color.properties")).FirstOrDefault(p => p.Key == "lilypad").Value : null;

var texInfo = new Dictionary<string, TexInfo>();
TexInfo Tex(string t) => texInfo.TryGetValue(t, out var i) ? i : texInfo[t] = TexInfo.Read(pack, t);
var colormaps = new Dictionary<TintSource, string>();
foreach (var (src, file) in new[] { (TintSource.Grass, "grass"), (TintSource.Foliage, "foliage"), (TintSource.DryFoliage, "dry_foliage") })
	if (pack.Has($"textures/colormap/{file}.png"))
	{
		var (w, h, px) = Png.Decode(pack.Bytes($"textures/colormap/{file}.png"));
		int o = ((int)((1 - 0.4f * 0.8f) * (h - 1)) * w + (int)((1 - 0.8f) * (w - 1))) * 4;
		colormaps[src] = $"#{px[o]:x2}{px[o + 1]:x2}{px[o + 2]:x2}";
	}

Face[] Glow(Face[] six) => six.Select(f => f with { Emission = f.Texture != null && Tex(f.Texture).Coverage > 0.02f ? MathF.Round(Tex(f.Texture).Emission, 3) : 0 }).ToArray();
static string LookName(string model) => model.StartsWith("block/") ? model[6..] : model;

string blocksDir = Path.Combine(outDir, ContentSet.EntriesDir);
var importFiles = new HashSet<string>();
var handEntries = new List<Entry>();
if (Directory.Exists(blocksDir))
	foreach (var f in Directory.GetFiles(blocksDir, "*.json"))
		if (ContentSet.Read<EntryFile>(f) is var file && file.Source == EntrySource.Import) importFiles.Add(f);
		else handEntries.AddRange(file.Entries);
var handIds = handEntries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

var voxelModels = new SortedSet<string>(StringComparer.Ordinal);
var entries = new List<(string Category, Entry Entry)>();
var missingTextures = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
var used = new SortedSet<string>(StringComparer.Ordinal);
foreach (var (name, bs) in states)
{
	if (handIds.Contains($"{Namespace}:{name}")) continue;
	var options = Mc.DefaultVariant(bs);
	var baseDefault = vanillaStates.TryGetValue(name, out var vbs) ? Mc.DefaultVariant(vbs)[0] : options[0];
	var (kind, template) = vbs != null ? Mc.Classify(Mc.ModelsOf(vbs).Prepend(baseDefault.Model), vanillaModels) : Mc.Classify(Mc.ModelsOf(bs).Prepend(options[0].Model), models);
	string model = options[0].Model;
	var tags = new SortedSet<string>(Rules.NameTags(name), StringComparer.Ordinal);
	if (vbs == null) tags.Add("pack_only");
	if (options.Length > 1) tags.Add(options.Select(o => o.Model).Distinct().Count() > 1 ? "random_variants" : "random_rotation");
	if (options[0].X != 0) tags.Add("default_rotated_x");

	Faces faces = null;
	SortedDictionary<string, string> textures = null;
	List<string> look = new();
	bool tinted = false;
	if (name is "water" or "lava") kind = EntryKind.Liquid;
	if (!models.ContainsKey(model)) { kind = EntryKind.Unresolved; tags.Add("missing_model"); }
	else if (kind is null or EntryKind.Cube && (Mc.CubeFaces(model, models, options[0].Y, out bool extra)
		?? (kind == EntryKind.Cube ? Mc.CubeFaces(baseDefault.Model, vanillaModels, baseDefault.Y, out _) : null)) is { } six)
	{
		kind = EntryKind.Cube;
		template ??= "elements";
		if (extra || Mc.CubeFaces(model, models, 0, out _) == null) tags.Add("extra_geometry");
		six = Glow(six);
		faces = Mc.Compress(six);
		look.AddRange(six.Select(f => f.Texture));
		used.UnionWith(six.Where(f => f.Overlay != null).Select(f => f.Overlay));
		tinted = six.Any(f => f.Tint);
	}
	else
	{
		if (kind is null or EntryKind.Cube) kind = Mc.Elements(model, models) != null ? EntryKind.Custom : EntryKind.Entity;
		var vars = Mc.TextureVars(model, models);
		textures = new(vars.Keys.Select(k => (k, Mc.Resolve("#" + k, vars))).Where(p => p.Item2 != null).ToDictionary(p => p.k, p => p.Item2), StringComparer.Ordinal);
		look.AddRange(textures.Where(p => p.Key != "particle" || textures.Count == 1).Select(p => p.Value));
		tinted = Mc.Elements(model, models)?.Any(e => e["faces"]?.AsObject().Any(f => f.Value?["tintindex"] != null) == true) == true;
	}
	if (kind == EntryKind.Liquid) tinted = Rules.Tints.ContainsKey(name); // liquid models have no elements to carry a tintindex
	if (look.Any(t => t == null)) tags.Add("missing_texture");
	look.RemoveAll(t => t == null);
	used.UnionWith(look);
	if (textures != null) used.UnionWith(textures.Values);
	foreach (var t in look.Concat(faces == null ? Enumerable.Empty<string>() : AllFaces(faces).Select(f => f.Overlay)).Where(t => t != null && !Tex(t).Exists))
	{
		tags.Add("missing_texture");
		(missingTextures.TryGetValue(name, out var s) ? s : missingTextures[name] = new(StringComparer.Ordinal)).Add(t);
	}
	var infos = look.Select(Tex).Where(i => i.Exists).ToList();
	if (infos.Any(i => i.Animated)) tags.Add("animated");
	if (zips.Length > 1 && look.Any(t => pack.Layer($"textures/{t}.png") == 0)) tags.Add("base_texture");

	var surface = layers.TryGetValue(name, out var layer) ? layer
		: infos.Any(i => i.Translucent) ? Surface.Translucent : infos.Any(i => i.Cutout) ? Surface.Cutout : Surface.Opaque;

	Tint tint = null;
	if (tinted)
	{
		var (src, color) = Rules.Tints.TryGetValue(name, out var known) ? known : (name.EndsWith("_leaves") ? TintSource.Foliage : TintSource.Grass, null);
		if (!Rules.Tints.ContainsKey(name)) tags.Add("tint_guessed");
		if (name == "lily_pad" && lilyPad != null) color = "#" + lilyPad;
		tint = new Tint(src, color ?? colormaps.GetValueOrDefault(src) ?? Rules.FixedTintColors.GetValueOrDefault(src));
	}

	bool emissive = infos.Any(i => i.Coverage > 0.02f);
	float emission = emissive && faces == null ? MathF.Round(infos.Average(i => i.Emission), 3) : 0;
	Light light = null;
	if (Rules.Lights.FirstOrDefault(l => l.Name.IsMatch(name)) is { Name: not null } l)
	{
		var rgb = new double[3];
		foreach (var i in infos) for (int c = 0; c < 3; c++) rgb[c] += emissive ? i.EmitRgb[c] : i.AvgRgb[c];
		double max = Math.Max(1e-6, rgb.Max());
		light = new Light(l.Level, l.When, infos.Count == 0 ? null : "#" + string.Concat(rgb.Select(c => ((int)Math.Round(c / max * 255)).ToString("x2"))));
	}

	var props = Mc.Props(bs);
	if (Rules.UnlistedStateValues.TryGetValue(name, out var unlisted) && props.TryGetValue(unlisted.State, out var listed))
		props[unlisted.State] = listed.Select(int.Parse).Append(unlisted.Value).Distinct().Order().Select(v => $"{v}").ToArray();
	int facings = props.GetValueOrDefault("facing")?.Contains("north") == true ? props["facing"].Length : 0;
	var orientation = props.GetValueOrDefault("axis")?.Contains("y") == true ? Orientation.Axis : facings == 4 ? Orientation.Horizontal : facings >= 5 ? Orientation.Facing : Orientation.None;
	if (kind == EntryKind.Liquid && textures != null)
	{
		foreach (var part in new[] { "still", "flow", "overlay" })
			if (Tex($"block/{name}_{part}").Exists) used.Add(textures[part] = $"block/{name}_{part}");
		props["level"] = Enumerable.Range(0, 16).Select(i => $"{i}").ToArray();
	}
	string multiblock = props.GetValueOrDefault("half") is ["lower", "upper"] ? "half" : props.GetValueOrDefault("part") is ["foot", "head"] ? "part" : null;

	var cubeLooks = new SortedDictionary<string, Faces>(StringComparer.Ordinal);
	Look MakeLook(McVariant v)
	{
		if (kind == EntryKind.Liquid) return null;
		if (!models.ContainsKey(v.Model)) { tags.Add("missing_model"); return null; }
		string n = LookName(v.Model);
		var look = new Look(X: v.X, Y: v.Y, Uvlock: v.Uvlock, Weight: v.Weight == 1 ? null : v.Weight);
		if (Mc.CubeFaces(v.Model, models, 0, out bool extra) is { } six && (!extra || kind == EntryKind.Cube))
		{
			six = Glow(six);
			cubeLooks[n] = Mc.Compress(six);
			used.UnionWith(six.SelectMany(f => new[] { f.Texture, f.Overlay }).Where(t => t != null && Tex(t).Exists));
			return look with { Cube = n };
		}
		if (Mc.Elements(v.Model, models) != null) { voxelModels.Add(v.Model); return look with { Model = n }; }
		string particle = Mc.Resolve("#particle", Mc.TextureVars(v.Model, models));
		if (particle == null || !Tex(particle).Exists) return null;
		tags.Add("placeholder");
		used.Add(particle);
		cubeLooks[n] = new Faces(All: new Face(particle));
		return look with { Cube = n };
	}
	Look[] Looks(McVariant[] options) => options.Select(MakeLook).Where(l => l != null).ToArray();
	var variants = Mc.Variants(bs).Select(c => new LookCase(c.When.Count == 0 ? null : c.When, Looks(c.Options))).Where(c => c.Looks.Length > 0).ToArray();
	var multipart = Mc.Parts(bs).Select(p => new LookPart(p.When?.ToArray(), Looks(p.Options))).Where(p => p.Looks.Length > 0).ToArray();

	var entry = new Entry($"{Namespace}:{name}", kind.Value, template, faces, textures, surface, tint, emission, light, orientation,
		props.Count == 0 ? null : props, multiblock, tags.Count == 0 ? null : tags.ToArray(), cubeLooks.Count == 0 ? null : cubeLooks,
		variants.Length == 0 ? null : variants, multipart.Length == 0 ? null : multipart);
	entries.Add((Rules.Category(name, kind.Value), entry));
}

var voxelizer = new Voxelizer(pack);
var library = new ModelLibrary();
foreach (var m in voxelModels) library.Add(voxelizer.Build(LookName(m), Mc.Elements(m, models), Mc.TextureVars(m, models)));
int Voxels(string model)
{
	int m = library.Models.FindIndex(i => i.Name == model), n = 0;
	for (int z = 0; z < ModelLibrary.Cell; z++) for (int y = 0; y < ModelLibrary.Cell; y++) for (int x = 0; x < ModelLibrary.Cell; x++)
		if (library.GetVoxel(m, x, y, z) != 0) n++;
	return n;
}
const int HalfBlock = ModelLibrary.Cell * ModelLibrary.Cell * ModelLibrary.Cell / 2;
if (Voxels("stone_slab") != HalfBlock || Voxels("stone_stairs") != HalfBlock * 3 / 2) throw new InvalidDataException($"stone slab or stairs voxelized wrong: {Voxels("stone_slab")} {Voxels("stone_stairs")}");

var items = new ItemImport(pack, models, outDir, entries.Select(e => (e.Entry.Id, e.Category)), voxelizer, Namespace, warnings);
var importedCategories = Rules.Categories.Concat(ItemImport.Categories).Select(c => c with { Source = EntrySource.Import }).ToArray();
string categoriesFile = Path.Combine(outDir, ContentSet.CategoriesFile);
var oldCategories = File.Exists(categoriesFile) ? ContentSet.Read<CategoryDef[]>(categoriesFile) : Array.Empty<CategoryDef>();
bool Imported(CategoryDef c) => c.Source == EntrySource.Import || importedCategories.Any(i => i.Id == c.Id);
var categories = oldCategories.Where(c => !Imported(c)).ToList();
categories.InsertRange(oldCategories.TakeWhile(c => !Imported(c)).Count(), importedCategories);
var set = new ContentSet(categories.ToArray(), entries.GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.Ordinal)
	.Select(g => new EntryFile(g.Key, g.Select(e => e.Entry).ToArray(), EntrySource.Import)).ToArray(), items.Files);
var taken = set.Files.Select(f => Path.Combine(blocksDir, f.Category + ".json")).FirstOrDefault(p => File.Exists(p) && !importFiles.Contains(p));
if (taken != null) throw new InvalidDataException($"{taken} is hand-written and has the name of an imported category file; rename it");
string[] textureDirs = { "block", "colormap", "item", "environment" };
foreach (var d in textureDirs.Select(t => Path.Combine(texDir, t)))
	if (Directory.Exists(d)) Directory.Delete(d, true);
foreach (var f in importFiles.Concat(items.ImportFiles)) File.Delete(f);
set.Save(outDir);
string libDir = Path.Combine(root, ContentPaths.ModelLibrary);
Directory.CreateDirectory(libDir);
using (var f = File.Create(Path.Combine(libDir, Catalog.BlockModelLibraryFile))) library.Save(f);
using (var f = File.Create(Path.Combine(libDir, Catalog.ItemModelLibraryFile))) items.Library.Save(f);
string modFile = Path.Combine(outDir, ModInfo.FileName);
if (!File.Exists(modFile)) File.WriteAllText(modFile, JsonSerializer.Serialize(new ModInfo(Namespace, "Base", "0.1.0"), ContentSet.Json));

used.UnionWith(handEntries.SelectMany(e => (e.Textures?.Values ?? Enumerable.Empty<string>())
	.Concat(new[] { e.Faces }.Concat(e.Cubes?.Values ?? Enumerable.Empty<Faces>()).Where(f => f != null).SelectMany(AllFaces).SelectMany(f => new[] { f.Texture, f.Overlay })))
	.Where(t => t != null && Tex(t).Exists));
used.UnionWith(items.Textures);
int copied = 0;
var copy = used.Where(t => Tex(t).Exists).Select(t => "textures/" + t + ".png")
	.Concat(pack.Under("textures/colormap/", ".png"))
	.Concat(ContentPaths.SkyTextures.Select(t => "textures/" + ContentPaths.TextureFile(t)))
	.SelectMany(p => new[] { p, p[..^4] + "_n.png", p[..^4] + "_s.png", p + ".mcmeta" }).Where(pack.Has);
foreach (var p in copy)
{
	if (!textureDirs.Contains(p.Split('/')[1])) throw new InvalidDataException($"{p} is outside the importer's texture folders");
	string dst = Path.Combine(texDir, p["textures/".Length..]);
	Directory.CreateDirectory(Path.GetDirectoryName(dst));
	File.WriteAllBytes(dst, pack.Bytes(p));
	copied++;
}

var check = ContentSet.Load(outDir);
foreach (var e in check.Entries) BlockRegistry.Define(e);
if (check.Files.Where(f => f.Source == EntrySource.Import).Sum(f => f.Entries.Length) != entries.Count) throw new InvalidDataException("Reloaded entry count differs");
if (check.ItemFiles.Where(f => f.Source == EntrySource.Import).Sum(f => f.Entries.Length) != items.Items.Count) throw new InvalidDataException("Reloaded item count differs");
BlockRegistry.From(check);
var lostFace = check.Entries.Where(e => e.Faces != null).SelectMany(e => AllFaces(e.Faces)).Select(f => f.Texture)
	.FirstOrDefault(t => Tex(t).Exists && !File.Exists(Path.Combine(texDir, ContentPaths.TextureFile(t))));
if (lostFace != null) throw new InvalidDataException($"Face texture {lostFace} was not copied");

var orphans = pack.Under("textures/block/", ".png").Where(p => pack.Layer(p) == zips.Length - 1).Select(p => p["textures/".Length..^4])
	.Where(t => !t.EndsWith("_n") && !t.EndsWith("_s") && !used.Contains(t)).Order(StringComparer.Ordinal).ToList();
var all = entries.Select(e => e.Entry).ToList();
var cubes = all.Where(e => e.Kind == EntryKind.Cube).ToList();
var sb = new StringBuilder();
sb.AppendLine("Layers (base first): " + string.Join(", ", layerPaths) + $"\nOutput {outDir}, {texDir}");
sb.AppendLine($"Blockstates: {states.Count} (base {vanillaStates.Count}, overridden {states.Keys.Count(k => pack.Layer($"blockstates/{k}.json") > 0)}, new {states.Count - vanillaStates.Count}); files copied: {copied}");
sb.AppendLine("\nKinds: " + string.Join(", ", all.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
sb.AppendLine("\nCategories:");
foreach (var top in Rules.Categories.Where(c => c.Parent == null))
{
	var leaves = Rules.Categories.Where(c => c.Parent == top.Id || c == top).Select(c => (c.Id, N: entries.Count(e => e.Category == c.Id))).Where(c => c.N > 0).ToList();
	if (leaves.Count > 0) sb.AppendLine($"  {top.Id,-13} {leaves.Sum(c => c.N),4}: " + string.Join(", ", leaves.Select(c => $"{c.Id} {c.N}")));
}
sb.AppendLine($"\nCubes {cubes.Count}: " + string.Join(", ", cubes.GroupBy(e => e.Surface).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
	+ $"; tinted {cubes.Count(e => e.Tint != null)}, emissive {cubes.Count(e => AllFaces(e.Faces).Any(f => f.Emission > 0))}, light {cubes.Count(e => e.Light != null)}"
	+ ", orientation " + string.Join("/", cubes.GroupBy(e => e.Orientation).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
	+ $"; layouts: all {cubes.Count(e => e.Faces.All != null)}, top/bottom/side {cubes.Count(e => e.Faces.Side != null)}, six faces {cubes.Count(e => e.Faces.Front != null)}, overlay {cubes.Count(e => AllFaces(e.Faces).Any(f => f.Overlay != null))}");
var looks = all.SelectMany(e => (e.Variants?.SelectMany(v => v.Looks) ?? Enumerable.Empty<Look>()).Concat(e.Multipart?.SelectMany(p => p.Looks) ?? Enumerable.Empty<Look>())).ToList();
sb.AppendLine($"\nLooks: variants {all.Count(e => e.Variants != null)} entries, multipart {all.Count(e => e.Multipart != null)}, none {all.Count(e => e.Variants == null && e.Multipart == null)}"
	+ $" ({string.Join(", ", all.Where(e => e.Variants == null && e.Multipart == null).Select(e => e.Id))})");
sb.AppendLine($"  {looks.Count} looks: cube {looks.Count(l => l.Cube != null)}, voxel model {looks.Count(l => l.Model != null)}, rotated {looks.Count(l => l.X != 0 || l.Y != 0)}, uvlock {looks.Count(l => l.Uvlock)}, weighted {looks.Count(l => l.Weight != null)}");
sb.AppendLine("  Entries with a voxel model, by kind: " + string.Join(", ", all.Where(e => e.Variants?.Any(v => v.Looks.Any(l => l.Model != null)) == true || e.Multipart != null)
	.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
sb.AppendLine($"  Placeholder cubes: {all.Count(e => e.Tags?.Contains("placeholder") == true)}; multiblock: {string.Join(", ", all.Where(e => e.Multiblock != null).GroupBy(e => e.Multiblock).Select(g => $"{g.Key} {g.Count()}"))}");
var pools = library.Pools;
sb.AppendLine($"\nBlock model library {Catalog.BlockModelLibraryFile}: {library.Models.Count} models ({library.Models.Count(m => m.Width == 2)} with 2-byte voxels, {library.Models.Count(m => library.CellTable[m.CellTableStart] == ModelPool.Empty)} empty), "
	+ $"{library.Materials.Count} materials, bricks {pools[0].BrickCount}+{pools[1].BrickCount}, cells {pools[0].CellCount}+{pools[1].CellCount}, pools {pools.Sum(p => p.Bytes) / 1024} kB, file {new FileInfo(Path.Combine(libDir, Catalog.BlockModelLibraryFile)).Length / 1024} kB");
sb.AppendLine($"  Elements voxelized by sampling (rotated): {voxelizer.RotatedElements}; models clipped to one block: {voxelizer.Clipped.Count} ({string.Join(", ", voxelizer.Clipped)})");
sb.AppendLine("  Empty models: " + string.Join(", ", library.Models.Where(m => library.CellTable[m.CellTableStart] == ModelPool.Empty).Select(m => m.Name)));
sb.AppendLine("  Largest palettes: " + string.Join(", ", library.Models.OrderByDescending(m => m.PaletteCount).ThenBy(m => m.Name, StringComparer.Ordinal).Take(10).Select(m => $"{m.Name} {m.PaletteCount - 1}")));
sb.Append(items.Summary(pack, zips.Length - 1, Path.Combine(libDir, Catalog.ItemModelLibraryFile)));
var unresolved = all.Where(e => e.Kind == EntryKind.Unresolved).Select(e => e.Id).ToList();
sb.AppendLine($"\nUnresolved (model in neither pack nor vanilla) {unresolved.Count}: {string.Join(", ", unresolved)}");
sb.AppendLine($"Missing textures in {missingTextures.Count} entries ({cubes.Count(e => e.Tags?.Contains("missing_texture") == true)} cubes): "
	+ string.Join(", ", missingTextures.Select(p => $"{p.Key} [{string.Join(" ", p.Value)}]")));
sb.AppendLine($"Tint source guessed: {string.Join(", ", all.Where(e => e.Tags?.Contains("tint_guessed") == true).Select(e => e.Id))}");
sb.AppendLine($"Entries using a base-layer texture: {all.Count(e => e.Tags?.Contains("base_texture") == true)}");
sb.AppendLine($"Top-layer block textures no entry uses: {orphans.Count}");
sb.AppendLine($"Base textures hidden by CTM: {ctmHidden}; placeholders replaced {placeholders.Count}: " + string.Join(", ", placeholders.Select(r => $"{r.Texture} <- {r.Source}")));
if (warnings.Count > 0) sb.AppendLine($"Warnings {warnings.Count}: " + string.Join("; ", warnings));
Console.Write(sb);
sb.AppendLine("\nUnused top-layer block textures:\n" + string.Join("\n", orphans));
File.WriteAllText(Path.Combine(texDir, "import-summary.txt"), sb.ToString());

static IEnumerable<Face> AllFaces(Faces f) => Enum.GetValues<FaceDir>().Select(f.Get);

sealed class Assets
{
	const string Root = "assets/minecraft/";
	readonly SortedDictionary<string, (ZipArchiveEntry Entry, int Layer)> files = new(StringComparer.Ordinal);

	public Assets(ZipArchive[] layers)
	{
		for (int l = 0; l < layers.Length; l++)
			foreach (var e in layers[l].Entries)
				if (e.FullName.IndexOf(Root, StringComparison.Ordinal) is int i and >= 0 && !e.FullName.EndsWith('/')) files[e.FullName[(i + Root.Length)..]] = (e, l);
	}

	public bool Has(string path) => files.ContainsKey(path);
	public int Layer(string path) => files.TryGetValue(path, out var f) ? f.Layer : -1;
	public void Use(string path, Assets from, string source) { if (from.files.TryGetValue(source, out var f)) files[path] = f; else files.Remove(path); }
	public IEnumerable<string> Under(string dir, string ext) => files.Keys.Where(k => k.StartsWith(dir, StringComparison.Ordinal) && k.EndsWith(ext, StringComparison.Ordinal));
	public string Text(string path) => Encoding.UTF8.GetString(Bytes(path)).TrimStart('\uFEFF');

	public byte[] Bytes(string path)
	{
		using var s = files[path].Entry.Open();
		var m = new MemoryStream();
		s.CopyTo(m);
		return m.ToArray();
	}
}

sealed record TexInfo(bool Exists, bool Cutout, bool Translucent, float Emission, float Coverage, double[] EmitRgb, double[] AvgRgb, bool Animated)
{
	public static TexInfo Read(Assets pack, string name)
	{
		string path = $"textures/{name}.png";
		if (!pack.Has(path)) return new(false, false, false, 0, 0, null, null, false);
		var (w, h, px) = Png.Decode(pack.Bytes(path));
		int n = w * h, partial = 0, holes = 0;
		var avg = new double[3];
		for (int i = 0; i < n; i++)
		{
			byte a = px[i * 4 + 3];
			if (a == 0) holes++; else if (a < 255) partial++;
			for (int c = 0; c < 3; c++) avg[c] += px[i * 4 + c] * a / 255.0;
		}
		double emission = 0, emissive = 0;
		var emit = new double[3];
		string spec = $"textures/{name}_s.png";
		if (pack.Has(spec))
		{
			var (_, _, s) = Png.Decode(pack.Bytes(spec));
			for (int i = 0; i < n && i * 4 < s.Length; i++)
			{
				byte a = s[i * 4 + 3];
				if (a is 0 or 255) continue;
				double e = a / 254.0;
				emission += e;
				emissive++;
				for (int c = 0; c < 3; c++) emit[c] += px[i * 4 + c] * e;
			}
		}
		return new(true, holes + partial > 0, partial > 0 && partial >= n / 10, (float)(emission / n), (float)(emissive / n), emit, avg, pack.Has(path + ".mcmeta"));
	}
}

record struct McVariant(string Model, int X, int Y, bool Uvlock, int Weight);

static class Mc
{
	static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
	static readonly string[] Dirs = { "up", "down", "north", "south", "west", "east" };

	public static Dictionary<string, JsonObject> Load(Assets a, string dir, List<string> warnings)
	{
		var d = new Dictionary<string, JsonObject>();
		foreach (var path in a.Under(dir, ".json"))
			try { d[path[dir.Length..^5]] = JsonNode.Parse(a.Text(path), null, JsonOptions).AsObject(); }
			catch (Exception e) when (e is JsonException or InvalidOperationException) { warnings.Add($"{path}: {e.Message.Split('\n')[0]}"); }
		return d;
	}

	public static IEnumerable<KeyValuePair<string, string>> Properties(string text) =>
		text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#' && l.Contains('='))
			.Select(l => new KeyValuePair<string, string>(l[..l.IndexOf('=')].Trim(), l[(l.IndexOf('=') + 1)..].Trim()));

	public static string Norm(string s)
	{
		s = s.Replace("minecraft:", "");
		return s.Contains('/') ? s : "block/" + s;
	}

	public static List<string> Chain(string model, Dictionary<string, JsonObject> models)
	{
		var chain = new List<string> { model };
		for (string cur = model; chain.Count < 32 && models.TryGetValue(cur, out var m) && m["parent"] is JsonValue p;)
			chain.Add(cur = Norm(p.GetValue<string>()));
		return chain;
	}

	public static Dictionary<string, string> TextureVars(string model, Dictionary<string, JsonObject> models)
	{
		var vars = new Dictionary<string, string>();
		foreach (var m in Enumerable.Reverse(Chain(model, models)))
			if (models.GetValueOrDefault(m)?["textures"] is JsonObject t)
				foreach (var (k, v) in t) vars[k] = (v is JsonObject o ? o["sprite"] : v).GetValue<string>();
		return vars;
	}

	public static string Resolve(string value, Dictionary<string, string> vars)
	{
		for (int i = 0; value != null && value.StartsWith('#') && i < 16; i++) value = vars.GetValueOrDefault(value[1..]);
		return value == null || value.StartsWith('#') ? null : Norm(value);
	}

	public static JsonArray Elements(string model, Dictionary<string, JsonObject> models) =>
		Chain(model, models).Select(m => models.GetValueOrDefault(m)?["elements"] as JsonArray).FirstOrDefault(e => e != null);

	public static (EntryKind?, string) Classify(IEnumerable<string> blockModels, Dictionary<string, JsonObject> models)
	{
		foreach (var m in blockModels.Distinct())
		{
			var chain = Chain(m, models).Skip(1).ToList();
			foreach (var (re, kind) in Rules.Kinds)
				if (chain.FirstOrDefault(re.IsMatch) is { } hit) return (kind, hit.Replace("block/", ""));
		}
		return (null, null);
	}

	static McVariant[] Opts(JsonNode n) => (n is JsonArray a ? a.Select(x => x.AsObject()) : new[] { n.AsObject() })
		.Select(o => new McVariant(Norm(o["model"].GetValue<string>()), o["x"]?.GetValue<int>() ?? 0, o["y"]?.GetValue<int>() ?? 0,
			o["uvlock"]?.GetValue<bool>() ?? false, o["weight"]?.GetValue<int>() ?? 1)).ToArray();

	static SortedDictionary<string, string> When() => new(StringComparer.Ordinal);

	static List<SortedDictionary<string, string>> Conditions(JsonObject w)
	{
		if (w["OR"] is JsonArray or) return or.SelectMany(x => Conditions(x.AsObject())).ToList();
		if (w["AND"] is JsonArray and)
			return and.Aggregate(new List<SortedDictionary<string, string>> { When() }, (acc, x) => acc.SelectMany(l => Conditions(x.AsObject())
				.Select(r => { var m = new SortedDictionary<string, string>(l, StringComparer.Ordinal); foreach (var (k, v) in r) m[k] = v; return m; })).ToList());
		var d = When();
		foreach (var (k, v) in w) d[k] = v.ToString();
		return new() { d };
	}

	public static IEnumerable<(SortedDictionary<string, string> When, McVariant[] Options)> Variants(JsonObject bs)
	{
		if (bs["variants"] is JsonObject vs)
			foreach (var (key, v) in vs)
			{
				var when = When();
				foreach (var kv in key.Split(',', StringSplitOptions.RemoveEmptyEntries)) when[kv.Split('=')[0]] = kv.Split('=')[1];
				yield return (when, Opts(v));
			}
	}

	public static IEnumerable<(List<SortedDictionary<string, string>> When, McVariant[] Options)> Parts(JsonObject bs) =>
		bs["multipart"] is JsonArray parts ? parts.Select(p => (p["when"] is JsonObject w ? Conditions(w) : null, Opts(p["apply"]))) : Enumerable.Empty<(List<SortedDictionary<string, string>>, McVariant[])>();

	static IEnumerable<(SortedDictionary<string, string> When, McVariant[] Options)> Cases(JsonObject bs) =>
		Variants(bs).Concat(Parts(bs).SelectMany(p => (p.When ?? new() { When() }).Select(w => (w, p.Options))));

	public static IEnumerable<string> ModelsOf(JsonObject bs) => Cases(bs).SelectMany(c => c.Options).Select(o => o.Model);

	public static McVariant[] DefaultVariant(JsonObject bs) => Cases(bs)
		.Select((c, i) => (c.Options, Score: c.When.Sum(p => p.Value is "north" or "y" ? 2 : p.Value == "true" ? -1 : 0), i))
		.OrderByDescending(c => c.Score).ThenBy(c => c.i).First().Options;

	public static SortedDictionary<string, string[]> Props(JsonObject bs)
	{
		var props = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
		foreach (var g in Cases(bs).SelectMany(c => c.When).GroupBy(p => p.Key))
		{
			var values = g.SelectMany(p => p.Value.Split('|')).Distinct().ToList();
			if (values.Count == 1 && values[0] is "true" or "false") values.Add(values[0] == "true" ? "false" : "true");
			if (values.Any(v => v is "low" or "tall" or "side") && !values.Contains("none")) values.Add("none");
			props[g.Key] = values.All(v => int.TryParse(v, out _)) ? values.OrderBy(int.Parse).ToArray()
				: values.OrderBy(v => v != "none").ThenBy(v => v, StringComparer.Ordinal).ToArray();
		}
		return props;
	}

	public static Face[] CubeFaces(string model, Dictionary<string, JsonObject> models, int y, out bool extra)
	{
		extra = false;
		var elements = Elements(model, models);
		if (elements == null) return null;
		var vars = TextureVars(model, models);
		static bool Full(JsonNode e) => e["from"].AsArray().All(v => v.GetValue<double>() == 0) && e["to"].AsArray().All(v => v.GetValue<double>() == 16);
		static JsonNode Side(JsonNode e, int i) => e["faces"]?[Dirs[i]] ?? e["faces"]?[Dirs[i ^ 1]];
		var baseEl = elements.FirstOrDefault(e => Full(e) && Enumerable.Range(0, 6).All(i => Side(e, i) != null));
		if (baseEl == null) return null;
		Face Make(JsonNode f) => new(Resolve(f["texture"].GetValue<string>(), vars), f["rotation"]?.GetValue<int>() ?? 0, f["tintindex"] != null);
		var faces = Enumerable.Range(0, 6).Select(i => Make(Side(baseEl, i))).ToArray();
		foreach (var e in elements.Where(e => e != baseEl))
		{
			if (!Full(e)) { extra = true; continue; }
			for (int i = 0; i < 6; i++)
				if (e["faces"]?[Dirs[i]] is { } f && Make(f) is { Texture: not null } o) faces[i] = faces[i] with { Overlay = o.Texture, Tint = o.Tint };
		}
		int k = ((y / 90) % 4 + 4) % 4;
		if (k == 0) return faces;
		int[] ring = { 2, 5, 3, 4 };
		var rotated = (Face[])faces.Clone();
		for (int i = 0; i < 4; i++) rotated[ring[(i + k) % 4]] = faces[ring[i]];
		rotated[0] = faces[0] with { Rotation = (faces[0].Rotation + y) % 360 };
		rotated[1] = faces[1] with { Rotation = (faces[1].Rotation + 360 - y % 360) % 360 };
		return rotated;
	}

	public static Faces Compress(Face[] f)
	{
		if (f.All(x => x == f[0])) return new Faces(All: f[0]);
		if (f[3] == f[2] && f[4] == f[2] && f[5] == f[2]) return new Faces(Top: f[0], Bottom: f[1], Side: f[2]);
		return new Faces(Top: f[0], Bottom: f[1], Front: f[2], Back: f[3], Left: f[4], Right: f[5]);
	}
}
