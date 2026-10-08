using Godot;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Renesance.Content;
using Renesance.Save;
using Renesance.World;

// User draw distance (docs/v2.md §5.17): horizon, LodPixels and the budget; the levels' radii follow from the window's pixel angle.
public enum ViewPreset { Low, Medium, High, Ultra }

// Counts and CPU ms of one NextStep (flight frame log, docs/v2-mereni.md).
public struct StreamFrame
{
	public int Queue, Sent, Columns, FillCap, Released, Entered, Requeued, Failed, Holes, Retrying, Incoming, Drained, Pending, Stale, QNear, QMid, QFar, SentNear, SentMid, SentFar, RequeuedFar, FarAgeMax, SentLod;
	public float FarAge; // mean frames a far chunk sent this frame waited since it was queued
	public float Budget;
	public double MoveMs, NearMs, PopMs, DrainMs, UploadMs, StepMs;
}

// A world chunk of a level of detail, as gen.compute reads released chunks (w = level).
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LevelChunk(int X, int Y, int Z, int Level);

// The world: settings, the spheres of the levels of detail streamed around the camera (docs/v2.md §4, §5.17), edits; main thread here, GPU side in World.Gpu.
public partial class World : Node3D
{
	// RadiusM: L0 only (LOD off, the default); HorizonM and LodPixels: with LOD.
	public static readonly Dictionary<ViewPreset, (float RadiusM, float HorizonM, float LodPixels, int BudgetMB)> Presets = new()
	{
		[ViewPreset.Low] = (300, 1200, 4, 1536), [ViewPreset.Medium] = (600, 2400, 3, 3072),
		[ViewPreset.High] = (900, 4800, 2, 4096), [ViewPreset.Ultra] = (1200, 9600, 2, 6144),
	};

	[Export] public Camera3D Camera;
	// L0 chunk the camera's Godot position is relative to (docs/v2.md §3.1); Rebase keeps that position inside it.
	public Vector3I Anchor;
	[Export] public string Generator = "flat";
	public JsonElement? GeneratorParams;
	public float TimeOfDay = 15f;
	public int Day;
	public bool TimePaused = true;
	[Setting(MenuTab.Look, "Preset", 1, 1200, 1, Label = "Day length (s per 24 h)")] public float DayLength = 30f;
	public double Clock; // s the time ran: cloud wind, star twinkle, texture animation
	[Setting(MenuTab.World, "Range and LOD (changes regenerate the world)", Regenerate = true, Label = "Range preset")] public ViewPreset View = ViewPreset.Ultra;
	[Setting(MenuTab.World, "Range and LOD (changes regenerate the world)", Regenerate = true, Label = "LOD (levels of detail to the horizon)")] public bool LodOn;             // --lod, menu, bookmarks: levels of detail to the horizon; off = L0 only to the preset's RadiusM, shaders without the level loop (LOD_ON 0)
	bool gpuLod;                   // LodOn the shaders were (are being) compiled for
	[Setting(MenuTab.World, "Range and LOD (changes regenerate the world)", 0, 20000, 300, Regenerate = true, Label = "Horizon (m over the preset, 0 = preset)")] public float HorizonM; // > 0: over the preset's
	[Setting(MenuTab.World, "Range and LOD (changes regenerate the world)", 0, 8, 0.5f, Regenerate = true, Label = "LodPixels (over the preset, 0 = preset)")] public float LodPixels;
	public int MaxLevels = Lod.MaxLevels; // --lod-levels (1 = L0 only), with LodOn
	public int LodRef; // --lod-ref: that level's band drawn one level finer (lod-vs-finer reference)
	public int DrawLevels; // > 0: rays read only that many levels (--lod-draw, coarse rings generated but not drawn)
	public int ClassifyPerFrame = 4096 * 15625 / Coords.ChunkVolume, FillPerFrame = 16384 * 125 / Layout.GroupCells, ColumnsPerFrame = 1024 * 625 / (Layout.Chunk * Layout.Chunk); // the volume of 4096 chunks, 16384 groups and 1024 columns of 25^3 / 5^3 blocks
	[Setting(MenuTab.World, "Streaming and budgets", 1, 256, 1, Label = "Edits (chunks per frame)")] public int EditChunksPerFrame = 64;
	[Setting(MenuTab.World, "Streaming and budgets", Label = "Fixed counts (per frame, deterministic)")] public bool FixedBudget;        // the counts above every frame (deterministic runs), else scaled by Budget
	[Setting(MenuTab.World, "Streaming and budgets", Label = "Freeze streaming")] public bool FreezeStreaming;    // the rings keep their centres (no shift, no teleport reset) so the camera can fly out over what is resident; Regenerate still resets at the camera
	[Setting(MenuTab.World, "Streaming and budgets", 0.25f, 8, 0.25f, Label = "GPU generation (ms per frame)")] public float GenTargetMs = 2;   // generation on the GPU per frame the adaptive budget aims at
	[Setting(MenuTab.World, "Streaming and budgets", 0.5f, 10, 0.5f, Label = "CPU streaming (ms per frame)")] public float StreamCpuMs = 3;   // main thread: popping the far queue stops for the frame this long after the ring shift (not with FixedBudget)
	public float Budget { get; private set; } = 1;
	public float NearRadius = 64, MidRadius = 300; // m: near kept complete (probe, loading screen), mid only probed (flights); the mid sphere is always L0
	public int WorldMB; // > 0: pools for this budget instead of the view preset's when it is smaller (--world-mb, flights' WorldMB)
	public int BudgetMB => WorldMB > 0 ? Math.Min(WorldMB, Presets[View].BudgetMB) : Presets[View].BudgetMB;
	public float ViewHorizonM => HorizonM > 0 ? HorizonM : LodOn ? Presets[View].HorizonM : Presets[View].RadiusM - Lod.Margin * Voxel.ChunkMeters;
	public float ViewLodPixels => LodPixels > 0 ? LodPixels : Presets[View].LodPixels;
	// Memory pressure (docs/v2.md §3.6, §5.17), by the fullest pool's share in use: from ShrinkTo only L0 within the mid sphere and the coarsest level are sent (FarGated), past the mid sphere a chunk may not take a pool's last 1/FarReserveDiv (gen.compute PACK), over ShrinkAt or gated for GatedHold frames the finer levels' radii shrink together, at their floor the coarsest's (the horizon); growth in reverse while the grown spheres would stay under GrowBelow.
	public const int FarReserveDiv = 10, MidMargin = 2; // MidMargin: chunks past the mid sphere that count as mid for the reserve, the gate and the job order (one about to enter, deferred or refused, would be missing for a frame)
	const float ShrinkAt = 0.85f, ShrinkTo = 0.78f, GrowBelow = 0.7f, Relayout = 1.1f; // Relayout: pixel angle change that lays the levels out again
	const int ShrinkHold = 12, GatedHold = 120, GrowHold = 90, GrowAfterShrink = 300, GrowStep = 2, EvictSweep = 1 << 16, EvictMax = 8192; // frames: readbacks lag, growth waits for a calm stretch; Evict*: slots walked and chunks released per frame (unseen first, then the radii shrink)
	public float Pressure { get; private set; }
	public bool FarGated { get; private set; }
	public int Shrinks { get; private set; }
	public int Grows { get; private set; }
	public LodLayout Lods { get; private set; }
	public float FarRadiusM => (stream?.Ring.Radius ?? 0) * Voxel.ChunkMeters; // L0
	public float FullRadiusM => (stream?.Ring.Reach ?? 0) * Voxel.ChunkMeters;
	public float HorizonNowM => streams == null ? 0 : streams[^1].Ring.Radius * Voxel.ChunkMeters * (1 << (streams.Length - 1));
	public float LevelRadiusM(int level) => streams == null || level >= streams.Length ? 0 : streams[level].Ring.Radius * Voxel.ChunkMeters * (1 << level);
	int sinceShrink, lowFrames, gatedFrames;
	bool settledLogged;
	readonly Stopwatch resetClock = new();
	public const int MaxEditsPerFrame = 1 << 18, LoadingBoost = 16, MaxSkipsPerFrame = 1 << 16, EnqueuePerFrame = 1 << 14, DrainSlice = 2048, CompactPerFrame = 8192, ShiftRows = 16384; // LoadingBoost: ceiling while loading (buffer sizes); then CPU caps per frame; ShiftRows: rows of a crossing's diff per frame and level (a new crossing finishes the old one)
	const float MinBudget = 0.25f, MaxBudget = 4, LoadingGenMs = 30, FillGenMs = 6, LoadHoles = 0.001f, LoadHoleSeconds = 3; // MaxBudget: the in-game ceiling; LoadingGenMs: generation target while the loading screen hides the frame (until the near and mid spheres are in and holes in view under LoadHoles or LoadHoleSeconds passed); FillGenMs: after it until the whole world is in
	public volatile float ViewHoles = 1; // share of the last frame's pixels that look through a missing chunk of the coarsest level (Primary/dda)
	bool filling;

