using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Renesance.World;

namespace Renesance.Content;

// Voxel models (docs/world-format.md), no Godot dependency; cells and bricks dedupe across models (palette-relative bytes).

public readonly record struct VoxelMaterial(uint Rgba, float Roughness, float Metallic, uint EmissionRgb, float EmissionStrength, bool Tint = false)
{
	public static VoxelMaterial FromRgba(byte r, byte g, byte b, byte a = 255) =>
		new((uint)(r | g << 8 | b << 16 | a << 24), 1f, 0f, 0, 0f);
}

public sealed class ModelInfo
{
	public string Name;
	public int SizeX, SizeY, SizeZ;     // in micro-voxels
	public int CellsX, CellsY, CellsZ;  // in blocks
	public int Width;                   // bytes per voxel: 1 or 2
	public int PaletteStart, PaletteCount; // into ModelLibrary.Materials, entry 0 = empty
	public int CellTableStart;          // into ModelLibrary.CellTable, CellsX*CellsY*CellsZ entries

	public int CellIndex(int cx, int cy, int cz) => CellTableStart + cx + CellsX * (cy + CellsY * cz);
}

// Cell record = 73 brick refs ([0..63] mip 0, [64..71] mip 1, [72] mip 2) + 65 tail values (mip 3 = 4^3, mip 4 = 1).
public sealed class ModelPool
{
	public const uint Empty = uint.MaxValue;
	public const int BrickRefs = 73, TailValues = 65, Mip1Ref = 64, Mip2Ref = 72, BrickVoxels = 512;

	public readonly int Width;
	public readonly List<byte> Bricks = new();   // 512 * Width bytes per brick, voxel x + 8 * (y + 8 * z)
	public readonly List<uint> CellRefs = new(); // BrickRefs per cell
	public readonly List<byte> CellTails = new(); // TailValues * Width bytes per cell
	readonly Dictionary<byte[], uint> brickIds = new(BytesComparer.Instance);
	readonly Dictionary<byte[], uint> cellIds = new(BytesComparer.Instance);

	public ModelPool(int width) => Width = width;

	public int BrickCount => Bricks.Count / (BrickVoxels * Width);
	public int CellCount => CellRefs.Count / BrickRefs;
	public long Bytes => Bricks.Count + CellRefs.Count * 4L + CellTails.Count;

	public uint InternBrick(byte[] data)
	{
		if (brickIds.TryGetValue(data, out var id)) return id;
		id = (uint)BrickCount;
		brickIds[data] = id;
		Bricks.AddRange(data);
		return id;
	}

	public uint InternCell(uint[] refs, byte[] tail)
	{
		var key = CellKey(refs, tail);
		if (cellIds.TryGetValue(key, out var id)) return id;
		id = (uint)CellCount;
		cellIds[key] = id;
		CellRefs.AddRange(refs);
		CellTails.AddRange(tail);
		return id;
	}

	public ushort Read(List<byte> src, int offset, int index) =>
		Width == 1 ? src[offset + index] : (ushort)(src[offset + 2 * index] | src[offset + 2 * index + 1] << 8);

	internal void Append(ModelPool o)
	{
		uint brickBase = (uint)BrickCount, cellBase = (uint)CellCount;
		Bricks.AddRange(o.Bricks);
		CellRefs.AddRange(o.CellRefs.Select(r => r == Empty ? r : r + brickBase));
		CellTails.AddRange(o.CellTails);
		foreach (var (key, id) in o.brickIds) brickIds.TryAdd(key, id + brickBase);
		int ts = TailValues * Width;
		for (int i = (int)cellBase; i < CellCount; i++)
			cellIds.TryAdd(CellKey(CellRefs.GetRange(i * BrickRefs, BrickRefs).ToArray(), CellTails.GetRange(i * ts, ts).ToArray()), (uint)i);
	}

	internal void RebuildDedupe()
	{
		brickIds.Clear(); cellIds.Clear();
		int bs = BrickVoxels * Width, ts = TailValues * Width;
		for (int i = 0; i < BrickCount; i++) brickIds[Bricks.GetRange(i * bs, bs).ToArray()] = (uint)i;
		for (int i = 0; i < CellCount; i++)
			cellIds[CellKey(CellRefs.GetRange(i * BrickRefs, BrickRefs).ToArray(), CellTails.GetRange(i * ts, ts).ToArray())] = (uint)i;
	}

	static byte[] CellKey(uint[] refs, byte[] tail)
	{
		var key = new byte[refs.Length * 4 + tail.Length];
		Buffer.BlockCopy(refs, 0, key, 0, refs.Length * 4);
		tail.CopyTo(key, refs.Length * 4);
		return key;
	}
}

