using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renesance.World;

// gen.compute steps (GEN_*), in dispatch order (docs/v2.md §4.2); PALETTE runs inside FILL, ALLOC inside PACK.
public enum GenStep : uint { Clear, Release, Heights, ColumnMinMax, Features, Classify, Scan, List, FillArgs, Fill, Pack, Lights, Fixup, Reclaim, Occupancy, Probe }

// Job.chunk.w in gen.compute.
public enum JobState : uint { Pending, Uniform, Mixed, Deferred, Failed }

// Render thread: ring buffers, pools and the generation dispatch (docs/v2.md §4.2); every level of detail in the same buffers (§5.17).
public partial class World
{
	// ponytail: fixed pool shares of WorldBudgetMB (docs/v2.md §3.6), re-split from measured use when it matters.
	static readonly float[] PoolShare = { 0.12f, 0.55f, 0.25f, 0.08f };
	public const int MaxChunkLights = 64, LightLists = 16384, LightChanges = 1024; // lights.glsli; ponytail: a chunk keeps its first 64 light cells, lists over LightLists drop (LL.fail)
	public static readonly int LightReach = (int)MathF.Ceiling(15 * Renesance.Content.BlockRegistry.LightMetersPerLevel / Coords.BlockMeters); // blocks of the strongest light (level 15)
	public static readonly int LightRing = (LightReach + Layout.Chunk - 1) / Layout.Chunk, LightNeighbours = (2 * LightRing + 1) * (2 * LightRing + 1) * (2 * LightRing + 1); // chunks around a light's own that it reaches
	public static readonly int LightTileChunks = Math.Max(8, 64 * 15625 / Coords.ChunkVolume); // Lights/cubemaps: a tile gathers lists from up to this many chunks (the volume of 64 chunks of 25^3), more goes per pixel
	const int LightHeaderBytes = 32 + LightChanges * 16;
	public static readonly int MaxColFeatures = Math.Max(64, (64 * Layout.Chunk * Layout.Chunk / 625 + 15) / 16 * 16); // per column, 64 for 25^2 blocks; ponytail: L0 overflow drops features (W.lod_stat.x, FeaturesDropped), raise if forests look thin
	const int ColumnBlocks = Layout.Chunk * Layout.Chunk, SlotAlign = 2048; // SlotAlign: a level's slots start on a word of the occupancy mip

	public volatile uint[] PoolUsed = new uint[4], PoolCap = new uint[4], PoolFail = new uint[4], PoolHigh = new uint[4], PoolFree = new uint[4]; // by WorldPool, from the async counter readback: Used = taken and not given back, High = bump high water, Free = free stack height
	public volatile float GenMs, UpkeepMs; // previous frame's generation on the GPU, CPU time of the last Upkeep
	public volatile uint FeaturesDropped, GroupFeaturesDropped; // since the reset: L0 column features over MaxColFeatures, coarse group features over the module's cap (W.lod_stat)
	public long PoolBytes(WorldPool p, bool capacity = false) => (long)(capacity ? PoolCap : PoolUsed)[(int)p] * Layout.PoolWords(p) * 4;
	public long FixedBytes { get; private set; } // directory, occupancy and column rings of every level

	[StructLayout(LayoutKind.Sequential)]
	struct LevelGpu // world.glsli Level
	{
		public Vector4I Min, Size, Off, Base;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct WorldGpu // world.glsli W, followed by the permutation table
	{
		public Vector4 Near;
		public Vector4I Probe, Lod;
		public LevelGpu L0, L1, L2, L3;
		public uint Used0, Used1, Used2, Used3, Cap0, Cap1, Cap2, Cap3, Fail0, Fail1, Fail2, Fail3;
		public int Free0, Free1, Free2, Free3, TopY0, TopY1, TopY2, TopY3;
		public uint Stat0, Stat1, Stat2, Stat3;
	}

	static readonly int FrameHeaderBytes = (int)Marshal.OffsetOf<WorldGpu>(nameof(WorldGpu.Used0)); // near .. levels, written every frame
	const string GenBegin = "gen_begin", GenEnd = "gen_end";
	readonly byte[] headerBytes = new byte[Unsafe.SizeOf<WorldGpu>()];
	readonly System.Collections.Concurrent.ConcurrentQueue<int> counterEpochs = new(); // render thread: epochs of the counter reads in flight, in order
	Callable countersRead;

