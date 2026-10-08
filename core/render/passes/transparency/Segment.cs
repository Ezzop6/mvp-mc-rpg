using Godot;
using System.Collections.Generic;

[Pass(PassId.Transparency, PassStage.Atmosphere, Order = 6, Enabled = false, Default = "segment", Reads = new[] { TargetId.Trans, TargetId.Inscatter, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class TransparencyPass : Variant { }

// Glass, ice and fog blocks in front of the hit (docs/v2.md §5.23): Primary sees through them (SEE_THROUGH while Trans exists) and fills Trans and Inscatter, this pass lays them over Color after the sky.
[Variant("segment")]
public sealed class Segment : TransparencyPass
{
	[Tune("Glass", 0, 2, Label = "Glass colour strength (stained glass tint per crossing)")] public static float GlassTint = 1f;
	[Tune("Glass", 0, 1, Label = "Glass reflectance (F0)")] public static float GlassReflectance = 0.04f;
	[Tune("Glass", 0, 2, Label = "Sun on glass")] public static float GlassSun = 0.5f;
	[Tune("Glass", 0, 4, Label = "Sky in glass reflection (x ambient)")] public static float GlassSky = 1.5f;
	[Tune("Fog blocks", 0, 4, Label = "Fog block density (x the block's density)")] public static float VolumeDensity = 1f;
	[Tune("Fog blocks", 0, 2, Label = "Sun in fog blocks")] public static float VolumeSun = 0.3f;

	readonly Dictionary<bool, Kernel> kernels = new();
	Kernel kernel;
	Rid set;

	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		bool sun = t.Has(TargetId.SunVis);
		if (!kernels.TryGetValue(sun, out kernel))
			kernels[sun] = kernel = Gpu.Compile("res://core/render/passes/transparency/transparency_segment.compute", 8, 8, [.. Renderer.Defines, .. sun ? new[] { "SUN_VIS" } : []]);
		if (Gpu.Rd.UniformSetIsValid(set)) Gpu.Rd.FreeRid(set);
		set = Gpu.Set(kernel.Shader, [(2, t[TargetId.Color]), (3, t[TargetId.Trans]), (4, t[TargetId.Inscatter]), .. sun ? new[] { (5, t[TargetId.SunVis]) } : []]);
	}

	public override void Record(Frame f) => Dispatch(kernel, set, f.Size);
}
