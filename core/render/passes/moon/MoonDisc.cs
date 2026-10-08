using Godot;
using Renesance.Content;

[Pass(PassId.Moon, PassStage.Atmosphere, Order = 2, Enabled = false, Default = "disc", Reads = new[] { TargetId.HitId, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class MoonPass : AtmosphereVariant { }

// Phase = cell of the 4 × 2 moon_phases texture: (World.Day + MoonPhase) % 8 (v1 World.Day % 8).
[Variant("disc")]
public sealed class MoonDisc : MoonPass
{
	const int Cols = 4, Rows = 2;
	[Tune("", 0.1f, 90, Label = "Moon texture size (deg)", Shader = true)] public float MoonSize = 10f;
	[Tune("", 0, 100, Shader = true)] public float MoonDiscIntensity = 1.5f;
	[Tune("", 0, Cols * Rows - 1, 1, Label = "Moon phase (added to the day)")] public float MoonPhase;
	public Vector4 SkySun, MoonRect;
	Rid texture;

	protected override string Shader => "res://core/render/passes/moon/moon_disc.compute";

	protected override (int, Rid, bool)[] Extra() => [(5, texture.IsValid ? texture : texture = SkyTexture(ContentPaths.MoonTexture), true)];

	protected override void Update(Frame f)
	{
		int phase = Mathf.PosMod((int)MoonPhase + Renderer.World.Day, Cols * Rows);
		(SkySun, MoonRect) = (SkySunVector(), new Vector4((float)(phase % Cols) / Cols, (float)(phase / Cols) / Rows, 1f / Cols, 1f / Rows));
	}
}
