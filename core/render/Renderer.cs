using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

// Generic debug views (docs/v2.md §6.1); any but None compiles the passes with DEBUG_VIEW.
public enum DebugView { None, Steps, Albedo, Cost, ModelLevel, Temporal, Footprint, Depth, AO, Sun, Lamp, LightCount, GI }

// The frame besides the passes, ms: CPU interval, scripts' _Process (Main's start to Renderer's end), Renderer's alone (NextStep, recording), Godot's viewport draw (CPU with frame setup, GPU), GPU span of every timestamp.
public enum FrameStat { Frame, Process, Renderer, GodotCpu, GodotGpu, GpuSpan }

// Camera, pass order and per-pass GPU times; the passes record on the render thread (docs/v2.md §5).
public partial class Renderer : Node
{
	[Export] public World World;
	[Export] public Camera3D Camera;
	[Export] public MeshInstance3D Screen;
	public const float DefaultScale = 1f;
	[Export] public float RenderScale = DefaultScale; // fraction of the window's pixels per axis, 0 = auto (AutoScale)
	[Setting(MenuTab.World, "Render", 30, 240, 10, Label = "Target FPS (auto scale)")] public float TargetFps = 60;
	public float EffectiveScale { get; private set; } = 0.5f;
	[Setting(MenuTab.World, "Render", Label = "Auto scale (holds the target FPS)")] public bool AutoRenderScale { get => RenderScale <= 0; set => RenderScale = value ? 0 : EffectiveScale; }
	[Setting(MenuTab.World, "Render", 0.25f, 1, 1f / 32, Label = "Render scale")] public float MenuRenderScale { get => EffectiveScale; set => RenderScale = value; }
	public Vector2I OutputSize { get; private set; }
	[Tune("Air", Label = "Sky top (sky, ambient, reflections)")] public static Color SkyTop = new(0.122f, 0.177f, 0.268f);
	[Tune("Air", Label = "Sky horizon (sky, haze, fog, ambient, reflections)")] public static Color SkyHorizon = new(0.383f, 0.395f, 0.415f);
	[Tune("Air", 0, 2, Label = "Sun in haze and fog")] public static float HazeSun = 0.6f;
	[Tune("Air", 1, 64, Label = "Sun in haze and fog sharpness")] public static float HazeSunFocus = 8f;
	[Tune("Light")] public static Color SunColor = new(1.0f, 0.96f, 0.9f);
	[Tune("Light", 0, 20)] public static float SunIntensity = 1f;
	[Tune("Light", 0, 4)] public static float AmbientIntensity = 1f;
	[Tune("Light", 0, 1, 0.05f, Label = "Ambient top bias (from above, with AO)")] public static float AmbientTopBias = 0.25f;
	[Tune("Light", Label = "Ambient tint (x the sky light)")] public static Color AmbientTint = new(1, 1, 1);
	[Tune("Light", Label = "Ground bounce (ambient added on faces turned down)")] public static Color GroundBounce = new(0, 0, 0);
	[Tune("Light", 0, 2, 0.01f, Label = "Sun saturation (sunlight colour)")] public static float SunSaturation = 1f;
	[Tune("Distant cutout", Label = "Solid (distant cutout surfaces, less shimmer)")] public static bool DistantCutoutSolid = true;
	[Tune("Distant cutout", 0, 6, 0.25f, Label = "From level (texture, 2^level texels per pixel)")] public static float DistantCutoutStart = 2f;
	[Tune("Distant cutout", 0.25f, 3, 0.25f, Label = "Solid after levels")] public static float DistantCutoutWidth = 1.5f;
	public readonly Pass[] Passes = Pass.Discover();
	public Vector2I RenderSize { get; private set; }
	public string[] Defines => view == DebugView.None ? [] : ["DEBUG_VIEW", $"VIEW_{Gpu.Snake(view.ToString())}"];
	public string DebugStats => string.Join("  ", variants?.Select(v => v.DebugStats).Where(s => s != null) ?? []);

