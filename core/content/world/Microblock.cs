using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class Microblock : Placeable
{
	[ExportGroup("Model")]
	[Export(PropertyHint.File, "*.vox")] public string Model { get; set; } = "";
	[Export(PropertyHint.Range, "1,8")] public int Scale { get; set; } = 1;
	[Export] public bool WashesAway { get; set; }
}
