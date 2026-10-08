using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class Item : Definition
{
	[Export(PropertyHint.File, "*.vox")] public string Model { get; set; } = "";
	[Export(PropertyHint.Range, "1,8")] public int ModelScale { get; set; } = 1;

	[ExportGroup("Tool")]
	[Export] public ToolKind Tool { get; set; }
	[Export(PropertyHint.Range, "0,10")] public int ToolTier { get; set; }
	[Export(PropertyHint.Range, "0.1,20,0.1")] public float Speed { get; set; } = 1;
	[Export(PropertyHint.Range, "0,10000")] public int Durability { get; set; }
}