sealed class BytesComparer : IEqualityComparer<byte[]>
{
	public static readonly BytesComparer Instance = new();
	public bool Equals(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
	public int GetHashCode(byte[] a) { var h = new HashCode(); h.AddBytes(a); return h.ToHashCode(); }
}

// Collects voxels of one model before it is packed into a library.
public sealed class VoxelModelBuilder
{
	public readonly string Name;
	public int SizeX, SizeY, SizeZ;
	internal readonly Dictionary<long, ushort> Voxels = new();
	internal readonly List<VoxelMaterial> Palette = new() { default }; // 0 = empty
	readonly Dictionary<VoxelMaterial, ushort> paletteIds = new();

	public VoxelModelBuilder(string name) => Name = name;

	public void Set(int x, int y, int z, VoxelMaterial m)
	{
		if (x < 0 || y < 0 || z < 0 || x >= 1 << 21 || y >= 1 << 21 || z >= 1 << 21)
			throw new ArgumentOutOfRangeException(nameof(x), $"voxel ({x},{y},{z}) outside 0..2^21");
		if (!paletteIds.TryGetValue(m, out var id))
		{
			if (Palette.Count > ushort.MaxValue) throw new InvalidOperationException("more than 65535 materials");
			id = (ushort)Palette.Count;
			paletteIds[m] = id;
			Palette.Add(m);
		}
		Voxels[Key(x, y, z)] = id;
		SizeX = Math.Max(SizeX, x + 1); SizeY = Math.Max(SizeY, y + 1); SizeZ = Math.Max(SizeZ, z + 1);
	}

	internal static long Key(int x, int y, int z) => (long)x | (long)y << 21 | (long)z << 42;
}

public sealed class ModelLibrary
{
	public const int Cell = 32, Brick = 8;
	const uint Magic = 0x4C4D5652; // "RVML"
	const int Version = 2, UntintedVersion = 1;

	public readonly List<ModelInfo> Models = new();
	public readonly List<VoxelMaterial> Materials = new(); // all palettes concatenated
	public readonly List<uint> CellTable = new();          // per model cell grid -> cell id in its pool, or Empty
	public readonly ModelPool[] Pools = { new(1), new(2) }; // by width - 1

	public ModelPool PoolOf(ModelInfo m) => Pools[m.Width - 1];

	public int Add(VoxelModelBuilder b)
	{
		var m = new ModelInfo
		{
			Name = b.Name, SizeX = b.SizeX, SizeY = b.SizeY, SizeZ = b.SizeZ,
			CellsX = Math.Max(1, (b.SizeX + Cell - 1) / Cell), CellsY = Math.Max(1, (b.SizeY + Cell - 1) / Cell),
			CellsZ = Math.Max(1, (b.SizeZ + Cell - 1) / Cell),
			Width = b.Palette.Count <= 256 ? 1 : 2,
			PaletteStart = Materials.Count, PaletteCount = b.Palette.Count, CellTableStart = CellTable.Count,
		};
		Materials.AddRange(b.Palette);

		var cells = new Dictionary<int, ushort[]>();
		foreach (var (key, v) in b.Voxels)
		{
			int x = (int)(key & 0x1FFFFF), y = (int)(key >> 21 & 0x1FFFFF), z = (int)(key >> 42);
			int ci = x / Cell + m.CellsX * (y / Cell + m.CellsY * (z / Cell));
			if (!cells.TryGetValue(ci, out var grid)) cells[ci] = grid = new ushort[Cell * Cell * Cell];
			grid[x % Cell + Cell * (y % Cell + Cell * (z % Cell))] = v;
		}
		var pool = PoolOf(m);
		for (int ci = 0; ci < m.CellsX * m.CellsY * m.CellsZ; ci++)
			CellTable.Add(cells.TryGetValue(ci, out var grid) ? PackCell(pool, grid) : ModelPool.Empty);
		Models.Add(m);
		return Models.Count - 1;
	}

