using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Renesance.Import;
using Renesance.Content;
using Renesance.World;

// Self-checks for the model library (core/data/content). Run: dotnet run --project tests/Models
static class Program
{
	static int failures;

	static void Check(bool ok, string what)
	{
		Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
		if (!ok) failures++;
	}

	static int Main()
	{
		AxisMapping();
		var (lib, chair) = ChairRoundTrip();
		Dedupe(lib, chair);
		WidePalette();
		Mips();
		MultiBlock();
		SceneGraph();
		Materials();
		BlockModels();
		Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
		return failures == 0 ? 0 : 1;
	}

	static void AxisMapping()
	{
		var b = Import(Vox(new[] { Model(1, 1, 11, (0, 0, 10, 1), (0, 0, 0, 1)) }));
		Check(b.SizeX == 1 && b.SizeY == 11 && b.SizeZ == 1, $".vox z-up -> y-up: size {b.SizeX}x{b.SizeY}x{b.SizeZ}");
		var lib = new ModelLibrary();
		int m = lib.Add(b);
		Check(lib.GetVoxel(m, 0, 10, 0) != 0 && lib.GetVoxel(m, 0, 5, 0) == 0, "voxel at vox z=10 lands at y=10");
	}

	static (ModelLibrary, int) ChairRoundTrip()
	{
		var v = new List<(int, int, int, int)>();
		void Box(int x0, int y0, int z0, int x1, int y1, int z1, int c)
		{
			for (int z = z0; z < z1; z++) for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) v.Add((x, y, z, c));
		}
		// Legs, seat, backrest, metal bolt, little lamp.
		foreach (var (x, y) in new[] { (0, 0), (21, 0), (0, 21), (21, 21) }) Box(x, y, 0, x + 3, y + 3, 20, 1);
		Box(0, 0, 20, 24, 24, 23, 2);
		Box(0, 21, 23, 24, 24, 48, 3);
		Box(5, 20, 30, 7, 21, 32, 4);
		Box(11, 20, 44, 13, 21, 46, 5);
		var matl = new Dictionary<int, Dictionary<string, string>>
		{
			[4] = new() { ["_type"] = "_metal", ["_metal"] = "0.9", ["_rough"] = "0.3" },
			[5] = new() { ["_type"] = "_emit", ["_emit"] = "0.5", ["_flux"] = "1" },
		};
		var lib = new ModelLibrary();
		int chair = lib.Add(Import(Vox(new[] { Model(24, 24, 48, v.Select(t => (t.Item1, t.Item2, t.Item3, t.Item4)).ToArray()) }, matl), "chair"));
		var info = lib.Models[chair];
		Check(info.CellsX == 1 && info.CellsY == 2 && info.CellsZ == 1 && info.Width == 1, $"chair: {info.CellsX}x{info.CellsY}x{info.CellsZ} cells, {info.PaletteCount - 1} materials, width {info.Width}");
		var p = lib.Pools[0];
		Console.WriteLine($"     chair: {v.Count} voxels, {p.CellCount} cells, {p.BrickCount} bricks, pool {p.Bytes} B (dense would be {2 * 32768} B)");
		Console.WriteLine($"     chair library file: {Saved(lib).Length} B");

