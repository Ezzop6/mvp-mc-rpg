using Godot;
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renesance.World;
using DataFormat = Godot.RenderingDevice.DataFormat;
using Usage = Godot.RenderingDevice.TextureUsageBits;

// A performance path, not an effect: "All effects" leaves it on (v1 Raster).
[Pass(PassId.Raster, PassStage.Primary, Order = -1, Effect = false, Default = "meshes", Writes = new[] { TargetId.Start })]
public abstract class RasterPass : Variant { }

// raster_build.compute and raster_cull.compute steps (MESH_*), in dispatch order.
public enum MeshStep : uint { Reset, Dirty, Check, Args, Build, Finalize, CullEarly, ApplyFree, CullLate, Start }

// Meshes of the chunks within MeshRadius, drawn with two-phase occlusion culling; Primary/dda starts each ray where it crosses the face drawn in its pixel (docs/v2.md §5.15).
[Variant("meshes")]
public sealed class Meshes : RasterPass
{
	[Tune("", 50, 1000, 12.5f)] public float MeshRadius = 500; // m
	[Tune("", 16, MaxBuilds, 16)] public float BuildsPerFrame = 1024;
	public const int PageQuads = 32, QuadIndices = 6, BudgetMB = 256, MaxBuilds = 4096, Shell = 2, CullGroup = 128, BuildGroup = 160;
	const string Dir = "res://core/render/passes/raster/";

	[StructLayout(LayoutKind.Sequential)]
	struct DrawArgs { public uint IndexCount, InstanceCount, FirstIndex, VertexOffset, FirstInstance; }

	[StructLayout(LayoutKind.Sequential)]
	struct State // mesh.glsli MeshState
	{
		public uint FreeCount, Taken, Popped, Pushed, FailCount, PageCap, QuadCount, PageHigh, ListCount, MinStale, Inside, Total;
		public int Ring;
		public float Trust;
		public uint CamSolid, Pad0, BuildX, BuildY, BuildZ, CullX, CullY, CullZ, Pad1, Pad2;
		public DrawArgs Draw0, Draw1;
	}

	static readonly uint BuildArgs = (uint)Marshal.OffsetOf<State>(nameof(State.BuildX)), CullArgs = (uint)Marshal.OffsetOf<State>(nameof(State.CullX)), Draws = (uint)Marshal.OffsetOf<State>(nameof(State.Draw0));

	// The pool and the mesh slots, handed to the next instance when the renderer re-creates its variants (Adopt).
	sealed class Store
	{
		public readonly RidGroup Pool = new(), Sphere = new();
		public Rid Sampler, Quads, Pages, Stack, State, Visible, PageVis, List, Indices, Slots, Offsets;
		public uint PageCap;
		public int Radius, Inside, Total, Ring, Resets = -1;
		public bool Reset;
	}

	Store s = new();
	Kernel build, cull, hiz;
	Rid pipeline, drawShader;
	long fbFormat;
	readonly RidGroup targets = new();
	Rid vis, depth, framebuffer, hizTex, buildSet, cullSet, drawSet;
	Rid[] hizSets = [];
	Vector2I[] hizSizes = [];
	int frames;
	volatile string stats;

