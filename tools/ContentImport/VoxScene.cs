using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Renesance.Content;

namespace Renesance.Import;

// One object of a .vox file in world voxel space (y up), colours as RGBA (r in the low byte), I = palette index.
public sealed class VoxObject
{
	public string Name;
	public readonly List<(int X, int Y, int Z, uint Rgba, byte I)> Voxels = new();
	public Dictionary<int, VoxMaterial> Materials = new(); // MATL by palette index, shared by the file
}

public enum VoxMaterialType { Diffuse, Metal, Glass, Emit, Blend, Media }

public sealed record VoxMaterial(VoxMaterialType Type, float Rough, float Metal, float Emit, float Flux)
{
	public static readonly VoxMaterial Default = new(VoxMaterialType.Diffuse, 1f, 0f, 0f, 0f);

	public static VoxMaterial Parse(Dictionary<string, string> d)
	{
		float F(string key, float def) =>
			d.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
		var type = d.GetValueOrDefault("_type") switch
		{
			"_metal" => VoxMaterialType.Metal, "_glass" => VoxMaterialType.Glass, "_emit" => VoxMaterialType.Emit,
			"_blend" => VoxMaterialType.Blend, "_media" => VoxMaterialType.Media, _ => VoxMaterialType.Diffuse,
		};
		return new(type, F("_rough", 1f), F("_metal", 0f), F("_emit", 0f), F("_flux", 0f));
	}
}

// .vox reader that applies nTRN translation + rotation; each child of the root group becomes its own object.
public static class VoxScene
{
	enum NodeKind { Transform, Group, Shape }
	const byte IdentityRotation = 4;
	sealed class Node { public NodeKind Kind; public int[] Children = Array.Empty<int>(); public int[] T = new int[3]; public byte R = IdentityRotation; public List<int> Models = new(); public string Name; }

	public static List<VoxObject> Load(string path)
	{
		using var f = File.OpenRead(path);
		return Read(f, Path.GetFileNameWithoutExtension(path));
	}

