using Godot;

[Pass(PassId.Stars, PassStage.Atmosphere, Order = 1, Enabled = false, Default = "cells", Reads = new[] { TargetId.HitId, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class StarsPass : AtmosphereVariant { }

// The sky turns about the pole of the sun's path once a day.
[Variant("cells")]
public sealed class StarCells : StarsPass
{
	[Tune("", 0, 100000, 1, Label = "Star count (whole sky sphere)", Shader = true)] public float StarCount = 2000f;
	[Tune("", 0.001f, 1, Label = "Star size (deg, Gaussian radius)", Shader = true)] public float StarSize = 0.03f;
	[Tune("", 0, 100, Shader = true)] public float StarBrightness = 8f;
	[Tune("", 0, 1, Shader = true)] public float StarTwinkle = 0.1f;
	[Tune("", 0, 50, Shader = true)] public float StarTwinkleSpeed = 1f;
	[Tune("", 0.001f, 1, Label = "Stars fade in (sun depth under the horizon, sin)", Shader = true)] public float StarFade = 0.15f;
	public Vector4 SkySun, SkyRot;

	static readonly Vector3 Pole = Renderer.SunDirection(6f).Cross(Renderer.SunDirection(12f)).Normalized();

	protected override string Shader => "res://core/render/passes/stars/stars_cells.compute";

	protected override void Update(Frame f)
	{
		var q = new Quaternion(Pole, -(Renderer.World.TimeOfDay - 6f) / 24f * Mathf.Tau);
		(SkySun, SkyRot) = (SkySunVector(), new Vector4(q.X, q.Y, q.Z, q.W));
	}
}
