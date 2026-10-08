// Checks: .vox -> template round trip, scene-graph rotation, catalog validity. Run: dotnet run
using System.Text;
using Renesance.Import;
using Renesance.Content;

string root = ContentPaths.FindRoot();
int failed = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; }

// A 3x1x2 model (vox z-up), colour index 1 = red, 2 = green; optional scene graph with rotation byte r.
byte[] Vox(byte? rot)
{
	var body = new MemoryStream(); var w = new BinaryWriter(body);
	void Chunk(string id, Action<BinaryWriter> content)
	{
		var c = new MemoryStream(); content(new BinaryWriter(c));
		w.Write(Encoding.ASCII.GetBytes(id)); w.Write((int)c.Length); w.Write(0); w.Write(c.ToArray());
	}
	void Str(BinaryWriter b, string s) { b.Write(s.Length); b.Write(Encoding.ASCII.GetBytes(s)); }
	Chunk("SIZE", b => { b.Write(3); b.Write(1); b.Write(2); });
	Chunk("XYZI", b => { b.Write(4); foreach (var (x, z, i) in new[] { (0, 0, 1), (1, 0, 1), (2, 0, 2), (0, 1, 2) }) { b.Write((byte)x); b.Write((byte)0); b.Write((byte)z); b.Write((byte)i); } });
	Chunk("RGBA", b => { b.Write(0xFF0000FFu); b.Write(0xFF00FF00u); for (int i = 2; i < 256; i++) b.Write(0xFF808080u); });
	if (rot is byte r)
	{
		Chunk("nTRN", b => { b.Write(0); b.Write(0); b.Write(1); b.Write(-1); b.Write(-1); b.Write(1); b.Write(0); });
		Chunk("nGRP", b => { b.Write(1); b.Write(0); b.Write(1); b.Write(2); });
		Chunk("nTRN", b => { b.Write(2); b.Write(0); b.Write(3); b.Write(-1); b.Write(-1); b.Write(1); b.Write(1); Str(b, "_r"); Str(b, r.ToString()); });
		Chunk("nSHP", b => { b.Write(3); b.Write(0); b.Write(1); b.Write(0); b.Write(0); });
	}
	var f = new MemoryStream(); var fw = new BinaryWriter(f);
	fw.Write(Encoding.ASCII.GetBytes("VOX ")); fw.Write(150);
	fw.Write(Encoding.ASCII.GetBytes("MAIN")); fw.Write(0); fw.Write((int)body.Length); fw.Write(body.ToArray());
	return f.ToArray();
}

string Map(uint rgba) => (rgba & 0xFF) > 128 ? "base:bricks" : "base:planks";
var plain = VoxScene.Read(new MemoryStream(Vox(null)), "t").Single();
var t = VoxConverter.Convert(plain, 1, Map);
Check(t.Sx == 3 && t.Sy == 2 && t.Sz == 1, $"size 3x2x1 (got {t.Sx}x{t.Sy}x{t.Sz})");
Check(t.Names[t[0, 0, 0] - 1] == "base:bricks" && t.Names[t[2, 0, 0] - 1] == "base:planks" && t[1, 1, 0] == 0, "voxels land on the right blocks");
string path = Path.Combine(Path.GetTempPath(), "import_test.rvst");
t.Save(path);
var back = Template.Load(path);
Check(back.Sx == t.Sx && back.Names.SequenceEqual(t.Names) && back.Cells.SequenceEqual(t.Cells), "template save/load round trip");
var x2 = VoxConverter.Convert(plain, 2, Map);
Check(x2.Sx == 6 && x2.Sy == 4 && x2[5, 1, 1] != 0, "scale x2");

// 90 degrees about vox z: the 3-long x row becomes 3 long along our z.
var rotated = VoxScene.Read(new MemoryStream(Vox(17)), "r").Single();
var tr = VoxConverter.Convert(rotated, 1, Map);
Check(tr.Sx == 1 && tr.Sz == 3 && tr.Sy == 2, $"nTRN rotation applied (got {tr.Sx}x{tr.Sy}x{tr.Sz})");

// Catalog: every template exists, sizes match, every block name is in the registry.
var reg = BlockRegistry.Load(Path.Combine(root, ContentPaths.Mod(BlockId.BaseMod)));
var dir = Path.Combine(root, ContentPaths.Structures);
var cat = Catalog.Load<TemplateCategory>(Path.Combine(root, ContentPaths.Mod(BlockId.BaseMod), Catalog.StructuresFile));
var bad = cat.Where(e =>
{
	var tt = Template.Load(Path.Combine(dir, e.File));
	return tt.Sx != e.Size[0] || tt.Sy != e.Size[1] || tt.Sz != e.Size[2] || tt.Names.Any(n => { try { _ = reg[n]; return false; } catch { return true; } });
}).Select(e => e.Name).ToList();
Check(cat.Count > 100 && bad.Count == 0, $"catalog: {cat.Count} templates valid{(bad.Count > 0 ? ", bad: " + string.Join(", ", bad.Take(5)) : "")}");

Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} failed");
return failed;
