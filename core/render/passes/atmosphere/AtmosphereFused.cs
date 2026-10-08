using Godot;
using System;
using Renesance.Content;

[Pass(PassId.Atmosphere, PassStage.Atmosphere, Order = -1, Default = "fused", Reads = new[] { TargetId.HitId, TargetId.Depth, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class AtmospherePass : AtmosphereVariant { }

// The enabled atmosphere passes right after it (Renderer.Fused: up to the first other pass, e.g. Fluid) in one read and write of Color, with their own code (.glsli) and values; off = each pass separately.
[Variant("fused")]
public sealed class AtmosphereFused : AtmospherePass
{
	public static readonly Type[] Members = [typeof(SkyGradient), typeof(StarCells), typeof(MoonDisc), typeof(SunDisc), typeof(CloudLayer), typeof(HazeAnalytic), typeof(HeightFogAnalytic)];
	public Vector4 SkySun, Fused; // Fused.x: bit i = Members[i] computed this frame
	Rid sun, moon;

	protected override string Shader => "res://core/render/passes/atmosphere/atmosphere_fused.compute";
	protected override Type[] ParamTypes => [GetType(), .. Members];
	protected override object ParamOwner(Type t) => t == GetType() ? this : Renderer.Fused(t);

	protected override (int, Rid, bool)[] Extra() =>
		[(5, sun.IsValid ? sun : sun = SkyTexture(ContentPaths.SunTexture), true), (6, moon.IsValid ? moon : moon = SkyTexture(ContentPaths.MoonTexture), true)];

	protected override void Update(Frame f)
	{
		int mask = 0;
		for (int i = 0; i < Members.Length; i++) if (Renderer.Fused(Members[i]) != null) mask |= 1 << i;
		(SkySun, Fused) = (SkySunVector(), new Vector4(mask, 0, 0, 0));
	}
}
