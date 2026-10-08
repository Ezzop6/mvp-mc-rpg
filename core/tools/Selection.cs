using System;
using System.Collections.Generic;
using Renesance.World;

namespace Renesance.Tools;

// A world-aligned block box from two corners A and B, tooling-level and free of Godot types (docs/v2.md §5.13).
public sealed class Selection
{
	public BlockPos? A { get; private set; }
	public BlockPos? B { get; private set; }

	public bool Complete => A.HasValue && B.HasValue;

	public void Set(bool first, BlockPos p) { if (first) A = p; else B = p; }

	public void Clear() => (A, B) = (null, null);

	// Lower corner and extent in blocks of the box through a and b, drawn at either corner order.
	public static (BlockPos Min, (int X, int Y, int Z) Size) Normalized(BlockPos a, BlockPos b) =>
		(new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
		 (Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1, Math.Abs(a.Z - b.Z) + 1));

	public BlockPos Min => Normalized(A.Value, B.Value).Min;

	public (int X, int Y, int Z) Size => Normalized(A.Value, B.Value).Size;

	public long Volume { get { var s = Size; return (long)s.X * s.Y * s.Z; } }

	public bool Contains(BlockPos p)
	{
		if (!Complete) return false;
		var (min, size) = Normalized(A.Value, B.Value);
		return p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X < min.X + size.X && p.Y < min.Y + size.Y && p.Z < min.Z + size.Z;
	}

	// Every cell of the box, x fastest, y and z next (the order World.ReadBox uses).
	public IEnumerable<BlockPos> Cells
	{
		get
		{
			var (min, size) = Normalized(A.Value, B.Value);
			for (int z = 0; z < size.Z; z++)
				for (int y = 0; y < size.Y; y++)
					for (int x = 0; x < size.X; x++) yield return new BlockPos(min.X + x, min.Y + y, min.Z + z);
		}
	}
}
