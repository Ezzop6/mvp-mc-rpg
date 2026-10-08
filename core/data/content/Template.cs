using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Renesance.Content;

// Block structure the generator stamps (.rvst, docs/world-format.md); cell 0 = leave the world, n = Names[n - 1].
public sealed class Template
{
	public const uint Version = 1;
	public const string Extension = ".rvst";
	static readonly byte[] Magic = "RVST"u8.ToArray();
	public int Sx, Sy, Sz;
	public List<string> Names = new();
	public ushort[] Cells;

	public Template(int sx, int sy, int sz) { Sx = sx; Sy = sy; Sz = sz; Cells = new ushort[sx * sy * sz]; }

	public ushort this[int x, int y, int z] { get => Cells[x + Sx * (y + Sy * z)]; set => Cells[x + Sx * (y + Sy * z)] = value; }

	public ushort NameIndex(string block)
	{
		int i = Names.IndexOf(block);
		if (i < 0) { Names.Add(block); i = Names.Count - 1; }
		return (ushort)(i + 1);
	}

	public void Save(string path)
	{
		using var w = new BinaryWriter(File.Create(path));
		w.Write(Magic);
		w.Write(Version);
		w.Write(Sx); w.Write(Sy); w.Write(Sz);
		w.Write(Names.Count);
		foreach (var n in Names) { var b = Encoding.UTF8.GetBytes(n); w.Write(b.Length); w.Write(b); }
		foreach (var c in Cells) w.Write(c);
	}

	public static Template Load(string path)
	{
		using var r = new BinaryReader(File.OpenRead(path));
		if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException($"{path}: not a template");
		if (r.ReadUInt32() != Version) throw new InvalidDataException($"{path}: unsupported template version");
		var t = new Template(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
		int n = r.ReadInt32();
		for (int i = 0; i < n; i++) t.Names.Add(Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())));
		for (int i = 0; i < t.Cells.Length; i++) t.Cells[i] = r.ReadUInt16();
		return t;
	}
}

public enum TemplateCategory { Tree, Bush, Rock, House, Ruin, Monument, Prop, Light, Vehicle, Scene, Tile }

public enum ModelCategory { Prop, Item }

public sealed class CatalogEntry<TCategory> where TCategory : struct, Enum
{
	public string Name { get; set; }
	public string File { get; set; }
	public TCategory Category { get; set; }
	public float Weight { get; set; } = 1;
	public int[] Size { get; set; }        // blocks x, y, z
	public int[] Anchor { get; set; }      // block inside the template placed on the ground point (bottom centre)
	public string Source { get; set; }
	public string License { get; set; }
}

public static class Catalog
{
	public const string StructuresFile = "structures.json", ModelsFile = "models.json", ModelLibraryFile = "models.rvml",
		BlockModelLibraryFile = "blocks.rvml", ItemModelLibraryFile = "items.rvml";
	static readonly JsonSerializerOptions opts = new()
	{
		WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};
	public static void Save<T>(string path, List<CatalogEntry<T>> entries) where T : struct, Enum => File.WriteAllText(path, JsonSerializer.Serialize(entries, opts));
	public static List<CatalogEntry<T>> Load<T>(string path) where T : struct, Enum => JsonSerializer.Deserialize<List<CatalogEntry<T>>>(File.ReadAllText(path), opts);
}
