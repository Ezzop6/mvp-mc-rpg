using Godot;

[Pass(PassId.Sky, PassStage.Atmosphere, Order = 0, Enabled = false, Default = "gradient", Reads = new[] { TargetId.HitId, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class SkyPass : AtmosphereVariant { }

// v1 sky_view on sky pixels from the air colours (Renderer.SkyTop, SkyHorizon); without this pass they keep Shade's ambient colour.
[Variant("gradient")]
public sealed class SkyGradient : SkyPass
{
	[Tune("", 0.01f, 1, Shader = true)] public float SkyCurve = 0.15f;
	[Tune("Sun halo", 1, 90, Label = "Sun halo angle (deg)", Shader = true)] public float SunHaloAngle = 30f;
	[Tune("Sun halo", 0.01f, 1, Shader = true)] public float SunHaloCurve = 0.15f;

	protected override string Shader => "res://core/render/passes/sky/sky_gradient.compute";
}