	// A switch re-creates every variant like Rebuilt.
	public DebugView View
	{
		get => view;
		set
		{
			if (value == view) return;
			view = value;
			if (ready) RenderingServer.CallOnRenderThread(Callable.From(Init));
		}
	}

	// Render thread.
	public GBuffer Targets { get; } = new();
	public Rid CameraBuffer { get; private set; }

	// The frame's flat light (ambient + sun), set in CameraFrame, for tools that shade outside the passes (the editor ghost).
	public static Color SunLight = Colors.White;

	const int TimingFrames = 15;
	readonly Dictionary<PassId, float[]> passMs = new();
	readonly float[] totalMs = new float[TimingFrames];
	readonly float[][] frameMs = Enum.GetValues<FrameStat>().Select(_ => new float[TimingFrames]).ToArray();
	int timingFrame;
	float autoTimer;
	volatile bool ready, restart;
	volatile DebugView view;
	Gpu gpu;
	Variant[] variants;
	readonly Frame frame = new();
	string layout, checkedLayout; // render thread, main thread
	// Main thread to render thread without allocating: inputs reused round robin (the render thread is never this many frames behind), one cached Callable.
	sealed class FrameInput
	{
		public Vector2I Size, Output;
		public string Layout;
		public bool[] Enabled;
		public string[] Selected;
		public IReadOnlyDictionary<string, float[]> Tune;
		public readonly byte[] Cam = new byte[Unsafe.SizeOf<CameraData>()];
		public World.Step Step;
		public bool Fresh, Loading;
	}
	readonly FrameInput[] inputs = new FrameInput[4];
	readonly System.Collections.Concurrent.ConcurrentQueue<FrameInput> pending = new();
	Callable renderNext;
	int nextInput;
	bool temporalOn;
	bool[] recording; // render thread: this frame's FrameInput.Enabled
	bool[] fused; // render thread: passes AtmosphereFused computes this frame
	int fusedPass;
	readonly Dictionary<string, float> sunWeights = new();

	// Median of the last 15 frames, ms; 0 when the pass did not run.
	public float PassMs(PassId id) => passMs.TryGetValue(id, out var ms) ? Median(ms) : 0;
	public float TotalMs => Median(totalMs);
	public ulong ProcessStarted; // Time.GetTicksUsec at Main._Process, the first script of the frame
	public float FrameMs(FrameStat s) => Median(frameMs[(int)s]);
	public long ProcessAllocated { get; private set; } // bytes _Process allocated on the main thread, NextStep included (flight log)
	long renderThreadAllocated;
	public long TakeRenderThreadAllocated() => Interlocked.Exchange(ref renderThreadAllocated, 0); // bytes RenderFrame allocated since the last call
	public float LastMs => totalMs[(timingFrame + TimingFrames - 1) % TimingFrames]; // previous frame, for flight metrics
	public float LastPassMs(PassId id) => passMs[id][(timingFrame + TimingFrames - 1) % TimingFrames];

	static float Median(float[] v) => v.Order().ElementAt(v.Length / 2);

	public override void _Ready()
	{
		ProcessPriority = 1000; // after the camera moved
		RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
		foreach (var p in Passes) passMs[p.Id] = new float[TimingFrames];
		for (int i = 0; i < inputs.Length; i++) inputs[i] = new FrameInput { Enabled = new bool[Passes.Length], Selected = new string[Passes.Length] };
		renderNext = Callable.From(() => { if (pending.TryDequeue(out var a)) RenderFrame(a); });
		(fused, fusedPass) = (new bool[Passes.Length], Array.FindIndex(Passes, p => p.Id == PassId.Atmosphere));
		Pass.Check(Passes);
		World.Rebuilt += Init;
		World.Regenerated += Restart;
		RenderingServer.CallOnRenderThread(Callable.From(Init));
	}

