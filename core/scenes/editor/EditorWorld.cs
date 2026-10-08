using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Renesance.Content;
using Renesance.Save;
using Renesance.World;

public readonly record struct BlockPick(BlockPos Block, BlockPos Place, Vector3I Normal, BlockState State, bool ObjectPart, float Distance);

// The creative editor's world (docs/v2.md §4.4): edits through World, the pick from the G-buffer, ReadBox on the GPU, saves in WorldSave, snapshot bookmarks through Main.
public partial class EditorWorld : Node
{
	public const int MaxReadBlocks = 128 * 128 * 128, RuleBorder = 2;
	public const long ReadCapacity = 2L * MaxReadBlocks;
	public const float PickBlocks = 128f;
	public static string SavesRoot => ProjectSettings.GlobalizePath("user://saves");

	[Export] public World World;
	[Export] public Renderer Renderer;
	[Export] public Player Player;
	[Export] public Main Main;
	[Export] public GhostBlocks Ghost;

	public WorldSave SaveFile { get; set; }
	public int Unsaved { get; set; } // edits since the last save, load or new world
	public BlockPick? LastPick { get; private set; }
	public event Action<BlockPick?> Picked;
	public bool Picking { get; set; } = true; // off in tests and shots, reads still run

	public BlockRegistry Registry => World.Registry;
	public WorldEdits Edits => World.Edits;
	public Camera3D Camera => World.Camera;
	public Vector3I OriginBlock => World.Anchor * Layout.Chunk; // world block at the Godot origin, moves with the camera

	readonly ConcurrentQueue<(BlockPos Min, Vector3I Size, BlockState Missing, Action<BlockState[]> Done)> reads = new();

	// After Renderer queued the frame: the pick and reads run behind its passes, with its camera.
	public override void _Ready()
	{
		ProcessPriority = 1001;
		World.Rebuilt += Forget;
	}

	public override void _Process(double delta)
	{
		if (!World.GpuReady) return;
		if (Picking)
		{
			var b = Camera.GlobalBasis;
			var ray = new PickRay(Camera.GlobalPosition / Voxel.BlockSize, b.X, b.Y, -b.Z, Mathf.Tan(Mathf.DegToRad(Camera.Fov) / 2), OriginBlock);
			RenderingServer.CallOnRenderThread(Callable.From(() => Pick(ray)));
		}
		while (reads.TryDequeue(out var r)) RenderingServer.CallOnRenderThread(Callable.From(() => ReadGpu(r.Min, r.Size, r.Missing, r.Done)));
	}

	public void SetBlock(BlockPos p, BlockState s)
	{
		if (Registry == null || s.Type >= Registry.Count) throw new ArgumentOutOfRangeException(nameof(s), s, "unknown block type (or the world isn't ready)");
		World.SetBlock(p, s);
		Unsaved++;
	}

	// Block states of [min, min + size), x fastest, at most ReadCapacity, `missing` where the world isn't in memory; done gets null while the world isn't ready.
	public void Read(BlockPos min, Vector3I size, Action<BlockState[]> done, BlockState missing = default)
	{
		if ((long)size.X * size.Y * size.Z > ReadCapacity) throw new ArgumentOutOfRangeException(nameof(size), size, $"1..{ReadCapacity} blocks");
		reads.Enqueue((min, size, missing, done));
	}

