using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class LightSource : Resource
{
	[Export(PropertyHint.ColorNoAlpha)] public Color Color { get; set; } = Colors.White;
	[Export(PropertyHint.Range, "0,20,0.1")] public float Intensity { get; set; } = 1;
	[Export(PropertyHint.Range, "0,64,0.5,suffix:m")] public float Range { get; set; } = 8;
	[Export(PropertyHint.Range, "0,180,1,suffix:°")] public float Cone { get; set; }
}
