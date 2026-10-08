using Godot;

namespace Renesance.Content;

public enum Matter { Solid, Granular, Liquid, Gas }

public enum Surface { Opaque, Cutout, Translucent }

public enum Orientation { None, Axis, Horizontal, Facing, Any }

public enum ToolKind { None, Pickaxe, Axe, Shovel, Shears }

[GlobalClass]
public abstract partial class Placeable : Definition
{
	[Export] public Matter Matter { get; set; }

	[ExportGroup("Placement")]
	[Export] public Orientation Orientation { get; set; }
	[Export] public Godot.Collections.Array<StateParam> States { get; set; }

	[ExportGroup("Look")]
	[Export] public Color Tint { get; set; } = Colors.White;
	[Export] public bool Invisible { get; set; }

	[ExportGroup("Light")]
	[Export] public LightSource Light { get; set; }
	[Export(PropertyHint.ColorNoAlpha)] public Color Emission { get; set; } = Colors.Black;
	[Export(PropertyHint.Range, "0,20,0.1")] public float EmissionEnergy { get; set; } = 1;

	[ExportGroup("Breaking")]
	[Export] public bool Breakable { get; set; } = true;
	[Export(PropertyHint.Range, "0,100,0.1")] public float Hardness { get; set; } = 1;
	[Export] public ToolKind Tool { get; set; }
	[Export(PropertyHint.Range, "0,10")] public int ToolTier { get; set; }
	[Export] public Godot.Collections.Array<Drop> Drops { get; set; } = new();

	public Drop[] ActualDrops => Drops.Count > 0 ? [.. Drops] : [new Drop { Thing = this }];
}
