using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renesance.Content;
using Renesance.World;
using Format = Renesance.Content.Format;

// BlockType.flags in blocks.glsli (F_* in the shaders).
[Flags]
public enum BlockFlags : uint { Opaque = 1, Cutout = 2, Translucent = 4, Fluid = 8, Invisible = 16, Light = 32, Volume = 64, Model = 128 }

// Block variants (BlockVariants) -> GPU: the BlockType rows and the texture layers. Layouts must match blocks.glsli.
public static class GpuBlockTable
{
	public const int TexSize = 32;            // texels per block face, permanent
	public const int TexMips = 6;
	public static readonly int[] MipOffsets = MipChain();
	public static int TexelsPerLayer => MipOffsets[TexMips];

	// BlockType.face[]: layer | (overlay + 1) << FaceOverlayShift | turns << FaceRotShift | tint << FaceTintShift | emission << FaceEmissionShift.
	public const int FaceLayerBits = 12, FaceOverlayShift = 12, FaceRotShift = 24, FaceTintShift = 26, FaceEmissionShift = 27, FaceEmissionMax = 31;
	public const int LookModelBits = 20, LookRotShift = 20, LookWeightShift = 25, LookWeightMax = 127;
	public const int ColormapSize = 256, TintMapShift = 16;
	public static readonly Format.TintSource[] Colormaps = { Format.TintSource.Grass, Format.TintSource.Foliage, Format.TintSource.DryFoliage };

	public sealed record Textures(Dictionary<string, int> Index, HashSet<int> Holes, bool[] Colormapped, byte[] Texels, byte[] ColormapTexels,
		(int Live, int Frames, TextureAnimation Animation)[] Animations);