	static uint PackCell(ModelPool pool, ushort[] l0)
	{
		var l1 = Downsample(l0, 32); var l2 = Downsample(l1, 16); var l3 = Downsample(l2, 8); var l4 = Downsample(l3, 4);
		var refs = new uint[ModelPool.BrickRefs];
		int r = 0;
		for (int bz = 0; bz < 4; bz++) for (int by = 0; by < 4; by++) for (int bx = 0; bx < 4; bx++) refs[r++] = PackBrick(pool, l0, 32, bx, by, bz);
		for (int bz = 0; bz < 2; bz++) for (int by = 0; by < 2; by++) for (int bx = 0; bx < 2; bx++) refs[r++] = PackBrick(pool, l1, 16, bx, by, bz);
		refs[r] = PackBrick(pool, l2, 8, 0, 0, 0);
		var tail = new byte[ModelPool.TailValues * pool.Width];
		for (int i = 0; i < 64; i++) Write(tail, pool.Width, i, l3[i]);
		Write(tail, pool.Width, ModelPool.TailValues - 1, l4[0]);
		return pool.InternCell(refs, tail);
	}

	static uint PackBrick(ModelPool pool, ushort[] src, int n, int bx, int by, int bz)
	{
		var data = new byte[ModelPool.BrickVoxels * pool.Width];
		bool any = false;
		for (int z = 0; z < 8; z++) for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
		{
			ushort v = src[bx * 8 + x + n * (by * 8 + y + n * (bz * 8 + z))];
			any |= v != 0;
			Write(data, pool.Width, x + 8 * (y + 8 * z), v);
		}
		return any ? pool.InternBrick(data) : ModelPool.Empty;
	}

	static void Write(byte[] dst, int width, int i, ushort v)
	{
		if (width == 1) dst[i] = (byte)v;
		else { dst[2 * i] = (byte)v; dst[2 * i + 1] = (byte)(v >> 8); }
	}

	// Parent is filled when >= 4 of 8 children are, with the most frequent child material (lowest index on ties).
	static ushort[] Downsample(ushort[] src, int n)
	{
		int h = n / 2;
		var dst = new ushort[h * h * h];
		Span<ushort> kids = stackalloc ushort[8];
		for (int z = 0; z < h; z++) for (int y = 0; y < h; y++) for (int x = 0; x < h; x++)
		{
			int filled = 0;
			for (int i = 0; i < 8; i++)
			{
				ushort v = src[2 * x + (i & 1) + n * (2 * y + (i >> 1 & 1) + n * (2 * z + (i >> 2)))];
				if (v != 0) kids[filled++] = v;
			}
			if (filled < 4) continue;
			ushort best = 0; int bestCount = 0;
			for (int i = 0; i < filled; i++)
			{
				int c = 0;
				for (int j = 0; j < filled; j++) if (kids[j] == kids[i]) c++;
				if (c > bestCount || (c == bestCount && kids[i] < best)) { best = kids[i]; bestCount = c; }
			}
			dst[x + h * (y + h * z)] = best;
		}
		return dst;
	}

	public int Append(ModelLibrary o)
	{
		int first = Models.Count, mats = Materials.Count, cells = CellTable.Count;
		var cellBase = Pools.Select(p => (uint)p.CellCount).ToArray();
		Materials.AddRange(o.Materials);
		CellTable.AddRange(o.CellTable);
		foreach (var m in o.Models)
		{
			Models.Add(new ModelInfo
			{
				Name = m.Name, SizeX = m.SizeX, SizeY = m.SizeY, SizeZ = m.SizeZ, CellsX = m.CellsX, CellsY = m.CellsY, CellsZ = m.CellsZ,
				Width = m.Width, PaletteStart = m.PaletteStart + mats, PaletteCount = m.PaletteCount, CellTableStart = m.CellTableStart + cells,
			});
			for (int i = cells + m.CellTableStart; i < cells + m.CellTableStart + m.CellsX * m.CellsY * m.CellsZ; i++)
				if (CellTable[i] != ModelPool.Empty) CellTable[i] += cellBase[m.Width - 1];
		}
		for (int p = 0; p < Pools.Length; p++) Pools[p].Append(o.Pools[p]);
		return first;
	}

	public int Compose(string name, IEnumerable<(int Model, Rotation24 Rotation)> parts)
	{
		var b = new VoxelModelBuilder(name);
		foreach (var (model, r) in parts)
		{
			var (p, s) = (r.Perm, r.Signs);
			static int At(int v, int sign) => sign > 0 ? v : Cell - 1 - v;
			var d = new int[3];
			for (int z = 0; z < Cell; z++) for (int y = 0; y < Cell; y++) for (int x = 0; x < Cell; x++)
			{
				ushort v = GetVoxel(model, x, y, z);
				if (v == 0) continue;
				d[p.X] = At(x, s.X); d[p.Y] = At(y, s.Y); d[p.Z] = At(z, s.Z);
				b.Set(d[0], d[1], d[2], GetMaterial(model, v));
			}
		}
		b.SizeX = b.SizeY = b.SizeZ = Cell;
		return Add(b);
	}

