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

// User draw distance: the sphere's radius and the memory budget.
public enum ViewPreset { Low, Medium, High, Ultra }

// Counts and CPU ms of one NextStep (flight frame log, docs/v2-mereni.md).
public struct StreamFrame
{
	public int Queue, Sent, Columns, FillCap, Released, Entered, Requeued, Failed, Holes, Retrying, Incoming, Drained, Pending, Stale, QNear, QMid, QFar, SentNear, SentMid, SentFar, RequeuedFar, FarAgeMax;
	public float FarAge; // mean frames a far chunk sent this frame waited since it was queued
	public float Budget;
	public double MoveMs, NearMs, PopMs, DrainMs, UploadMs, StepMs;
}

// The world: settings, the sphere of chunks streamed around the camera (docs/v2.md §4), edits; main thread here, GPU side in World.Gpu.
public partial class World : Node3D
{
	public static readonly Dictionary<ViewPreset, (float RadiusM, int BudgetMB)> Presets = new()
	{
		[ViewPreset.Low] = (300, 1536), [ViewPreset.Medium] = (600, 3072),
		[ViewPreset.High] = (900, 4096), [ViewPreset.Ultra] = (1200, 6144),
	};
	public const int MaxRadius = 160; // chunks (a ring of 324^3 slots, 0.27 GB of directory)

	[Export] public Camera3D Camera;
	// Chunk the camera's Godot position is relative to (docs/v2.md §3.1); Rebase keeps that position inside it.
	public Vector3I Anchor;
	[Export] public string Generator = "flat";
	public JsonElement? GeneratorParams;
	public float TimeOfDay = 15f;
	public int Day;
	public bool TimePaused = true;
	[Setting(MenuTab.Look, "Preset", 1, 1200, 1, Label = "Day length (s per 24 h)")] public float DayLength = 30f;
	public double Clock; // s the time ran: cloud wind, star twinkle, texture animation
	[Setting(MenuTab.World, "Range (changes regenerate the world)", Regenerate = true, Label = "Range preset")] public ViewPreset View = ViewPreset.Ultra;
	public int ClassifyPerFrame = 4096 * 15625 / Coords.ChunkVolume, FillPerFrame = 16384 * 125 / Layout.GroupCells, ColumnsPerFrame = 1024 * 625 / (Layout.Chunk * Layout.Chunk); // the volume of 4096 chunks, 16384 groups and 1024 columns of 25^3 / 5^3 blocks
	[Setting(MenuTab.World, "Streaming and budgets", 1, 256, 1, Label = "Edits (chunks per frame)")] public int EditChunksPerFrame = 64;
	[Setting(MenuTab.World, "Streaming and budgets", Label = "Fixed counts (per frame, deterministic)")] public bool FixedBudget;        // the counts above every frame (deterministic runs), else scaled by Budget
	[Setting(MenuTab.World, "Streaming and budgets", Label = "Freeze streaming")] public bool FreezeStreaming;    // the ring keeps its centre (no shift, no teleport reset) so the camera can fly out over what is resident; Regenerate still resets at the camera
	[Setting(MenuTab.World, "Streaming and budgets", 0.25f, 8, 0.25f, Label = "GPU generation (ms per frame)")] public float GenTargetMs = 2;   // generation on the GPU per frame the adaptive budget aims at
	[Setting(MenuTab.World, "Streaming and budgets", 0.5f, 10, 0.5f, Label = "CPU streaming (ms per frame)")] public float StreamCpuMs = 3;   // main thread: popping the far queue stops for the frame this long after the ring shift (not with FixedBudget)
	public float Budget { get; private set; } = 1;
	public float NearRadius = 64, MidRadius = 300; // m: near kept complete (probe, loading screen), mid only probed (flights)
	public int WorldMB; // > 0: pools for this budget instead of the view preset's when it is smaller (--world-mb, flights' WorldMB)
	public int BudgetMB => WorldMB > 0 ? Math.Min(WorldMB, Presets[View].BudgetMB) : Presets[View].BudgetMB;
	int ViewRadius => Math.Min((int)MathF.Ceiling(Presets[View].RadiusM / Voxel.ChunkMeters), MaxRadius);
	// Memory pressure (docs/v2.md §3.6), by the fullest pool's share in use: from ShrinkTo only the mid sphere is sent (FarGated), past the mid sphere a chunk may not take a pool's last 1/FarReserveDiv (gen.compute PACK), over ShrinkAt or gated for GatedHold frames the radius shrinks; growth in reverse while the grown sphere would stay under GrowBelow.
	public const int FarReserveDiv = 10, MidMargin = 2; // MidMargin: chunks past the mid sphere that count as mid for the reserve, the gate and the job order (one about to enter, deferred or refused, would be missing for a frame)
	const float ShrinkAt = 0.85f, ShrinkTo = 0.78f, GrowBelow = 0.7f;
	const int ShrinkHold = 12, GatedHold = 120, GrowHold = 90, GrowAfterShrink = 300, GrowStep = 2, EvictSweep = 1 << 16, EvictMax = 8192; // frames: readbacks lag, growth waits for a calm stretch; Evict*: slots walked and chunks released per frame (unseen first, then the radius shrinks)
	public float Pressure { get; private set; }
	public bool FarGated { get; private set; }
	public int Shrinks { get; private set; }
	public int Grows { get; private set; }
	public (int Y0, int Y1) ChunkRows { get; private set; } // the generator's chunk rows (GPU probe, stream)
	public float FarRadiusM => (stream?.Ring.Radius ?? 0) * Voxel.ChunkMeters;
	public float FullRadiusM => (stream?.Ring.Reach ?? 0) * Voxel.ChunkMeters;
	int sinceShrink, lowFrames, gatedFrames;
	bool settledLogged;
	readonly Stopwatch resetClock = new();
	public const int RetierPerFrame = 1 << 16, MaxEditsPerFrame = 1 << 18, LoadingBoost = 16, MaxSkipsPerFrame = 1 << 16, EnqueuePerFrame = 1 << 14, DrainSlice = 2048, CompactPerFrame = 8192, ShiftRows = 16384; // LoadingBoost: ceiling while loading (buffer sizes); then CPU caps per frame; ShiftRows: rows of a crossing's diff per frame (a new crossing finishes the old one)
	const float MinBudget = 0.25f, MaxBudget = 4, LoadingGenMs = 30, FillGenMs = 6, LoadSeconds = 3; // MaxBudget: the in-game ceiling; LoadingGenMs: generation target while the loading screen hides the frame (until the near and mid spheres are in and the world settled or LoadSeconds passed); FillGenMs: after it until the whole world is in
	bool filling;
	float fillShare = 1; // jobs FILL took of the ones sent (deferred ones come back): the classify count follows it so jobs are not classified twice

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

