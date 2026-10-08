using System;
using System.Collections.Generic;
using Renesance.World;

// Fixed-size record pools (docs/v2.md §3.6): chunk records, then the brick classes 0-2.
public enum WorldPool { Records, Brick4, Brick16, BrickFull }

// The world format (docs/v2.md §3.3), shared with the shaders through Gpu's prelude; Godot-free for tests/World.
public static class Layout
{
	public const int Chunk = Coords.ChunkSize, Group = Coords.GroupSize, GroupsPerAxis = Chunk / Group, Groups = GroupsPerAxis * GroupsPerAxis * GroupsPerAxis, GroupCells = Group * Group * Group;
	public const uint Uni = 1u << 31, None = 0x7FFFFFFF, Part = 1u << 30;
	public const int FlagShift = 22, ClassShift = 28, FluidLevelShift = 22;
	public const uint IdMask = (1u << FlagShift) - 1, IndexMask = (1u << ClassShift) - 1, ClassAir = 3;
	// A byte per cell; the air box needs bytes 1-3 of the last index word (GroupCells % 4 == 1), else a word of its own.
	public const int IndexWords = (GroupCells + 3) / 4 + (GroupCells % 4 == 1 ? 0 : 1), BoxWord = IndexWords - 1;
	public const int StampWord = 2 * Groups, ChunkRecWords = (StampWord + 1 + 7) / 8 * 8; // StampWord: record word PACK stamps per generation (Raster meshes)
	public const int MaskWords = ((Groups > GroupCells ? Groups : GroupCells) + 31) / 32; // gen.compute bit masks over a chunk's groups or a group's cells
	public static readonly int[] ClassPairs = { 4, 16, GroupCells };

	static Layout()
	{
		if (Chunk % Group != 0 || Group > 7 || GroupsPerAxis > 8) throw new InvalidOperationException("air boxes pack 3 bits per axis: Group <= 7, Chunk / Group <= 8 and a whole number");
		if (GroupCells <= ClassPairs[1] || GroupCells > 256) throw new InvalidOperationException("brick classes need 16 < Group^3 <= 256 (a byte per cell)");
	}

	public static int BrickWords(int cls) => IndexWords + 2 * ClassPairs[cls];
	public static int ClassOf(int pairs) => Array.FindIndex(ClassPairs, n => pairs <= n);
	public static int PoolWords(WorldPool p) => p == WorldPool.Records ? ChunkRecWords : BrickWords((int)p - 1);

	// CPU mirror of gen.compute FILL: palette in first-use order, a byte per cell; class -1 = one pair (a uniform group).
	public static (int Class, uint[] Words) PackBrick(ReadOnlySpan<(uint Block, uint Fluid)> cells)
	{
		var palette = new List<(uint, uint)>();
		var index = new int[cells.Length];
		for (int v = 0; v < cells.Length; v++)
		{
			int i = palette.IndexOf(cells[v]);
			if (i < 0) { i = palette.Count; palette.Add(cells[v]); }
			index[v] = i;
		}
		if (palette.Count == 1) return (-1, null);
		int cls = ClassOf(palette.Count);
		var words = new uint[BrickWords(cls)];
		for (int v = 0; v < cells.Length; v++) words[v >> 2] |= (uint)index[v] << ((v & 3) * 8);
		for (int i = 0; i < palette.Count; i++) (words[IndexWords + 2 * i], words[IndexWords + 2 * i + 1]) = palette[i];
		return (cls, words);
	}

	public static (uint Block, uint Fluid) Cell(uint[] brick, int v)
	{
		int i = (int)(brick[v >> 2] >> ((v & 3) * 8)) & 0xFF;
		return (brick[IndexWords + 2 * i], brick[IndexWords + 2 * i + 1]);
	}
}
