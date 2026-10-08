using Godot;

[Pass(PassId.HeightFog, PassStage.Atmosphere, Order = 8, Enabled = false, Default = "analytic", Reads = new[] { TargetId.Depth, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class HeightFogPass : AtmosphereVariant { }

// Towards the haze colour (haze.glsli), the air of Renderer like Haze.
[Variant("analytic")]
public sealed class HeightFogAnalytic : HeightFogPass
{
	[Tune("", 0, 0.01f, 0.00001f, Label = "Fog density (/m)", Shader = true)] public float FogDensity = 0.0008f;
	[Tune("", 0.001f, 0.2f, 0.001f, Label = "Fog falloff (/m)", Shader = true)] public float FogFalloff = 0.03f;
	[Tune("", -100, 300, 1, Label = "Fog base height (m)", Shader = true)] public float FogHeight = 0f;

	protected override string Shader => "res://core/render/passes/heightfog/heightfog_analytic.compute";
}