	Kernel gen;                                                  // persistent
	Rid types, tex, colormaps, modelData, items, genParams;      // block table and generator: blockTable group
	Rid[] genBuffers = Array.Empty<Rid>();
	Rid header, columns, dir, records, occ, occMip, vis, heights, colmm, featCount, feats, jobs, jobGroups, mixed, groupWork, fillArgs, results, freeList, genSet, argsSet; // Gpu.World
	Rid editBuf, release; // grown on demand
	public Rid LightBuffer { get; private set; } // lights.glsli LL, render thread
	readonly RidGroup work = new(), loadingWork = new(); // UseWork: in-game sizes for good, loading sizes only while loading
	(Rid Columns, Rid Jobs, Rid JobGroups, Rid Mixed, Rid GroupWork, Rid Results) small, big;
	int workBoost;
	Rid[] bricks = new Rid[3];
	RingWindow[] gpuRings;
	long[] slotBase = Array.Empty<long>(), colBase = Array.Empty<long>();
	long totalSlots;
	long editCap, releaseCap;
	uint featureCandidates; // of the generator gen was compiled for
	readonly RidGroup blockTable = new(), generatorBuffers = new();

	public bool RingReady => gpuRings != null; // render thread
	public Rid TexBuffer => tex;
	public GpuBlockTable.Textures Textures => textures;
	public int RingResets { get; private set; } // render thread: ResetRing calls, the rings were emptied
	uint packStamp; // per PACK dispatch, into the records it writes (Layout.StampWord)

	static string ShaderKey(WorldGenerator g) => $"{g.ShaderModule}\n{g.FeatureCandidates}\n{g.ShaderConstants()}\n{g.ClimateShader()}\n{g.BufferKey}";

	void InitGpu()
	{
		var rd = Gpu.Rd;
		types = blockTable.Add(rd.StorageBufferCreate((uint)(Variants.MaxCount * GpuBlockTable.RowBytes)));
		tex = blockTable.Add(rd.StorageBufferCreate((uint)textures.Texels.Length, textures.Texels));
		colormaps = blockTable.Add(rd.StorageBufferCreate((uint)textures.ColormapTexels.Length, textures.ColormapTexels));
		var library = Models.Pack();
		modelData = blockTable.Add(rd.StorageBufferCreate((uint)library.Length, library));
		items = blockTable.Add(rd.StorageBufferCreate(48, new byte[48]));
		InitGenerator();
	}

	void InitGenerator()
	{
		var rd = Gpu.Rd;
		Gpu.Init(Variants, generator, gpuLod);
		gen = Gpu.Compile("gen.compute", (int)Math.Max(128, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(Math.Max(Layout.Groups + 1, Layout.GroupCells), LightNeighbours)))); // FILL: thread per cell, PACK: per group and one for the record, LIGHTS: per neighbour chunk
		featureCandidates = (uint)generator.FeatureCandidates;
		var p = generator.ShaderParams();
		genParams = generatorBuffers.Add(rd.StorageBufferCreate((uint)p.Length, p));
		genBuffers = generator.LoadBuffers(Variants).Select(b => generatorBuffers.Add(rd.StorageBufferCreate((uint)b.Length, b))).ToArray();
		foreach (var (b, rid) in new[] { (WorldBinding.Types, types), (WorldBinding.Tex, tex), (WorldBinding.Colormaps, colormaps), (WorldBinding.ModelData, modelData), (WorldBinding.Items, items), (WorldBinding.GenParams, genParams) })
			Gpu.Publish(b, rid);
		for (int i = 0; i < genBuffers.Length; i++) Gpu.Publish(WorldBinding.GenBuffers, genBuffers[i], i);
		gpuRings = null;
		ready = true;
	}

	void Rebuild()
	{
		Gpu.Free();
		generatorBuffers.Free(Gpu.Rd);
		InitGenerator();
		Rebuilt?.Invoke();
	}

	void FreeGpu()
	{
		Gpu.Free();
		generatorBuffers.Free(Gpu.Rd);
		blockTable.Free(Gpu.Rd);
		foreach (var r in new[] { editBuf, release }) if (r.IsValid) Gpu.Rd.FreeRid(r);
		work.Free(Gpu.Rd);
		loadingWork.Free(Gpu.Rd);
		(small, big) = (default, default);
	}

