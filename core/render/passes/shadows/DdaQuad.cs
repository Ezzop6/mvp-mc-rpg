using Godot;
using System;
using System.Globalization;

[Pass(PassId.Shadows, PassStage.Lighting, Enabled = false, Default = "dda-quad", Reads = new[] { TargetId.Depth, TargetId.HitId }, Writes = new[] { TargetId.SunVis })]
public abstract class ShadowsPass : Variant { }

// Sun shadows as v1 (docs/v2.md §5.21): a thread per 2x2 quad, one trace() towards the sun when it is flat, per pixel on edges.
[Variant("dda-quad")]
public class DdaQuad : ShadowsPass
{
	const int StatWords = 8;
	[Tune("", 8, 400, 8, Label = "Shadow distance (m)", Shader = true)] public float ShadowDistance = 200f;
	[Tune("", 0.001f, 0.1f, 0.001f, Label = "Shadow bias (blocks)", Shader = true)] public float ShadowBias = 0.01f;
	[Tune("", 0, 200, 4, Label = "Shadow fade band (m before the distance, 0 = hard cut)", Shader = true)] public float ShadowFade = 0f;
	[Tune("Look", 0, 1, 0.01f, Label = "Shadow strength (1 = no sun in shadow)", Shader = true)] public float ShadowStrength = 1f;
	[Tune("Look", Label = "Shadow tint (colour of the sun left in shadow, strength < 1)", Shader = true)] public Color ShadowTint = new(1, 1, 1);
	readonly System.Collections.Generic.Dictionary<(bool, bool, bool), Kernel> kernels = new();
	Kernel kernel;
	Rid set, paramBuf, stats;
	bool debug;
	volatile string debugStats;

	protected virtual string[] Extra => [];
	protected virtual int Quad => 2; // pixels per thread and axis

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		debug = Renderer.View != DebugView.None;
		paramBuf = gpu.Buffer(gpu.Persistent, ParamsBytes().Length);
		if (debug) stats = gpu.Buffer(gpu.Persistent, StatWords * 4);
	}

	// With Transparency (its Trans target exists) glass, water and fog tint the shadow and the fog in front gets its own ray; with Water or Liquid liquid does, from where the bent ray hit (Bent: HitPos exists).
	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		bool see = t.Has(TargetId.Trans), water = t.Has(TargetId.Water) || t.Has(TargetId.Liquid), bent = t.Has(TargetId.HitPos);
		if (!kernels.TryGetValue((see, water, bent), out kernel))
			kernels[(see, water, bent)] = kernel = Gpu.Compile("res://core/render/passes/shadows/shadows_dda.compute", 8, 8, [.. Renderer.Defines, .. Extra, ParamsDefine(), .. see ? new[] { "SEE_THROUGH" } : [], .. water ? new[] { "SEE_WATER" } : [], .. bent ? new[] { "BENT" } : []]);
		(int, Rid)[] b = [(0, paramBuf), (1, Renderer.CameraBuffer), (2, t[TargetId.Depth]), (3, t[TargetId.HitId]), (4, t[TargetId.SunVis]), .. see ? new[] { (6, t[TargetId.Trans]) } : [], .. bent ? new[] { (7, t[TargetId.HitPos]) } : []];
		set = Gpu.Set(kernel.Shader, debug ? [.. b, (5, stats)] : b);
	}

	public override void Record(Frame f)
	{
		var p = ParamsBytes();
		Gpu.Rd.BufferUpdate(paramBuf, 0, (uint)p.Length, p);
		if (debug) Gpu.Rd.BufferClear(stats, 0, StatWords * 4);
		Dispatch(kernel, set, new Vector2I((f.Size.X + Quad - 1) / Quad, (f.Size.Y + Quad - 1) / Quad));
		if (debug) Gpu.Rd.BufferGetDataAsync(stats, Callable.From<byte[]>(OnStats));
	}

	void OnStats(byte[] d)
	{
		uint U(int i) => BitConverter.ToUInt32(d, 4 * i);
		float rays = Math.Max(U(1), 1);
		string F(int i) => (U(i) / rays).ToString("0.0", CultureInfo.InvariantCulture);
		debugStats = $"shadows: rays per pixel {(U(1) / (float)Math.Max(U(0), 1)).ToString("0.000", CultureInfo.InvariantCulture)}, per ray dda {F(2)} (chunk {F(4)} group {F(5)} cell {F(6)}) march {F(3)}, out of steps {U(7)}";
	}

	public override string DebugStats => debugStats;
}

[Variant("dda")]
public sealed class DdaPixel : DdaQuad
{
	protected override string[] Extra => ["SHADOW_PER_PIXEL"];
	protected override int Quad => 1;
}