	public readonly WorldEdits Edits = new();
	public BlockRegistry Registry { get; private set; }
	public BlockVariants Variants { get; private set; }
	public readonly Gpu Gpu = new();
	public readonly ModelStore Models = new(); // packed on the render thread in InitGpu
	public bool GpuReady => ready;
	public RingWindow Ring => stream?.Ring;
	public WorldGenerator GeneratorInstance => generator;
	// Render thread: shaders, prelude and the world set were made anew (another generator); passes compile again.
	public event Action Rebuilt;
	public event Action Regenerated; // main thread, every Regenerate: frame history of the old world is stale

	public int Queued => streams == null ? 0 : streams.Sum(s => s.Pending + s.Incoming + s.Shifts) + urgent.Count + deferredBack.Count;
	// Every wanted chunk went through generation (stored, uniform or failed).
	public bool Settled => ready && stream != null && (Queued == 0 || FarGated && OnlyFarQueued) && Volatile.Read(ref inFlight) == 0;
	bool OnlyFarQueued => stream.MidEntries + stream.Incoming + stream.Shifts + urgent.Count == 0 && deferredBack.IsEmpty && nearMissing == 0 && midMissing == 0; // what waits is past the mid sphere
	// After a reset (start, teleport, Regenerate) until the probe finds the near sphere complete.
	public bool QuickLoad; // played by hand: the loading screen only waits for the near sphere, the rest fills in play
	public bool Loading => ready && (stream == null || loading);
	public int NearMissing => nearMissing;
	public int MidMissing => midMissing;
	public long Redone => Interlocked.Read(ref redone); // entries generated over a resident one since start (edits, else a bookkeeping bug)
	public int NearTotal { get; private set; }
	public int SentLastFrame { get; private set; }
	public StreamFrame Frame { get; private set; } // the last NextStep, for the flight log
	public long StepAllocated { get; private set; } // bytes the last NextStep allocated (flight log)
	public int LevelPending(int level) => streams == null || level >= streams.Length ? 0 : streams[level].Pending + streams[level].Incoming;

