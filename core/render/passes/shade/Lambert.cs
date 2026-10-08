using Godot;
using System.Collections.Generic;
using System.Linq;

[Pass(PassId.Shade, PassStage.Compose, Required = true, Default = "lambert", Reads = new[] { TargetId.Albedo, TargetId.HitId }, Writes = new[] { TargetId.Color })]
public abstract class ShadePass : Variant { }

// albedo × (sun × max(N·L, 0) × SunVis + ambient); the sky gets the ambient colour. A term's target, when an enabled pass declares it, compiles the shader with its define and binding.
[Variant("lambert")]
public sealed class Lambert : ShadePass
{
	[Tune("Emission", Label = "Block emission (an effect)")] public bool Emission;
	[Tune("Emission", 0, 50, Label = "Block emission scale (face emission 1, times the albedo)", Shader = true)] public float EmissionScale = 12f;
	[Tune("Emission", 0, 20, Label = "Model emission scale (material emission strength 1, times the albedo)", Shader = true)] public float ModelEmission = 4f;
	[Tune("Emission", 0, 50, Label = "Liquid emission scale (lava, face emission 1, times the albedo)", Shader = true)] public float LiquidEmission = 12f;

	static readonly (TargetId Target, string Define, int Binding)[] terms = [(TargetId.Ao, "AO", 5), (TargetId.SunVis, "SUN_VIS", 6), (TargetId.Lamp, "LAMP", 7), (TargetId.Gi, "GI", 8)];
	readonly Dictionary<string, Kernel> kernels = new();
	Kernel kernel;
	Rid set, paramsBuffer;
	bool emission; // SHADE_EMISSION: Albedo.a × EmissionScale (models ModelEmission), params at binding 0

	bool EmissionOn => Emission && !Pass.EffectsOff;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		paramsBuffer = gpu.Buffer(gpu.Persistent, ParamsBytes().Length);
	}

	public override void Resize(Vector2I size)
	{
		emission = EmissionOn;
		var on = terms.Where(t => Renderer.Targets.Has(t.Target)).ToArray();
		string[] defines = [.. on.Select(t => t.Define), .. emission ? new[] { ParamsDefine(), "SHADE_EMISSION" } : []];
		string key = string.Join(",", defines);
		if (!kernels.TryGetValue(key, out kernel))
			kernels[key] = kernel = Gpu.Compile("res://core/render/passes/shade/shade_lambert.compute", 8, 8, [.. Renderer.Defines, .. defines]);
		if (Gpu.Rd.UniformSetIsValid(set)) Gpu.Rd.FreeRid(set);
		set = Gpu.Set(kernel.Shader, [(1, Renderer.CameraBuffer), (2, Renderer.Targets[TargetId.Albedo]), (3, Renderer.Targets[TargetId.HitId]), (4, Renderer.Targets[TargetId.Color]), .. on.Select(t => (t.Binding, Renderer.Targets[t.Target])), .. emission ? new[] { (0, paramsBuffer) } : []]);
	}

	public override void Record(Frame f)
	{
		if (EmissionOn != emission) Resize(f.Size);
		if (emission) { var p = ParamsBytes(); Gpu.Rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p); }
		Dispatch(kernel, set, f.Size);
	}
}
