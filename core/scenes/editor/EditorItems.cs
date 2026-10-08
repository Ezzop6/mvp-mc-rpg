using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Content;
using Format = Renesance.Content.Format;

public static class EditorItems
{
	const int IconSize = 64;
	static readonly Dictionary<ushort, Texture2D> icons = new();

	public static bool IsItem(BlockDef d) => d.Placeable;

	public static IEnumerable<BlockDef> All(BlockRegistry registry) => registry.All.Where(d => d.Placeable || d.Item != null);

	public static string CategoryName(BlockRegistry registry, string id) => registry.Content.Categories.FirstOrDefault(c => c.Id == id)?.Name ?? id;

	public static string DisplayName(BlockDef d)
	{
		var path = BlockId.Parse(d.Item?.Id ?? d.Name).Path.Replace('_', ' ');
		return char.ToUpperInvariant(path[0]) + path[1..];
	}

	public static string Tooltip(BlockRegistry registry, BlockDef d) => $"{DisplayName(d)}\n{CategoryName(registry, d.Category)}\n{d.Item?.Id ?? d.Name}";

	public static bool Matches(BlockRegistry registry, BlockDef d, string query) =>
		string.IsNullOrWhiteSpace(query) || new[] { DisplayName(d), d.Name, CategoryName(registry, d.Category) }
			.Any(s => s.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

	static Image Texture(string name)
	{
		var img = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://{ContentPaths.Textures}/{ContentPaths.TextureFile(name)}"));
		if (img.GetHeight() > img.GetWidth()) img = img.GetRegion(new Rect2I(0, 0, img.GetWidth(), img.GetWidth())); // animated: first frame
		img.Convert(Image.Format.Rgba8);
		img.Resize(IconSize, IconSize, Image.Interpolation.Nearest);
		return img;
	}

	// The front (else side, else all) face, tinted as in the world: the overlay when the face has one, else the texture.
	public static Texture2D Icon(BlockDef d)
	{
		if (icons.TryGetValue(d.Id, out var icon)) return icon;
		if (d.Item?.Layers is [var first, ..] layers)
		{
			var sprite = Texture(first);
			foreach (var layer in layers.Skip(1)) sprite.BlendRect(Texture(layer), new Rect2I(0, 0, IconSize, IconSize), Vector2I.Zero);
			return icons[d.Id] = ImageTexture.CreateFromImage(sprite);
		}
		var e = d.Entry;
		var face = (e.Faces ?? e.Cubes?.Values.FirstOrDefault())?.Get(Format.FaceDir.Front) ?? new Format.Face(BlockLooks.Particle(e), Tint: true);
		var tint = face.Tint && e.Tint != null ? new Color(e.Tint.Color) : Colors.White;
		var img = face.Texture != null ? Texture(face.Texture) : Image.CreateEmpty(IconSize, IconSize, false, Image.Format.Rgba8);
		var overlay = face.Overlay != null ? Texture(face.Overlay) : null;
		for (int y = 0; y < IconSize; y++)
			for (int x = 0; x < IconSize; x++)
			{
				var c = img.GetPixel(x, y);
				if (overlay?.GetPixel(x, y) is { A: >= 0.5f } o) c = o * tint;
				else if (overlay == null) c *= tint;
				img.SetPixel(x, y, new Color(c.R, c.G, c.B, Math.Max(c.A, 0.35f)));
			}
		return icons[d.Id] = ImageTexture.CreateFromImage(img);
	}
}