	public int Queued => stream == null ? 0 : stream.Pending + stream.Incoming + stream.Shifts + urgent.Count + deferredBack.Count;
	// Every wanted chunk went through generation (stored, uniform or failed).
	public bool Settled => ready && stream != null && (stream.Done && urgent.Count == 0 && deferredBack.IsEmpty || FarGated && OnlyFarQueued) && Volatile.Read(ref inFlight) == 0;
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

	WorldGenerator generator;
	GpuBlockTable.Textures textures;
	ChunkStream stream;
	readonly List<ChunkPos> released = new();
	readonly List<Vector4I> releasedAll = new();
	readonly List<Vector4I> cols = new();
	readonly List<JobGpu> jobList = new();
	readonly List<JobGpu> farJobs = new();
	readonly List<uint> edits = new();
	Vector3I[] nearOffsets = Array.Empty<Vector3I>(); // the near sphere around the camera chunk, nearest first
	int rowsSent, epoch;
	bool resetRequested = true, loading = true, budgetBound;
	volatile bool ready;
	volatile int nearMissing = -1, midMissing = -1, nearEpoch = -1;
	long redone;
	int inFlight;
	readonly Queue<ChunkPos> urgent = new();
	readonly HashSet<ChunkPos> urgentSet = new();
	readonly ConcurrentQueue<(int Epoch, ChunkPos Chunk, bool Failed)> deferredBack = new(); // render thread -> main thread
	readonly ConcurrentQueue<(int Epoch, byte[] Bits)> visibleBack = new(); // render thread -> main thread (ReadVisible)
	public int Evicted { get; private set; } // chunks released as unseen under memory pressure since the reset