	// Edits win over the world (docs/v2.md §5.13).
	public void ReadBox(BlockPos min, Vector3I size, Action<BlockState[]> done, BlockState missing = default)
	{
		if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || (long)size.X * size.Y * size.Z > MaxReadBlocks) throw new ArgumentOutOfRangeException(nameof(size), size, $"1..{MaxReadBlocks} blocks");
		Read(min, size, done, missing);
	}

	// The box through the block rules with a RuleBorder of neighbours: every block that changes (`force`: every asked one) with its prior state.
	public void Resolve(BlockPos min, Vector3I size, Func<int, BlockState?> state, bool force, Action<List<(BlockPos Pos, BlockState State, BlockState Prior)>> done)
	{
		var grown = size + Vector3I.One * 2 * RuleBorder;
		int b = (long)grown.X * grown.Y * grown.Z <= ReadCapacity ? RuleBorder : 0;
		var rmin = new BlockPos(min.X - b, min.Y - b, min.Z - b);
		var rsize = size + Vector3I.One * 2 * b;
		Read(rmin, rsize, all =>
		{
			if (all == null) { done(null); return; }
			BlockState At(BlockPos p)
			{
				int x = p.X - rmin.X, y = p.Y - rmin.Y, z = p.Z - rmin.Z;
				return Edits.TryGet(p, out var e) ? e : x >= 0 && y >= 0 && z >= 0 && x < rsize.X && y < rsize.Y && z < rsize.Z ? all[x + rsize.X * (y + rsize.Y * z)] : BlockState.Air;
			}
			var asked = new List<(BlockPos, BlockState)>();
			for (int i = 0; i < size.X * size.Y * size.Z; i++)
			{
				var p = new BlockPos(min.X + i % size.X, min.Y + i / size.X % size.Y, min.Z + i / (size.X * size.Y));
				if (state(i) is { } s && (force || s != At(p))) asked.Add((p, s));
			}
			done(BlockRules.Apply(Registry, At, asked).Select(c => (c.Pos, c.State, At(c.Pos))).ToList());
		});
	}

	// Edits of [min, min + size) as (index in the box, state), for a Read to put over the world's blocks.
	static List<(int Index, BlockState State)> EditsIn(WorldEdits edits, BlockPos min, Vector3I size)
	{
		var overlay = new List<(int, BlockState)>();
		var c0 = min.Chunk; var c1 = new BlockPos(min.X + size.X - 1, min.Y + size.Y - 1, min.Z + size.Z - 1).Chunk;
		for (int cz = c0.Z; cz <= c1.Z; cz++)
			for (int cy = c0.Y; cy <= c1.Y; cy++)
				for (int cx = c0.X; cx <= c1.X; cx++)
					foreach (var (p, s) in edits.InChunk(new ChunkPos(cx, cy, cz)))
					{
						int x = p.X - min.X, y = p.Y - min.Y, z = p.Z - min.Z;
						if (x >= 0 && y >= 0 && z >= 0 && x < size.X && y < size.Y && z < size.Z) overlay.Add((x + size.X * (y + size.Y * z), s));
					}
		return overlay;
	}

	public string Describe(BlockPos p) => Registry == null ? null : World.GeneratorInstance?.Describe(Registry, p);

	public void DropItem(Vector3 pos, string model) { } // ponytail: no dropped items yet (TODO.md)

	public void NewWorld(string generator)
	{
		(World.Generator, World.GeneratorParams) = (generator, null);
		World.Edits.Clear();
		World.Regenerate();
		(SaveFile, Unsaved) = (null, 0);
		Player.Spawn();
	}

	public void Save(WorldSave save)
	{
		Main.WriteWorld(save);
		(SaveFile, Unsaved) = (save, 0);
	}

	public void Load(WorldSave save)
	{
		World.Load(save);
		Main.ApplyBookmark(null, save.Header);
		(SaveFile, Unsaved) = (save, 0);
	}

	// Into tests/bookmarks: null name = a new auto-named one, else overwritten.
	public Snapshot SaveBookmark(string name, string note = null) => Main.SaveBookmark(name, note);

	public void LoadBookmark(Snapshot s) => Main.LoadBookmark(s);

	// --- render thread ---

	readonly record struct PickRay(Vector3 Frac, Vector3 Right, Vector3 Up, Vector3 Fwd, float TanHalf, Vector3I Origin);

	Kernel readKernel, pickKernel;
	Rid readSet, readCamera, readIds, pickBuffer, pickSet, pickCamera; // in World.Gpu.Persistent, gone with it on Rebuilt
	bool picking;

	void Forget() => (readKernel, pickKernel, readSet, readCamera, readIds, pickBuffer, pickSet, pickCamera, picking) = (default, default, default, default, default, default, default, default, false);

	// The centre pixel's ray (camera.glsli pixel_dir) traced by pick_ray.compute: glass, ice and fog blocks stop it, fluids don't, whatever Transparency and Water do to the G-buffer.
	void Pick(PickRay r)
	{
		var size = Renderer.Targets.Size;
		if (picking || !World.RingReady || !Renderer.CameraBuffer.IsValid || size.X <= 0) return;
		var gpu = World.Gpu;
		var rd = gpu.Rd;
		if (!pickKernel.Shader.IsValid) pickKernel = gpu.Compile("res://core/scenes/editor/pick_ray.compute", 1);
		if (!pickBuffer.IsValid) pickBuffer = gpu.Buffer(gpu.Persistent, 80);
		if (pickCamera != Renderer.CameraBuffer) (pickCamera, pickSet) = (Renderer.CameraBuffer, gpu.Set(pickKernel.Shader, (1, Renderer.CameraBuffer), (2, pickBuffer)));
		var px = size / 2;
		var ndc = (new Vector2(px.X, px.Y) + Vector2.One * 0.5f) / size * 2 - Vector2.One;
		var dir = (r.Fwd + r.Right * (ndc.X * r.TanHalf * size.X / size.Y) - r.Up * (ndc.Y * r.TanHalf)).Normalized();
		var input = MemoryMarshal.AsBytes(new[] { new Vector4I(r.Origin.X, r.Origin.Y, r.Origin.Z, 0) }.AsSpan()).ToArray()
			.Concat(MemoryMarshal.AsBytes(new[] { new Vector4(r.Frac.X, r.Frac.Y, r.Frac.Z, 0), new Vector4(dir.X, dir.Y, dir.Z, PickBlocks + 1) }.AsSpan()).ToArray()).ToArray();
		rd.BufferUpdate(pickBuffer, 0, (uint)input.Length, input);
		long cl = gpu.Begin(pickKernel);
		rd.ComputeListBindUniformSet(cl, pickSet, 0);
		rd.ComputeListDispatch(cl, 1, 1, 1);
		rd.ComputeListEnd();
		picking = true;
		rd.BufferGetDataAsync(pickBuffer, Callable.From<byte[]>(b =>
		{
			picking = false;
			float t = BitConverter.ToSingle(b, 0);
			uint hit = BitConverter.ToUInt32(b, 16);
			Callable.From(() => OnPicked(t, hit, r, dir)).CallDeferred();
		}), 48, 32);
	}

	// Main thread.
	void OnPicked(float t, uint hit, PickRay r, Vector3 dir)
	{
		BlockPick? pick = null;
		uint face = hit & HitBits.FaceMask;
		if (face != HitBits.Sky && t <= PickBlocks && Registry != null)
		{
			var n = Vector3I.Zero;
			n[(int)face >> 1] = (face & 1) != 0 ? 1 : -1;
			var c = (Vector3I)(r.Frac + dir * t - (Vector3)n * 1e-3f).Floor() + r.Origin;
			var block = new BlockPos(c.X, c.Y, c.Z);
			int id = (int)(hit >> HitBits.VariantShift & HitBits.VariantMask);
			var state = Edits.TryGet(block, out var e) ? e : World.Variants.State(id);
			bool part = state.Type == Registry[BlockRegistry.ObjectName].Id;
			pick = new BlockPick(block, new BlockPos(c.X + n.X, c.Y + n.Y, c.Z + n.Z), n, state, part, t * Voxel.BlockSize);
		}
		LastPick = pick;
		Picked?.Invoke(pick);
	}

	void ReadGpu(BlockPos min, Vector3I size, BlockState missing, Action<BlockState[]> done)
	{
		var gpu = World.Gpu;
		var rd = gpu.Rd;
		if (!World.RingReady || !Renderer.CameraBuffer.IsValid) { Callable.From(() => done(null)).CallDeferred(); return; }
		if (!readKernel.Shader.IsValid) readKernel = gpu.Compile("res://core/scenes/editor/read_box.compute", 64);
		if (!readIds.IsValid) readIds = gpu.Buffer(gpu.Persistent, ReadCapacity * sizeof(uint));
		if (readCamera != Renderer.CameraBuffer) (readCamera, readSet) = (Renderer.CameraBuffer, gpu.Set(readKernel.Shader, (1, Renderer.CameraBuffer), (2, readIds)));
		long n = (long)size.X * size.Y * size.Z, groups = (n + readKernel.X - 1) / readKernel.X;
		uint x = (uint)Math.Min(groups, 65535), y = (uint)((groups + x - 1) / x);
		var push = MemoryMarshal.AsBytes(new[] { new Vector4I(min.X, min.Y, min.Z, 0), new Vector4I(size.X, size.Y, size.Z, (int)(x * readKernel.X)) }.AsSpan()).ToArray();
		long cl = gpu.Begin(readKernel);
		rd.ComputeListBindUniformSet(cl, readSet, 0);
		rd.ComputeListSetPushConstant(cl, push, (uint)push.Length);
		rd.ComputeListDispatch(cl, x, y, 1);
		rd.ComputeListEnd();
		rd.BufferGetDataAsync(readIds, Callable.From<byte[]>(d => Callable.From(() =>
		{
			var ids = MemoryMarshal.Cast<byte, uint>(d);
			var states = new BlockState[n];
			for (int i = 0; i < n; i++) states[i] = ids[i] == uint.MaxValue ? missing : World.Variants.State((int)ids[i]);
			foreach (var (i, s) in EditsIn(Edits, min, size)) states[i] = s;
			done(states);
		}).CallDeferred()), 0, (uint)(n * sizeof(uint)));
	}
}
