using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class Drop : Resource
{
	[Export] public Definition Thing { get; set; }
	[Export(PropertyHint.Range, "0,64")] public int Min { get; set; } = 1;
	[Export(PropertyHint.Range, "0,64")] public int Max { get; set; } = 1;
	[Export(PropertyHint.Range, "0,1,0.01")] public float Chance { get; set; } = 1;
}
