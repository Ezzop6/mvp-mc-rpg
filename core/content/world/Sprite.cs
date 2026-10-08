using Godot;

namespace Renesance.Content;

public enum SpriteShape { Cross, Billboard, Plane }

[GlobalClass]
public abstract partial class Sprite : Placeable
{
	[ExportGroup("Sprite")]
	[Export] public SpriteShape Shape { get; set; }
	[Export] public Surface Surface { get; set; } = Surface.Cutout;
	[Export] public Texture2D Texture { get; set; }
	[Export(PropertyHint.Range, "0.1,4,0.05,suffix:blocks")] public float Height { get; set; } = 1;

	public override Texture2D Icon => Texture;
}