	LevelGpu LevelOf(RingWindow r, int l, LodLayout lods)
	{
		var o = r.Offset;
		return new LevelGpu
		{
			Min = new(r.Min.X, r.Min.Y, r.Min.Z, (int)Math.Min(lods.SwitchBlocks(l), 1e9f)), Size = new(r.SizeXZ, r.SizeY, r.SizeXZ, 0),
			Off = new(o.X, o.Y, o.Z, r.Radius), Base = new((int)slotBase[l], (int)colBase[l], 0, 0),
		};
	}

	// Render thread, before the passes of the frame: reset or ring shifts, release, generation, then the near probe.
	public void Upkeep(Step s)
	{
		if (s == null || !ready) return;
		long started = Stopwatch.GetTimestamp();
		var rd = Gpu.Rd;
		var t = Gpu.Captured;
		GenMs = t.Count == 0 ? -1 : t.TryGetValue(GenBegin, out var t0) && t.TryGetValue(GenEnd, out var t1) ? (t1 - t0) / 1e6f : 0;
		Models.Upload(rd, modelData);
		if (s.BlockRows is { } b) rd.BufferUpdate(types, (uint)(b.First * GpuBlockTable.RowBytes), (uint)b.Rows.Length, b.Rows);
		if (s.Reset != null) ResetRing(s.Reset, s.Gen);
		if (gpuRings == null) return;
		if (s.WorkBoost != workBoost) UseWork(s.WorkBoost);
		var h = new WorldGpu { Near = new(s.Camera.X, s.Camera.Y, s.Camera.Z, s.NearChunks), Probe = new(0, 0, 0, s.MidChunks), Lod = new(s.DrawLevels, 0, 0, 0) };
		for (int l = 0; l < s.Rings.Length; l++) Level(ref h, l) = LevelOf(s.Rings[l], l, s.Lods);
		Gpu.Write(h, headerBytes);
		rd.BufferUpdate(header, 0, (uint)FrameHeaderBytes, headerBytes);
		if (s.Released.Length > 0 || s.JobCount > 0) Generate(s);
		if (++visFrame % VisFrames == 0) ReadVisible(s.Epoch);
		s.Columns.Return(); s.Jobs.Return(); s.EditRecords.Return(); s.Released.Return();
		int n = s.MidChunks;
		long cl = Gpu.Begin(gen);
		rd.ComputeListBindUniformSet(cl, genSet, 0);
		Gpu.Dispatch(cl, gen, (uint)GenStep.Probe, (2 * n + 1) * (2 * n + 1) * (2 * n + 1));
		rd.ComputeListEnd();
		counterEpochs.Enqueue(s.Epoch);
		if (countersRead.Delegate == null) countersRead = Callable.From<byte[]>(d => OnCounters(d, counterEpochs.TryDequeue(out var e) ? e : -1));
		rd.BufferGetDataAsync(header, countersRead, 0, (uint)Unsafe.SizeOf<WorldGpu>());
		UpkeepMs = (float)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
	}

	static ref LevelGpu Level(ref WorldGpu h, int l)
	{
		switch (l)
		{
			case 0: return ref h.L0;
			case 1: return ref h.L1;
			case 2: return ref h.L2;
			default: return ref h.L3;
		}
	}