	public static List<VoxObject> Read(Stream s, string name)
	{
		var r = new BinaryReader(s);
		if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "VOX ") throw new InvalidDataException($"{name}: not a .vox file");
		r.ReadInt32();
		var sizes = new List<(int X, int Y, int Z)>();
		var models = new List<List<(byte X, byte Y, byte Z, byte I)>>();
		var palette = DefaultPalette();
		var nodes = new Dictionary<int, Node>();
		var matl = new Dictionary<int, VoxMaterial>();
		while (s.Position < s.Length)
		{
			string id = Encoding.ASCII.GetString(r.ReadBytes(4));
			int content = r.ReadInt32(), children = r.ReadInt32();
			long end = s.Position + content;
			switch (id)
			{
				case "MAIN": continue;
				case "SIZE": sizes.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32())); break;
				case "XYZI":
					int n = r.ReadInt32();
					var m = new List<(byte, byte, byte, byte)>(n);
					for (int i = 0; i < n; i++) m.Add((r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()));
					models.Add(m);
					break;
				case "RGBA":
					for (int i = 0; i < 255; i++) palette[i + 1] = r.ReadUInt32();
					break;
				case "MATL": matl[r.ReadInt32()] = VoxMaterial.Parse(Dict(r)); break;
				case "nTRN":
				{
					var node = new Node { Kind = NodeKind.Transform };
					int nid = r.ReadInt32();
					node.Name = Dict(r).GetValueOrDefault("_name");
					node.Children = new[] { r.ReadInt32() };
					r.ReadInt32(); r.ReadInt32();
					int frames = r.ReadInt32();
					for (int i = 0; i < frames; i++)
					{
						var d = Dict(r);
						if (i > 0) continue;
						if (d.TryGetValue("_t", out var t)) node.T = Array.ConvertAll(t.Split(' '), int.Parse);
						if (d.TryGetValue("_r", out var rot)) node.R = byte.Parse(rot);
					}
					nodes[nid] = node;
					break;
				}
				case "nGRP":
				{
					int nid = r.ReadInt32(); Dict(r);
					var c = new int[r.ReadInt32()];
					for (int i = 0; i < c.Length; i++) c[i] = r.ReadInt32();
					nodes[nid] = new Node { Kind = NodeKind.Group, Children = c };
					break;
				}
				case "nSHP":
				{
					int nid = r.ReadInt32(); Dict(r);
					var node = new Node { Kind = NodeKind.Shape };
					int k = r.ReadInt32();
					for (int i = 0; i < k; i++) { node.Models.Add(r.ReadInt32()); Dict(r); }
					nodes[nid] = node;
					break;
				}
			}
			s.Position = end;
		}

		var result = new List<VoxObject>();
		if (nodes.Count == 0) // old files without a scene graph: all models in one object, untransformed
		{
			var o = new VoxObject { Name = name, Materials = matl };
			for (int mi = 0; mi < models.Count; mi++) Emit(o, models[mi], sizes[mi], palette, Identity, new int[3]);
			result.Add(o);
			return result;
		}
		var root = nodes[nodes[0].Children[0]];
		var tops = root.Kind == NodeKind.Group ? root.Children : new[] { 0 };
		for (int i = 0; i < tops.Length; i++)
		{
			var o = new VoxObject { Name = tops.Length == 1 ? name : $"{name}_{i}", Materials = matl };
			Walk(tops[i], Identity, new int[3], nodes, models, sizes, palette, o);
			if (o.Voxels.Count > 0) result.Add(o);
		}
		return result;
	}

	static readonly int[,] Identity = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

	static void Walk(int id, int[,] rot, int[] pos, Dictionary<int, Node> nodes, List<List<(byte, byte, byte, byte)>> models,
		List<(int, int, int)> sizes, uint[] palette, VoxObject o)
	{
		var node = nodes[id];
		switch (node.Kind)
		{
			case NodeKind.Transform:
				var local = Rotation(node.R);
				var npos = Add(pos, Mul(rot, node.T));
				Walk(node.Children[0], Mul(rot, local), npos, nodes, models, sizes, palette, o);
				break;
			case NodeKind.Group:
				foreach (var c in node.Children) Walk(c, rot, pos, nodes, models, sizes, palette, o);
				break;
			case NodeKind.Shape:
				foreach (var m in node.Models) Emit(o, models[m], sizes[m], palette, rot, pos);
				break;
		}
	}

	// MagicaVoxel pivots a model at floor(size / 2). Axes: ours (x, y, z) = vox (x, z, -y), keeps handedness.
	static void Emit(VoxObject o, List<(byte X, byte Y, byte Z, byte I)> voxels, (int X, int Y, int Z) size, uint[] palette, int[,] rot, int[] pos)
	{
		foreach (var v in voxels)
		{
			var p = Add(pos, Mul(rot, new[] { v.X - size.X / 2, v.Y - size.Y / 2, v.Z - size.Z / 2 }));
			o.Voxels.Add((p[0], p[2], -p[1], palette[v.I], v.I));
		}
	}

	// _r byte: bits 0-1 column of the non-zero in row 0, bits 2-3 for row 1, bits 4/5/6 sign of rows 0/1/2.
	static int[,] Rotation(byte r)
	{
		int c0 = r & 3, c1 = (r >> 2) & 3, c2 = 3 - c0 - c1;
		var m = new int[3, 3];
		m[0, c0] = (r & 16) != 0 ? -1 : 1;
		m[1, c1] = (r & 32) != 0 ? -1 : 1;
		m[2, c2] = (r & 64) != 0 ? -1 : 1;
		return m;
	}

	static int[,] Mul(int[,] a, int[,] b)
	{
		var m = new int[3, 3];
		for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) m[i, j] += a[i, k] * b[k, j];
		return m;
	}

	static int[] Mul(int[,] a, int[] v) => new[] { a[0, 0] * v[0] + a[0, 1] * v[1] + a[0, 2] * v[2], a[1, 0] * v[0] + a[1, 1] * v[1] + a[1, 2] * v[2], a[2, 0] * v[0] + a[2, 1] * v[1] + a[2, 2] * v[2] };
	static int[] Add(int[] a, int[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };

	// Objects (of one file) -> one micro-voxel model, each voxel `scale`^3 micro-voxels, shifted to start at 0.
	public static VoxelModelBuilder ToModel(string name, IReadOnlyList<VoxObject> objects, int scale = 1)
	{
		var b = new VoxelModelBuilder(name);
		var all = objects.SelectMany(o => o.Voxels.Select(v => (v, o.Materials))).ToList();
		if (all.Count == 0) return b;
		int minX = all.Min(e => e.v.X), minY = all.Min(e => e.v.Y), minZ = all.Min(e => e.v.Z);
		foreach (var (v, matl) in all)
		{
			var m = Material(v.Rgba, matl.GetValueOrDefault(v.I, VoxMaterial.Default));
			for (int z = 0; z < scale; z++) for (int y = 0; y < scale; y++) for (int x = 0; x < scale; x++)
				b.Set((v.X - minX) * scale + x, (v.Y - minY) * scale + y, (v.Z - minZ) * scale + z, m);
		}
		return b;
	}

	static VoxelMaterial Material(uint rgba, VoxMaterial m)
	{
		bool emit = m.Type == VoxMaterialType.Emit;
		return new VoxelMaterial(rgba, m.Rough, m.Type == VoxMaterialType.Metal ? m.Metal : 0f,
			emit ? rgba & 0xFFFFFF : 0, emit ? m.Emit * (1 + m.Flux) : 0f); // ponytail: _glass/_ior not mapped
	}

	static Dictionary<string, string> Dict(BinaryReader r)
	{
		var d = new Dictionary<string, string>();
		int n = r.ReadInt32();
		for (int i = 0; i < n; i++) d[Str(r)] = Str(r);
		return d;
	}

	static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));

	// ponytail: files without an RGBA chunk get a grey ramp, not MagicaVoxel's built-in palette.
	static uint[] DefaultPalette()
	{
		var p = new uint[256];
		for (uint i = 0; i < 256; i++) p[i] = 0xFF000000 | (255 - i) * 0x010101;
		return p;
	}
}