	// Visibility bits into the stream's eviction stamps.
	void ApplyVisible()
	{
		while (visibleBack.TryDequeue(out var v))
			if (v.Epoch == epoch && stream != null) stream.MarkSeen(MemoryMarshal.Cast<byte, uint>(v.Bits));
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
		public void Clear() =>
			(Reset, Ring, Gen, BlockRows, Columns, Jobs, EditRecords, Released, ColumnCount, JobCount, FillCap, Epoch, WorkBoost, Camera, NearChunks, MidChunks, Work) = (null, null, null, null, default, default, default, default, 0, 0, 0, 0, 0, default, 0, 0, 0);

		public RingWindow Reset, Ring;
		public WorldGenerator Gen;
		public (int First, byte[] Rows)? BlockRows;
		public Upload Columns, Jobs, EditRecords, Released;
		public int ColumnCount, JobCount, FillCap, Epoch, WorkBoost, Work;
		public ChunkPos Camera;
		public float NearChunks;
		public int MidChunks;
		public (int Y0, int Y1) Rows;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct JobGpu // gen.compute Job
	{
		public Vector4I Chunk; // w = JobState
		public uint EditFirst, EditCount, Pad0, Pad1;
		public uint Count0, Count1, Count2, Solid;
		public uint Rec, Base0, Base1, Base2;
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

	// After the camera moved: its whole chunks go into Anchor.
	public void Rebase()
	{
		var c = (Vector3I)(Camera.GlobalPosition / Voxel.ChunkMeters).Floor();
		if (c == Vector3I.Zero) return;
		Anchor += c;
		Camera.GlobalPosition -= (Vector3)c * Voxel.ChunkMeters;
	}

	public float SurfaceHeight(float x, float z) => generator.SurfaceHeight(x, z);

	// New generator or parameters: the sphere is generated again around the camera.
	public void Regenerate()
	{
		var old = generator;
		generator = CreateGenerator();
		resetRequested = true;
		Regenerated?.Invoke();
		if (old == null || ShaderKey(generator) == ShaderKey(old)) return;
		ready = false;
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

	// The chunk is generated again with priority -1 (docs/v2.md §4.4); a queued one picks the edit up anyway.
	public void SetBlock(BlockPos p, BlockState s)
	{
		Variants.Id(s);
		Edits.Set(p, s);
		stream?.Wake(p.Chunk);
		if (stream?.State(p.Chunk) == SlotState.Sent && urgentSet.Add(p.Chunk)) urgent.Enqueue(p.Chunk);
	}

	// How far the queue may generate: until the near and mid spheres are complete (GPU probe), and under memory pressure, only the mid sphere.
	int Gate => !MidDone || FarGated ? stream.Mid.Radius : int.MaxValue;
	bool MidDone => nearEpoch == epoch && nearMissing == 0 && midMissing == 0 && stream.MidEntries == 0;

	// Main thread, after the camera moved: reset or ring shift (a crossing's diff spread over frames, outside the pop budget so it does not starve the pops), this frame's generation work within the budgets, then the block rows its edits registered (docs/v2.md §4.3).
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
		int ClassOf(ChunkPos c)
		{
			long dx = c.X - cam.X, dy = c.Y - cam.Y, dz = c.Z - cam.Z, d2 = dx * dx + dy * dy + dz * dz;
			return d2 <= nearChunks * nearChunks ? 0 : d2 <= (midChunks + MidMargin) * (midChunks + MidMargin) ? 1 : 2; // mid with its margin (queued first, never throttled)
		}
		var cone = ViewCone();
		if (stream != null) { stream.Look(cone.Forward, cone.Cos); stream.Retier(RetierPerFrame); }
		while (deferredBack.TryDequeue(out var d))
		{
			if (d.Epoch != epoch) continue;
			if (d.Failed) { stream?.Fail(d.Chunk); f.Failed++; continue; }
			stream?.Requeue(d.Chunk);
			f.Requeued++;
			if (ClassOf(d.Chunk) == 2) f.RequeuedFar++;
		}
		if (resetRequested || stream == null || Jumped(cam)) Reset(step, cam, cone);
		else
		{
			ApplyVisible();
			releasedAll.Clear();
			int incoming = stream.Incoming;
			released.Clear();
			if (cam != stream.Ring.Center) stream.Move(cam, released, ShiftRows);
			else stream.Advance(released, ShiftRows);
			Collect();
			Regulate(f.Failed);
			(f.Released, f.Entered) = (releasedAll.Count, stream.Incoming - incoming);
			if (releasedAll.Count > 0) step.Released = Upload.From(releasedAll);
		}
		stream.Now = (int)Engine.GetProcessFrames();
		stream.Retry();
		long t1 = Stopwatch.GetTimestamp();
		cpuEnd = t1 + (long)(StreamCpuMs * Stopwatch.Frequency / 1000);
		int incomingBefore = stream.Incoming, due = Math.Max(DrainSlice, stream.DrainDue);
		for (int n = 0; stream.Incoming > 0 && (n < due || n < EnqueuePerFrame && !OverCpu()); n += DrainSlice) stream.Drain(DrainSlice);
		if (stream.NeedsCompact) stream.Compact(CompactPerFrame);
		f.Drained = incomingBefore - stream.Incoming;
		long t1d = Stopwatch.GetTimestamp(); // entered chunks are queued before the pops: a crossing frame has them to send
		if (loading && nearEpoch == epoch && nearMissing == 0 && (QuickLoad || midMissing == 0 && stream.MidEntries == 0 && (Settled || resetClock.Elapsed.TotalSeconds > LoadSeconds)))
		{
			loading = false;
			GD.Print($"[world] loaded {resetClock.Elapsed.TotalSeconds:0.00} s after the reset");
		}
		if (!loading && filling && (Settled || QuickLoad)) filling = false; // started without waiting (QuickLoad): the game GPU target at once
		if (!settledLogged && Settled && !loading) { settledLogged = true; GD.Print($"[world] settled {resetClock.Elapsed.TotalSeconds:0.00} s after the reset: pools {string.Join(" ", Enum.GetValues<WorldPool>().Select(p => $"{p} {PoolBytes(p) / 1048576.0:0}"))} MB, fixed {FixedBytes / 1048576.0:0} MB, features dropped {FeaturesDropped}, evicted {Evicted}"); }
		Budget = NextBudget(f.Requeued > 0);
		step.WorkBoost = loading || filling ? LoadingBoost : (int)MaxBudget;
		fillShare = Math.Clamp(0.8f * fillShare + 0.2f * (1 - (float)f.Requeued / Math.Max(SentLastFrame, 1)), 0.1f, 1);
		int classify = (int)(WorkCap() * fillShare), columns = (int)(ColumnsPerFrame * Budget), colCount = 0;
		step.Ring = stream.Ring;
		step.Rows = ChunkRows;
		(step.Epoch, step.Camera, step.NearChunks, step.MidChunks, step.FillCap) = (epoch, cam, NearRadius / Voxel.ChunkMeters, (int)(MidRadius / Voxel.ChunkMeters), (int)(FillPerFrame * Budget));

		cols.Clear();
		jobList.Clear();
		edits.Clear();
		bool anyEdits = Edits.Count + Edits.FluidCount > 0;
		long farAges = 0;
		bool Add(ChunkPos c)
		{
			bool column = !stream.HasColumn(c);
			if (column && colCount >= columns) return false;
			int editFirst = edits.Count / 4;
			if (anyEdits) EditRecords(c, edits);
			if (edits.Count / 4 > MaxEditsPerFrame && jobList.Count > 0) { edits.RemoveRange(editFirst * 4, edits.Count - editFirst * 4); return false; }
			if (column) { stream.SetColumn(c); cols.Add(new Vector4I(c.X, c.Z, 0, 0)); colCount++; }
			int cls = ClassOf(c);
			if (cls == 2) { int age = stream.Age(c); farAges += age; f.FarAgeMax = Math.Max(f.FarAgeMax, age); }
			(cls == 0 ? ref f.SentNear : ref cls == 1 ? ref f.SentMid : ref f.SentFar)++;
			jobList.Add(new JobGpu { Chunk = new(c.X, c.Y, c.Z, 0), EditFirst = (uint)editFirst, EditCount = (uint)(edits.Count / 4 - editFirst) });
			return true;
		}
		bool full = false;
		for (int n = 0; n < EditChunksPerFrame && urgent.Count > 0 && !(full = jobList.Count >= classify); n++)
		{
			var c = urgent.Peek();
			if (stream.State(c) == SlotState.Sent && stream.Ring.Wanted(c) && !Add(c)) { full = true; break; }
			urgentSet.Remove(urgent.Dequeue());
		}
		long t2 = Stopwatch.GetTimestamp();
		foreach (var d in nearOffsets)
		{
			var c = new ChunkPos(cam.X + d.X, cam.Y + d.Y, cam.Z + d.Z);
			if (full || (full = jobList.Count >= classify)) break;
			if (stream.Claim(c) && !Add(c)) { stream.Requeue(c); full = true; }
		}
		long t3 = Stopwatch.GetTimestamp();
		int skips = MaxSkipsPerFrame, gate = Gate;
		int midCap = Math.Max(classify, (int)(ClassifyPerFrame * MaxBudget)); // the mid sphere is never throttled by the far work
		bool Full() => full = jobList.Count >= (stream.MidEntries > 0 ? midCap : classify) || (jobList.Count & 255) == 0 && stream.MidEntries == 0 && OverCpu();
		while (!full && !Full())
		{
			if (!stream.TryPop(out var c, ref skips, gate)) break;
			if (!Add(c)) { stream.Requeue(c); full = true; }
		}
		long t4 = Stopwatch.GetTimestamp();
		budgetBound = full;
		step.Work = jobList.Count;
		if (f.SentNear + f.SentMid > 0) step.FillCap = Math.Max(step.FillCap, (int)(FillPerFrame * MaxBudget));
		SentLastFrame = jobList.Count;
		FarLast();
		if (jobList.Count > 0)
		{
			(step.Columns, step.Jobs) = (Upload.From(cols), Upload.From(jobList));
			if (edits.Count > 0) step.EditRecords = Upload.From(edits);
			(step.ColumnCount, step.JobCount) = (cols.Count, jobList.Count);
			Interlocked.Increment(ref inFlight);
		}
		long t5 = Stopwatch.GetTimestamp();
		static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
		(f.Queue, f.Sent, f.Columns, f.FillCap, f.Budget) = (Queued, jobList.Count, colCount, step.FillCap, Budget);
		(f.MoveMs, f.NearMs, f.PopMs, f.DrainMs, f.UploadMs, f.StepMs) = (Ms(t0, t1), Ms(t2, t3), Ms(t3, t4), Ms(t1, t1d), Ms(t4, t5), Ms(t0, t5));
		int nb = (int)nearChunks + 1, mb = (int)midChunks + 1;
		(f.Incoming, f.Pending, f.QNear, f.QMid, f.QFar) = (stream.Incoming, stream.Pending, stream.Entries(0, nb), stream.Entries(nb, mb) + stream.MidEntries, stream.Entries(mb, int.MaxValue));
		(f.Holes, f.Retrying) = (stream.Failed, stream.Retrying);
		(f.Stale, f.FarAge) = (f.QNear + f.QMid + f.QFar - f.Pending, f.SentFar == 0 ? 0 : (float)farAges / f.SentFar);
		Frame = f;
		step.BlockRows = NewBlockRows();
		StepAllocated = GC.GetAllocatedBytesForCurrentThread() - allocStart;
		return step;
	}

	// Jobs past the mid sphere after the others (stable): FILL defers from the first job that does not fit on, so a near or mid chunk popped late (a queue entry from when it was far) is never deferred for far ones.
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
			if (dx * dx + dy * dy + dz * dz > mid * mid) farJobs.Add(j);
			else jobList[k++] = j;
		}
		if (farJobs.Count == 0 || k == 0) return;
		jobList.RemoveRange(k, jobList.Count - k);
		jobList.AddRange(farJobs);
	}

