using Godot;

[Pass(PassId.Temporal, PassStage.Temporal, Enabled = false, Default = "catmull-variance", Reads = new[] { TargetId.Color, TargetId.Depth }, Writes = new[] { TargetId.Output, TargetId.History0, TargetId.History1 })]
public abstract class TemporalPass : Variant { }

// v1 resolve.compute on Color: the jittered render samples accumulated at the output size (upscaling), with the reprojected history clipped to their variance box.
[Variant("catmull-variance")]
public sealed class CatmullVariance : TemporalPass
{
	[Tune("", 0.05f, 1, 0.05f, Label = "Current frame weight in motion (sharp <-> steady)", Shader = true)] public float MotionBlend = 0.4f;
	[Tune(Label = "History (off = every frame alone)")] public bool TemporalHistory = true;

	static readonly Color NoHistory = new(0, 0, 0, -1);
	Kernel kernel;
	Rid paramsBuffer, history;
	readonly Rid[] sets = new Rid[2]; // set i reads History i and writes the other
	int read;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		kernel = gpu.Compile("res://core/render/passes/temporal/temporal_catmull_variance.compute", 8, 8, [.. Renderer.Defines, ParamsDefine()]);
		paramsBuffer = gpu.Buffer(gpu.Persistent, ParamsBytes().Length);
	}

	// New history textures (first use, output size) start empty.
	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		Rid h0 = t[TargetId.History0], h1 = t[TargetId.History1];
		for (int i = 0; i < 2; i++)
			sets[i] = Gpu.Set(kernel.Shader, (0, paramsBuffer), (1, Renderer.CameraBuffer), (2, t[TargetId.Color]), (3, t[TargetId.Depth]), (4, i == 0 ? h0 : h1), (5, i == 0 ? h1 : h0), (6, t[TargetId.Output]));
		if (h0 == history) return;
		(history, read) = (h0, 0);
		Gpu.Rd.TextureClear(h0, NoHistory, 0, 1, 0, 1);
	}

	public override void Record(Frame f)
	{
		if (!TemporalHistory) Gpu.Rd.TextureClear(Renderer.Targets[read == 0 ? TargetId.History0 : TargetId.History1], NoHistory, 0, 1, 0, 1);
		var p = ParamsBytes();
		Gpu.Rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p);
		Dispatch(kernel, sets[read], f.Output);
		read ^= 1;
	}
}
