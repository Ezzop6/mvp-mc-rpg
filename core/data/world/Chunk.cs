using System;
using System.Collections.Generic;
using System.IO;

namespace Renesance.World;

// Palette of block states + bit-packed indices (1/2/4/8/16 bits).
public sealed class Chunk
{
	const int Volume = Coords.ChunkVolume;

	[Flags] enum Layers : byte { Amount = 1, Gas = 2, Entities = 4 }

	readonly List<BlockState> palette = new();
	readonly List<int> refs = new();   // blocks using each palette entry (0 = free slot)
	ulong[] bits;                       // null = every block is palette[0]
	int width;                          // bits per index, 0 when bits == null

	// Optional layers, null until first used; Amount = liquid level / granular fill.
	public byte[] Amount;
	public byte[] Gas;
	public Dictionary<int, byte[]> Entities;       // block index -> custom data

	public Chunk(BlockState fill = default) { palette.Add(fill); refs.Add(Volume); }

	public int PaletteCount => palette.Count;
	public int IndexBits => width;
	public bool IsUniform => bits == null;

	public static int Index(int x, int y, int z) => x + Coords.ChunkSize * (y + Coords.ChunkSize * z);

	public BlockState Get(int x, int y, int z) => Get(Index(x, y, z));
	public BlockState Get(int i) => palette[bits == null ? 0 : Read(i)];

	public void Set(int x, int y, int z, BlockState s) => Set(Index(x, y, z), s);

	public void Set(int i, BlockState s)
	{
		int old = bits == null ? 0 : Read(i);
		if (palette[old] == s) return;
		int p = palette.IndexOf(s);
		if (p < 0 || refs[p] == 0) // reuse a freed slot before growing
		{
			p = refs.IndexOf(0);
			if (p < 0) { p = palette.Count; palette.Add(s); refs.Add(0); }
			else palette[p] = s;
			if (palette.Count > 1 << width) Repack(WidthFor(palette.Count));
		}
		refs[old]--;
		refs[p]++;
		Write(i, p);
	}

	// Drops unused palette entries and shrinks the index width (back to no array when uniform).
	public void Compact()
	{
		var map = new int[palette.Count];
		var newPalette = new List<BlockState>();
		var newRefs = new List<int>();
		for (int p = 0; p < palette.Count; p++)
		{
			if (refs[p] == 0) { map[p] = -1; continue; }
			map[p] = newPalette.Count;
			newPalette.Add(palette[p]);
			newRefs.Add(refs[p]);
		}
		var indices = new int[Volume];
		if (bits != null) for (int i = 0; i < Volume; i++) indices[i] = map[Read(i)];
		palette.Clear(); palette.AddRange(newPalette);
		refs.Clear(); refs.AddRange(newRefs);
		width = 0; bits = null;
		if (palette.Count > 1) { Repack(WidthFor(palette.Count), zero: true); for (int i = 0; i < Volume; i++) Write(i, indices[i]); }
	}

	static int WidthFor(int count) => count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : count <= 256 ? 8 : 16;

	void Repack(int newWidth, bool zero = false)
	{
		var old = bits; int oldWidth = width;
		width = newWidth;
		bits = new ulong[(Volume + 64 / width - 1) / (64 / width)];
		if (zero || old == null) return; // a uniform chunk is all index 0 = zeroed array
		for (int i = 0; i < Volume; i++) Write(i, ReadFrom(old, oldWidth, i));
	}

	int Read(int i) => ReadFrom(bits, width, i);
	static int ReadFrom(ulong[] b, int w, int i) { int per = 64 / w; return (int)(b[i / per] >> (i % per * w) & ((1UL << w) - 1)); }

	void Write(int i, int v)
	{
		int per = 64 / width, shift = i % per * width;
		ulong mask = ((1UL << width) - 1) << shift;
		bits[i / per] = (bits[i / per] & ~mask) | ((ulong)v << shift);
	}

	public void WriteTo(BinaryWriter w)
	{
		Compact();
		w.Write(palette.Count);
		foreach (var st in palette) w.Write(st.Raw);
		w.Write((byte)width);
		if (bits != null) foreach (var x in bits) w.Write(x);
		w.Write((byte)((Amount != null ? Layers.Amount : 0) | (Gas != null ? Layers.Gas : 0) | (Entities != null ? Layers.Entities : 0)));
		if (Amount != null) w.Write(Amount);
		if (Gas != null) w.Write(Gas);
		if (Entities != null)
		{
			w.Write(Entities.Count);
			foreach (var (i, data) in Entities) { w.Write(i); w.Write(data.Length); w.Write(data); }
		}
	}

	// typeRemap: saved block type id -> current registry id (see BlockRegistry.RemapFrom), null = identity.
	public static Chunk ReadFrom(BinaryReader r, ushort[] typeRemap = null)
	{
		var c = new Chunk();
		c.palette.Clear(); c.refs.Clear();
		int n = r.ReadInt32();
		for (int p = 0; p < n; p++)
		{
			var s = new BlockState(r.ReadUInt32());
			if (typeRemap != null) s = new BlockState(typeRemap[s.Type], s.Data);
			c.palette.Add(s); c.refs.Add(0);
		}
		c.width = r.ReadByte();
		if (c.width > 0)
		{
			c.bits = new ulong[(Volume + 64 / c.width - 1) / (64 / c.width)];
			for (int i = 0; i < c.bits.Length; i++) c.bits[i] = r.ReadUInt64();
			for (int i = 0; i < Volume; i++) c.refs[c.Read(i)]++;
		}
		else c.refs[0] = Volume;
		var layers = (Layers)r.ReadByte();
		if (layers.HasFlag(Layers.Amount)) c.Amount = r.ReadBytes(Volume);
		if (layers.HasFlag(Layers.Gas)) c.Gas = r.ReadBytes(Volume);
		if (layers.HasFlag(Layers.Entities))
		{
			int count = r.ReadInt32();
			c.Entities = new Dictionary<int, byte[]>(count);
			for (int e = 0; e < count; e++) { int i = r.ReadInt32(); c.Entities[i] = r.ReadBytes(r.ReadInt32()); }
		}
		return c;
	}
}