		var loaded = ModelLibrary.Load(new MemoryStream(Saved(lib)));
		Check(Saved(loaded).AsSpan().SequenceEqual(Saved(lib)), "save -> load -> save is byte-identical");
		Check(SameVoxels(lib, loaded, chair), "loaded chair has identical voxels on all mip levels");
		Check(loaded.GetMaterial(chair, lib.GetVoxel(chair, 0, 0, 0)) == lib.GetMaterial(chair, lib.GetVoxel(chair, 0, 0, 0)), "materials survive the round trip");
		return (lib, chair);
	}

	static void Dedupe(ModelLibrary lib, int chair)
	{
		var p = lib.Pools[0];
		int bricks = p.BrickCount, cells = p.CellCount;
		var copy = new VoxelModelBuilder("chair2");
		var src = lib.Models[chair];
		for (int z = 0; z < src.SizeZ; z++) for (int y = 0; y < src.SizeY; y++) for (int x = 0; x < src.SizeX; x++)
		{
			ushort i = lib.GetVoxel(chair, x, y, z);
			if (i != 0) copy.Set(x, y, z, lib.GetMaterial(chair, i));
		}
		lib.Add(copy);
		Check(p.BrickCount == bricks && p.CellCount == cells, "second identical model adds no bricks or cells");

		var stripes = new VoxelModelBuilder("stripes"); // two identical cells side by side
		for (int z = 0; z < 32; z++) for (int y = 0; y < 32; y += 2) for (int x = 0; x < 64; x++)
			stripes.Set(x, y, z, VoxelMaterial.FromRgba(200, 50, 50));
		cells = p.CellCount;
		int m = lib.Add(stripes);
		Check(p.CellCount == cells + 1 && lib.CellTable[lib.Models[m].CellIndex(0, 0, 0)] == lib.CellTable[lib.Models[m].CellIndex(1, 0, 0)],
			"identical cells within a model are stored once");
	}

	static void WidePalette()
	{
		var b = new VoxelModelBuilder("rainbow");
		for (int i = 0; i < 300; i++) b.Set(i % 32, i / 32, 0, VoxelMaterial.FromRgba((byte)i, (byte)(i >> 8), 7));
		var lib = new ModelLibrary();
		int m = lib.Add(b);
		Check(lib.Models[m].Width == 2, $"300 materials -> 2 bytes per voxel (width {lib.Models[m].Width})");
		var loaded = ModelLibrary.Load(new MemoryStream(Saved(lib)));
		Check(loaded.GetVoxel(m, 299 % 32, 299 / 32, 0) == 300 && SameVoxels(lib, loaded, m), "indices above 255 survive save/load");
	}

	static void Mips()
	{
		var a = VoxelMaterial.FromRgba(255, 0, 0);
		var c = VoxelMaterial.FromRgba(0, 0, 255);
		var solid = new VoxelModelBuilder("solid");
		for (int z = 0; z < 32; z++) for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) solid.Set(x, y, z, a);
		var lib = new ModelLibrary();
		int m = lib.Add(solid);
		Check(Enumerable.Range(0, 5).All(l => lib.GetVoxel(m, 0, 0, 0, l) == 1), "solid cell is solid on every mip level");

		var dot = new VoxelModelBuilder("dot");
		dot.Set(5, 5, 5, a);
		int d = lib.Add(dot);
		Check(lib.GetVoxel(d, 5, 5, 5) == 1 && lib.GetVoxel(d, 2, 2, 2, 1) == 0 && lib.GetVoxel(d, 0, 0, 0, 4) == 0, "single voxel fades out at mip 1 (needs 4 of 8)");

		var tie = new VoxelModelBuilder("tie"); // 4 of each material in one 2x2x2 block -> lowest index wins
		for (int i = 0; i < 8; i++) tie.Set(i & 1, i >> 1 & 1, i >> 2, i < 4 ? a : c);
		int t = lib.Add(tie);
		Check(lib.GetVoxel(t, 0, 0, 0, 1) == 1, "mip takes the most frequent material, lowest index on a tie");
	}

	static void MultiBlock()
	{
		var vox = new List<(int, int, int, int)>();
		for (int z = 0; z < 33; z++) for (int y = 0; y < 40; y++) for (int x = 0; x < 70; x++)
			if ((x + y + z) % 7 == 0) vox.Add((x, y, z, 1 + (x / 10) % 3));
		var lib = new ModelLibrary();
		int m = lib.Add(Import(Vox(new[] { Model(70, 40, 33, vox.ToArray()) })));
		var i = lib.Models[m];
		Check(i.CellsX == 3 && i.CellsY == 2 && i.CellsZ == 2, $"70x40x33 .vox -> {i.CellsX}x{i.CellsY}x{i.CellsZ} cells (x, y-up, z)");
		Check(SameVoxels(lib, ModelLibrary.Load(new MemoryStream(Saved(lib))), m), "multi-block model round trip");
	}

	static void SceneGraph()
	{
		var cube = Enumerable.Range(0, 64).Select(i => (i & 3, i >> 2 & 3, i >> 4, 1)).ToArray();
		var bytes = Vox(new[] { Model(4, 4, 4, cube), Model(4, 4, 4, cube) }, scene: true);
		var b = Import(bytes);
		Check(b.SizeX == 44 && b.SizeY == 4 && b.SizeZ == 4, $"nTRN translation places the second model 40 voxels away (size {b.SizeX}x{b.SizeY}x{b.SizeZ})");
	}

	static void Materials()
	{
		var matl = new Dictionary<int, Dictionary<string, string>>
		{
			[1] = new() { ["_type"] = "_metal", ["_metal"] = "0.8", ["_rough"] = "0.2" },
			[2] = new() { ["_type"] = "_emit", ["_emit"] = "0.5", ["_flux"] = "1" },
		};
		var b = Import(Vox(new[] { Model(2, 1, 1, (0, 0, 0, 1), (1, 0, 0, 2)) }, matl));
		var lib = new ModelLibrary();
		int m = lib.Add(b);
		var metal = lib.GetMaterial(m, lib.GetVoxel(m, 0, 0, 0));
		var lamp = lib.GetMaterial(m, lib.GetVoxel(m, 1, 0, 0));
		Check(Math.Abs(metal.Metallic - 0.8f) < 1e-6 && Math.Abs(metal.Roughness - 0.2f) < 1e-6, "MATL _metal -> metallic/roughness");
		Check(Math.Abs(lamp.EmissionStrength - 1f) < 1e-6 && lamp.EmissionRgb == (lamp.Rgba & 0xFFFFFF), "MATL _emit -> emission colour and strength");
		var leaf = new VoxelModelBuilder("leaf");
		leaf.Set(0, 0, 0, VoxelMaterial.FromRgba(90, 90, 90) with { Tint = true });
		var tinted = new ModelLibrary();
		int t = tinted.Add(leaf);
		Check(ModelLibrary.Load(new MemoryStream(Saved(tinted))).GetMaterial(t, 1).Tint, "tint flag survives save/load");
	}

	static void BlockModels()
	{
		Check(Rotation24.FromMinecraft(90, 0).Image(1) == (0, 0, -1) && Rotation24.FromMinecraft(0, 90).Image(2) == (-1, 0, 0)
			&& Rotation24.FromMinecraft(0, 0).Value == 0 && Rotation24.FromMinecraft(0, 180) == Rotation24.Yaw[2],
			"Minecraft x turns the top north, y turns north east (clockwise from above)");
		var red = VoxelMaterial.FromRgba(255, 0, 0);
		var post = new VoxelModelBuilder("post");
		for (int y = 0; y < 32; y++) post.Set(15, y, 15, red);
		var side = new VoxelModelBuilder("side");
		for (int z = 0; z < 8; z++) side.Set(16, 10, z, VoxelMaterial.FromRgba(0, 255, 0) with { Tint = true });
		var lib = new ModelLibrary();
		int p = lib.Add(post), s = lib.Add(side);
		int c = lib.Compose("post+east", new[] { (p, new Rotation24(0)), (s, Rotation24.FromMinecraft(0, 90)) });
		Check(lib.GetVoxel(c, 15, 31, 15) != 0 && lib.GetVoxel(c, 31, 10, 16) != 0 && lib.GetVoxel(c, 16, 10, 0) == 0
			&& lib.GetMaterial(c, lib.GetVoxel(c, 31, 10, 16)).Tint, "composed model: the post and the side turned east, tint kept");
		var other = new ModelLibrary();
		other.Add(side);
		int first = other.Append(lib);
		Check(first == 1 && Enumerable.Range(0, 3).All(m => SameVoxels(lib, other, m, first + m)), "appended library keeps every model's voxels");
		int bricks = other.Pools[0].BrickCount;
		other.Compose("post+east again", new[] { (first, new Rotation24(0)), (first + 1, Rotation24.FromMinecraft(0, 90)) });
		Check(other.Pools[0].BrickCount == bricks, "composing an appended combination again adds no bricks");
	}

	static bool SameVoxels(ModelLibrary a, ModelLibrary b, int m, int mb)
	{
		var i = a.Models[m];
		for (int l = 0; l <= 4; l++)
		{
			int n = 32 >> l;
			for (int z = 0; z < i.CellsZ * n; z++) for (int y = 0; y < i.CellsY * n; y++) for (int x = 0; x < i.CellsX * n; x++)
			{
				ushort va = a.GetVoxel(m, x, y, z, l);
				if (va != b.GetVoxel(mb, x, y, z, l) || a.GetMaterial(m, va) != b.GetMaterial(mb, va)) return false;
			}
		}
		return true;
	}

	static bool SameVoxels(ModelLibrary a, ModelLibrary b, int m) => SameVoxels(a, b, m, m);

	static byte[] Saved(ModelLibrary lib)
	{
		var ms = new MemoryStream();
		lib.Save(ms);
		return ms.ToArray();
	}

	static VoxelModelBuilder Import(byte[] vox, string name = "test") => VoxScene.ToModel(name, VoxScene.Read(new MemoryStream(vox), name));

	static (int X, int Y, int Z, (int, int, int, int)[] V) Model(int x, int y, int z, params (int, int, int, int)[] v) => (x, y, z, v);

	// Minimal .vox writer; scene: nTRN 0 -> nGRP 1 -> per model nTRN (x += 40 * i) -> nSHP.
	static byte[] Vox((int X, int Y, int Z, (int, int, int, int)[] V)[] models, Dictionary<int, Dictionary<string, string>> matl = null, bool scene = false)
	{
		var body = new MemoryStream();
		var w = new BinaryWriter(body);
		void Chunk(string id, Action<BinaryWriter> content)
		{
			var c = new MemoryStream();
			content(new BinaryWriter(c));
			w.Write(Encoding.ASCII.GetBytes(id)); w.Write((int)c.Length); w.Write(0); w.Write(c.ToArray());
		}
		void Str(BinaryWriter b, string s) { var bytes = Encoding.UTF8.GetBytes(s); b.Write(bytes.Length); b.Write(bytes); }
		void Dict(BinaryWriter b, Dictionary<string, string> d) { b.Write(d.Count); foreach (var (k, v) in d) { Str(b, k); Str(b, v); } }

		foreach (var m in models)
		{
			Chunk("SIZE", b => { b.Write(m.X); b.Write(m.Y); b.Write(m.Z); });
			Chunk("XYZI", b => { b.Write(m.V.Length); foreach (var (x, y, z, c) in m.V) { b.Write((byte)x); b.Write((byte)y); b.Write((byte)z); b.Write((byte)c); } });
		}
		Chunk("RGBA", b => { for (int i = 1; i <= 256; i++) b.Write((uint)((i * 37 & 255) | (i * 91 & 255) << 8 | (i * 13 & 255) << 16 | 255u << 24)); });
		if (matl != null) foreach (var (id, d) in matl) Chunk("MATL", b => { b.Write(id); Dict(b, d); });
		if (scene)
		{
			var empty = new Dictionary<string, string>();
			Chunk("nTRN", b => { b.Write(0); Dict(b, empty); b.Write(1); b.Write(-1); b.Write(0); b.Write(1); Dict(b, empty); });
			Chunk("nGRP", b => { b.Write(1); Dict(b, empty); b.Write(models.Length); for (int i = 0; i < models.Length; i++) b.Write(2 + 2 * i); });
			for (int i = 0; i < models.Length; i++)
			{
				int k = i;
				Chunk("nTRN", b => { b.Write(2 + 2 * k); Dict(b, empty); b.Write(3 + 2 * k); b.Write(-1); b.Write(0); b.Write(1); Dict(b, new() { ["_t"] = $"{40 * k} 0 0" }); });
				Chunk("nSHP", b => { b.Write(3 + 2 * k); Dict(b, empty); b.Write(1); b.Write(k); Dict(b, empty); });
			}
		}
		var file = new MemoryStream();
		var f = new BinaryWriter(file);
		f.Write(Encoding.ASCII.GetBytes("VOX ")); f.Write(150);
		f.Write(Encoding.ASCII.GetBytes("MAIN")); f.Write(0); f.Write((int)body.Length); f.Write(body.ToArray());
		return file.ToArray();
	}
}
