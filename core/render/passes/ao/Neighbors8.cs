using Godot;

[Pass(PassId.AO, PassStage.Lighting, Enabled = false, Default = "neighbors8", Reads = new[] { TargetId.Depth, TargetId.HitId }, Writes = new[] { TargetId.Ao })]
public abstract class AoPass : Variant { }

// v1 smooth AO per pixel, without v1's world cache (docs/v2.md §5.19): an edit regenerates chunks, the next frame reads them.
[Variant("neighbors8")]
public sealed class Neighbors8 : AoPass
{
	[Tune("", 0, 1, 0.05f, Shader = true)] public float AoStrength = 0.55f;
	[Tune("", 0.25f, 4, 0.05f, Label = "AO curve (power, > 1 dark only deep in corners)", Shader = true)] public float AoCurve = 1f;
	[Tune("", 0, 1, 0.05f, Label = "AO of cutout occluders (leaves, x a solid block)", Shader = true)] public float AoCutout = 1f;
	readonly System.Collections.Generic.Dictionary<bool, Kernel> kernels = new();
	Kernel kernel;
	Rid paramsBuffer, set;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		paramsBuffer = gpu.Buffer(gpu.Persistent, ParamsBytes().Length);
	}

	// With Water (its HitPos target exists) bent rays' hits come from HitPos.
	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		bool bent = t.Has(TargetId.HitPos);
		if (!kernels.TryGetValue(bent, out kernel))
			kernels[bent] = kernel = Gpu.Compile("res://core/render/passes/ao/ao_neighbors8.compute", 8, 8, bent ? [ParamsDefine(), "BENT"] : [ParamsDefine()]);
		set = Gpu.Set(kernel.Shader, [(0, paramsBuffer), (1, Renderer.CameraBuffer), (2, t[TargetId.Depth]), (3, t[TargetId.HitId]), (4, t[TargetId.Ao]), .. bent ? new[] { (5, t[TargetId.HitPos]) } : []]);
	}

	public override void Record(Frame f)
	{
		var p = ParamsBytes();
		Gpu.Rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p);
		Dispatch(kernel, set, f.Size);
	}
}