	public override string DebugStats => stats;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		string[] defines = [.. Enum.GetValues<MeshStep>().Select(s => $"MESH_{(Gpu.Snake(s.ToString()))} {(uint)s}u"), $"PAGE_QUADS {PageQuads}u", $"QUAD_INDICES {QuadIndices}u"];
		build = gpu.Compile("res://core/render/passes/raster/raster_build.compute", BuildGroup, 1, defines);
		if (build.X < 6 * Layout.Chunk) throw new InvalidOperationException("raster_build.compute: GROUP_X < 6 * CHUNK (MESH_BUILD: thread per slice)");
		cull = gpu.Compile("res://core/render/passes/raster/raster_cull.compute", CullGroup, 1, defines);
		hiz = gpu.Compile("res://core/render/passes/raster/raster_hiz.compute", 8, 8);
		drawShader = gpu.CompileRaster(Dir + "raster_draw.raster", defines);
		var rd = gpu.Rd;
		fbFormat = rd.FramebufferFormatCreate(new Godot.Collections.Array<RDAttachmentFormat>
		{
			new() { Format = DataFormat.R16G16B16A16Uint, UsageFlags = (uint)(Usage.ColorAttachmentBit | Usage.StorageBit) },
			new() { Format = DataFormat.D32Sfloat, UsageFlags = (uint)(Usage.DepthStencilAttachmentBit | Usage.SamplingBit) },
		});
		pipeline = gpu.Persistent.Add(rd.RenderPipelineCreate(drawShader, fbFormat, RenderingDevice.InvalidFormatId, RenderingDevice.RenderPrimitive.Triangles,
			new RDPipelineRasterizationState { CullMode = RenderingDevice.PolygonCullMode.Back, FrontFace = RenderingDevice.PolygonFrontFace.CounterClockwise },
			new RDPipelineMultisampleState(),
			new RDPipelineDepthStencilState { EnableDepthTest = true, EnableDepthWrite = true, DepthCompareOperator = RenderingDevice.CompareOperator.Greater },
			new RDPipelineColorBlendState { Attachments = new() { new RDPipelineColorBlendStateAttachment() } }));
	}

	public override void Adopt(Variant old)
	{
		if (old is Meshes m) (s, m.s) = (m.s, s);
	}

	// The page pool on first use (a disabled pass takes no memory).
	void Allocate()
	{
		var rd = Gpu.Rd;
		uint cap = s.PageCap = (uint)((long)BudgetMB * 1048576 / (PageQuads * 4 + 32 + 4 + 8 + 4));
		Rid Buf(long bytes, byte[] data = null, RenderingDevice.StorageBufferUsage usage = 0) => Gpu.Buffer(s.Pool, bytes, data, usage);
		s.Quads = Buf(cap * PageQuads * 4L);
		s.Pages = Buf(cap * 32L);
		s.Stack = Buf(cap * 4L);
		s.Visible = Buf(cap * 8L);
		s.PageVis = Buf(cap * 4L, new byte[cap * 4]);
		s.List = Buf(MaxBuilds * 4);
		s.State = Buf(Unsafe.SizeOf<State>(), null, RenderingDevice.StorageBufferUsage.Indirect);
		var bytes = new byte[PageQuads * QuadIndices * 2];
		int[] corners = [0, 1, 2, 0, 2, 3];
		for (int q = 0; q < PageQuads; q++)
			for (int k = 0; k < QuadIndices; k++) BitConverter.TryWriteBytes(bytes.AsSpan((q * QuadIndices + k) * 2), (ushort)(q * 4 + corners[k]));
		s.Indices = rd.IndexArrayCreate(s.Pool.Add(rd.IndexBufferCreate(PageQuads * QuadIndices, RenderingDevice.IndexBufferFormat.Uint16, bytes)), 0, PageQuads * QuadIndices);
		s.Sampler = s.Pool.Add(rd.SamplerCreate(new RDSamplerState()));
	}

	// Chunk offsets of the sphere and its shell, nearest first, and the mesh slot ring that holds them without aliasing.
	void Configure(int r)
	{
		s.Sphere.Free(Gpu.Rd);
		s.Radius = r;
		int rs = r + Shell;
		var o = (from z in Enumerable.Range(-rs, 2 * rs + 1) from y in Enumerable.Range(-rs, 2 * rs + 1) from x in Enumerable.Range(-rs, 2 * rs + 1)
				 let d = x * x + y * y + z * z where d <= rs * rs orderby d select (d, (uint)(x + 128 | y + 128 << 8 | z + 128 << 16))).ToArray();
		(s.Inside, s.Total, s.Ring) = (o.Count(e => e.d <= r * r), o.Length, 2 * rs + 2);
		s.Offsets = Gpu.Buffer(s.Sphere, s.Total * 4L, MemoryMarshal.AsBytes(o.Select(e => e.Item2).ToArray().AsSpan()).ToArray());
		s.Slots = Gpu.Buffer(s.Sphere, (long)s.Ring * s.Ring * s.Ring * 16);
		s.Reset = true;
	}

	public override void Resize(Vector2I size)
	{
		var rd = Gpu.Rd;
		if (!s.State.IsValid) Allocate();
		if (s.Radius == 0) Configure(Radius());
		targets.Free(rd);
		vis = targets.Add(Texture(DataFormat.R16G16B16A16Uint, size, Usage.ColorAttachmentBit | Usage.StorageBit));
		depth = targets.Add(Texture(DataFormat.D32Sfloat, size, Usage.DepthStencilAttachmentBit | Usage.SamplingBit));
		framebuffer = targets.Add(rd.FramebufferCreate(new Godot.Collections.Array<Rid> { vis, depth }, fbFormat));
		var b = new Vector2I((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(size.X + 1) / 2), (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(size.Y + 1) / 2));
		int mips = System.Numerics.BitOperations.Log2((uint)Math.Max(b.X, b.Y)) + 1;
		hizTex = targets.Add(Texture(DataFormat.R32Sfloat, b, Usage.StorageBit | Usage.SamplingBit, mips));
		(hizSets, hizSizes) = (new Rid[mips], new Vector2I[mips]);
		Rid prev = depth;
		for (int m = 0; m < mips; m++)
		{
			var mip = rd.TextureCreateSharedFromSlice(new RDTextureView(), hizTex, 0, (uint)m, 1, RenderingDevice.TextureSliceType.Slice2D); // freed with hizTex
			hizSizes[m] = new Vector2I(Math.Max(1, b.X >> m), Math.Max(1, b.Y >> m));
			hizSets[m] = Set(hiz, (0, prev, true), (1, mip, false));
			prev = mip;
		}
		Bind();
	}

	void Bind()
	{
		var cam = Renderer.CameraBuffer;
		buildSet = Set(build, (1, cam, false), (2, s.Quads, false), (3, s.Pages, false), (4, s.Stack, false), (5, s.State, false), (6, s.Slots, false), (7, s.Offsets, false), (8, s.List, false),
			(9, vis, false), (10, Renderer.Targets[TargetId.Start], false));
		cullSet = Set(cull, (1, cam, false), (3, s.Pages, false), (4, s.Stack, false), (5, s.State, false), (6, s.Slots, false), (7, s.Offsets, false), (11, s.Visible, false), (12, s.PageVis, false), (13, hizTex, true));
		drawSet = Gpu.Rd.UniformSetCreate(Uniforms((1, cam, false), (2, s.Quads, false), (3, s.Pages, false), (11, s.Visible, false)), drawShader, 0);
	}

	Godot.Collections.Array<RDUniform> Uniforms(params (int Binding, Rid Rid, bool Sampled)[] bindings)
	{
		var list = new Godot.Collections.Array<RDUniform>();
		foreach (var (b, rid, sampled) in bindings)
		{
			var u = new RDUniform { Binding = b, UniformType = sampled ? RenderingDevice.UniformType.SamplerWithTexture : Gpu.Rd.TextureIsValid(rid) ? RenderingDevice.UniformType.Image : RenderingDevice.UniformType.StorageBuffer };
			if (sampled) u.AddId(s.Sampler);
			u.AddId(rid);
			list.Add(u);
		}
		return list;
	}

	Rid Set(Kernel k, params (int Binding, Rid Rid, bool Sampled)[] bindings) => Gpu.Rd.UniformSetCreate(Uniforms(bindings), k.Shader, 0);

	Rid Texture(DataFormat format, Vector2I size, Usage usage, int mips = 1) =>
		Gpu.Rd.TextureCreate(new RDTextureFormat { Format = format, Width = (uint)size.X, Height = (uint)size.Y, Mipmaps = (uint)mips, UsageBits = usage }, new RDTextureView());

	// Chunks, kept well inside the L0 sphere (the mesh slots and the directory must not alias) and inside L0's band (docs/v2.md §5.17): past it rays go coarser and a coarse surface may stand in front of a face.
	int Radius()
	{
		var w = Renderer.World;
		float d1 = w.Lods is { Levels: > 1 } l ? l.SwitchM[1] - 3 * Voxel.ChunkMeters : float.MaxValue;
		return Math.Clamp((int)(Math.Min(MeshRadius, d1) / Voxel.ChunkMeters), 1, Math.Max(1, (w.Ring?.Reach ?? 1) - Shell - 2));
	}

	public override void Record(Frame f)
	{
		var rd = Gpu.Rd;
		if (Radius() != s.Radius) { Configure(Radius()); Bind(); }
		if (Renderer.World.RingResets != s.Resets) (s.Resets, s.Reset) = (Renderer.World.RingResets, true);
		int budget = Math.Clamp((int)BuildsPerFrame, 1, MaxBuilds);
		if (s.Reset)
		{
			var b = Gpu.Bytes(new State { PageCap = s.PageCap, Inside = (uint)s.Inside, Total = (uint)s.Total, Ring = s.Ring });
			rd.BufferUpdate(s.State, 0, (uint)b.Length, b);
		}
		long cl = Gpu.Begin(build);
		rd.ComputeListBindUniformSet(cl, buildSet, 0);
		if (s.Reset) Gpu.Dispatch(cl, build, (uint)MeshStep.Reset, Math.Max(s.PageCap, (long)s.Ring * s.Ring * s.Ring));
		s.Reset = false;
		Gpu.Dispatch(cl, build, (uint)MeshStep.Dirty, s.Total);
		Gpu.Dispatch(cl, build, (uint)MeshStep.Check, s.Inside, budget);
		Gpu.Dispatch(cl, build, (uint)MeshStep.Args, 1, budget);
		rd.ComputeListSetPushConstant(cl, Gpu.Push((int)MeshStep.Build), Gpu.PushBytes);
		rd.ComputeListDispatchIndirect(cl, s.State, BuildArgs);
		rd.ComputeListAddBarrier(cl);
		Gpu.Dispatch(cl, build, (uint)MeshStep.Finalize, 1, CullGroup);
		rd.ComputeListEnd();

		Cull(MeshStep.CullEarly, true);
		Draw(0);
		cl = Gpu.Begin(hiz);
		for (int m = 0; m < hizSets.Length; m++)
		{
			rd.ComputeListBindUniformSet(cl, hizSets[m], 0);
			var (x, y) = Gpu.Groups(hizSizes[m], hiz);
			rd.ComputeListDispatch(cl, x, y, 1);
			rd.ComputeListAddBarrier(cl);
		}
		rd.ComputeListEnd();
		Cull(MeshStep.CullLate, false);
		Draw(1);

		cl = Gpu.Begin(build);
		rd.ComputeListBindUniformSet(cl, buildSet, 0);
		Gpu.Dispatch(cl, build, (uint)MeshStep.Start, (long)f.Size.X * f.Size.Y);
		rd.ComputeListEnd();
		if (frames++ % 30 == 0) rd.BufferGetDataAsync(s.State, Callable.From<byte[]>(OnState));
	}

	void Cull(MeshStep step, bool applyFree)
	{
		var rd = Gpu.Rd;
		long cl = Gpu.Begin(cull);
		rd.ComputeListBindUniformSet(cl, cullSet, 0);
		rd.ComputeListSetPushConstant(cl, Gpu.Push((int)step), Gpu.PushBytes);
		rd.ComputeListDispatchIndirect(cl, s.State, CullArgs);
		rd.ComputeListAddBarrier(cl);
		if (applyFree) Gpu.Dispatch(cl, cull, (uint)MeshStep.ApplyFree, 1);
		rd.ComputeListEnd();
	}

	void Draw(int k)
	{
		var rd = Gpu.Rd;
		long dl = rd.DrawListBegin(framebuffer, k == 0 ? RenderingDevice.DrawFlags.ClearAll : RenderingDevice.DrawFlags.DefaultAll, [new Color(0, 0, 0, 0)], 0f);
		rd.DrawListBindRenderPipeline(dl, pipeline);
		rd.DrawListBindUniformSet(dl, drawSet, 0);
		rd.DrawListBindIndexArray(dl, s.Indices);
		rd.DrawListSetPushConstant(dl, Gpu.Push(k * (int)s.PageCap), Gpu.PushBytes);
		rd.DrawListDrawIndirect(dl, true, s.State, Draws + (uint)(k * Unsafe.SizeOf<DrawArgs>()));
		rd.DrawListEnd();
	}

	void OnState(byte[] d)
	{
		var m = MemoryMarshal.Read<State>(d);
		uint cap = m.PageCap, used = cap - m.FreeCount;
		stats = $"raster: pages {used}/{cap} ({(used * (PageQuads * 4 + 32) / 1048576.0).ToString("0", CultureInfo.InvariantCulture)} MB), quads {m.QuadCount}, drawn {m.Draw0.InstanceCount}+{m.Draw1.InstanceCount}, stale {m.ListCount}, fail {m.FailCount}, trust {(m.Trust * Voxel.BlockSize).ToString("0", CultureInfo.InvariantCulture)} m";
	}

	public override void Free()
	{
		var rd = Gpu.Rd;
		targets.Free(rd);
		s.Sphere.Free(rd);
		s.Pool.Free(rd);
		s = new Store();
	}
}