	// After World's GPU side (World.InitGpu, Rebuild): the shaders compile against its prelude.
	void Init()
	{
		gpu = World.Gpu;
		CameraBuffer = gpu.Persistent.Add(gpu.Rd.StorageBufferCreate((uint)Unsafe.SizeOf<CameraData>()));
		var old = variants ?? [];
		variants = Passes.Select(p => p.Create(p.Selected)).ToArray();
		for (int i = 0; i < variants.Length; i++) { variants[i].Renderer = this; if (Passes[i].Active) variants[i].Init(gpu); } // the others compile when they are first enabled
		foreach (var v in old.Where(v => v.Initialized))
		{
			if (variants.FirstOrDefault(n => n.Id == v.Id) is { Initialized: true } n) n.Adopt(v);
			if (v is GodotQuad q) q.Retire(); else v.Free();
		}
		layout = null;
		ready = true;
	}

	// The next frame starts as the first: no camera history, jitter and frame counter at 0, new targets (empty temporal history).
	public void Restart() => restart = true;

	public override void _ExitTree()
	{
		ready = false;
		if (variants == null) return;
		foreach (var q in variants.OfType<GodotQuad>()) q.Detach();
		RenderingServer.CallOnRenderThread(Callable.From(() =>
		{
			foreach (var v in variants.Where(v => v.Initialized)) v.Free();
			Targets.Free(gpu.Rd);
		}));
	}

