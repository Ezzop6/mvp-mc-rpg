using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Renesance.World;

namespace Renesance.Save;

// A placed model (multiblock object) on the block grid.
public sealed record ObjectInstance(uint ModelId, Rotation24 Rotation, BlockPos Origin, byte[] Data = null);

// FourCC of a region section, bytes in file order.
public enum SectionTag : uint
{
	Blocks = 'B' | 'L' << 8 | 'K' << 16 | 'S' << 24,
	Fluids = 'F' | 'L' << 8 | 'U' << 16 | 'I' << 24,
	Objects = 'O' | 'B' << 8 | 'J' << 16 | 'S' << 24,
}

// A section this version does not know, kept byte for byte and written back.
public sealed record Section(SectionTag Tag, ushort Version, byte[] Payload);

// Sparse edits over the generator per chunk, plus the region's object instances; tagged sections (docs/world-format.md).
public sealed class Region
{
	public const int Version = 3;
	const ushort BlocksVersion = 1, FluidsVersion = 1, ObjectsVersion = 1;
	static readonly byte[] Magic = "RNSR"u8.ToArray();

	public readonly RegionPos Pos;
	public readonly Dictionary<int, Dictionary<int, BlockState>> Edits = new(); // ChunkPos.LocalIndex -> BlockPos.LocalIndex -> state
	public readonly Dictionary<int, Dictionary<int, FluidState>> Fluids = new(); // same keys, fluid per cell
	public readonly Dictionary<int, List<Section>> UnknownChunkSections = new();
	public readonly List<Section> UnknownSections = new();
	public readonly List<ObjectInstance> Instances = new();

	public Region(RegionPos pos) => Pos = pos;

	public static string FileName(RegionPos p) => $"r.{p.X}.{p.Y}.{p.Z}.bin";

