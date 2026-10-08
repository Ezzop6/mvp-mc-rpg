using Godot;
using Renesance.Content;

[Pass(PassId.Sun, PassStage.Atmosphere, Order = 3, Enabled = false, Default = "disc", Reads = new[] { TargetId.HitId, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class SunPass : AtmosphereVariant { }

[Variant("disc")]
public sealed class SunDisc : SunPass
{
	[Tune("", 0.1f, 90, Label = "Sun texture size (deg)", Shader = true)] public float SunSize = 12f;
	[Tune("", 0, 200, Shader = true)] public float SunDiscIntensity = 8f;
	public Vector4 SkySun;
	Rid texture;

	protected override string Shader => "res://core/render/passes/sun/sun_disc.compute";

	protected override (int, Rid, bool)[] Extra() => [(5, texture.IsValid ? texture : texture = SkyTexture(ContentPaths.SunTexture), true)];

	protected override void Update(Frame f) => SkySun = SkySunVector();
}