	// Coordinates are in the mip level's grid (32 >> level per cell).
	public ushort GetVoxel(int model, int x, int y, int z, int level = 0)
	{
		var m = Models[model];
		int n = Cell >> level;
		if (x < 0 || y < 0 || z < 0 || x >= m.CellsX * n || y >= m.CellsY * n || z >= m.CellsZ * n) return 0;
		uint cell = CellTable[m.CellIndex(x / n, y / n, z / n)];
		if (cell == ModelPool.Empty) return 0;
		var pool = PoolOf(m);
		x %= n; y %= n; z %= n;
		if (level >= 3)
		{
			int i = level == 4 ? ModelPool.TailValues - 1 : x + 4 * (y + 4 * z);
			return pool.Read(pool.CellTails, (int)cell * ModelPool.TailValues * pool.Width, i);
		}
		int bricksPerAxis = n / Brick, refBase = level == 0 ? 0 : level == 1 ? ModelPool.Mip1Ref : ModelPool.Mip2Ref;
		uint brick = pool.CellRefs[(int)cell * ModelPool.BrickRefs + refBase + x / 8 + bricksPerAxis * (y / 8 + bricksPerAxis * (z / 8))];
		if (brick == ModelPool.Empty) return 0;
		return pool.Read(pool.Bricks, (int)brick * ModelPool.BrickVoxels * pool.Width, x % 8 + 8 * (y % 8 + 8 * (z % 8)));
	}

	public VoxelMaterial GetMaterial(int model, ushort index) => Materials[Models[model].PaletteStart + index];

	public void Save(Stream s)
	{
		using var w = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
		w.Write(Magic); w.Write(Version);
		w.Write(Models.Count);
		foreach (var m in Models)
		{
			w.Write(m.Name ?? "");
			foreach (var v in new[] { m.SizeX, m.SizeY, m.SizeZ, m.CellsX, m.CellsY, m.CellsZ, m.Width, m.PaletteStart, m.PaletteCount, m.CellTableStart })
				w.Write(v);
		}
		w.Write(Materials.Count);
		foreach (var mat in Materials)
		{
			w.Write(mat.Rgba); w.Write(mat.Roughness); w.Write(mat.Metallic); w.Write(mat.EmissionRgb); w.Write(mat.EmissionStrength); w.Write(mat.Tint);
		}
		WriteUints(w, CellTable);
		foreach (var p in Pools)
		{
			w.Write(p.Bricks.Count); w.Write(p.Bricks.ToArray());
			WriteUints(w, p.CellRefs);
			w.Write(p.CellTails.Count); w.Write(p.CellTails.ToArray());
		}
	}

	public static ModelLibrary Load(Stream s)
	{
		using var r = new BinaryReader(s, System.Text.Encoding.UTF8, leaveOpen: true);
		if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a model library");
		int version = r.ReadInt32();
		if (version is not (Version or UntintedVersion)) throw new InvalidDataException($"model library version {version}, expected {Version}");
		var lib = new ModelLibrary();
		int models = r.ReadInt32();
		for (int i = 0; i < models; i++)
			lib.Models.Add(new ModelInfo
			{
				Name = r.ReadString(), SizeX = r.ReadInt32(), SizeY = r.ReadInt32(), SizeZ = r.ReadInt32(),
				CellsX = r.ReadInt32(), CellsY = r.ReadInt32(), CellsZ = r.ReadInt32(), Width = r.ReadInt32(),
				PaletteStart = r.ReadInt32(), PaletteCount = r.ReadInt32(), CellTableStart = r.ReadInt32(),
			});
		int mats = r.ReadInt32();
		for (int i = 0; i < mats; i++)
			lib.Materials.Add(new VoxelMaterial(r.ReadUInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadUInt32(), r.ReadSingle(), version != UntintedVersion && r.ReadBoolean()));
		ReadUints(r, lib.CellTable);
		foreach (var p in lib.Pools)
		{
			p.Bricks.AddRange(r.ReadBytes(r.ReadInt32()));
			ReadUints(r, p.CellRefs);
			p.CellTails.AddRange(r.ReadBytes(r.ReadInt32()));
			p.RebuildDedupe();
		}
		return lib;
	}

	static void WriteUints(BinaryWriter w, List<uint> list) { w.Write(list.Count); foreach (var v in list) w.Write(v); }
	static void ReadUints(BinaryReader r, List<uint> list) { int n = r.ReadInt32(); for (int i = 0; i < n; i++) list.Add(r.ReadUInt32()); }
}
