using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class Block : Placeable
{
	[ExportGroup("Block")]
	[Export] public Surface Surface { get; set; }
	[Export] public Texture2D Texture { get; set; }
	[Export] public Texture2D Top { get; set; }
	[Export] public Texture2D Bottom { get; set; }
	[Export] public bool Connected { get; set; }

	public override Texture2D Icon => Texture;
}
