using Godot;

[Pass(PassId.Clouds, PassStage.Atmosphere, Order = 4, Enabled = false, Default = "layer", Reads = new[] { TargetId.HitId, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class CloudsPass : AtmosphereVariant { }

[Variant("layer")]
public sealed class CloudLayer : CloudsPass
{
	[Tune("", 0, 1, Shader = true)] public float CloudCoverage = 0.4f;
	[Tune("", 0.001f, 1, Label = "Cloud edge softness", Shader = true)] public float CloudSoftness = 0.15f;
	[Tune("", 0, 20000, 10, Label = "Cloud altitude (m)", Shader = true)] public float CloudAltitude = 450f;
	[Tune("", 1, 10000, 10, Label = "Cloud thickness (m)", Shader = true)] public float CloudThickness = 250f;
	[Tune("", 0, 1, 0.0005f, Label = "Cloud density (/m)", Shader = true)] public float CloudDensity = 0.015f;
	[Tune("", 10, 100000, 10, Label = "Cloud size (m)", Shader = true)] public float CloudScale = 1800f;
	[Tune("", 0, 300, Label = "Wind (m/s)", Shader = true)] public float CloudSpeed = 8f;
	[Tune("", 0, 360, 1, Label = "Wind direction (deg)", Shader = true)] public float CloudWindAngle = 30f;
	[Tune(Shader = true)] public Color CloudColor = new(1f, 1f, 1f);
	[Tune("", 0, 20, Label = "Cloud ambient light", Shader = true)] public float CloudAmbient = 1.2f;
	[Tune("", 100, 500000, 100, Label = "Cloud fade distance (m)", Shader = true)] public float CloudFade = 20000f;
	public Vector4 SkySun;

	protected override string Shader => "res://core/render/passes/clouds/clouds_layer.compute";

	protected override void Update(Frame f) => SkySun = SkySunVector();
}
