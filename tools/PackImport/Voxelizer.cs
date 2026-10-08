using System.Text.Json.Nodes;
using Renesance.Content;

sealed class Voxelizer
{
	const int N = ModelLibrary.Cell;
	const double Voxel = 16.0 / N;
	static readonly string[] Dirs = { "up", "down", "north", "south", "west", "east" };
	static readonly int[] DirAxis = { 1, 1, 2, 2, 0, 0 };

	readonly Assets pack;
	readonly Dictionary<string, Texture> textures = new();
	public readonly SortedSet<string> Clipped = new(StringComparer.Ordinal);
	public int RotatedElements;

	public Voxelizer(Assets pack) => this.pack = pack;

	sealed record Texture(int W, int H, byte[] Rgba, int SW, int SH, byte[] Spec, bool Translucent);

	sealed record FaceSrc(Texture Tex, double[] Uv, int Turns, bool Tint);

	Texture Get(string name)
	{
		if (name == null) return null;
		if (textures.TryGetValue(name, out var t)) return t;
		string path = $"textures/{name}.png", spec = $"textures/{name}_s.png";
		if (!pack.Has(path)) return textures[name] = null;
		var (w, h, px) = Png.Decode(pack.Bytes(path));
		if (pack.Has(path + ".mcmeta")) h = Math.Min(h, w);
		var (sw, sh, s) = pack.Has(spec) ? Png.Decode(pack.Bytes(spec)) : (0, 0, null);
		int partial = 0;
		for (int i = 0; i < w * h; i++) if (px[i * 4 + 3] is > 0 and < 255) partial++;
		return textures[name] = new Texture(w, h, px, sw, Math.Min(sh, sw), s, partial > 0 && partial >= w * h / 10);
	}

	public VoxelModelBuilder Build(string name, JsonArray elements, Dictionary<string, string> vars)
	{
		var grid = new VoxelMaterial?[N * N * N];
		foreach (var e in elements) Element(grid, name, e.AsObject(), vars);
		FillCavities(grid);
		var b = new VoxelModelBuilder(name);
		for (int i = 0; i < grid.Length; i++) if (grid[i] is { } m) b.Set(i % N, i / N % N, i / (N * N), m);
		b.SizeX = b.SizeY = b.SizeZ = N;
		return b;
	}

	static IEnumerable<int> Neighbours(int i)
	{
		int x = i % N, y = i / N % N, z = i / (N * N);
		if (x > 0) yield return i - 1;
		if (x < N - 1) yield return i + 1;
		if (y > 0) yield return i - N;
		if (y < N - 1) yield return i + N;
		if (z > 0) yield return i - N * N;
		if (z < N - 1) yield return i + N * N;
	}

	static void FillCavities(VoxelMaterial?[] grid)
	{
		var outside = new bool[grid.Length];
		var queue = new Queue<int>();
		for (int i = 0; i < grid.Length; i++)
		{
			int x = i % N, y = i / N % N, z = i / (N * N);
			if (grid[i] == null && (x % (N - 1) == 0 || y % (N - 1) == 0 || z % (N - 1) == 0)) { outside[i] = true; queue.Enqueue(i); }
		}
		while (queue.TryDequeue(out int i))
			foreach (int n in Neighbours(i))
				if (grid[n] == null && !outside[n]) { outside[n] = true; queue.Enqueue(n); }
		var holes = Enumerable.Range(0, grid.Length).Where(i => grid[i] == null && !outside[i]).ToList();
		while (holes.Count > 0)
		{
			var fills = holes.Select(i => (i, Neighbours(i).Select(n => grid[n]).FirstOrDefault(m => m != null))).Where(f => f.Item2 != null).ToList();
			if (fills.Count == 0) break;
			foreach (var (i, m) in fills) grid[i] = m;
			holes.RemoveAll(i => grid[i] != null);
		}
	}

	static double[] Vec(JsonNode n) => n.AsArray().Select(v => v.GetValue<double>()).ToArray();

	static double[] AutoUv(int dir, double[] f, double[] t) => dir switch
	{
		0 => new[] { f[0], f[2], t[0], t[2] },
		1 => new[] { f[0], 16 - t[2], t[0], 16 - f[2] },
		2 => new[] { 16 - t[0], 16 - t[1], 16 - f[0], 16 - f[1] },
		3 => new[] { f[0], 16 - t[1], t[0], 16 - f[1] },
		4 => new[] { f[2], 16 - t[1], t[2], 16 - f[1] },
		_ => new[] { 16 - t[2], 16 - t[1], 16 - f[2], 16 - f[1] },
	};

