using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class StateParam : Resource
{
	[Export] public string Name { get; set; } = "";
	[Export(PropertyHint.Range, "2,256")] public int Values { get; set; } = 2;
}
