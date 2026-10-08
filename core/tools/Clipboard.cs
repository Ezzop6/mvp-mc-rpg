using Godot;
using Renesance.World;

namespace Renesance.Tools;

// A copied box of block states with corner A's offset; rotation and flip keep A fixed (docs/v2.md §5.13).
public sealed class Clipboard
{
	public Vector3I Size { get; }
	public BlockState[] States { get; }
	public Vector3I Anchor { get; }

	public Clipboard(Vector3I size, BlockState[] states, Vector3I anchor) => (Size, States, Anchor) = (size, states, anchor);

	public int Index(int x, int y, int z) => x + Size.X * (y + Size.Y * z);

	public BlockState At(int x, int y, int z) =>
		x >= 0 && y >= 0 && z >= 0 && x < Size.X && y < Size.Y && z < Size.Z ? States[Index(x, y, z)] : BlockState.Air;

	// x' = sz - 1 - z, z' = x: a quarter turn around Y, corner A stays where it is.
	public Clipboard Rotate()
	{
		var s = Size;
		var r = new Vector3I(s.Z, s.Y, s.X);
		var states = new BlockState[States.Length];
		for (int z = 0; z < s.Z; z++)
			for (int y = 0; y < s.Y; y++)
				for (int x = 0; x < s.X; x++)
					states[(s.Z - 1 - z) + r.X * (y + r.Y * x)] = States[Index(x, y, z)];
		return new Clipboard(r, states, new Vector3I(s.Z - 1 - Anchor.Z, Anchor.Y, Anchor.X));
	}

	// x' = sx - 1 - x: a mirror across the YZ plane, corner A stays where it is.
	public Clipboard Flip()
	{
		var s = Size;
		var states = new BlockState[States.Length];
		for (int z = 0; z < s.Z; z++)
			for (int y = 0; y < s.Y; y++)
				for (int x = 0; x < s.X; x++)
					states[Index(s.X - 1 - x, y, z)] = States[Index(x, y, z)];
		return new Clipboard(s, states, new Vector3I(s.X - 1 - Anchor.X, Anchor.Y, Anchor.Z));
	}
}
