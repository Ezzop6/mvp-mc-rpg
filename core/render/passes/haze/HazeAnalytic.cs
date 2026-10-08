using Godot;

[Pass(PassId.Haze, PassStage.Atmosphere, Order = 7, Enabled = false, Default = "analytic", Reads = new[] { TargetId.Depth, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class HazePass : AtmosphereVariant { }

// The haze colour is the air of Renderer (SkyHorizon, HazeSun, HazeSunFocus), shared with Sky and HeightFog.
[Variant("analytic")]
public sealed class HazeAnalytic : HazePass
{
	[Tune("", 0, 0.001f, 0.000002f, Label = "Haze density (/m)", Shader = true)] public float HazeDensity = 0.00012f;

	protected override string Shader => "res://core/render/passes/haze/haze_analytic.compute";
}
