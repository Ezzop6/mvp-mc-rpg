using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Renesance.Content;
using Renesance.World;

namespace Renesance.Save;

// A world folder <root>/<name>/: world.json and per dimension <dimension>/r.x.y.z.bin with the sparse edits (docs/world-format.md).
public sealed class WorldSave
{
	public readonly string Dir;
	public WorldHeader Header;

	WorldSave(string dir, WorldHeader header) { Dir = Path.GetFullPath(dir); Header = header; }

	public string Name => Path.GetFileName(Dir);
	string HeaderPath => Path.Combine(Dir, WorldHeader.FileName);

	public static bool ValidName(string name) =>
		!string.IsNullOrWhiteSpace(name) && name.Trim() == name && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars().Append('\\').ToArray()) < 0;

	public static bool Exists(string dir) => File.Exists(Path.Combine(dir, WorldHeader.FileName));

	// Nothing is written until Save.
	public static WorldSave Create(string root, string name)
	{
		if (!ValidName(name)) throw new ArgumentException($"invalid world name '{name}'", nameof(name));
		return new WorldSave(Path.Combine(root, name), new WorldHeader { Name = name });
	}

	public static WorldSave Open(string dir)
	{
		var header = WorldHeader.Load(Path.Combine(dir, WorldHeader.FileName));
		if (header.Format > WorldHeader.CurrentFormat) throw new InvalidDataException($"{dir}: world format {header.Format}, newer than {WorldHeader.CurrentFormat}");
		return new WorldSave(dir, header);
	}

	public static IEnumerable<string> List(string root) =>
		Directory.Exists(root) ? Directory.GetDirectories(root).Where(Exists).Select(Path.GetFileName).Order(StringComparer.Ordinal) : Enumerable.Empty<string>();

	public static void Delete(string root, string name)
	{
		string dir = Path.Combine(root, name);
		if (!ValidName(name) || !Exists(dir)) throw new ArgumentException($"no world '{name}' in {root}", nameof(name));
		Directory.Delete(dir, recursive: true);
	}

	public static WorldSave Rename(string root, string name, string newName)
	{
		if (!ValidName(newName)) throw new ArgumentException($"invalid world name '{newName}'", nameof(newName));
		string dir = Path.Combine(root, newName);
		if (Directory.Exists(dir)) throw new IOException($"{dir} already exists");
		var save = Open(Path.Combine(root, name));
		Directory.Move(save.Dir, dir);
		save = new WorldSave(dir, save.Header);
		save.Header.Name = newName;
		save.SaveHeader();
		return save;
	}

	// Writes the dimension's regions (stale or truncated ones removed, unknown sections of replaced files kept), then world.json with the used block names.
	public void Save(BlockRegistry registry, WorldEdits edits, string dimension = WorldHeader.DefaultDimension)
	{
		string dimDir = Path.Combine(Dir, dimension);
		Directory.CreateDirectory(dimDir);
		var regions = new Dictionary<RegionPos, Region>();
		var used = new HashSet<ushort>();
		Region At(RegionPos p) => regions.TryGetValue(p, out var r) ? r : regions[p] = new Region(p);
		foreach (var c in edits.Chunks)
		{
			var region = At(c.Region);
			var blocks = edits.InChunk(c).ToDictionary(e => e.Pos.LocalIndex, e => e.State);
			var fluids = edits.FluidsInChunk(c).ToDictionary(e => e.Pos.LocalIndex, e => e.Fluid);
			if (blocks.Count > 0) region.Edits[c.LocalIndex] = blocks;
			if (fluids.Count > 0) region.Fluids[c.LocalIndex] = fluids;
			used.UnionWith(blocks.Values.Select(s => s.Type).Concat(fluids.Values.Select(f => f.Type)));
		}
		foreach (var f in Directory.GetFiles(dimDir, "r.*.bin"))
		{
			Region old;
			try { old = Region.Load(f); } catch (EndOfStreamException) { continue; }
			if (old.UnknownSections.Count == 0 && old.UnknownChunkSections.Count == 0) continue;
			var region = At(old.Pos);
			region.UnknownSections.AddRange(old.UnknownSections);
			foreach (var (c, sections) in old.UnknownChunkSections) region.UnknownChunkSections[c] = sections;
		}
		var written = regions.Values.Select(r => Path.Combine(dimDir, Region.FileName(r.Pos))).ToHashSet();
		foreach (var r in regions.Values) r.Save(Path.Combine(dimDir, Region.FileName(r.Pos)));
		foreach (var f in Directory.GetFiles(dimDir, "r.*.bin").Where(f => !written.Contains(f))) File.Delete(f);
		Header.Blocks = used.Order().ToDictionary(id => registry[id].Name, id => id);
		(Header.ChunkSize, Header.RegionSize) = (Coords.ChunkSize, Coords.RegionSize);
		SaveHeader();
	}

	public void SaveHeader()
	{
		Directory.CreateDirectory(Dir);
		Header.Save(HeaderPath);
	}

	// Adds the dimension's saved block and fluid edits to `into`, block ids remapped by name to `registry`; regions of another chunk or region size are read by block position.
	public void Load(BlockRegistry registry, WorldEdits into, string dimension = WorldHeader.DefaultDimension)
	{
		string dimDir = Path.Combine(Dir, dimension);
		if (!Directory.Exists(dimDir)) return;
		var remap = registry.RemapFrom(Header.Blocks);
		int cs = Header.ChunkSize ?? 25, rs = Header.RegionSize ?? 5;
		BlockPos At(RegionPos r, int ci, int b)
		{
			int cx = r.X * rs + ci % rs, cy = r.Y * rs + ci / rs % rs, cz = r.Z * rs + ci / (rs * rs);
			return new BlockPos(cx * cs + b % cs, cy * cs + b / cs % cs, cz * cs + b / (cs * cs));
		}
		foreach (var f in Directory.GetFiles(dimDir, "r.*.bin"))
		{
			var region = Region.Load(f, remap);
			foreach (var (ci, blocks) in region.Edits)
				foreach (var (b, s) in blocks) into.Set(At(region.Pos, ci, b), s);
			foreach (var (ci, fluids) in region.Fluids)
				foreach (var (b, fl) in fluids) into.SetFluid(At(region.Pos, ci, b), fl);
		}
	}
}