	WorldGenerator generator;
	GpuBlockTable.Textures textures;
	ChunkStream stream; // L0
	ChunkStream[] streams; // per level
	HashSet<ChunkPos>[] coarseEdits = Array.Empty<HashSet<ChunkPos>>(); // per level: coarse chunks holding an edit (only grows until a reset)
	readonly List<ChunkPos> released = new();
	readonly List<LevelChunk> releasedAll = new();
	readonly List<Vector4I>[] cols = Enumerable.Range(0, Lod.MaxLevels).Select(_ => new List<Vector4I>()).ToArray();
	readonly List<Vector4I> colsAll = new();
	readonly List<JobGpu> jobList = new();
	readonly List<JobGpu> farJobs = new();
	readonly List<uint> edits = new();
	readonly bool[] exhausted = new bool[Lod.MaxLevels];
	Vector3I[] nearOffsets = Array.Empty<Vector3I>(); // the near sphere around the camera chunk, nearest first
	int rowsSent, epoch;
	bool resetRequested = true, loading = true, budgetBound;
	volatile bool ready;
	volatile int nearMissing = -1, midMissing = -1, nearEpoch = -1;
	long redone;
	int inFlight;
	readonly Queue<ChunkPos> urgent = new();
	readonly HashSet<ChunkPos> urgentSet = new();
	readonly ConcurrentQueue<(int Epoch, ChunkPos Chunk, int Level, bool Failed)> deferredBack = new(); // render thread -> main thread
	readonly ConcurrentQueue<(int Epoch, byte[] Bits, long[] SlotBase)> visibleBack = new(); // render thread -> main thread (ReadVisible)
	public int Evicted { get; private set; } // chunks released as unseen under memory pressure since the reset

	// Visibility bits per level into its stream's priority tiers (seen first) and eviction stamps.
	void ApplyVisible()
	{
		while (visibleBack.TryDequeue(out var v))
		{
			if (v.Epoch != epoch || streams == null || v.SlotBase.Length != streams.Length) continue;
			var words = MemoryMarshal.Cast<byte, uint>(v.Bits);
			for (int l = 0; l < streams.Length; l++)
			{
				int first = (int)(v.SlotBase[l] / 32), count = (int)((streams[l].Ring.Slots + 31) / 32);
				if (first + count <= words.Length) streams[l].MarkSeen(words.Slice(first, count));
			}
		}
	}

	// Bytes for one BufferUpdate, rented from the shared pool and returned after the frame's upkeep (no large allocations per frame).
	public readonly record struct Upload(byte[] Data, int Length)
	{
		public static Upload From<T>(List<T> items) where T : struct
		{
			var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(items));
			var data = ArrayPool<byte>.Shared.Rent(Math.Max(bytes.Length, 1));
			bytes.CopyTo(data);
			return new Upload(data, bytes.Length);
		}

