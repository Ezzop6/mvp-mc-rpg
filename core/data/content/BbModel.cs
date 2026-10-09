using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Renesance.Content.Format;

namespace Renesance.Content;

public sealed record BbFace(float[] Uv, int? Texture, int Rotation);

public sealed record BbCube(float[] From, float[] To, float[] Rotation, Dictionary<string, BbFace> Faces);

public sealed record BbTexture(int UvWidth, int UvHeight, int W, int H, byte[] Rgba);

// Blockbench .bbmodel: 32 units = 1 block.
public sealed record BbModel(BbCube[] Cubes, BbTexture[] Textures, int ElementCount)
{
	public const int BlockUnits = 32, FaceTexels = 32;
	static readonly (string Name, FaceDir Dir)[] faceDirs =
		{ ("up", FaceDir.Top), ("down", FaceDir.Bottom), ("north", FaceDir.Front), ("south", FaceDir.Back), ("west", FaceDir.Left), ("east", FaceDir.Right) };

	public static BbModel Read(string path)
	{
		using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
		var root = doc.RootElement;
		var res = root.TryGetProperty("resolution", out var r) ? (W: r.GetProperty("width").GetInt32(), H: r.GetProperty("height").GetInt32()) : (W: 16, H: 16);
		static float[] Floats(JsonElement e, string name) => e.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(v => v.GetSingle()).ToArray() : new float[3];
		var elements = root.GetProperty("elements").EnumerateArray().ToArray();
		var cubes = elements.Where(e => !e.TryGetProperty("type", out var t) || t.GetString() == "cube").Select(e => new BbCube(Floats(e, "from"), Floats(e, "to"), Floats(e, "rotation"),
			e.GetProperty("faces").EnumerateObject().ToDictionary(f => f.Name, f => new BbFace(Floats(f.Value, "uv"),
				f.Value.TryGetProperty("texture", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null,
				f.Value.TryGetProperty("rotation", out var rot) ? rot.GetInt32() : 0)))).ToArray();
		var textures = root.TryGetProperty("textures", out var ts) ? ts.EnumerateArray().Select(t =>
		{
			var src = t.GetProperty("source").GetString();
			if (src?.StartsWith("data:image/png;base64,") != true) throw new InvalidDataException($"{path}: texture is not an embedded PNG");
			var (w, h, rgba) = Png.Decode(Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]));
			int Int(string name, int fallback) => t.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
			return new BbTexture(Int("uv_width", res.W), Int("uv_height", res.H), w, h, rgba);
		}).ToArray() : Array.Empty<BbTexture>();
		return new BbModel(cubes, textures, elements.Length);
	}

	// One unrotated cube of a block's size; where it sits in the model doesn't matter.
	public bool IsFullBlock => ElementCount == 1 && Cubes is [var c] && Enumerable.Range(0, 3).All(i => c.To[i] - c.From[i] == BlockUnits) && c.Rotation.All(a => a == 0);

	// The face's UV rectangle, nearest-sampled to FaceTexels^2 RGBA; reversed UVs mirror.
	public byte[] Cut(BbFace f)
	{
		var t = Textures[f.Texture.Value];
		float sx = (float)t.W / t.UvWidth, sy = (float)t.H / t.UvHeight;
		var o = new byte[FaceTexels * FaceTexels * 4];
		for (int y = 0; y < FaceTexels; y++)
			for (int x = 0; x < FaceTexels; x++)
			{
				int px = Math.Clamp((int)MathF.Floor((f.Uv[0] + (x + 0.5f) / FaceTexels * (f.Uv[2] - f.Uv[0])) * sx), 0, t.W - 1);
				int py = Math.Clamp((int)MathF.Floor((f.Uv[1] + (y + 0.5f) / FaceTexels * (f.Uv[3] - f.Uv[1])) * sy), 0, t.H - 1);
				Array.Copy(t.Rgba, (py * t.W + px) * 4, o, (y * FaceTexels + x) * 4, 4);
			}
		return o;
	}

	// A full-block cube or liquid entry (liquid: up -> still, north -> flow); face images go to images under "<bbmodel path>#<face>".
	public static Entry Block(Entry e, string assetsDir, Dictionary<string, (int W, int H, byte[] Rgba)> images)
	{
		var path = Path.Combine(assetsDir, e.Bbmodel);
		if (e.Kind is not (EntryKind.Cube or EntryKind.Liquid)) throw new InvalidDataException($"{path}: kind {e.Kind} is not supported from .bbmodel");
		var m = Read(path);
		if (!m.IsFullBlock) throw new InvalidDataException($"{path}: only a single cube covering the whole block is supported");
		var faces = new Dictionary<FaceDir, Face>();
		foreach (var (name, dir) in faceDirs)
		{
			if (m.Cubes[0].Faces.GetValueOrDefault(name) is not { Texture: not null } f) continue;
			if (f.Texture >= m.Textures.Length) throw new InvalidDataException($"{path}: face {name} uses missing texture {f.Texture}");
			var key = $"{e.Bbmodel}#{name}";
			images[key] = (FaceTexels, FaceTexels, m.Cut(f));
			faces[dir] = new Face(key, f.Rotation);
		}
		Face F(FaceDir d) => faces.GetValueOrDefault(d);
		if (e.Kind == EntryKind.Liquid)
			return e with { Textures = new(e.Textures ?? new()) { [BlockLooks.StillTexture] = F(FaceDir.Top)?.Texture, [BlockLooks.FlowTexture] = F(FaceDir.Front)?.Texture } };
		return e with { Faces = new Faces(Top: F(FaceDir.Top), Bottom: F(FaceDir.Bottom), Front: F(FaceDir.Front), Back: F(FaceDir.Back), Left: F(FaceDir.Left), Right: F(FaceDir.Right)) };
	}
}
