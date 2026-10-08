using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Renesance.Content.Format;
using Renesance.World;

namespace Renesance.Content;

public readonly record struct BlockId(string Mod, string Path)
{
	public const string BaseMod = "base";

	public static BlockId Parse(string name, string source = "")
	{
		int i = name?.IndexOf(':') ?? -1;
		return i >= 0 ? new(name[..i], name[(i + 1)..]) : throw new InvalidDataException($"{source}: id must be namespaced (mod:name)");
	}
}

// Color: linear-ish RGB 0..1 (the entry's hex), Intensity scales it, Range in m.
public sealed record LightDef(float[] Color, float Intensity, float Range);

// A block state property packed into the 16 data bits; value index = position in Values.
public sealed record BlockParam(string Name, string[] Values, int Shift, int Bits)
{
	public int Count => Values.Length;
	public ushort Mask => (ushort)(((1 << Bits) - 1) << Shift);
	public int Get(BlockState s) => (s.Data >> Shift) & ((1 << Bits) - 1);
	public string Value(BlockState s) => Values[Get(s)];
	public int IndexOf(string value) => Array.IndexOf(Values, value);
	public BlockState With(BlockState s, int value)
	{
		if ((uint)value >= (uint)Count) throw new ArgumentOutOfRangeException(Name, value, $"0..{Count - 1}");
		return new BlockState(s.Type, (ushort)((s.Data & ~Mask) | (value << Shift)));
	}
	public BlockState With(BlockState s, string value) => With(s, IndexOf(value));
}

public sealed class BlockDef
{
	public string Name;            // namespaced, e.g. "base:stone"
	public ushort Id;              // runtime id, not stable between runs/mods
	public Entry Entry;            // null: engine block without geometry (air, object anchor)
	public ItemEntry Item;
	public string Category;        // leaf category of the entry
	public BlockParam[] Params = Array.Empty<BlockParam>();
	public BlockParam Orientation; // the axis / facing state of Entry.Orientation
	public LightDef Light;
	public (BlockParam Param, int Value)? LightWhen;
	public ushort RenderMask;      // state bits that change how the block looks or lights (orientation, light condition, look cases)
	public ushort DefaultData;

	public BlockState Default => new(Id, DefaultData);
	public bool Invisible => Entry == null;
	public bool Placeable => Entry != null && Item == null;
	public bool Liquid => Entry?.Kind == EntryKind.Liquid;
	public bool Lit(BlockState s) => Light != null && (LightWhen is not { } w || w.Param.Get(s) == w.Value);
	public BlockParam Param(string name) => Params.First(p => p.Name == name);
}

// Block types from a ContentSet: entries with a look become blocks; id 0 is air, the object anchor is an engine block.
public sealed class BlockRegistry
{
	public const string AirName = "base:air", ObjectName = "base:object", ItemPrefix = "item/";
	public const ushort AirId = 0;
	public const float LightMetersPerLevel = 0.8f, LightIntensityPerLevel = 4f / 15;

	readonly List<BlockDef> byId = new();
	readonly Dictionary<string, BlockDef> byName = new();

	public ContentSet Content { get; }

	public BlockRegistry(ContentSet content = null)
	{
		Content = content ?? new ContentSet(Array.Empty<CategoryDef>(), Array.Empty<EntryFile>());
		Add(new BlockDef { Name = AirName });
		Add(new BlockDef { Name = ObjectName });
	}

	public int Count => byId.Count;
	public IReadOnlyList<BlockDef> All => byId; // index = id
	public BlockDef this[ushort id] => byId[id];
	public BlockDef this[string name] => byName[name];
	public BlockDef Get(BlockState s) => byId[s.Type];
	public bool Contains(string name) => byName.ContainsKey(name);

	public static BlockRegistry Load(string modDir) => From(ContentSet.Load(modDir));

	// Ids in name order, so a given content set is deterministic.
	public static BlockRegistry From(ContentSet content)
	{
		var reg = new BlockRegistry(content);
		foreach (var (e, category) in content.Files.SelectMany(f => f.Entries.Select(e => (e, f.Category)))
			.Where(x => BlockLooks.HasLooks(x.e)).OrderBy(x => x.e.Id, StringComparer.Ordinal))
			reg.Add(Define(e, category));
		foreach (var (item, category) in content.Items.Where(x => x.Item.Model != null).OrderBy(x => x.Item.Id, StringComparer.Ordinal))
		{
			var id = BlockId.Parse(item.Id, item.Id);
			var def = Define(new Entry($"{id.Mod}:{ItemPrefix}{id.Path}", EntryKind.Custom, Variants: new[] { new LookCase(null, new[] { new Look(Model: item.Model) }) }), category);
			def.Item = item;
			reg.Add(def);
		}
		return reg;
	}

