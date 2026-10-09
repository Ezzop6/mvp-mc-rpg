using Godot;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;

[Pass(PassId.Primary, PassStage.Primary, Required = true, Default = "dda", Reads = new[] { TargetId.Start }, Writes = new[] { TargetId.Depth, TargetId.HitId, TargetId.Albedo })]
public abstract class PrimaryPass : Variant { }

// DDA through the L0 ring with occupancy skips, model blocks and object parts by march_model (rays.glsli); the world is set 1.
[Variant("dda")]
public class Dda : PrimaryPass
{
	const int StatWords = 13;
	readonly System.Collections.Generic.Dictionary<(bool, bool, bool), Kernel> kernels = new();
	Kernel kernel;
	Rid set, stats;
	bool debug;
	volatile string debugStats;

	protected virtual string[] Defines => Renderer.Defines;
	protected virtual (int, Rid)[] Bindings => [];

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		debug = Renderer.View != DebugView.None;
		if (debug) stats = gpu.Buffer(gpu.Persistent, StatWords * 4);
	}

	// With Transparency (its Trans target exists) the ray sees through glass and fog and fills Trans and Inscatter; with Water (its Water target) it refracts at water surfaces and goes on under water; with Liquid (Liquid target) it goes straight through translucent liquids.
	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		bool see = t.Has(TargetId.Trans), water = t.Has(TargetId.Water), liquid = t.Has(TargetId.Liquid);
		if (!kernels.TryGetValue((see, water, liquid), out kernel))
			kernels[(see, water, liquid)] = kernel = Gpu.Compile("res://core/render/passes/primary/primary_dda.compute", 8, 8, [.. Defines, .. see ? new[] { "SEE_THROUGH", "SEE_SCATTER" } : [], .. water ? new[] { "WATER" } : [], .. liquid ? new[] { "LIQUID" } : []]);
		(int, Rid)[] b = [(1, Renderer.CameraBuffer), (2, t[TargetId.Depth]), (3, t[TargetId.HitId]), (4, t[TargetId.Albedo]), (5, t[TargetId.Start]), .. Bindings];
		if (see) b = [.. b, (9, t[TargetId.Trans]), (10, t[TargetId.Inscatter])];
		if (water) b = [.. b, (11, t[TargetId.Water]), (12, t[TargetId.HitPos])];
		if (liquid) b = [.. b, (11, t[TargetId.Liquid]), (12, t[TargetId.LiquidId])];
		set = Gpu.Set(kernel.Shader, debug ? [.. b, (6, stats)] : b);
	}

	public override void Record(Frame f)
	{
		if (debug) Gpu.Rd.BufferClear(stats, 0, StatWords * 4);
		Dispatch(kernel, set, f.Size);
		if (debug) Gpu.Rd.BufferGetDataAsync(stats, Callable.From<byte[]>(OnStats));
	}

	// Per pixel means of the debug compile's counters.
	void OnStats(byte[] d)
	{
		float px = Math.Max(BitConverter.ToUInt32(d, 0), 1);
		string F(int i) => (BitConverter.ToUInt32(d, 4 * i) / px).ToString("0.00", CultureInfo.InvariantCulture);
		uint worst = BitConverter.ToUInt32(d, 32);
		debugStats = $"primary per pixel: dda {F(1)} (chunk {F(4)} group {F(5)} cell {F(6)}, warp max {F(7)})  march {F(2)}  model cells {F(3)}  slowest {worst >> 18} at {(worst >> 9 & 511) * 8},{(worst & 511) * 8}  over 256 {BitConverter.ToUInt32(d, 36)} over 1024 {BitConverter.ToUInt32(d, 40)}  backstop {BitConverter.ToUInt32(d, 44)}  out of steps {BitConverter.ToUInt32(d, 48)}";
	}

	public override string DebugStats => debugStats;
}

// Primary/dda that also sets a bit for every ring slot its rays step in (docs/v2-mereni.md, Viditelnost chunků): measurement of visibility-driven streaming. Requests (SeenFeedback) report the bits against the resident world and clear them.
[Variant("dda-seen")]
public sealed class DdaSeen : Dda
{
	const long Words = (256L * 256 * 256 + 31) / 32; // L0 rings up to 256 slots per axis, mark_seen checks the bound
	Rid seen;

	protected override string[] Defines => [.. base.Defines, "SEEN_FEEDBACK"];
	protected override (int, Rid)[] Bindings => [(7, seen)];

	public override void Init(Gpu gpu)
	{
		seen = gpu.Buffer(gpu.Persistent, Words * 4);
		base.Init(gpu);
		SeenFeedback.Active = true;
	}

	public override void Record(Frame f)
	{
		base.Record(f);
		while (SeenFeedback.Requests.TryDequeue(out var label))
		{
			if (label != null) Renderer.World.SeenReport(Gpu.Rd.BufferGetData(seen), label);
			Gpu.Rd.BufferClear(seen, 0, (uint)(Words * 4));
		}
	}
}

// Main thread -> render thread: label = report the seen bits then clear them, null = clear only; ignored unless Primary runs dda-seen.
public static class SeenFeedback
{
	public static volatile bool Active;
	public static readonly ConcurrentQueue<string> Requests = new();
	public static void Request(string label) { if (Active) Requests.Enqueue(label); }
}