	public void Save(string path)
	{
		string tmp = path + ".tmp";
		using (var file = File.Create(tmp))
		{
			file.Write(Magic);
			var bw0 = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true); // not disposed: brotli below closes the file
			bw0.Write(Version);
			bw0.Write(Pos.X); bw0.Write(Pos.Y); bw0.Write(Pos.Z);
			bw0.Flush();
			using var brotli = new BrotliStream(file, CompressionLevel.Optimal);
			using var w = new BinaryWriter(brotli);
			var chunks = Edits.Keys.Union(Fluids.Keys).Union(UnknownChunkSections.Keys).Order().ToList();
			w.Write(chunks.Count);
			foreach (int c in chunks)
			{
				w.Write((byte)c);
				var sections = new List<Section>();
				if (Edits.TryGetValue(c, out var blocks)) sections.Add(Encode(SectionTag.Blocks, BlocksVersion, s =>
				{
					s.Write(blocks.Count);
					foreach (var (b, st) in blocks.OrderBy(kv => kv.Key)) { s.Write((ushort)b); s.Write(st.Raw); }
				}));
				if (Fluids.TryGetValue(c, out var fluids)) sections.Add(Encode(SectionTag.Fluids, FluidsVersion, s =>
				{
					s.Write(fluids.Count);
					foreach (var (b, f) in fluids.OrderBy(kv => kv.Key)) { s.Write((ushort)b); s.Write(f.Type); s.Write(f.Level); }
				}));
				WriteSections(w, sections.Concat(UnknownChunkSections.GetValueOrDefault(c) ?? new()));
			}
			var objects = Encode(SectionTag.Objects, ObjectsVersion, s =>
			{
				s.Write(Instances.Count);
				foreach (var o in Instances)
				{
					s.Write(o.ModelId); s.Write(o.Rotation.Value);
					s.Write(o.Origin.X); s.Write(o.Origin.Y); s.Write(o.Origin.Z);
					s.Write(o.Data?.Length ?? -1);
					if (o.Data != null) s.Write(o.Data);
				}
			});
			WriteSections(w, UnknownSections.Prepend(objects));
		}
		File.Move(tmp, path, overwrite: true);
	}

	static Section Encode(SectionTag tag, ushort version, Action<BinaryWriter> body)
	{
		var ms = new MemoryStream();
		using (var w = new BinaryWriter(ms)) body(w);
		return new Section(tag, version, ms.ToArray());
	}

	static void WriteSections(BinaryWriter w, IEnumerable<Section> sections)
	{
		var list = sections.ToList();
		w.Write((ushort)list.Count);
		foreach (var s in list) { w.Write((uint)s.Tag); w.Write(s.Version); w.Write((uint)s.Payload.Length); w.Write(s.Payload); }
	}

	static IEnumerable<(Section Section, BinaryReader Body)> ReadSections(BinaryReader r, string path)
	{
		int n = r.ReadUInt16();
		for (int k = 0; k < n; k++)
		{
			var s = new Section((SectionTag)r.ReadUInt32(), r.ReadUInt16(), r.ReadBytes((int)r.ReadUInt32()));
			ushort known = s.Tag switch { SectionTag.Blocks => BlocksVersion, SectionTag.Fluids => FluidsVersion, SectionTag.Objects => ObjectsVersion, _ => 0 };
			if (s.Version > known && known > 0) throw new InvalidDataException($"{path}: section {s.Tag} version {s.Version}, newer than {known}");
			yield return (s, new BinaryReader(new MemoryStream(s.Payload)));
		}
	}

	// typeRemap / modelRemap: saved ids -> current ids (null = identity); fluid types use typeRemap. Reads v2 and v3.
	public static Region Load(string path, ushort[] typeRemap = null, uint[] modelRemap = null)
	{
		using var file = File.OpenRead(path);
		var magic = new byte[4];
		file.ReadExactly(magic);
		if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException($"{path}: not a region file");
		using var r0 = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
		int version = r0.ReadInt32();
		if (version is not (2 or Version)) throw new InvalidDataException($"{path}: region version {version}, expected 2..{Version}");
		var region = new Region(new RegionPos(r0.ReadInt32(), r0.ReadInt32(), r0.ReadInt32()));
		using var r = new BinaryReader(new BrotliStream(file, CompressionMode.Decompress));
		ushort Type(ushort t) => typeRemap == null ? t : typeRemap[t];
		int chunks = r.ReadInt32();
		for (int k = 0; k < chunks; k++)
		{
			int c = r.ReadByte();
			if (version == 2) { ReadBlocks(r, region.Edits[c] = new(), Type); continue; }
			foreach (var (s, body) in ReadSections(r, path))
				switch (s.Tag)
				{
					case SectionTag.Blocks: ReadBlocks(body, region.Edits[c] = new(), Type); break;
					case SectionTag.Fluids:
						var fluids = region.Fluids[c] = new();
						for (int j = body.ReadInt32(); j > 0; j--) fluids[body.ReadUInt16()] = new FluidState(Type(body.ReadUInt16()), body.ReadByte());
						break;
					default:
						if (!region.UnknownChunkSections.TryGetValue(c, out var list)) region.UnknownChunkSections[c] = list = new();
						list.Add(s);
						break;
				}
		}
		if (version == 2) ReadObjects(r, region, modelRemap);
		else
			foreach (var (s, body) in ReadSections(r, path))
				if (s.Tag == SectionTag.Objects) ReadObjects(body, region, modelRemap);
				else region.UnknownSections.Add(s);
		return region;
	}

	static void ReadBlocks(BinaryReader r, Dictionary<int, BlockState> into, Func<ushort, ushort> type)
	{
		for (int j = r.ReadInt32(); j > 0; j--)
		{
			int b = r.ReadUInt16();
			var s = new BlockState(r.ReadUInt32());
			into[b] = new BlockState(type(s.Type), s.Data);
		}
	}

	static void ReadObjects(BinaryReader r, Region region, uint[] modelRemap)
	{
		for (int k = r.ReadInt32(); k > 0; k--)
		{
			uint model = r.ReadUInt32();
			if (modelRemap != null) model = modelRemap[model];
			var rot = new Rotation24(r.ReadByte());
			var origin = new BlockPos(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
			int len = r.ReadInt32();
			region.Instances.Add(new ObjectInstance(model, rot, origin, len < 0 ? null : r.ReadBytes(len)));
		}
	}
}