	public BlockDef Add(BlockDef def)
	{
		if (byName.ContainsKey(def.Name)) throw new InvalidDataException($"Duplicate block id '{def.Name}'");
		if (byId.Count > ushort.MaxValue) throw new InvalidDataException("More than 65535 block types");
		def.Id = (ushort)byId.Count;
		byId.Add(def);
		byName[def.Name] = def;
		return def;
	}

	public static BlockDef Define(Entry e, string category = null)
	{
		BlockId.Parse(e.Id, e.Id);
		var def = new BlockDef { Name = e.Id, Entry = e, Category = category };
		var list = new List<BlockParam>();
		int shift = 0;
		foreach (var (name, values) in e.States ?? new()) // sorted by name = bit order
		{
			if (values.Length < 2) throw new InvalidDataException($"{e.Id}: state '{name}' needs at least 2 values");
			int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(values.Length - 1));
			list.Add(new BlockParam(name, values, shift, bits));
			shift += bits;
		}
		if (shift > 16) throw new InvalidDataException($"{e.Id}: states need {shift} bits, only 16 available");
		def.Params = list.ToArray();
		if (e.Orientation != Format.Orientation.None)
		{
			string state = e.Orientation == Format.Orientation.Axis ? "axis" : "facing";
			def.Orientation = def.Params.FirstOrDefault(p => p.Name == state) ?? throw new InvalidDataException($"{e.Id}: orientation {e.Orientation} without a '{state}' state");
			int v = def.Orientation.IndexOf(BlockFacing.DefaultValue(e.Orientation));
			if (v < 0) throw new InvalidDataException($"{e.Id}: '{state}' has no default value {BlockFacing.DefaultValue(e.Orientation)}");
			def.DefaultData = def.Orientation.With(new BlockState(0, 0), v).Data;
		}
		var when = e.Light?.When?.Split('=');
		var whenParam = when == null ? null : def.Params.FirstOrDefault(p => p.Name == when[0]);
		if (e.Light is { Level: > 0 } l && (when == null || whenParam != null))
		{
			def.Light = new LightDef(l.Color == null ? new[] { 1f, 1f, 1f } : Rgb(l.Color, e.Id), l.Level * LightIntensityPerLevel, l.Level * LightMetersPerLevel);
			if (whenParam != null)
			{
				int v = when.Length == 2 ? whenParam.IndexOf(when[1]) : -1;
				if (v < 0) throw new InvalidDataException($"{e.Id}: light condition '{l.When}' names no value of '{whenParam.Name}'");
				def.LightWhen = (whenParam, v);
			}
		}
		def.RenderMask = (ushort)((def.Orientation?.Mask ?? 0) | (def.LightWhen?.Param.Mask ?? 0)
			| BlockLooks.StatesUsed(e).Select(n => def.Params.FirstOrDefault(p => p.Name == n)?.Mask ?? 0).Aggregate(0, (a, m) => a | m));
		def.DefaultData = BlockRules.DefaultState(def).Data;
		return def;
	}

	// "#rrggbb" -> 0..1
	public static float[] Rgb(string hex, string source = "")
	{
		if (hex is not { Length: 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
			throw new InvalidDataException($"{source}: colour '{hex}' is not #rrggbb");
		return new[] { (v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f };
	}

	// name -> id table stored with a world.
	public Dictionary<string, ushort> NameTable() => byId.ToDictionary(d => d.Name, d => d.Id);

	// Saved id -> current id. Unknown names throw: mapping them to air would lose player data on the next save.
	public ushort[] RemapFrom(IReadOnlyDictionary<string, ushort> saved)
	{
		var missing = saved.Keys.Where(n => !byName.ContainsKey(n)).ToList();
		if (missing.Count > 0) throw new InvalidDataException("Save uses unknown block types: " + string.Join(", ", missing));
		var map = new ushort[saved.Count == 0 ? 1 : saved.Values.Max() + 1];
		foreach (var (name, oldId) in saved) map[oldId] = byName[name].Id;
		return map;
	}
}