	long genSeen;
	float msPerWork; // GPU generation ms per job (Step.Work), smoothed

	// Jobs this frame: the generation target over the measured GPU ms per job (the multiplicative Budget lags two frames and overshot), within the Budget's limits; Budget until a measure came back, and with FixedBudget.
	float WorkCap()
	{
		long g = Interlocked.Read(ref genSample);
		if (g != genSeen)
		{
			genSeen = g;
			int w = (int)(g >> 32);
			float ms = BitConverter.Int32BitsToSingle((int)g);
			if (w > 0 && ms > 0) msPerWork = msPerWork <= 0 ? ms / w : 0.7f * msPerWork + 0.3f * ms / w;
		}
		if (FixedBudget || msPerWork <= 0) return ClassifyPerFrame * Budget;
		float target = loading ? LoadingGenMs : filling ? FillGenMs : GenTargetMs;
		return Math.Clamp(target / msPerWork, ClassifyPerFrame * MinBudget, ClassifyPerFrame * (loading || filling ? LoadingBoost : MaxBudget));
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

	// The sphere's radius by the fullest pool (async counters of this reset). Over ShrinkAt (or a failed chunk over ShrinkTo), or gated for GatedHold frames before the queue emptied once: unseen chunks past the mid sphere are evicted first, else the radius goes to the sphere that holds what is generated (its share of the wanted chunks, by volume),
	// scaled to ~ShrinkTo (surface terrain: memory ~ radius^2), at least one chunk less, never inside the mid sphere. Growth needs an empty queue (the measure is final) and room for the grown sphere under GrowBelow for GrowHold frames, GrowAfterShrink after a shrink.
	void Regulate(int failed)
	{
		if (nearEpoch != epoch) return;
		float p = 0;
		for (int i = 0; i < 4; i++) if (PoolCap[i] > 0) p = Math.Max(p, (float)PoolUsed[i] / PoolCap[i]);
		(Pressure, FarGated) = (p, p >= ShrinkTo);
		var ring = stream.Ring;
		int min = Math.Min(ring.Reach, Mathf.CeilToInt(MidRadius / Voxel.ChunkMeters) + MidMargin);
		sinceShrink++;
		gatedFrames = stream.Pending == 0 ? 0 : FarGated ? gatedFrames + 1 : gatedFrames; // gated frames until the queue empties once
		bool over = (p > ShrinkAt || failed > 0 && p > ShrinkTo) && sinceShrink > ShrinkHold;
		if (over || gatedFrames > GatedHold)
		{
			int evicted = stream.Evict(min, EvictSweep, EvictMax, released);
			Collect();
			if (evicted > 0)
			{
				Evicted += evicted;
				(lowFrames, gatedFrames) = (0, 0);
				return;
			}
			if (ring.Radius > min)
			{
				double wanted = 4 / 3.0 * Math.PI * ring.Radius * ring.Radius * ring.RadiusY, done = Math.Clamp(1 - stream.Pending / wanted, 0.05, 1);
				int r = (int)(ring.Radius * Math.Cbrt(done) * Math.Sqrt(ShrinkTo / Math.Max(p, ShrinkTo)));
				stream.Resize(Math.Clamp(r, min, ring.Radius - 1), released, ShiftRows);
				Collect();
				(sinceShrink, lowFrames, gatedFrames) = (0, 0, 0);
				Shrinks++;
				return;
			}
		}
		if (ring.Radius >= ring.Reach) { lowFrames = 0; return; }
		float grown = (ring.Radius + GrowStep) / (float)ring.Radius;
		if (p * grown * grown >= GrowBelow) lowFrames = 0;
		else if (stream.Pending == 0) lowFrames++;
		if (lowFrames <= GrowHold || Shrinks > 0 && sinceShrink <= GrowAfterShrink) return;
		stream.Resize(ring.Radius + GrowStep, released, ShiftRows);
		Collect();
		lowFrames = 0;
		Grows++;
	}

	// The released chunks into this frame's release list.
	void Collect()
	{
		foreach (var r in released) releasedAll.Add(new Vector4I(r.X, r.Y, r.Z, 0));
		released.Clear();
	}

	// Everything is released on the GPU and queued again nearest first; the loading screen stays until the near sphere is in.
	void Reset(Step step, ChunkPos cam, (System.Numerics.Vector3 Forward, float Cos) cone)
	{
		resetRequested = false;
		settledLogged = false;
		resetClock.Restart();
		epoch++;
		var (y0, y1) = generator.ChunkYRange();
		ChunkRows = (y0, y1);
		stream = new ChunkStream(new RingWindow(cam, ViewRadius, 1f), (int)(MidRadius / Voxel.ChunkMeters) + MidMargin, cone.Forward, cone.Cos, y0, y1, Edits.Has);
		foreach (var c in Edits.Chunks) stream.Wake(c); // the constructor queues the generator's rows only
		(Pressure, FarGated, Shrinks, Grows, sinceShrink, lowFrames, gatedFrames, msPerWork) = (0, false, 0, 0, 0, 0, 0, 0);
		(step.Reset, step.Gen) = (stream.Ring, generator);
		urgent.Clear();
		urgentSet.Clear();
		(loading, filling, Evicted) = (true, true, 0);
		float near = NearRadius / Voxel.ChunkMeters;
		int k = Mathf.CeilToInt(near);
		var offsets = new List<Vector3I>();
		for (int z = -k; z <= k; z++) for (int y = -k; y <= k; y++) for (int x = -k; x <= k; x++) if (x * x + y * y + z * z <= near * near) offsets.Add(new(x, y, z));
		nearOffsets = offsets.OrderBy(o => o.LengthSquared()).ToArray();
		NearTotal = nearOffsets.Length;
		GD.Print($"[world] radius {stream.Ring.Radius * Voxel.ChunkMeters:0} m, generator rows {y0}..{y1}");
	}

	// Edits of chunk c as gen.compute reads them, sorted: (group * GroupCells + cell, variant or ~0, fluid word or ~0, 0).
	void EditRecords(ChunkPos c, List<uint> into)
	{
		if (!Edits.Has(c)) return; // most chunks have no edits: no per-chunk sort and allocation
		int size = Layout.Chunk, gs = Layout.Group, gpa = Layout.GroupsPerAxis; // gs: cells per group edge, gpa: groups per chunk edge
		var cells = new SortedDictionary<uint, (uint Block, uint Fluid)>();
		const uint none = uint.MaxValue;
		uint Key(BlockPos p)
		{
			int x = p.X - c.X * size, y = p.Y - c.Y * size, z = p.Z - c.Z * size;
			return (uint)((x / gs + gpa * (y / gs + gpa * (z / gs))) * Layout.GroupCells + x % gs + gs * (y % gs + gs * (z % gs)));
		}
		foreach (var (p, s) in Edits.InChunk(c)) cells[Key(p)] = ((uint)Variants.Id(s), none);
		foreach (var (p, fl) in Edits.FluidsInChunk(c))
		{
			uint k = Key(p), w = fl.Type == 0 ? 0 : (uint)Variants.Id(Registry[fl.Type].Default) | (uint)fl.Level << Layout.FluidLevelShift;
			cells[k] = (cells.TryGetValue(k, out var old) ? old.Block : none, w);
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
	void Generated(int stepEpoch, IEnumerable<(ChunkPos Chunk, bool Failed)> deferred)
	{
		foreach (var (c, failed) in deferred) deferredBack.Enqueue((stepEpoch, c, failed));
		Interlocked.Decrement(ref inFlight);
	}

	// Render thread, after the readback of a frame's counters: the near probe of that frame.
	void Probed(int stepEpoch, int near, int mid, int redo)
	{
		(nearMissing, midMissing, nearEpoch) = (near, mid, stepEpoch);
		Interlocked.Add(ref redone, redo);
	}
}