		public void Return() { if (Data != null) ArrayPool<byte>.Shared.Return(Data); }
	}

	// Reused round robin (NextStep): the render thread is never this many frames behind.
	readonly Step[] steps = { new(), new(), new(), new() };
	int nextStep;

	public sealed class Step
	{
		public void Clear()
		{
			(Reset, Lods, Rings, Gen, BlockRows, Columns, Jobs, EditRecords, Released, ColumnCount, JobCount, FillCap, Epoch, WorkBoost, Camera, NearChunks, MidChunks, DrawLevels) = (null, null, null, null, null, default, default, default, default, 0, 0, 0, 0, 0, default, 0, 0, 0);
			Array.Clear(LevelColumns);
		}

		public RingWindow[] Reset, Rings; // per level
		public LodLayout Lods;
		public WorldGenerator Gen;
		public (int First, byte[] Rows)? BlockRows;
		public Upload Columns, Jobs, EditRecords, Released;
		public int ColumnCount, JobCount, FillCap, Epoch, WorkBoost;
		public readonly int[] LevelColumns = new int[Lod.MaxLevels]; // Columns is ordered by level
		public ChunkPos Camera;
		public float NearChunks;
		public int MidChunks, DrawLevels;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct JobGpu // gen.compute Job
	{
		public Vector4I Chunk; // w = JobState
		public uint EditFirst, EditCount, Pad0, Pad1;
		public uint Count0, Count1, Count2, Solid;
		public uint Rec, Base0, Base1, Level;
	}

	WorldGenerator CreateGenerator() => WorldGenerator.Create(Generator, GeneratorParams);

	public override void _Ready()
	{
		Registry = BlockRegistry.Load(ProjectSettings.GlobalizePath("res://" + ContentPaths.Mod(BlockId.BaseMod))); // ponytail: base mod only
		Variants = new BlockVariants(Registry, 1 << Layout.FlagShift);
		Variants.ReserveDefaults();
		textures = GpuBlockTable.Load(Registry);
		generator = CreateGenerator();
		ProcessPriority = -1;
		gpuLod = LodOn;
		RenderingServer.CallOnRenderThread(Callable.From(InitGpu));
	}

	public override void _Process(double delta)
	{
		if (TimePaused) return;
		Clock += delta;
		float t = TimeOfDay + (float)delta * 24f / DayLength;
		Day += (int)(t / 24f);
		TimeOfDay = t % 24f;
	}

	public override void _ExitTree()
	{
		ready = false;
		RenderingServer.CallOnRenderThread(Callable.From(FreeGpu));
	}

	// World metres, float: for spawning and the HUD, rays use Anchor and the local position.
	public Vector3 CameraPosition
	{
		get => (Vector3)Anchor * Voxel.ChunkMeters + Camera.GlobalPosition;
		set
		{
			Anchor = (Vector3I)(value / Voxel.ChunkMeters).Floor();
			Camera.GlobalPosition = value - (Vector3)Anchor * Voxel.ChunkMeters;
		}
	}

	public ChunkPos CameraChunk
	{
		get
		{
			var c = Anchor + (Vector3I)(Camera.GlobalPosition / Voxel.ChunkMeters).Floor();
			return new ChunkPos(c.X, c.Y, c.Z);
		}
	}

	const float ViewMarginDeg = 10;

	// The queue's view cone (ChunkStream.Look): camera forward and the cosine of the frustum's half diagonal plus a margin.
	(System.Numerics.Vector3 Forward, float Cos) ViewCone()
	{
		var f = -Camera.GlobalBasis.Z;
		float aspect = GetViewport().GetVisibleRect().Size.Aspect(), half = Mathf.Atan(Mathf.Tan(Mathf.DegToRad(Camera.Fov) / 2) * Mathf.Sqrt(1 + aspect * aspect)) + Mathf.DegToRad(ViewMarginDeg);
		return (new(f.X, f.Y, f.Z), half >= Mathf.Pi ? -1 : Mathf.Cos(half));
	}

	// Radians per pixel (Godot's fov is vertical) of the screen the window is on, or of the window when it is taller: the levels' footprint (docs/v2.md §5.17); fullscreen and window changes on one screen keep the layout.
	float PixelAngle() => 2 * Mathf.Tan(Mathf.DegToRad(Camera.Fov) / 2) / Math.Max(Math.Max(GetWindow().Size.Y, DisplayServer.ScreenGetSize(GetWindow().CurrentScreen).Y), 1);

	// After the camera moved: its whole chunks go into Anchor.
	public void Rebase()
	{
		var c = (Vector3I)(Camera.GlobalPosition / Voxel.ChunkMeters).Floor();
		if (c == Vector3I.Zero) return;
		Anchor += c;
		Camera.GlobalPosition -= (Vector3)c * Voxel.ChunkMeters;
	}

	public float SurfaceHeight(float x, float z) => generator.SurfaceHeight(x, z);

	// New generator or parameters: the spheres are generated again around the camera.
	public void Regenerate()
	{
		var old = generator;
		generator = CreateGenerator();
		resetRequested = true;
		Regenerated?.Invoke();
		if (old == null || ShaderKey(generator) == ShaderKey(old) && gpuLod == LodOn) return;
		(ready, gpuLod) = (false, LodOn);
		RenderingServer.CallOnRenderThread(Callable.From(Rebuild));
	}

	// Edits and generator of a saved world (region v3); the view stays with the caller.
	public void Load(WorldSave save)
	{
		var info = save.Header.Dimensions[save.Header.Player.Dimension];
		(Generator, GeneratorParams) = (info.Generator, info.Params);
		Edits.Clear();
		save.Load(Registry, Edits, save.Header.Player.Dimension);
		Regenerate();
	}

	// The chunk is generated again with priority -1 (docs/v2.md §4.4), the coarse chunks over it with their normal priority; a queued one picks the edit up anyway.
	public void SetBlock(BlockPos p, BlockState s)
	{
		Variants.Id(s);
		Edits.Set(p, s);
		if (stream?.State(p.Chunk) == SlotState.Sent && urgentSet.Add(p.Chunk)) urgent.Enqueue(p.Chunk);
		for (int l = 1; l < coarseEdits.Length; l++)
		{
			var c = Lod.Parent(p.Chunk, l);
			coarseEdits[l].Add(c);
			if (streams[l].State(c) == SlotState.Sent) streams[l].Requeue(c);
		}
	}

	int Gate(int level) => !FarGated ? int.MaxValue : level == 0 ? stream.Mid.Radius : level == streams.Length - 1 ? int.MaxValue : -1;

	// Main thread, after the camera moved: reset or ring shifts (a crossing's diff spread over frames, outside the pop budget so it does not starve the pops), this frame's generation work within the budgets, then the block rows its edits registered (docs/v2.md §4.3).
	public Step NextStep()
	{
		if (!ready) return null;
		long allocStart = GC.GetAllocatedBytesForCurrentThread();
		long t0 = Stopwatch.GetTimestamp(), cpuEnd = long.MaxValue;
		bool OverCpu() => !FixedBudget && !loading && Stopwatch.GetTimestamp() > cpuEnd;
		var f = new StreamFrame();
		var step = steps[nextStep++ % steps.Length];
		step.Clear();
		var cam = FreezeStreaming && stream != null && !resetRequested ? stream.Ring.Center : CameraChunk;
		float nearChunks = NearRadius / Voxel.ChunkMeters, midChunks = MidRadius / Voxel.ChunkMeters;
		int ClassOf(ChunkPos c, int level)
		{
			if (level > 0) return 2;
			long dx = c.X - cam.X, dy = c.Y - cam.Y, dz = c.Z - cam.Z, d2 = dx * dx + dy * dy + dz * dz;
			return d2 <= nearChunks * nearChunks ? 0 : d2 <= (midChunks + MidMargin) * (midChunks + MidMargin) ? 1 : 2; // mid with its margin (queued first, never throttled)
		}
		var cone = ViewCone();
		if (streams != null) foreach (var s in streams) s.Look(cone.Forward, cone.Cos);
		while (deferredBack.TryDequeue(out var d))
		{
			if (d.Epoch != epoch) continue;
			if (d.Failed) { streams?[d.Level].Fail(d.Chunk); f.Failed++; continue; }
			streams?[d.Level].Requeue(d.Chunk);
			f.Requeued++;
			if (ClassOf(d.Chunk, d.Level) == 2) f.RequeuedFar++;
		}
		float alpha = PixelAngle();
		bool relayout = Lods is { Levels: > 1 } && !FreezeStreaming && (alpha > Lods.PixelAngle * Relayout || alpha * Relayout < Lods.PixelAngle);
		if (resetRequested || relayout || stream == null || Jumped(cam)) Reset(step, cam, cone, alpha);
		else
		{
			ApplyVisible();
			releasedAll.Clear();
			int incoming = streams.Sum(s => s.Incoming);
			released.Clear();
			for (int l = 0; l < streams.Length; l++)
			{
				var s = streams[l];
				var c = Lod.Parent(cam, l);
				if (c != s.Ring.Center) s.Move(c, released, ShiftRows);
				else s.Advance(released, ShiftRows);
				Collect(l);
			}
			Regulate(f.Failed);
			(f.Released, f.Entered) = (releasedAll.Count, streams.Sum(s => s.Incoming) - incoming);
			if (releasedAll.Count > 0) step.Released = Upload.From(releasedAll);
		}
		foreach (var s in streams)
		{
			s.Now = (int)Engine.GetProcessFrames();
			s.Retry();
		}
		long t1 = Stopwatch.GetTimestamp();
		cpuEnd = t1 + (long)(StreamCpuMs * Stopwatch.Frequency / 1000);
		bool firstImage = LodOn && ViewHoles <= LoadHoles || resetClock.Elapsed.TotalSeconds > LoadHoleSeconds; // the coarse levels in view, or a short wait for them (without LOD: L0 settled or that wait)
		if (loading && nearEpoch == epoch && nearMissing == 0 && (QuickLoad || midMissing == 0 && stream.MidEntries == 0 && (Settled || firstImage)))
		{
			loading = false;
			GD.Print($"[world] loaded {resetClock.Elapsed.TotalSeconds:0.00} s after the reset (holes in view {ViewHoles:P2})");
		}
		if (!loading && filling && Settled) filling = false;
		if (!settledLogged && Settled && !loading) { settledLogged = true; GD.Print($"[world] settled {resetClock.Elapsed.TotalSeconds:0.00} s after the reset: pools {string.Join(" ", Enum.GetValues<WorldPool>().Select(p => $"{p} {PoolBytes(p) / 1048576.0:0}"))} MB, fixed {FixedBytes / 1048576.0:0} MB, features dropped {FeaturesDropped} / {GroupFeaturesDropped}, evicted {Evicted}"); }
		Budget = NextBudget(f.Requeued > 0);
		step.WorkBoost = loading || filling ? LoadingBoost : (int)MaxBudget;
		int classify = (int)(ClassifyPerFrame * Budget), columns = (int)(ColumnsPerFrame * Budget), colCount = 0;
		step.Rings = streams.Select(s => s.Ring).ToArray();
		step.DrawLevels = DrawLevels > 0 ? Math.Min(DrawLevels, streams.Length) : streams.Length;
		(step.Lods, step.Epoch, step.Camera, step.NearChunks, step.MidChunks, step.FillCap) = (Lods, epoch, cam, NearRadius / Voxel.ChunkMeters, (int)(MidRadius / Voxel.ChunkMeters), (int)(FillPerFrame * Budget));

		foreach (var c in cols) c.Clear();
		jobList.Clear();
		edits.Clear();
		bool anyEdits = Edits.Count + Edits.FluidCount > 0;
		long farAges = 0;
		bool Add(ChunkPos c, int level)
		{
			var s = streams[level];
			bool column = !s.HasColumn(c);
			if (column && colCount >= columns) return false;
			int editFirst = edits.Count / 4;
			if (anyEdits) EditRecords(c, level, edits);
			if (edits.Count / 4 > MaxEditsPerFrame && jobList.Count > 0) { edits.RemoveRange(editFirst * 4, edits.Count - editFirst * 4); return false; }
			if (column) { s.SetColumn(c); cols[level].Add(new Vector4I(c.X, c.Z, level, 0)); colCount++; }
			int cls = ClassOf(c, level);
			if (cls == 2 && level == 0) { int age = s.Age(c); farAges += age; f.FarAgeMax = Math.Max(f.FarAgeMax, age); }
			if (level > 0) f.SentLod++;
			else (cls == 0 ? ref f.SentNear : ref cls == 1 ? ref f.SentMid : ref f.SentFar)++;
			jobList.Add(new JobGpu { Chunk = new(c.X, c.Y, c.Z, 0), EditFirst = (uint)editFirst, EditCount = (uint)(edits.Count / 4 - editFirst), Level = (uint)level });
			return true;
		}
		bool full = false;
		for (int n = 0; n < EditChunksPerFrame && urgent.Count > 0 && !(full = jobList.Count >= classify); n++)
		{
			var c = urgent.Peek();
			if (stream.State(c) == SlotState.Sent && stream.Ring.Wanted(c) && !Add(c, 0)) { full = true; break; }
			urgentSet.Remove(urgent.Dequeue());
		}
		long t2 = Stopwatch.GetTimestamp();
		foreach (var d in nearOffsets)
		{
			var c = new ChunkPos(cam.X + d.X, cam.Y + d.Y, cam.Z + d.Z);
			if (full || (full = jobList.Count >= classify)) break;
			if (stream.Claim(c) && !Add(c, 0)) { stream.Requeue(c); full = true; }
		}
		long t3 = Stopwatch.GetTimestamp();
		int skips = MaxSkipsPerFrame;
		Array.Clear(exhausted);
		int midCap = Math.Max(classify, (int)(ClassifyPerFrame * MaxBudget)); // the mid sphere is never throttled by expensive coarse work
		bool Full() => full = jobList.Count >= (stream.MidEntries > 0 ? midCap : classify) || (jobList.Count & 255) == 0 && stream.MidEntries == 0 && OverCpu();
		while (!full && !Full())
		{
			int best = -1, key = int.MaxValue, next = int.MaxValue; // the level to pop from (coarser first on a tie: a parent before its children) while its key stays within the next one's
			for (int l = streams.Length - 1; l >= 0; l--)
			{
				int k = exhausted[l] ? int.MaxValue : streams[l].NextKey(Gate(l));
				if (k < key) (best, key, next) = (l, k, key);
				else if (k < next) next = k;
			}
			if (best < 0 || skips <= 0) break;
			var s = streams[best];
			int gate = Gate(best);
			do
			{
				if (!s.TryPop(out var c, ref skips, gate)) { exhausted[best] = true; break; }
				if (!Add(c, best)) { s.Requeue(c); full = true; break; }
			} while (!Full() && (next == int.MaxValue || s.NextKey(gate) <= next));
		}
		long t4 = Stopwatch.GetTimestamp();
		int incomingBefore = streams.Sum(s => s.Incoming);
		foreach (var s in streams)
		{
			int due = Math.Max(DrainSlice, s.DrainDue);
			for (int n = 0; s.Incoming > 0 && (n < due || n < EnqueuePerFrame && !OverCpu()); n += DrainSlice) s.Drain(DrainSlice);
			if (s.NeedsCompact) s.Compact(CompactPerFrame);
		}
		f.Drained = incomingBefore - streams.Sum(s => s.Incoming);
		long t4d = Stopwatch.GetTimestamp();
		budgetBound = full;
		if (f.SentNear + f.SentMid > 0) step.FillCap = Math.Max(step.FillCap, (int)(FillPerFrame * MaxBudget));
		SentLastFrame = jobList.Count;
		FarLast();
		if (jobList.Count > 0)
		{
			colsAll.Clear();
			for (int l = 0; l < cols.Length; l++) { colsAll.AddRange(cols[l]); step.LevelColumns[l] = cols[l].Count; }
			(step.Columns, step.Jobs) = (Upload.From(colsAll), Upload.From(jobList));
			if (edits.Count > 0) step.EditRecords = Upload.From(edits);
			(step.ColumnCount, step.JobCount) = (colsAll.Count, jobList.Count);
			Interlocked.Increment(ref inFlight);
		}
		long t5 = Stopwatch.GetTimestamp();
		static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
		(f.Queue, f.Sent, f.Columns, f.FillCap, f.Budget) = (Queued, jobList.Count, colCount, step.FillCap, Budget);
		(f.MoveMs, f.NearMs, f.PopMs, f.DrainMs, f.UploadMs, f.StepMs) = (Ms(t0, t1), Ms(t2, t3), Ms(t3, t4), Ms(t4, t4d), Ms(t4d, t5), Ms(t0, t5));
		int nb = (int)nearChunks + 1, mb = (int)midChunks + 1;
		(f.Incoming, f.Pending, f.QNear, f.QMid, f.QFar) = (streams.Sum(s => s.Incoming), streams.Sum(s => s.Pending), stream.Entries(0, nb), stream.Entries(nb, mb) + stream.MidEntries, stream.Entries(mb, int.MaxValue) + streams.Skip(1).Sum(s => s.Count));
		(f.Holes, f.Retrying) = (streams.Sum(s => s.Failed), streams.Sum(s => s.Retrying));
		(f.Stale, f.FarAge) = (f.QNear + f.QMid + f.QFar - f.Pending, f.SentFar == 0 ? 0 : (float)farAges / f.SentFar);
		Frame = f;
		step.BlockRows = NewBlockRows();
		StepAllocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;
		return step;
	}

	// Jobs past the mid sphere (and coarse ones) after the others (stable): FILL defers from the first job that does not fit on, so a near or mid chunk popped late (a queue entry from when it was far) is never deferred for far ones.
	void FarLast()
	{
		farJobs.Clear();
		int k = 0;
		float mid = MidRadius / Voxel.ChunkMeters + MidMargin;
		var cam = stream.Ring.Center;
		for (int i = 0; i < jobList.Count; i++)
		{
			var j = jobList[i];
			long dx = j.Chunk.X - cam.X, dy = j.Chunk.Y - cam.Y, dz = j.Chunk.Z - cam.Z;
			if (j.Level > 0 || dx * dx + dy * dy + dz * dz > mid * mid) farJobs.Add(j);
			else jobList[k++] = j;
		}
		if (farJobs.Count == 0 || k == 0) return;
		jobList.RemoveRange(k, jobList.Count - k);
		jobList.AddRange(farJobs);
	}

	// Multiplier of the per-frame counts (docs/v2.md §4.3): with FixedBudget LoadingBoost while loading, else 1; otherwise it grows while the counts limit the work (or the GPU deferred) and generation stays under the target, and shrinks over it. Loading aims at LoadingGenMs within MaxBudget-LoadingBoost, the game at GenTargetMs up to MaxBudget. GenMs < 0 (no timestamps came back, the GPU is frames behind): hold.
	float NextBudget(bool deferred)
	{
		if (FixedBudget) return loading ? LoadingBoost : 1;
		float target = loading ? LoadingGenMs : filling ? FillGenMs : GenTargetMs, ms = GenMs < 0 ? target : GenMs, k = Budget;
		if (ms > target) k *= Math.Max(0.5f, target / ms);
		else if (budgetBound || deferred) k *= Math.Min(1.25f, target / Math.Max(ms, 0.05f));
		return Math.Clamp(k, loading ? MaxBudget : MinBudget, loading || filling ? LoadingBoost : MaxBudget);
	}

	// A move the ring shift should not walk (teleport): released and refilled as a whole.
	bool Jumped(ChunkPos c) => Math.Max(Math.Abs(c.X - stream.Ring.Center.X), Math.Max(Math.Abs(c.Y - stream.Ring.Center.Y), Math.Abs(c.Z - stream.Ring.Center.Z))) > stream.Ring.Reach / 2;

	// The spheres' radii by the fullest pool (async counters of this reset). Over ShrinkAt (or a failed chunk over ShrinkTo), or gated for GatedHold frames before the queue emptied once: the finer levels to the sphere that holds what is generated (L0's share of its wanted chunks, by volume),
	// scaled to ~ShrinkTo (surface terrain: memory ~ radius^2), at least one chunk less, L0 never inside the mid sphere; at that floor the coarsest level (the horizon) the same way. Growth in reverse (the horizon first) needs an empty queue (the measure is final) and room for the grown sphere under GrowBelow for GrowHold frames, GrowAfterShrink after a shrink.
	// Released shells are NONE on the GPU: rays go coarser there, past the coarsest it is sky.
	void Regulate(int failed)
	{
		if (nearEpoch != epoch) return;
		float p = 0;
		for (int i = 0; i < 4; i++) if (PoolCap[i] > 0) p = Math.Max(p, (float)PoolUsed[i] / PoolCap[i]);
		(Pressure, FarGated) = (p, p >= ShrinkTo);
		int last = streams.Length - 1, pending = streams.Sum(s => s.Pending);
		var ring = stream.Ring;
		int min = Math.Min(ring.Reach, Mathf.CeilToInt(MidRadius / Voxel.ChunkMeters) + MidMargin);
		bool fineAtFloor = ring.Radius <= min;
		sinceShrink++;
		gatedFrames = pending == 0 ? 0 : FarGated ? gatedFrames + 1 : gatedFrames; // gated frames until the queue empties once
		bool over = (p > ShrinkAt || failed > 0 && p > ShrinkTo) && sinceShrink > ShrinkHold;
		static int Shrunk(ChunkStream s, float p)
		{
			var r = s.Ring;
			double wanted = 4 / 3.0 * Math.PI * r.Radius * r.Radius * r.RadiusY, done = Math.Clamp(1 - s.Pending / wanted, 0.05, 1);
			return (int)(r.Radius * Math.Cbrt(done) * Math.Sqrt(ShrinkTo / Math.Max(p, ShrinkTo)));
		}
		if (over || gatedFrames > GatedHold)
		{
			int evicted = 0, far = Mathf.CeilToInt(MidRadius / Voxel.ChunkMeters) + MidMargin;
			for (int l = 0; l < Math.Max(last, 1); l++)
			{
				evicted += streams[l].Evict(l == 0 ? far : 0, EvictSweep, EvictMax, released);
				Collect(l);
			}
			if (evicted > 0)
			{
				Evicted += evicted;
				(lowFrames, gatedFrames) = (0, 0);
				return;
			}
			bool shrunk = false;
			if (!fineAtFloor)
			{
				int r0 = Math.Clamp(Shrunk(stream, p), min, ring.Radius - 1);
				ScaleFine(r0, last);
				shrunk = true;
			}
			else if (last > 0)
			{
				var c = streams[last];
				int floor = Mathf.CeilToInt(LevelRadiusM(last - 1) / (Voxel.ChunkMeters * (1 << last))) + Lod.Margin;
				if (c.Ring.Radius > floor) { c.Resize(Math.Clamp(Shrunk(c, p), floor, c.Ring.Radius - 1), released, ShiftRows); Collect(last); shrunk = true; }
			}
			if (shrunk)
			{
				(sinceShrink, lowFrames, gatedFrames) = (0, 0, 0);
				Shrinks++;
				return;
			}
		}
		var grow = last > 0 && streams[last].Ring.Radius < streams[last].Ring.Reach ? streams[last] : ring.Radius < ring.Reach ? stream : null;
		if (grow == null) { lowFrames = 0; return; }
		float grown = (grow.Ring.Radius + GrowStep) / (float)grow.Ring.Radius;
		if (p * grown * grown >= GrowBelow) lowFrames = 0;
		else if (pending == 0) lowFrames++;
		if (lowFrames <= GrowHold || Shrinks > 0 && sinceShrink <= GrowAfterShrink) return;
		if (grow == stream) ScaleFine(ring.Radius + GrowStep, last);
		else { grow.Resize(grow.Ring.Radius + GrowStep, released, ShiftRows); Collect(last); }
		lowFrames = 0;
		Grows++;
	}

	// The finer levels (all but the coarsest, or L0 alone) at L0 radius r0 and the same share of their reach.
	void ScaleFine(int r0, int last)
	{
		float s = r0 / (float)stream.Ring.Reach;
		for (int l = 0; l < Math.Max(last, 1); l++)
		{
			var st = streams[l];
			st.Resize(l == 0 ? r0 : Math.Max(Lod.Margin + 1, (int)MathF.Round(s * st.Ring.Reach)), released, ShiftRows);
			Collect(l);
		}
	}

	// A Resize's released chunks of level l into this frame's release list.
	void Collect(int l)
	{
		foreach (var r in released) releasedAll.Add(new LevelChunk(r.X, r.Y, r.Z, l));
		released.Clear();
	}

	// Everything is released on the GPU and queued again nearest first; the loading screen stays until the near sphere is in.
	void Reset(Step step, ChunkPos cam, (System.Numerics.Vector3 Forward, float Cos) cone, float alpha)
	{
		resetRequested = false;
		settledLogged = false;
		resetClock.Restart();
		epoch++;
		Lods = Lod.Plan(alpha, ViewLodPixels, ViewHorizonM, LodOn ? MidRadius + MidMargin * Voxel.ChunkMeters : 0, LodOn ? MaxLevels : 1, LodRef);
		streams = new ChunkStream[Lods.Levels];
		for (int l = 0; l < streams.Length; l++)
			streams[l] = new ChunkStream(new RingWindow(Lod.Parent(cam, l), Lods.Radius[l], 1f), l == 0 ? (int)(MidRadius / Voxel.ChunkMeters) + MidMargin : 0, cone.Forward, cone.Cos);
		stream = streams[0];
		coarseEdits = Enumerable.Range(0, streams.Length).Select(_ => new HashSet<ChunkPos>()).ToArray();
		foreach (var c in Edits.Chunks)
			for (int l = 1; l < streams.Length; l++) coarseEdits[l].Add(Lod.Parent(c, l));
		(Pressure, FarGated, Shrinks, Grows, sinceShrink, lowFrames, gatedFrames) = (0, false, 0, 0, 0, 0, 0);
		(step.Reset, step.Gen) = (streams.Select(s => s.Ring).ToArray(), generator);
		urgent.Clear();
		urgentSet.Clear();
		(loading, filling, ViewHoles, Evicted) = (true, true, 1, 0);
		float near = NearRadius / Voxel.ChunkMeters;
		int k = Mathf.CeilToInt(near);
		var offsets = new List<Vector3I>();
		for (int z = -k; z <= k; z++) for (int y = -k; y <= k; y++) for (int x = -k; x <= k; x++) if (x * x + y * y + z * z <= near * near) offsets.Add(new(x, y, z));
		nearOffsets = offsets.OrderBy(o => o.LengthSquared()).ToArray();
		NearTotal = nearOffsets.Length;
		GD.Print($"[world] levels {Lods.Levels}, from {string.Join(" / ", Lods.SwitchM.Select(m => m.ToString("0")))} m, radius {string.Join(" / ", Enumerable.Range(0, Lods.Levels).Select(l => Lods.RadiusM(l).ToString("0")))} m (pixel {alpha * 1e4:0.00}e-4 rad, LodPixels {Lods.LodPixels})");
	}

	// Edits of chunk c of level `level` as gen.compute reads them, sorted: (group * GroupCells + cell, variant or ~0, fluid word or ~0, 0); at L >= 1 the key is times 8 + the sample (Lod.SampleOf), edits off the samples are left out.
	void EditRecords(ChunkPos c, int level, List<uint> into)
	{
		if (level > 0 ? !coarseEdits[level].Contains(c) : !Edits.Has(c)) return; // most chunks have no edits: no per-chunk sort and allocation
		int n = 1 << level, size = Layout.Chunk * n, g = Layout.Group, ga = Layout.GroupsPerAxis;
		var cells = new SortedDictionary<uint, (uint Block, uint Fluid)>();
		const uint none = uint.MaxValue;
		uint Key(BlockPos p)
		{
			int x = p.X - c.X * size, y = p.Y - c.Y * size, z = p.Z - c.Z * size, cx = x >> level, cy = y >> level, cz = z >> level;
			uint k = (uint)((cx / g + ga * (cy / g + ga * (cz / g))) * Layout.GroupCells + cx % g + g * (cy % g + g * (cz % g)));
			if (level == 0) return k;
			int s = Lod.SampleOf(level, x & (n - 1), y & (n - 1), z & (n - 1));
			return s < 0 ? none : k * Lod.Samples + (uint)s;
		}
		for (int dz = 0; dz < n; dz++)
			for (int dy = 0; dy < n; dy++)
				for (int dx = 0; dx < n; dx++)
				{
					var e = new ChunkPos(c.X * n + dx, c.Y * n + dy, c.Z * n + dz);
					foreach (var (p, s) in Edits.InChunk(e)) if (Key(p) is var k && k != none) cells[k] = ((uint)Variants.Id(s), none);
					foreach (var (p, fl) in Edits.FluidsInChunk(e))
					{
						uint k = Key(p);
						if (k == none) continue;
						uint w = fl.Type == 0 ? 0 : (uint)Variants.Id(Registry[fl.Type].Default) | (uint)fl.Level << Layout.FluidLevelShift;
						cells[k] = (cells.TryGetValue(k, out var old) ? old.Block : none, w);
					}
				}
		foreach (var (k, e) in cells) into.AddRange(new[] { k, e.Block, e.Fluid, 0u });
	}

	(int, byte[])? NewBlockRows()
	{
		int n = Variants.Count;
		if (n == rowsSent) return null;
		var rows = (rowsSent, GpuBlockTable.Rows(Registry, Variants.From(rowsSent), textures, Models.Looks));
		rowsSent = n;
		return rows;
	}


	// Render thread, after the readback of a generation frame.
	void Generated(int stepEpoch, IEnumerable<(ChunkPos Chunk, int Level, bool Failed)> deferred)
	{
		foreach (var (c, l, failed) in deferred) deferredBack.Enqueue((stepEpoch, c, l, failed));
		Interlocked.Decrement(ref inFlight);
	}

	// Render thread, after the readback of a frame's counters: the near probe of that frame.
	void Probed(int stepEpoch, int near, int mid, int redo)
	{
		(nearMissing, midMissing, nearEpoch) = (near, mid, stepEpoch);
		Interlocked.Add(ref redone, redo);
	}
}