	static int[] MipChain()
	{
		var o = new int[TexMips + 1];
		for (int l = 0; l < TexMips; l++) o[l + 1] = o[l] + (TexSize >> l) * (TexSize >> l);
		return o;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct GpuBlockType
	{
		public uint Flags, Face0, Face1, Face2, Face3, Face4, Face5, Looks;
		public Vector4 Tint, LightColor;
	}

	public static int RowBytes => Unsafe.SizeOf<GpuBlockType>();

	public static uint LookWord(int model, int rotation, int cumulativeWeight) =>
		(uint)model | (uint)rotation << LookRotShift | (uint)cumulativeWeight << LookWeightShift;

	public static List<string> Layers(BlockRegistry reg) =>
		reg.All.Where(d => d.Entry != null).SelectMany(d => BlockLooks.AllFaces(d.Entry)).SelectMany(f => Enum.GetValues<Format.FaceDir>().Select(f.Get))
			.SelectMany(f => new[] { f?.Texture, f?.Overlay }).Where(t => t != null).Distinct().ToList();

	static Vector4 Linear(string hex)
	{
		var c = BlockRegistry.Rgb(hex);
		return new Vector4(Mathf.Pow(c[0], 2.2f), Mathf.Pow(c[1], 2.2f), Mathf.Pow(c[2], 2.2f), 1f);
	}

	// Textures.Holes: an opaque cube with transparent texels renders as cutout; only cutout blocks are alpha tested.
	static GpuBlockType Row(BlockDef d, BlockState s, Textures tex, Func<VariantLook, uint[]> modelLooks)
	{
		var e = d.Entry;
		if (e == null) return new GpuBlockType { Flags = d.Id == BlockRegistry.AirId ? 0 : (uint)BlockFlags.Invisible };
		var look = BlockLooks.Resolve(d, s);
		var words = new uint[BlockFacing.Faces];
		var l = d.Lit(s) ? d.Light : null;
		var row = new GpuBlockType
		{
			Tint = (e.Tint?.Color is { } t ? Linear(t) : Vector4.One) with { W = BlockLooks.LiquidHeight(d, s) },
			LightColor = l == null ? default : new(l.Color[0] * l.Intensity, l.Color[1] * l.Intensity, l.Color[2] * l.Intensity, l.Range / Voxel.BlockSize),
		};
		BlockFlags flags;
		if (e.Kind == Format.EntryKind.Volume)
		{
			row.Tint.W = e.Density;
			flags = BlockFlags.Volume;
		}
		else if (look.Cube == null)
		{
			var looks = look.Empty ? Array.Empty<uint>() : modelLooks(look);
			looks.CopyTo(words, 0);
			row.Looks = (uint)looks.Length;
			flags = looks.Length == 0 ? BlockFlags.Invisible : BlockFlags.Model;
		}
		else
		{
			bool hasHoles = false;
			var faces = BlockFacing.Map(d, s);
			for (int code = 0; code < words.Length; code++)
			{
				var (face, turns) = faces[code];
				var f = look.Cube.Get(face);
				if (f?.Texture == null) continue;
				hasHoles |= tex.Holes.Contains(tex.Index[f.Texture]);
				words[code] = (uint)tex.Index[f.Texture] | (uint)(f.Overlay == null ? 0 : tex.Index[f.Overlay] + 1) << FaceOverlayShift
					| (uint)((f.Rotation / 90 + turns) & 3) << FaceRotShift | (f.Tint && e.Tint != null ? 1u : 0u) << FaceTintShift
					| (uint)Math.Clamp(Mathf.RoundToInt(f.Emission * FaceEmissionMax), 0, FaceEmissionMax) << FaceEmissionShift;
			}
			flags = d.Liquid ? (e.Surface == Format.Surface.Translucent ? BlockFlags.Fluid | BlockFlags.Translucent : BlockFlags.Fluid) : e.Surface switch
			{
				Format.Surface.Cutout => BlockFlags.Cutout,
				Format.Surface.Translucent => BlockFlags.Translucent,
				_ => hasHoles ? BlockFlags.Cutout : BlockFlags.Opaque,
			};
		}
		if (l != null) flags |= BlockFlags.Light;
		int map = e.Tint == null ? -1 : Array.IndexOf(Colormaps, e.Tint.Source);
		uint tintMap = map >= 0 && tex.Colormapped[map] ? (uint)map + 1 : 0;
		(row.Flags, row.Face0, row.Face1, row.Face2, row.Face3, row.Face4, row.Face5) = ((uint)flags | tintMap << TintMapShift, words[0], words[1], words[2], words[3], words[4], words[5]);
		return row;
	}

	// Rows of the given variants, in order.
	public static byte[] Rows(BlockRegistry reg, IEnumerable<BlockState> variants, Textures tex, Func<VariantLook, uint[]> modelLooks) =>
		MemoryMarshal.AsBytes(variants.Select(s => Row(reg.Get(s), s, tex, modelLooks)).ToArray().AsSpan()).ToArray();

	static string Global(string file) => ProjectSettings.GlobalizePath($"res://{ContentPaths.Textures}/{file}");

	// Raw file: assets/ lives outside Godot's import pipeline.
	static Image Load(string file)
	{
		var img = Image.LoadFromFile(Global(file)) ?? throw new FileNotFoundException(file);
		img.Convert(Image.Format.Rgba8);
		return img;
	}

	// Texels: RGBA8 32 -> 1 mip chains of the still textures, a live layer per animation, then the animation tiles.
	public static Textures Load(BlockRegistry reg)
	{
		var names = Layers(reg);
		var images = names.ToDictionary(n => n, n => Load(ContentPaths.TextureFile(n)));
		var anims = names.ToDictionary(n => n, n => File.Exists(Global(ContentPaths.TextureMeta(n)))
			? TextureAnimation.Parse(File.ReadAllText(Global(ContentPaths.TextureMeta(n))), images[n].GetHeight() / images[n].GetWidth()) : null);
		var faced = names.Where(n => anims[n] == null).Concat(names.Where(n => anims[n] != null)).ToList();
		if (faced.Count >= 1 << FaceLayerBits)
			throw new InvalidOperationException($"{faced.Count} block texture layers, BlockType.face[] holds {(1 << FaceLayerBits) - 1}");
		var index = faced.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

		using var ms = new MemoryStream();
		using var w = new BinaryWriter(ms);
		var holes = new HashSet<int>();
		static Image Tile(Image img, int t) => img.GetHeight() <= img.GetWidth() ? img : img.GetRegion(new Rect2I(0, t * img.GetWidth(), img.GetWidth(), img.GetWidth()));
		foreach (var n in faced)
			if (WriteLayer(w, Tile(images[n], anims[n]?.Frames[0] ?? 0))) holes.Add(index[n]);
		int layer = faced.Count;
		var animated = new List<(int, int, TextureAnimation)>();
		foreach (var n in faced.Where(n => anims[n] != null))
		{
			var img = images[n];
			int tiles = img.GetHeight() / img.GetWidth();
			for (int t = 0; t < tiles; t++)
				if (WriteLayer(w, Tile(img, t))) holes.Add(index[n]);
			animated.Add((index[n], layer, anims[n]));
			layer += tiles;
		}

		var colormapped = new bool[Colormaps.Length];
		var maps = new List<uint>();
		for (int i = 0; i < Colormaps.Length; i++)
		{
			var file = ContentPaths.TextureFile($"colormap/{Gpu.Snake(Colormaps[i].ToString()).ToLowerInvariant()}");
			colormapped[i] = File.Exists(Global(file));
			if (!colormapped[i]) { maps.AddRange(new uint[ColormapSize * ColormapSize]); continue; }
			var img = Load(file);
			if (img.GetWidth() != ColormapSize || img.GetHeight() != ColormapSize) img.Resize(ColormapSize, ColormapSize);
			maps.AddRange(MemoryMarshal.Cast<byte, uint>(img.GetData()).ToArray());
		}
		return new Textures(index, holes, colormapped, ms.ToArray(), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(maps)).ToArray(), animated.ToArray());
	}