	static double[,] Rot(int axis, double degrees)
	{
		double c = Math.Cos(degrees * Math.PI / 180), s = Math.Sin(degrees * Math.PI / 180);
		return axis switch
		{
			0 => new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } },
			1 => new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } },
			_ => new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } },
		};
	}

	static double[,] Mul(double[,] a, double[,] b)
	{
		var m = new double[3, 3];
		for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) m[i, j] += a[i, k] * b[k, j];
		return m;
	}

	static (double[,] R, double[] Origin, double[] Scale) Transform(JsonObject r)
	{
		var origin = Vec(r["origin"]);
		var scale = new[] { 1.0, 1, 1 };
		if (r["axis"] is JsonNode axisNode)
		{
			int axis = "xyz".IndexOf(axisNode.GetValue<string>());
			double angle = r["angle"]?.GetValue<double>() ?? 0;
			if (r["rescale"]?.GetValue<bool>() == true)
				for (int a = 0; a < 3; a++) if (a != axis) scale[a] = 1 / Math.Cos(Math.Abs(angle) * Math.PI / 180);
			return (Rot(axis, angle), origin, scale);
		}
		double Angle(string k) => r[k]?.GetValue<double>() ?? 0;
		return (Mul(Mul(Rot(0, Angle("x")), Rot(1, Angle("y"))), Rot(2, Angle("z"))), origin, scale);
	}

	void Element(VoxelMaterial?[] grid, string name, JsonObject e, Dictionary<string, string> vars)
	{
		double[] from = Vec(e["from"]), to = Vec(e["to"]);
		var faces = new FaceSrc[6];
		for (int d = 0; d < 6; d++)
			if (e["faces"]?[Dirs[d]] is JsonObject f && Get(Mc.Resolve(f["texture"]?.GetValue<string>(), vars)) is { } tex)
				faces[d] = new(tex, f["uv"] is JsonArray uv ? uv.Select(v => v.GetValue<double>()).ToArray() : AutoUv(d, from, to),
					(f["rotation"]?.GetValue<int>() ?? 0) / 90 & 3, f["tintindex"] != null);
		if (faces.All(f => f == null)) return;

		double[] lo = new double[3], hi = new double[3];
		var thin = new bool[3];
		for (int a = 0; a < 3; a++)
		{
			thin[a] = to[a] - from[a] < Voxel;
			double c = (from[a] + to[a]) / 2;
			(lo[a], hi[a]) = !thin[a] ? (from[a], to[a]) : c >= 8 ? (c - Voxel, c) : (c, c + Voxel);
		}
		var (r, origin, scale) = e["rotation"] is JsonObject ro ? Transform(ro) : (null, null, null);
		if (r != null) RotatedElements++;

		var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
		var max = new[] { double.MinValue, double.MinValue, double.MinValue };
		for (int i = 0; i < 8; i++)
		{
			var p = new[] { (i & 1) == 0 ? lo[0] : hi[0], (i & 2) == 0 ? lo[1] : hi[1], (i & 4) == 0 ? lo[2] : hi[2] };
			if (r != null) p = Forward(r, origin, scale, p);
			for (int a = 0; a < 3; a++) { min[a] = Math.Min(min[a], p[a]); max[a] = Math.Max(max[a], p[a]); }
		}
		int[] v0 = min.Select(m => (int)Math.Floor(m / Voxel)).ToArray(), v1 = max.Select(m => (int)Math.Ceiling(m / Voxel)).ToArray();
		var q = new double[3];
		for (int z = v0[2]; z < v1[2]; z++) for (int y = v0[1]; y < v1[1]; y++) for (int x = v0[0]; x < v1[0]; x++)
		{
			q[0] = (x + 0.5) * Voxel; q[1] = (y + 0.5) * Voxel; q[2] = (z + 0.5) * Voxel;
			if (r != null) q = Inverse(r, origin, scale, q);
			if (q[0] < lo[0] || q[0] >= hi[0] || q[1] < lo[1] || q[1] >= hi[1] || q[2] < lo[2] || q[2] >= hi[2]) continue;
			int dir = Nearest(q, from, to, thin, faces);
			if (faces[dir] == null) continue;
			if (Sample(faces[dir], dir, q, from, to) is not { } m) continue;
			if ((uint)x >= N || (uint)y >= N || (uint)z >= N) { Clipped.Add(name); continue; }
			grid[x + N * (y + N * z)] = m;
		}
	}

	static double[] Forward(double[,] r, double[] o, double[] s, double[] p)
	{
		var d = new double[3];
		for (int i = 0; i < 3; i++) d[i] = (r[i, 0] * (p[0] - o[0]) + r[i, 1] * (p[1] - o[1]) + r[i, 2] * (p[2] - o[2])) * s[i] + o[i];
		return d;
	}

	static double[] Inverse(double[,] r, double[] o, double[] s, double[] p)
	{
		var d = new double[3];
		for (int i = 0; i < 3; i++) d[i] = (p[i] - o[i]) / s[i];
		var q = new double[3];
		for (int i = 0; i < 3; i++) q[i] = r[0, i] * d[0] + r[1, i] * d[1] + r[2, i] * d[2] + o[i];
		return q;
	}

	static int Nearest(double[] q, double[] from, double[] to, bool[] thin, FaceSrc[] faces)
	{
		int best = 0;
		double bestDist = double.MaxValue;
		for (int d = 0; d < 6; d++)
		{
			int a = DirAxis[d];
			double dist = thin[a] ? 0 : d is 0 or 3 or 5 ? to[a] - q[a] : q[a] - from[a];
			if (dist < bestDist || dist == bestDist && faces[best] == null && faces[d] != null) { best = d; bestDist = dist; }
		}
		return best;
	}

	static double Frac(double v, double lo, double hi) => hi - lo <= Voxel + 1e-9 ? 0.5 : Math.Clamp((v - lo) / (hi - lo), 0, 1);

	static int Texel(double c, double a, double b, int size)
	{
		int lo = (int)Math.Floor(Math.Min(a, b) / 16 * size), hi = (int)Math.Ceiling(Math.Max(a, b) / 16 * size) - 1;
		return Math.Clamp((int)Math.Floor(c / 16 * size), Math.Max(lo, 0), Math.Min(Math.Max(hi, lo), size - 1));
	}

	static VoxelMaterial? Sample(FaceSrc f, int dir, double[] q, double[] from, double[] to)
	{
		double a = dir switch
		{
			0 or 1 or 3 => Frac(q[0], from[0], to[0]),
			2 => 1 - Frac(q[0], from[0], to[0]),
			4 => Frac(q[2], from[2], to[2]),
			_ => 1 - Frac(q[2], from[2], to[2]),
		};
		double b = dir switch
		{
			0 => Frac(q[2], from[2], to[2]),
			1 => 1 - Frac(q[2], from[2], to[2]),
			_ => 1 - Frac(q[1], from[1], to[1]),
		};
		for (int i = 0; i < f.Turns; i++) (a, b) = (b, 1 - a);
		var t = f.Tex;
		double u = f.Uv[0] + (f.Uv[2] - f.Uv[0]) * a, v = f.Uv[1] + (f.Uv[3] - f.Uv[1]) * b;
		int tx = Texel(u, f.Uv[0], f.Uv[2], t.W), ty = Texel(v, f.Uv[1], f.Uv[3], t.H);
		return Material(t, tx, ty, f.Tint, 0xFFFFFF);
	}

	static VoxelMaterial? Material(Texture t, int tx, int ty, bool tint, uint multiply)
	{
		int o = (ty * t.W + tx) * 4;
		byte alpha = t.Rgba[o + 3];
		if (t.Translucent ? alpha == 0 : alpha < 128) return null;
		uint rgb = 0;
		for (int c = 0; c < 3; c++) rgb |= (uint)(t.Rgba[o + c] * (multiply >> 8 * c & 255) / 255) << 8 * c;
		byte glow = 255;
		if (t.Spec != null && t.SW > 0)
		{
			int so = (Math.Min(ty * t.SH / t.H, t.SH - 1) * t.SW + Math.Min(tx * t.SW / t.W, t.SW - 1)) * 4;
			glow = t.Spec[so + 3];
		}
		bool emits = glow is > 0 and < 255;
		return new VoxelMaterial(rgb | (uint)(t.Translucent ? alpha : 255) << 24, 1f, 0f, emits ? rgb : 0, emits ? glow / 254f : 0f, tint);
	}

	public VoxelModelBuilder Extrude(string name, IEnumerable<(string Texture, uint Multiply)> layers)
	{
		var b = new VoxelModelBuilder(name);
		var textures = layers.Select(l => (Tex: Get(l.Texture), l.Multiply)).Where(l => l.Tex != null).ToArray();
		for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
		{
			VoxelMaterial? m = null;
			foreach (var (t, mul) in textures) m = Material(t, x * t.W / N, (N - 1 - y) * t.H / N, false, mul) ?? m;
			if (m is { } v) b.Set(x, y, N / 2, v);
		}
		b.SizeX = b.SizeY = b.SizeZ = N;
		return b;
	}
}
