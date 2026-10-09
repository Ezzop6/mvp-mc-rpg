using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Renesance.Content.Format;

public enum EntryKind
{
	Cube, SingleFace, Stairs, Slab, Wall, Fence, FenceGate, Door, Trapdoor, Pane, Carpet, Button, PressurePlate, Rail,
	Torch, Lantern, Candle, Fire, Cross, Potted, Crop, Stem, RedstoneDust, ThinBlock, Piston, Liquid, Volume, Custom, Entity, Unresolved,
}

public enum Surface { Opaque, Cutout, Translucent }

public enum Matter { Solid, Granular, Liquid, Gas }

public enum Orientation { None, Axis, Horizontal, Facing }

public enum TintSource { Constant, Grass, Foliage, DryFoliage, Water, Redstone, Stem }

public enum FaceDir { Top, Bottom, Front, Back, Left, Right }

public enum EntrySource { Hand, Import }

public sealed record Face(string Texture, int Rotation = 0, bool Tint = false, string Overlay = null, float Emission = 0);

public sealed record Faces(Face All = null, Face Top = null, Face Bottom = null, Face Side = null,
	Face Front = null, Face Back = null, Face Left = null, Face Right = null)
{
	public Face Get(FaceDir d) => d switch
	{
		FaceDir.Top => Top ?? All,
		FaceDir.Bottom => Bottom ?? All,
		FaceDir.Front => Front ?? Side ?? All,
		FaceDir.Back => Back ?? Side ?? All,
		FaceDir.Left => Left ?? Side ?? All,
		_ => Right ?? Side ?? All,
	};
}

public sealed record Tint([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TintSource Source, string Color);

public sealed record Light(int Level, string When = null, string Color = null);

public sealed record Look(string Cube = null, string Model = null, int X = 0, int Y = 0, bool Uvlock = false, int? Weight = null);

public sealed record LookCase(SortedDictionary<string, string> When, Look[] Looks);

public sealed record LookPart(SortedDictionary<string, string>[] When, Look[] Looks);

public sealed record Entry(string Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] EntryKind Kind, string Template = null, Faces Faces = null,
	SortedDictionary<string, string> Textures = null, Surface Surface = Surface.Opaque, Tint Tint = null, float Emission = 0,
	Light Light = null, Orientation Orientation = Orientation.None, SortedDictionary<string, string[]> States = null, string Multiblock = null,
	string[] Tags = null, SortedDictionary<string, Faces> Cubes = null, LookCase[] Variants = null, LookPart[] Multipart = null,
	Matter Matter = Matter.Solid, float Density = 0, string Bbmodel = null);

public sealed record CategoryDef(string Id, string Name, string Parent = null, EntrySource Source = EntrySource.Hand);

public sealed record EntryFile(string Category, [property: JsonPropertyOrder(1)] Entry[] Entries, EntrySource Source = EntrySource.Hand);

public sealed record ItemEntry(string Id, string Block = null, string Model = null, string[] Layers = null);

public sealed record ItemFile(string Category, [property: JsonPropertyOrder(1)] ItemEntry[] Entries, EntrySource Source = EntrySource.Hand);

public sealed record ModInfo(string Id, string Name, string Version)
{
	public const string FileName = "mod.json";
}

public sealed record ContentSet(CategoryDef[] Categories, EntryFile[] Files, ItemFile[] ItemFiles = null)
{
	public const string CategoriesFile = "categories.json", EntriesDir = "blocks", ItemsDir = "items";

	public static readonly JsonSerializerOptions Json = new()
	{
		WriteIndented = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	// Face images read from .bbmodel files, by texture name.
	public Dictionary<string, (int W, int H, byte[] Rgba)> Images { get; init; } = new();

	public IEnumerable<Entry> Entries => Files.SelectMany(f => f.Entries);
	public IEnumerable<(ItemEntry Item, string Category)> Items => (ItemFiles ?? Array.Empty<ItemFile>()).SelectMany(f => f.Entries.Select(e => (e, f.Category)));

	public static ContentSet Load(string dir)
	{
		var images = new Dictionary<string, (int W, int H, byte[] Rgba)>();
		string assets = Path.Combine(dir, "..", "..", ContentPaths.Assets);
		return new(
			Read<CategoryDef[]>(Path.Combine(dir, CategoriesFile)),
			Directory.GetFiles(Path.Combine(dir, EntriesDir), "*.json").Order().Select(Read<EntryFile>)
				.Select(f => f with { Entries = f.Entries.Select(e => e.Bbmodel == null ? e : BbModel.Block(e, assets, images)).ToArray() }).ToArray(),
			Directory.Exists(Path.Combine(dir, ItemsDir)) ? Directory.GetFiles(Path.Combine(dir, ItemsDir), "*.json").Order().Select(Read<ItemFile>).ToArray() : Array.Empty<ItemFile>())
		{ Images = images };
	}

	public void Save(string dir)
	{
		Directory.CreateDirectory(Path.Combine(dir, EntriesDir));
		File.WriteAllText(Path.Combine(dir, CategoriesFile), Serialize(Categories));
		foreach (var f in Files) File.WriteAllText(Path.Combine(dir, EntriesDir, f.Category + ".json"), Serialize(f));
		if (ItemFiles?.Length > 0) Directory.CreateDirectory(Path.Combine(dir, ItemsDir));
		foreach (var f in ItemFiles ?? Array.Empty<ItemFile>()) File.WriteAllText(Path.Combine(dir, ItemsDir, f.Category + ".json"), Serialize(f));
	}

	const int LineWidth = 160;

	public static string Serialize<T>(T value)
	{
		var sb = new StringBuilder();
		Write(sb, JsonSerializer.SerializeToNode(value, Json), 0, 0);
		return sb.Append('\n').ToString();
	}

	static void Write(StringBuilder sb, JsonNode n, int depth, int prefix)
	{
		string flat = Flat(n);
		if (n is not (JsonObject or JsonArray) || 2 * depth + prefix + flat.Length <= LineWidth) { sb.Append(flat); return; }
		bool obj = n is JsonObject;
		var items = obj ? n.AsObject().Select(p => (Key: Key(p.Key) + ": ", p.Value)) : n.AsArray().Select(v => (Key: "", Value: v));
		sb.Append(obj ? '{' : '[');
		bool first = true;
		foreach (var (key, v) in items)
		{
			sb.Append(first ? "\n" : ",\n").Append(' ', 2 * depth + 2).Append(key);
			Write(sb, v, depth + 1, key.Length);
			first = false;
		}
		sb.Append('\n').Append(' ', 2 * depth).Append(obj ? '}' : ']');
	}

	static string Key(string k) => JsonValue.Create(k).ToJsonString();

	static string Flat(JsonNode n) => n switch
	{
		null => "null",
		JsonObject o => o.Count == 0 ? "{}" : "{ " + string.Join(", ", o.Select(p => Key(p.Key) + ": " + Flat(p.Value))) + " }",
		JsonArray a => "[" + string.Join(", ", a.Select(Flat)) + "]",
		_ => n.ToJsonString(),
	};

	public static T Read<T>(string path)
	{
		try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
		catch (JsonException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
	}
}
