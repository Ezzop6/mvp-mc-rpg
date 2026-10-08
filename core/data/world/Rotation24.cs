namespace Renesance.World;

public readonly record struct Rotation24(byte Value)
{
	public static readonly Rotation24[] Yaw = { new(0), new(20), new(1), new(21) };

	public (int X, int Y, int Z) Perm
	{
		get { int k = Value >> 2; return k < 3 ? (k, (k + 1) % 3, (k + 2) % 3) : (k % 3, (k + 2) % 3, (k + 1) % 3); }
	}

	public (int X, int Y, int Z) Signs
	{
		get { int s0 = (Value & 1) != 0 ? -1 : 1, s1 = (Value & 2) != 0 ? -1 : 1; return (s0, s1, Value >> 2 < 3 ? s0 * s1 : -s0 * s1); }
	}

	public (int X, int Y, int Z) Image(int axis)
	{
		var (p, s) = (Perm, Signs);
		int to = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z, sign = axis == 0 ? s.X : axis == 1 ? s.Y : s.Z;
		return (to == 0 ? sign : 0, to == 1 ? sign : 0, to == 2 ? sign : 0);
	}

	public static Rotation24 FromMinecraft(int x, int y)
	{
		static (int, int, int) Turn((int X, int Y, int Z) v, int x, int y)
		{
			for (int k = 0; k < (x / 90 & 3); k++) v = (v.X, v.Z, -v.Y);
			for (int k = 0; k < (y / 90 & 3); k++) v = (-v.Z, v.Y, v.X);
			return v;
		}
		for (byte r = 0; r < 24; r++)
		{
			var rot = new Rotation24(r);
			if (rot.Image(0) == Turn((1, 0, 0), x, y) && rot.Image(1) == Turn((0, 1, 0), x, y) && rot.Image(2) == Turn((0, 0, 1), x, y)) return rot;
		}
		throw new System.ArgumentException($"rotation x {x}, y {y} is not a multiple of 90 degrees");
	}
}