	public override void _Process(double delta)
	{
		if (!ready) return;
		long allocStart = GC.GetAllocatedBytesForCurrentThread();
		ulong started = Time.GetTicksUsec();
		var vp = GetViewport().GetViewportRid();
		int f = timingFrame % TimingFrames;
		frameMs[(int)FrameStat.Frame][f] = (float)(delta * 1000);
		frameMs[(int)FrameStat.GodotCpu][f] = (float)(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp) + RenderingServer.GetFrameSetupTimeCpu());
		frameMs[(int)FrameStat.GodotGpu][f] = (float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp);
		bool fresh = restart;
		if (fresh) (restart, prev, jitterFrame) = (false, null, 0);
		var output = OutputSize = GetWindow().Size;
		EffectiveScale = RenderScale > 0 ? RenderScale : AutoScale((float)delta);
		RenderSize = new Vector2I(Math.Max(8, (int)(output.X * EffectiveScale)), Math.Max(8, (int)(output.Y * EffectiveScale)));
		var a = inputs[nextInput++ % inputs.Length];
		var run = Pass.Runnable(Passes);
		bool changed = a.Size != RenderSize || a.Output != output;
		for (int i = 0; i < Passes.Length; i++)
		{
			changed |= a.Enabled[i] != run[i] || a.Selected[i] != Passes[i].Selected;
			(a.Enabled[i], a.Selected[i]) = (run[i], Passes[i].Selected);
			if (Passes[i].Id == PassId.Temporal) temporalOn = a.Enabled[i];
		}
		(a.Size, a.Output) = (RenderSize, output);
		if (changed || a.Layout == null) a.Layout = LayoutKey(a);
		checkedLayout = a.Layout;
		Gpu.Write(CameraFrame(RenderSize, output, temporalOn), a.Cam);
		(a.Step, a.Tune, a.Fresh, a.Loading) = (World.NextStep(), TuneSet.Values, fresh, World.Loading);
		pending.Enqueue(a);
		RenderingServer.CallOnRenderThread(renderNext);
		ulong now = Time.GetTicksUsec();
		frameMs[(int)FrameStat.Renderer][f] = (now - started) / 1000f;
		frameMs[(int)FrameStat.Process][f] = ProcessStarted > 0 ? (now - ProcessStarted) / 1000f : 0;
		ProcessAllocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;
	}

	// The same string instance for the same targets (compared by reference every frame).
	string LayoutKey(FrameInput a)
	{
		string key = $"{a.Size} {a.Output} {string.Concat(a.Enabled.Select(e => e ? 1 : 0))} {string.Join(",", a.Selected)}";
		return key == checkedLayout ? checkedLayout : key;
	}

	// Small, rare steps toward the frame budget of TargetFps less 3 ms; every change re-creates the render-size targets.
	float AutoScale(float delta)
	{
		if ((autoTimer += delta) < 0.25f || TotalMs <= 0) return EffectiveScale;
		autoTimer = 0;
		float scale = EffectiveScale * Mathf.Clamp(Mathf.Sqrt((1000f / TargetFps - 3f) / TotalMs), 0.9f, 1.1f);
		return Mathf.Clamp(Mathf.Snapped(scale, 1f / 32), 0.25f, 1f);
	}

	// Anchor = the world's L0 chunk, frac = the camera's Godot position within it (World.Rebase), in blocks; prev = last frame's camera in this anchor; with Temporal jittered, texture and model levels by the output's pixel.
	CameraData CameraFrame(Vector2I size, Vector2I output, bool temporal)
	{
		var (sunDir, sunCol, ambient) = Sun();
		SunLight = new Color(ambient.R + sunCol.R * 0.85f, ambient.G + sunCol.G * 0.85f, ambient.B + sunCol.B * 0.85f);
		var b = Camera.GlobalBasis;
		var a = World.Anchor;
		var frac = Camera.GlobalPosition / Voxel.BlockSize;
		var (pa, pf, pb) = prev ?? (a, frac, b);
		prev = (a, frac, b);
		int k = (int)(jitterFrame++ % JitterPhases(size, output)) + 1;
		float tanHalf = Mathf.Tan(Mathf.DegToRad(Camera.Fov) / 2);
		static Vector4 V(Vector3 v, float w = 0) => new(v.X, v.Y, v.Z, w);
		return new CameraData
		{
			Anchor = new Vector4I(a.X, a.Y, a.Z, 0), Frac = V(frac),
			Right = V(b.X, (float)size.X / size.Y), Up = V(b.Y), Fwd = V(-b.Z, tanHalf),
			SunDir = V(sunDir), SunCol = new Vector4(sunCol.R, sunCol.G, sunCol.B, 0), Ambient = new Vector4(ambient.R, ambient.G, ambient.B, AmbientTopBias),
			Lens = new Vector4(2 * tanHalf / size.Y, 1e9f, 2 * tanHalf / (temporal ? output.Y : size.Y), 0),
			PrevFrac = V(pf + (Vector3)((pa - a) * Layout.Chunk)), PrevRight = V(pb.X), PrevUp = V(pb.Y), PrevFwd = V(-pb.Z),
			Jitter = temporal ? new Vector4(Halton(k, 2) - 0.5f, Halton(k, 3) - 0.5f, 0, 0) : default,
			Cutout = new Vector4(DistantCutoutStart, DistantCutoutSolid ? DistantCutoutWidth : 0, 0, 0), Frame = frame.Index,
			Glass = new Vector4(Segment.GlassTint, Segment.GlassReflectance, Segment.GlassSun, Segment.GlassSky), Media = new Vector4(ReflectRefract.WaterAbsorption, Segment.VolumeDensity, Segment.VolumeSun, Fluid.FluidFadeEnd),
			Water = new Vector4(ReflectRefract.WaterWaves, (float)(World.Clock * ReflectRefract.WaterWaveSpeed), ReflectRefract.WaterIor, ReflectRefract.WaterReflectance),
			WaterDeep = new Vector4(ReflectRefract.WaterDeep, ReflectRefract.WaterDeepSun, ReflectRefract.WaterGlint, ReflectRefract.WaterGlintFocus),
			SkyTop = new Vector4(Renderer.SkyTop.R, Renderer.SkyTop.G, Renderer.SkyTop.B, HazeSun), SkyHorizon = new Vector4(Renderer.SkyHorizon.R, Renderer.SkyHorizon.G, Renderer.SkyHorizon.B, HazeSunFocus),
			Ground = new Vector4(GroundBounce.R, GroundBounce.G, GroundBounce.B, 0), WaterTint = new Vector4(ReflectRefract.WaterDeepTint.R, ReflectRefract.WaterDeepTint.G, ReflectRefract.WaterDeepTint.B, 0),
		};
	}

	(Vector3I Anchor, Vector3 Frac, Basis Basis)? prev;
	uint jitterFrame;

	// 8 samples per output pixel: a render pixel covers 1 / scale^2 of them.
	static int JitterPhases(Vector2I size, Vector2I output) => (int)MathF.Ceiling(8f * output.Y * output.Y / ((float)size.Y * size.Y));

	// Halton bases 2 and 3 over JitterPhases frames.
	static float Halton(int i, int b)
	{
		float f = 1, r = 0;
		for (; i > 0; i /= b) { f /= b; r += f * (i % b); }
		return r;
	}

	public static Vector3 SunDirection(float hours)
	{
		float angle = (hours - 6f) / 24f * Mathf.Tau;
		float el = Mathf.Sin(angle) * 1.2f, az = Mathf.Cos(angle) * 1.6f + 0.6f;
		return new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az)).Normalized();
	}

	// The presets blended by the sun's height; at night the moon lights from the opposite direction.
	(Vector3 dir, Color col, Color ambient) Sun()
	{
		var d = SunDirection(World.TimeOfDay);
		float day = Mathf.SmoothStep(-0.08f, 0.12f, d.Y), noon = Mathf.SmoothStep(0.05f, 0.35f, d.Y);
		(sunWeights[TuneSet.Day], sunWeights[TuneSet.Dusk], sunWeights[TuneSet.Night]) = (day * noon, day * (1 - noon), 1 - day);
		TuneSet.Apply(Passes, sunWeights);
		bool night = d.Y < 0;
		float energy = SunIntensity * (night ? Mathf.SmoothStep(0f, 0.2f, -d.Y) : day);
		var sun = SunColor;
		if (SunSaturation != 1) { float l = sun.R * 0.2126f + sun.G * 0.7152f + sun.B * 0.0722f; sun = new Color(Mathf.Max(l + (sun.R - l) * SunSaturation, 0), Mathf.Max(l + (sun.G - l) * SunSaturation, 0), Mathf.Max(l + (sun.B - l) * SunSaturation, 0)); }
		return (night ? -d : d, sun * energy, (SkyTop + SkyHorizon) * 0.5f * AmbientIntensity * AmbientTint);
	}

	[StructLayout(LayoutKind.Sequential)]
	struct CameraData // camera.glsli
	{
		public Vector4I Anchor;
		public Vector4 Frac, Right, Up, Fwd, SunDir, SunCol, Ambient, Lens, PrevFrac, PrevRight, PrevUp, PrevFwd, Jitter, Cutout, Glass, Media, Water, WaterDeep, SkyTop, SkyHorizon, Ground, WaterTint;
		public uint Frame, Pad0, Pad1, Pad2;
	}

	// --- render thread ---

	void RenderFrame(FrameInput a)
	{
		if (!ready) return;
		long allocStart = GC.GetAllocatedBytesForCurrentThread();
		var rd = gpu.Rd;
		if (a.Fresh) { (frame.Index, layout) = (0, null); Targets.Free(rd); }
		gpu.CaptureTimestamps();
		ReadTimes();
		World.Upkeep(a.Step);
		if (!World.RingReady) return;
		Switch(a.Selected, a.Enabled);
		if (a.Layout != layout) Relayout(a.Size, a.Output, a.Enabled, a.Layout);
		rd.BufferUpdate(CameraBuffer, 0, (uint)a.Cam.Length, a.Cam);
		(frame.Gpu, frame.Targets, frame.Size, frame.Output, recording) = (gpu, Targets, a.Size, a.Output, a.Enabled);
		Fuse(a);
		for (int i = 0; i < variants.Length; i++)
		{
			if (!a.Enabled[i] || fused[i] != (i == fusedPass) || a.Loading && Passes[i].Info.Effect && !Passes[i].Info.Required) continue; // the loading screen hides the frame: generation gets the GPU
			gpu.Timestamp(variants[i].TimestampBegin);
			TuneSet.Write(variants[i], a.Tune);
			variants[i].Record(frame);
			gpu.Timestamp(variants[i].TimestampEnd);
		}
		frame.Index++;
		Interlocked.Add(ref renderThreadAllocated, GC.GetAllocatedBytesForCurrentThread() - allocStart);
	}

	// The enabled atmosphere passes after the Atmosphere pass up to the first it can't compute (another pass in between keeps the order): their values are set here, AtmosphereFused draws them.
	void Fuse(FrameInput a)
	{
		Array.Clear(fused);
		if (!a.Enabled[fusedPass] || a.Loading) return;
		for (int i = fusedPass + 1; i < variants.Length && Passes[i].Info.Stage == PassStage.Atmosphere; i++)
		{
			if (!a.Enabled[i]) continue;
			if (Array.IndexOf(AtmosphereFused.Members, variants[i].GetType()) < 0) break;
			fused[i] = fused[fusedPass] = true; // fusedPass: something to draw
			TuneSet.Write(variants[i], a.Tune);
			((AtmosphereVariant)variants[i]).Prepare(frame);
		}
	}

	// Render thread: the variant of type t AtmosphereFused computes this frame, else null.
	public Variant Fused(Type t)
	{
		for (int i = fusedPass + 1; i < variants.Length; i++)
			if (fused[i] && variants[i].GetType() == t) return variants[i];
		return null;
	}

	// Render thread: the pass's variant when it records in this frame, else null.
	public Variant Recording(PassId id)
	{
		for (int i = 0; i < Passes.Length; i++)
			if (Passes[i].Id == id) return recording?[i] == true ? variants[i] : null;
		return null;
	}

	// Another variant from the preset replaces the pass's old one, an enabled pass's variant compiles on first use; the layout (it has the names) then resizes it.
	void Switch(string[] selected, bool[] enabled)
	{
		for (int i = 0; i < variants.Length; i++)
		{
			if (variants[i].Id.Name == selected[i]) continue;
			if (variants[i].Initialized) variants[i].Free();
			variants[i] = Passes[i].Create(selected[i]);
			variants[i].Renderer = this;
		}
		for (int i = 0; i < variants.Length; i++)
			if (enabled[i] && !variants[i].Initialized) { variants[i].Init(gpu); layout = null; }
	}

	// Targets follow the enabled passes; a disabled pass leaves its targets' defaults. Reads/writes come from the variant each pass actually runs (the frame's selection), so a variant switch cannot desync from the layout.
	void Relayout(Vector2I size, Vector2I output, bool[] enabled, string newLayout)
	{
		layout = newLayout;
		var declared = new HashSet<TargetId>();
		for (int i = 0; i < variants.Length; i++)
			if (enabled[i]) declared.UnionWith(Passes[i].ReadsOf(variants[i].Id.Name).Concat(Passes[i].WritesOf(variants[i].Id.Name)));
		Targets.Allocate(gpu.Rd, size, output, declared);
		for (int i = 0; i < variants.Length; i++)
		{
			if (enabled[i]) variants[i].Resize(size);
			else foreach (var t in Passes[i].WritesOf(variants[i].Id.Name)) Targets.Fill(gpu.Rd, t);
		}
	}

	void ReadTimes()
	{
		var t = gpu.Captured;
		int k = timingFrame++ % TimingFrames;
		ulong first = ulong.MaxValue, last = 0, min = ulong.MaxValue, max = 0;
		foreach (var v in variants)
		{
			bool ran = t.TryGetValue(v.TimestampBegin, out var b) & t.TryGetValue(v.TimestampEnd, out var e);
			passMs[v.Id.Pass][k] = ran ? (e - b) / 1e6f : 0;
			if (ran) (first, last) = (Math.Min(first, b), Math.Max(last, e));
		}
		foreach (var v in t.Values) (min, max) = (Math.Min(min, v), Math.Max(max, v));
		totalMs[k] = last > first ? (last - first) / 1e6f : 0;
		frameMs[(int)FrameStat.GpuSpan][k] = t.Count > 1 ? (max - min) / 1e6f : 0;
	}
}