	static bool WriteLayer(BinaryWriter w, Image img)
	{
		if (img.GetWidth() != TexSize)
			img.Resize(TexSize, TexSize, img.GetWidth() > TexSize ? Image.Interpolation.Trilinear : Image.Interpolation.Nearest);
		var data = img.GetData();
		var level = new Color[TexSize * TexSize];
		for (int i = 0; i < level.Length; i++) level[i] = Color.Color8(data[4 * i], data[4 * i + 1], data[4 * i + 2], data[4 * i + 3]);
		bool holes = level.Any(c => c.A < 0.5f);
		float coverage = level.Any(c => c.A8 == 0) ? level.Count(c => c.A >= 0.5f) / (float)level.Length : 0;
		for (int s = TexSize; s >= 1; s /= 2)
		{
			foreach (var c in level) { w.Write((byte)c.R8); w.Write((byte)c.G8); w.Write((byte)c.B8); w.Write((byte)c.A8); }
			if (s == 1) break;
			// Alpha-weighted: transparent texels (often black) must not darken distant foliage.
			var next = new Color[s * s / 4];
			for (int y = 0; y < s / 2; y++)
				for (int x = 0; x < s / 2; x++)
				{
					Color a = level[2 * x + 2 * y * s], b = level[2 * x + 1 + 2 * y * s];
					Color c = level[2 * x + (2 * y + 1) * s], d = level[2 * x + 1 + (2 * y + 1) * s];
					float wa = a.A + b.A + c.A + d.A;
					var rgb = wa > 0 ? (a * a.A + b * b.A + c * c.A + d * d.A) / wa : Colors.Black;
					next[x + y * s / 2] = new Color(rgb.R, rgb.G, rgb.B, wa / 4);
				}
			level = next;
			KeepCoverage(level, coverage);
		}
		return holes;
	}

	// Scales alpha up so at least the base level's share of texels passes the 0.5 cutout test (docs/content-format.md, Stěny na GPU).
	static void KeepCoverage(Color[] level, float coverage)
	{
		int keep = (int)MathF.Ceiling(coverage * level.Length - 1e-4f);
		if (keep == 0) return;
		float a = level.Select(c => c.A).OrderDescending().ElementAt(keep - 1);
		float scale = a > 0 ? 0.51f / a : 1;
		if (scale <= 1) return;
		for (int i = 0; i < level.Length; i++) level[i].A = MathF.Min(level[i].A * scale, 1);
	}
}