	// Release everything: counters, free stacks and the directory start empty (every chunk NONE, CLEAR); the generator parameters go in even when the shader key did not change.
	void ResetRing(RingWindow[] rings, WorldGenerator g)
	{
		var rd = Gpu.Rd;
		if (gpuRings == null || gpuRings.Length != rings.Length || gpuRings.Zip(rings).Any(p => p.First.SizeXZ != p.Second.SizeXZ || p.First.SizeY != p.Second.SizeY)) Allocate(rings);
		gpuRings = rings;
		RingResets++;
		var h = new WorldGpu { TopY0 = rings[0].Min.Y, TopY1 = rings.Length > 1 ? rings[1].Min.Y : 0, TopY2 = rings.Length > 2 ? rings[2].Min.Y : 0, TopY3 = rings.Length > 3 ? rings[3].Min.Y : 0 };
		var cap = PoolCaps(BudgetMB);
		(h.Cap0, h.Cap1, h.Cap2, h.Cap3) = (cap[0], cap[1], cap[2], cap[3]);
		var bytes = Gpu.Bytes(h).Concat(g.Perlin.Table.SelectMany(BitConverter.GetBytes)).ToArray();
		rd.BufferUpdate(header, 0, (uint)bytes.Length, bytes);
		var p = g.ShaderParams();
		rd.BufferUpdate(genParams, 0, (uint)p.Length, p);
		long cl = Gpu.Begin(gen);
		rd.ComputeListBindUniformSet(cl, genSet, 0);
		Gpu.Dispatch(cl, gen, (uint)GenStep.Clear, totalSlots);
		rd.ComputeListEnd();
		rd.BufferClear(vis, 0, (uint)OccBytes(totalSlots));
		rd.BufferClear(LightBuffer, 0, LightHeaderBytes);
		rd.BufferUpdate(LightBuffer, 8, 4, BitConverter.GetBytes((uint)LightLists));
		(PoolUsed, PoolCap, PoolFail, PoolHigh, PoolFree) = (new uint[4], cap, new uint[4], new uint[4], new uint[4]);
		(FeaturesDropped, GroupFeaturesDropped) = (0, 0);
	}

	uint[] PoolCaps(int mb) => Enumerable.Range(0, 4).Select(p => (uint)(mb * 1048576L * PoolShare[p] / (Layout.PoolWords((WorldPool)p) * 4))).ToArray();
	static long OccBytes(long bits) => (bits + 31) / 32 * 4;

	// Every level's slots one after the other (SlotAlign), then their column slots.
	void Allocate(RingWindow[] rings)
	{
		var rd = Gpu.Rd;
		Gpu.World.Free(rd);
		Rid Buf(long bytes, byte[] data = null) => Gpu.Buffer(Gpu.World, Math.Max(bytes, 16), data);
		(slotBase, colBase) = (new long[rings.Length], new long[rings.Length]);
		long slots = 0, cols = 0;
		for (int l = 0; l < rings.Length; l++)
		{
			(slotBase[l], colBase[l]) = (slots, cols);
			slots += (rings[l].Slots + SlotAlign - 1) / SlotAlign * SlotAlign;
			cols += rings[l].Columns;
		}
		totalSlots = slots;
		if (slots > int.MaxValue / 8) throw new InvalidOperationException($"world rings too large ({slots} slots)");
		var cap = PoolCaps(Presets[View].BudgetMB); // the preset's size: BudgetMB only lowers the caps in the header
		header = Buf(Unsafe.SizeOf<WorldGpu>() + Perlin.TableSize * 4);
		dir = Buf(slots * 8);
		records = Buf(cap[0] * (long)Layout.ChunkRecWords * 4);
		for (int c = 0; c < 3; c++) bricks[c] = Buf(cap[c + 1] * (long)Layout.BrickWords(c) * 4);
		occ = Buf(OccBytes(slots));
		occMip = Buf(OccBytes(slots / 64));
		vis = Buf(OccBytes(slots));
		heights = Buf(cols * ColumnBlocks * 4);
		colmm = Buf(cols * 8L);
		featCount = Buf(cols * 4L);
		feats = Buf(cols * MaxColFeatures * 16);
		LightBuffer = Buf(LightHeaderBytes + LightLists * (1L + 2 * MaxChunkLights) * 4);
		FixedBytes = LightHeaderBytes + LightLists * (1L + 2 * MaxChunkLights) * 4 + slots * 8 + 2 * OccBytes(slots) + OccBytes(slots / 64) + cols * (ColumnBlocks * 4 + 8 + 4 + MaxColFeatures * 16);
		fillArgs = Gpu.Buffer(Gpu.World, 12, null, RenderingDevice.StorageBufferUsage.Indirect);
		freeList = Buf(cap.Sum(c => (long)c) * 4);
		(editCap, releaseCap) = (0, 0);
		EnsureBuffers(1024, 4096);
		if (workBoost == 0) UseWork(LoadingBoost);
		foreach (var (b, rid) in new[] { (WorldBinding.Dir, dir), (WorldBinding.Records, records), (WorldBinding.Brick4, bricks[0]), (WorldBinding.Brick16, bricks[1]), (WorldBinding.BrickFull, bricks[2]),
			(WorldBinding.Occupancy, occ), (WorldBinding.OccupancyMip, occMip), (WorldBinding.World, header), (WorldBinding.ColFeatures, feats), (WorldBinding.Visible, vis), (WorldBinding.Lights, LightBuffer) })
			Gpu.Publish(b, rid);
	}

