using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class Plant : Sprite
{
	[ExportGroup("Plant")]
	[Export] public bool WashesAway { get; set; }
}
