using System;
using System.Linq;
using Renesance.Content.Format;
using Renesance.World;

namespace Renesance.Content;

// Block orientation (docs/content-format.md); face codes as faces.glsli: axis * 2 + positive (0 west .. 5 south).
public static class BlockFacing
{
	public const int Faces = 6;
	static readonly string[] directions = { "west", "east", "down", "up", "north", "south" };
	static readonly FaceDir[] modelFaces = { FaceDir.Left, FaceDir.Right, FaceDir.Bottom, FaceDir.Top, FaceDir.Front, FaceDir.Back };

	public static string DefaultValue(Orientation o) => o == Orientation.Axis ? "y" : "north";
	public static string Direction(int code) => directions[code];

	static int[] Dir(int code) { var v = new int[3]; v[code >> 1] = (code & 1) != 0 ? 1 : -1; return v; }
	static int Code(int[] v) { int a = v[0] != 0 ? 0 : v[1] != 0 ? 1 : 2; return a * 2 + (v[a] > 0 ? 1 : 0); }
	static int Dot(int[] a, int[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
	static int[] V(int x, int y, int z) => new[] { x, y, z };

	// u, v of a face seen from outside, texture row 0 at the top: Minecraft's face UVs, and blocks.glsli face_uv.
	static (int[] U, int[] V) Frame(int code) => code switch
	{
		0 => (V(0, 0, 1), V(0, -1, 0)),
		1 => (V(0, 0, -1), V(0, -1, 0)),
		2 => (V(1, 0, 0), V(0, 0, -1)),
		3 => (V(1, 0, 0), V(0, 0, 1)),
		4 => (V(-1, 0, 0), V(0, -1, 0)),
		_ => (V(1, 0, 0), V(0, -1, 0)),
	};

	// Model -> world: the images of x, y, z.
	static int[][] Rotation(string value) => value switch
	{
		"x" => new[] { V(0, -1, 0), V(1, 0, 0), V(0, 0, 1) },
		"z" or "up" => new[] { V(1, 0, 0), V(0, 0, 1), V(0, -1, 0) },
		"down" => new[] { V(1, 0, 0), V(0, 0, -1), V(0, 1, 0) },
		"east" => new[] { V(0, 0, 1), V(0, 1, 0), V(-1, 0, 0) },
		"south" => new[] { V(-1, 0, 0), V(0, 1, 0), V(0, 0, -1) },
		"west" => new[] { V(0, 0, -1), V(0, 1, 0), V(1, 0, 0) },
		_ => new[] { V(1, 0, 0), V(0, 1, 0), V(0, 0, 1) },
	};

	static int[] Apply(int[][] r, int[] v) => V(
		v[0] * r[0][0] + v[1] * r[1][0] + v[2] * r[2][0], v[0] * r[0][1] + v[1] * r[1][1] + v[2] * r[2][1], v[0] * r[0][2] + v[1] * r[1][2] + v[2] * r[2][2]);

	// Per world face code: the entry face shown there and the clockwise UV quarter turns the random look, then the orientation add.
	public static (FaceDir Face, int Turns)[] Map(BlockDef d, BlockState s, Rotation24 look = default)
	{
		var o = Rotation(d.Orientation?.Value(s));
		var r = Enumerable.Range(0, 3).Select(i => look.Image(i)).Select(v => Apply(o, V(v.X, v.Y, v.Z))).ToArray();
		var map = new (FaceDir, int)[Faces];
		for (int code = 0; code < Faces; code++)
		{
			var w = Dir(code);
			int model = Code(V(Dot(r[0], w), Dot(r[1], w), Dot(r[2], w)));
			var (wu, wv) = Frame(code);
			var u = Apply(r, Frame(model).U);
			map[code] = (modelFaces[model], (Dot(u, wu), Dot(u, wv)) switch { (1, 0) => 0, (0, 1) => 1, (-1, 0) => 2, _ => 3 });
		}
		return map;
	}

	// Minecraft's placement (docs/content-format.md, Pokládání); normal = zero when the clicked face is unknown.
	public static BlockState Place(BlockDef d, (int X, int Y, int Z) normal, (float X, float Y, float Z) look)
	{
		if (d.Orientation == null) return d.Default;
		if (d.Entry.Orientation == Orientation.Axis)
		{
			var (x, y, z) = normal == default ? (Math.Abs(look.X), Math.Abs(look.Y), Math.Abs(look.Z)) : ((float)Math.Abs(normal.X), Math.Abs(normal.Y), Math.Abs(normal.Z));
			return d.Orientation.With(d.Default, x >= y && x >= z ? "x" : y >= z ? "y" : "z");
		}
		float sign = d.Entry.Kind is EntryKind.Stairs or EntryKind.Door or EntryKind.FenceGate || d.Entry.Multiblock == BlockRules.PartMultiblock ? 1 : -1;
		var t = new[] { sign * look.X, d.Entry.Orientation == Orientation.Horizontal ? 0 : sign * look.Y, sign * look.Z };
		int axis = Math.Abs(t[0]) >= Math.Abs(t[1]) && Math.Abs(t[0]) >= Math.Abs(t[2]) ? 0 : Math.Abs(t[1]) >= Math.Abs(t[2]) ? 1 : 2;
		int v = d.Orientation.IndexOf(Direction(axis * 2 + (t[axis] > 0 ? 1 : 0)));
		return v < 0 ? d.Default : d.Orientation.With(d.Default, v);
	}
}