	// Edit records and released chunks grow their buffers (and gen's sets) when a frame brings more.
	void EnsureBuffers(long editRecords, long released)
	{
		var rd = Gpu.Rd;
		if (editRecords <= editCap && released <= releaseCap) return;
		Rid Grow(Rid old, ref long cap, long need, int bytes)
		{
			if (need <= cap) return old;
			cap = Math.Max(need, cap * 2);
			if (old.IsValid) rd.FreeRid(old);
			return rd.StorageBufferCreate((uint)(cap * bytes));
		}
		editBuf = Grow(editBuf, ref editCap, editRecords, 16);
		release = Grow(release, ref releaseCap, released, 16);
		if (workBoost > 0) MakeSets();
	}

	// Per-frame job and fill buffers for budgets up to `boost` (Step.WorkBoost): MaxBudget in game (~90 MB, kept), LoadingBoost while loading (~370 MB more, made at the reset behind the loading screen, freed when it ends). Freed RIDs wait for their frame (results' async read is already copied).
	void UseWork(int boost)
	{
		if (boost == LoadingBoost && !big.Jobs.IsValid) big = WorkBuffers(loadingWork, boost);
		if (!small.Jobs.IsValid) small = WorkBuffers(work, (int)MaxBudget);
		(columns, jobs, jobGroups, mixed, groupWork, results) = boost == LoadingBoost ? big : small;
		if (boost != LoadingBoost) { loadingWork.Free(Gpu.Rd); big = default; }
		workBoost = boost;
		MakeSets();
	}

	(Rid, Rid, Rid, Rid, Rid, Rid) WorkBuffers(RidGroup owner, int boost)
	{
		Rid Buf(long bytes) => Gpu.Buffer(owner, Math.Max(bytes, 16));
		int jobCap = ClassifyPerFrame * boost, fillCap = FillPerFrame * boost;
		return (Buf(ColumnsPerFrame * boost * 16L), Buf(jobCap * 64L), Buf(jobCap * (long)Layout.Groups * 8), Buf(16 + fillCap * 4L), Buf(fillCap * (long)Layout.BrickWords(2) * 4), Buf(16 + jobCap * 16L));
	}

	void MakeSets()
	{
		Rid GenSet(Rid args) => Gpu.Set(gen.Shader, (0, heights), (1, colmm), (2, featCount), (3, feats), (4, columns), (5, jobs), (6, jobGroups),
			(7, mixed), (8, groupWork), (9, args), (10, editBuf), (11, results), (12, release), (13, freeList));
		genSet = GenSet(results);
		argsSet = GenSet(fillArgs);
	}

	void Generate(Step s)
	{
		var rd = Gpu.Rd;
		int nRel = s.Released.Length / 16;
		EnsureBuffers(s.EditRecords.Length / 16, nRel);
		if (nRel > 0) rd.BufferUpdate(release, 0, (uint)s.Released.Length, s.Released.Data);
		if (s.JobCount > 0)
		{
			if (s.Columns.Length > 0) rd.BufferUpdate(columns, 0, (uint)s.Columns.Length, s.Columns.Data);
			rd.BufferUpdate(jobs, 0, (uint)s.Jobs.Length, s.Jobs.Data);
			if (s.EditRecords.Length > 0) rd.BufferUpdate(editBuf, 0, (uint)s.EditRecords.Length, s.EditRecords.Data);
			rd.BufferUpdate(mixed, 0, 16, Gpu.Push(0, s.FillCap, s.JobCount));
			rd.BufferUpdate(results, 0, 16, Gpu.Push(0));
		}

		Gpu.Timestamp(GenBegin);
		long cl = Gpu.Begin(gen);
		rd.ComputeListBindUniformSet(cl, genSet, 0);
		void Run(GenStep step, long count, int first = 0) => Gpu.Dispatch(cl, gen, (uint)step, count, first);
		Run(GenStep.Release, nRel * (long)gen.X);
		if (s.JobCount > 0)
		{
			Run(GenStep.Heights, s.ColumnCount * (long)ColumnBlocks);
			Run(GenStep.ColumnMinMax, s.ColumnCount * (long)gen.X);
			Run(GenStep.Features, s.LevelColumns[0] * (long)featureCandidates); // L0 columns come first
			Run(GenStep.Classify, s.JobCount * (long)gen.X);
			Run(GenStep.Scan, gen.X);
			Run(GenStep.List, s.JobCount * (long)gen.X);
			rd.ComputeListBindUniformSet(cl, argsSet, 0);
			Run(GenStep.FillArgs, 1);
		}
		rd.ComputeListEnd();
		if (s.JobCount > 0)
		{
			cl = Gpu.Begin(gen);
			rd.ComputeListBindUniformSet(cl, genSet, 0);
			rd.ComputeListSetPushConstant(cl, Gpu.Push((int)GenStep.Fill), Gpu.PushBytes);
			rd.ComputeListDispatchIndirect(cl, fillArgs, 0);
			rd.ComputeListAddBarrier(cl);
			Gpu.Dispatch(cl, gen, (uint)GenStep.Pack, s.JobCount * (long)gen.X, (int)++packStamp);
			Run(GenStep.Lights, s.JobCount * (long)gen.X);
			Run(GenStep.Fixup, 1);
			Run(GenStep.Reclaim, s.JobCount * (long)gen.X);
			Run(GenStep.Occupancy, s.JobCount);
			rd.ComputeListEnd();
		}
		Gpu.Timestamp(GenEnd);
		if (s.JobCount == 0) return;

		int stepEpoch = s.Epoch;
		rd.BufferGetDataAsync(results, Callable.From<byte[]>(d =>
		{
			var r = MemoryMarshal.Cast<byte, Vector4I>(d);
			int n = Math.Min((int)BitConverter.ToUInt32(d, 0), r.Length - 1);
			var deferred = new List<(ChunkPos, int, bool)>();
			for (int i = 1; i <= n; i++) deferred.Add((new ChunkPos(r[i].X, r[i].Y, r[i].Z), r[i].W >> 1, (r[i].W & 1) != 0));
			Generated(stepEpoch, deferred);
		}), 0, (uint)(16 + s.JobCount * 16));
	}

	// The visibility bits the sampled primary rays set since the last read (docs/v2.md §5.17), then cleared; ApplyVisible hands them to the streams.
	const int VisFrames = 30;
	int visFrame;
	void ReadVisible(int stepEpoch)
	{
		uint bytes = (uint)OccBytes(totalSlots);
		var starts = slotBase.ToArray();
		Gpu.Rd.BufferGetDataAsync(vis, Callable.From<byte[]>(d => visibleBack.Enqueue((stepEpoch, d, starts))), 0, bytes);
		Gpu.Rd.BufferClear(vis, 0, bytes);
	}

	void OnCounters(byte[] d, int stepEpoch)
	{
		var w = MemoryMarshal.Read<WorldGpu>(d);
		uint InUse(uint used, uint cap, int free) => Math.Min(used, cap) - (uint)Math.Clamp(free, 0, (int)Math.Min(used, cap));
		// In place (stats: a reader may see a mix of two frames).
		(PoolCap[0], PoolCap[1], PoolCap[2], PoolCap[3]) = (w.Cap0, w.Cap1, w.Cap2, w.Cap3);
		(PoolUsed[0], PoolUsed[1], PoolUsed[2], PoolUsed[3]) = (InUse(w.Used0, w.Cap0, w.Free0), InUse(w.Used1, w.Cap1, w.Free1), InUse(w.Used2, w.Cap2, w.Free2), InUse(w.Used3, w.Cap3, w.Free3));
		(PoolFail[0], PoolFail[1], PoolFail[2], PoolFail[3]) = (w.Fail0, w.Fail1, w.Fail2, w.Fail3);
		(PoolHigh[0], PoolHigh[1], PoolHigh[2], PoolHigh[3]) = (w.Used0, w.Used1, w.Used2, w.Used3);
		(PoolFree[0], PoolFree[1], PoolFree[2], PoolFree[3]) = ((uint)Math.Max(w.Free0, 0), (uint)Math.Max(w.Free1, 0), (uint)Math.Max(w.Free2, 0), (uint)Math.Max(w.Free3, 0));
		(FeaturesDropped, GroupFeaturesDropped) = (w.Stat0, w.Stat1);
		Probed(stepEpoch, w.Probe.X, w.Probe.Y, w.Probe.Z);
	}
}
