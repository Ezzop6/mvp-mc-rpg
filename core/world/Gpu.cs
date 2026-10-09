using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Renesance.Content;
using Renesance.World;

// Set 1 of every world-reading shader (WB_* in the prelude); GenBuffers + i for the generator's buffers, so it stays last.
public enum WorldBinding { Dir, Records, Brick4, Brick16, BrickFull, Occupancy, OccupancyMip, World, Types, Tex, Colormaps, ModelData, Items, ColFeatures, Visible, Lights, GenParams, GenBuffers }

public sealed class RidGroup
{
	readonly List<Rid> rids = new();
	public Rid Add(Rid rid) { rids.Add(rid); return rid; }
	public void Free(RenderingDevice rd) // reverse order: resources (and their uniform sets) before shaders
	{
		for (int i = rids.Count - 1; i >= 0; i--) if (rids[i].IsValid) rd.FreeRid(rids[i]);
		rids.Clear();
	}
}

public readonly record struct Kernel(Rid Shader, Rid Pipe, int X = 1, int Y = 1);

// Render thread: RenderingDevice side of the world and renderer: generated prelude, compile, uniform sets (set 1 = the world), dispatch, timestamps.
public sealed class Gpu
{
	public const uint WorldSet = 1, PushBytes = 16;

	public RenderingDevice Rd { get; } = RenderingServer.GetRenderingDevice();
	public readonly RidGroup Persistent = new(), World = new(); // World: buffers of the current ring and pools
	public string Prelude { get; private set; }
	static readonly Dictionary<string, RDShaderSpirV> spirvCache = new(); // by full source: a generator switch back or a pass toggled again skips glslang (render thread)

	readonly Dictionary<int, Rid> worldBindings = new();
	readonly Dictionary<Rid, Rid> worldSets = new(); // per shader
	readonly HashSet<Rid> worldShaders = new();
	string[] includeDirs;
	string generatorModule, generatorPrelude; // generatorPrelude: only where GENERATOR_MODULE is included, so a generator switch leaves the other shaders' sources alone

	public void Init(BlockVariants variants, WorldGenerator generator)
	{
		generatorModule = generator.ShaderModule;
		includeDirs = new[] { "res://core/render", "res://core/world", generator.ShaderModule.GetBaseDir() };
		Prelude = BuildPrelude(variants, generator);
		generatorPrelude = GeneratorPrelude(variants, generator);
	}

	public void Free()
	{
		worldSets.Clear(); // freed with their resources
		World.Free(Rd);
		Persistent.Free(Rd);
		worldBindings.Clear();
		worldShaders.Clear();
	}

	static string BuildPrelude(BlockVariants variants, WorldGenerator generator)
	{
		var sb = new StringBuilder("#version 450\n#extension GL_GOOGLE_cpp_style_line_directive : enable\n#extension GL_EXT_control_flow_attributes : enable\n");
		sb.Append(CultureInfo.InvariantCulture, $"const float BS = {Voxel.BlockSize};\nconst int PERM_SIZE = {Perlin.TableSize};\n");
		sb.Append($"const int CHUNK = {Layout.Chunk}, GROUP = {Layout.Group}, GROUPS_PER_AXIS = {Layout.GroupsPerAxis};\nconst uint GROUPS_PER_CHUNK = {Layout.Groups}u, GROUP_CELLS = {Layout.GroupCells}u, MASK_WORDS = {Layout.MaskWords}u, JOB_BITS = {32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Layout.Groups - 1)}u, CHUNK_REC_WORDS = {Layout.ChunkRecWords}u, BRICK_INDEX_WORDS = {Layout.IndexWords}u, BRICK_BOX_WORD = {Layout.BoxWord}u, CHUNK_STAMP_WORD = {Layout.StampWord}u;\n");
		sb.Append($"const uint BRICK4_WORDS = {Layout.BrickWords(0)}u, BRICK16_WORDS = {Layout.BrickWords(1)}u, BRICK_FULL_WORDS = {Layout.BrickWords(2)}u, BRICK4_PAIRS = {Layout.ClassPairs[0]}u, BRICK16_PAIRS = {Layout.ClassPairs[1]}u;\n");
		sb.Append($"const uint UNI = {Layout.Uni}u, NONE = {Layout.None}u, PART = {Layout.Part}u, PAL_FLAG_SHIFT = {Layout.FlagShift}u, PAL_ID_MASK = {Layout.IdMask}u, FLUID_LEVEL_SHIFT = {Layout.FluidLevelShift}u;\n");
		sb.Append($"const uint CLASS_SHIFT = {Layout.ClassShift}u, CLASS_AIR = {Layout.ClassAir}u, INDEX_MASK = {Layout.IndexMask}u;\n");
		sb.Append($"const uint MAX_COL_FEATURES = {(global::World.MaxColFeatures)}u, FAR_RESERVE_DIV = {(global::World.FarReserveDiv)}u, MID_MARGIN = {(global::World.MidMargin)}u;\n");
		sb.Append($"const uint MAX_CHUNK_LIGHTS = {(global::World.MaxChunkLights)}u, LIGHT_CHANGES = {(global::World.LightChanges)}u, LIGHT_REACH_BLOCKS = {(global::World.LightReach)}u, LIGHT_RING = {(global::World.LightRing)}u, LIGHT_NEIGHBOURS = {(global::World.LightNeighbours)}u, LIGHT_TILE_CHUNKS = {(global::World.LightTileChunks)}u;\n");
		sb.Append($"const int TEX_SIZE = {GpuBlockTable.TexSize}, TEX_MIPS = {GpuBlockTable.TexMips}, TEXELS_PER_LAYER = {GpuBlockTable.TexelsPerLayer};\n");
		sb.Append($"const int MIP_OFS[TEX_MIPS] = int[]({string.Join(", ", GpuBlockTable.MipOffsets[..GpuBlockTable.TexMips])});\n");
		sb.Append($"const uint FACE_LAYER_MASK = {(1 << GpuBlockTable.FaceLayerBits) - 1}u, FACE_OVERLAY_SHIFT = {GpuBlockTable.FaceOverlayShift}u, FACE_ROT_SHIFT = {GpuBlockTable.FaceRotShift}u, " +
			$"FACE_TINT = {1u << GpuBlockTable.FaceTintShift}u, FACE_EMISSION_SHIFT = {GpuBlockTable.FaceEmissionShift}u, FACE_EMISSION_MAX = {GpuBlockTable.FaceEmissionMax}u;\n");
		sb.Append($"const int COLORMAP_SIZE = {GpuBlockTable.ColormapSize};\nconst uint TINT_MAP_SHIFT = {GpuBlockTable.TintMapShift}u;\n");
		sb.Append($"const uint LOOK_MODEL_MASK = {(1u << GpuBlockTable.LookModelBits) - 1}u, LOOK_ROT_SHIFT = {GpuBlockTable.LookRotShift}u, LOOK_WEIGHT_SHIFT = {GpuBlockTable.LookWeightShift}u;\n");
		sb.Append($"const uint YAW_ROT[{Rotation24.Yaw.Length}] = uint[]({string.Join(", ", Rotation24.Yaw.Select(r => $"{r.Value}u"))});\n");
		sb.Append($"const uint MODEL_REC_WORDS = {ModelStore.ModelRecWords}u, MATERIAL_WORDS = {ModelStore.MaterialWords}u, POOL_STRIDE = {ModelStore.PoolStride}u;\nconst int MICRO = {ModelLibrary.Cell};\n");
		sb.Append($"const uint BRICK_REFS = {ModelPool.BrickRefs}u, TAIL_VALUES = {ModelPool.TailValues}u, MIP1_REF = {ModelPool.Mip1Ref}u, MIP2_REF = {ModelPool.Mip2Ref}u, BRICK_VOXELS = {ModelPool.BrickVoxels}u, EMPTY_REF = {ModelPool.Empty}u;\n");
		sb.Append($"const uint WORLD_SET = {WorldSet}u;\n");
		void Enum<T>(string prefix, bool flagsOnly) where T : struct, System.Enum
		{
			foreach (var v in System.Enum.GetValues<T>())
			{
				ulong n = Convert.ToUInt64(v);
				if (!flagsOnly || System.Numerics.BitOperations.IsPow2(n)) sb.Append($"const uint {prefix}{Snake(v.ToString())} = {n}u;\n");
			}
		}
		Enum<BlockFlags>("F_", true);
		Enum<WorldBinding>("WB_", false);
		Enum<WorldPool>("PL_", false);
		Enum<GenStep>("GEN_", false);
		Enum<JobState>("JOB_", false);
		Enum<ModelHeader>("MH_", false);
		Enum<PoolSection>("POOL_", false);
		sb.Append($"const uint {BlockConstant(BlockRegistry.ObjectName)} = {Id(variants, BlockRegistry.ObjectName)}u;\n");
		sb.Append(GBuffer.Prelude());
		sb.Append(generator.ClimateShader());
		return sb.ToString();
	}

	static uint Id(BlockVariants variants, string name) => (uint)variants.Id(variants.Registry[name].Default);

	static string GeneratorPrelude(BlockVariants variants, WorldGenerator generator) =>
		$"const uint FEATURE_CANDIDATES = {generator.FeatureCandidates}u;\n" +
		string.Concat(generator.Blocks.Where(n => n != BlockRegistry.ObjectName).Distinct().Select(n => $"const uint {BlockConstant(n)} = {Id(variants, n)}u;\n")) +
		generator.ShaderConstants();

	// base:glass_red -> B_GLASS_RED, mymod:pipe -> B_MYMOD_PIPE
	public static string BlockConstant(string name)
	{
		var id = BlockId.Parse(name);
		return $"B_{(id.Mod == BlockId.BaseMod ? id.Path : $"{id.Mod}_{id.Path}").ToUpperInvariant()}";
	}

	public static string Snake(string camel) => Regex.Replace(camel, "(?<=[a-z0-9])([A-Z])", "_$1").ToUpperInvariant();

	public static (uint x, uint y) Groups(Vector2I size, Kernel k) => ((uint)((size.X + k.X - 1) / k.X), (uint)((size.Y + k.Y - 1) / k.Y));

	const string GeneratorInclude = "GENERATOR_MODULE";

	string Find(string name, string dir) =>
		name.StartsWith("res://") ? name : new[] { dir }.Concat(includeDirs).Where(d => d != null).Select(d => $"{d}/{name}").FirstOrDefault(Godot.FileAccess.FileExists) ?? throw new FileNotFoundException(name);

	string Source(string path, HashSet<string> seen)
	{
		if (!seen.Add(path)) return "";
		var sb = new StringBuilder($"#line 1 \"{path.GetFile()}\"\n");
		var lines = Godot.FileAccess.GetFileAsString(path).Split('\n'); // ponytail: not a resource, add *.compute/*.glsli to export filters
		for (int i = 0; i < lines.Length; i++)
		{
			var m = Regex.Match(lines[i], "^\\s*#include \"(.+)\"");
			if (!m.Success) { sb.Append(lines[i]).Append('\n'); continue; }
			bool module = m.Groups[1].Value == GeneratorInclude;
			if (module) sb.Append(generatorPrelude);
			sb.Append(Source(Find(module ? generatorModule : m.Groups[1].Value, path.GetBaseDir()), seen)).Append($"#line {i + 2} \"{path.GetFile()}\"\n");
		}
		return sb.ToString();
	}

	// path: res://... or a name in the include dirs (includes: the shader's folder first); defines e.g. DEBUG_VIEW. A shader that declares set 1 gets it bound by Begin.
	public Kernel Compile(string path, int x, int y = 1, params string[] defines)
	{
		string src = Source(Find(path, null), new());
		string head = Prelude + string.Concat(defines.Select(d => $"#define {d}\n")) + $"const uint GROUP_X = {x}u, GROUP_Y = {y}u;\n";
		if (!spirvCache.TryGetValue(head + src, out var spirv))
		{
			spirv = Rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceCompute = head + src, Language = RenderingDevice.ShaderLanguage.Glsl });
			if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException($"{path}: {spirv.CompileErrorCompute}");
			spirvCache[head + src] = spirv;
		}
		var shader = Persistent.Add(Rd.ShaderCreateFromSpirV(spirv, path.GetFile().GetBaseName()));
		if (src.Contains("set = WORLD_SET")) worldShaders.Add(shader);
		return new Kernel(shader, Rd.ComputePipelineCreate(shader), x, y);
	}

	// Vertex and fragment shader from one file (VERTEX / FRAGMENT defined), for a render pipeline; set 0 only.
	public Rid CompileRaster(string path, params string[] defines)
	{
		string head = Prelude + string.Concat(defines.Select(d => $"#define {d}\n")), src = Source(Find(path, null), new());
		var spirv = Rd.ShaderCompileSpirVFromSource(new RDShaderSource { SourceVertex = head + "#define VERTEX\n" + src, SourceFragment = head + "#define FRAGMENT\n" + src, Language = RenderingDevice.ShaderLanguage.Glsl });
		foreach (var e in new[] { spirv.CompileErrorVertex, spirv.CompileErrorFragment })
			if (!string.IsNullOrEmpty(e)) throw new InvalidOperationException($"{path}: {e}");
		return Persistent.Add(Rd.ShaderCreateFromSpirV(spirv, path.GetFile().GetBaseName()));
	}

	// Set 0: textures bind as storage images, the rest as storage buffers.
	public Rid Set(Rid shader, params (int binding, Rid rid)[] bindings) => Set(shader, 0, bindings);

	Rid Set(Rid shader, uint index, IEnumerable<(int binding, Rid rid)> bindings)
	{
		var list = new Godot.Collections.Array<RDUniform>();
		foreach (var (binding, rid) in bindings)
		{
			var u = new RDUniform { UniformType = Rd.TextureIsValid(rid) ? RenderingDevice.UniformType.Image : RenderingDevice.UniformType.StorageBuffer, Binding = binding };
			u.AddId(rid);
			list.Add(u);
		}
		return Rd.UniformSetCreate(list, shader, index);
	}

	// Into set 1 of every world-reading shader; their sets are made again on the next Begin.
	public void Publish(WorldBinding binding, Rid rid, int index = 0)
	{
		worldBindings[(int)binding + index] = rid;
		foreach (var s in worldSets.Values) if (Rd.UniformSetIsValid(s)) Rd.FreeRid(s);
		worldSets.Clear();
	}

	public long Begin(Kernel k)
	{
		long cl = Rd.ComputeListBegin();
		Rd.ComputeListBindComputePipeline(cl, k.Pipe);
		if (worldShaders.Contains(k.Shader))
		{
			if (!worldSets.TryGetValue(k.Shader, out var w)) worldSets[k.Shader] = w = Set(k.Shader, WorldSet, worldBindings.Select(b => (b.Key, b.Value)));
			Rd.ComputeListBindUniformSet(cl, w, WorldSet);
		}
		return cl;
	}

	// Push constant: step, count, invocations per dispatch row, first item; groupPerItem = a workgroup per item.
	public void Dispatch(long cl, Kernel k, uint step, long count, int first = 0, bool groupPerItem = false)
	{
		if (count <= 0) return;
		int groupSize = k.X * k.Y;
		long wgs = groupPerItem ? count : (count + groupSize - 1) / groupSize;
		uint x = (uint)Math.Min(wgs, 65535), y = (uint)((wgs + x - 1) / x);
		Rd.ComputeListSetPushConstant(cl, Push((int)step, (int)count, (int)(groupPerItem ? x : x * groupSize), first), PushBytes);
		Rd.ComputeListDispatch(cl, x, y, 1);
		Rd.ComputeListAddBarrier(cl);
	}

	// Push constants in a per-thread buffer: Godot copies it in the call that takes it.
	public static byte[] Push(int a, int b = 0, int c = 0, int d = 0)
	{
		var pc = push ??= new byte[PushBytes];
		BitConverter.TryWriteBytes(pc.AsSpan(0), a);
		BitConverter.TryWriteBytes(pc.AsSpan(4), b);
		BitConverter.TryWriteBytes(pc.AsSpan(8), c);
		BitConverter.TryWriteBytes(pc.AsSpan(12), d);
		return pc;
	}
	[ThreadStatic] static byte[] push;

	public static byte[] Bytes<T>(in T v) where T : struct => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in v)).ToArray();
	public static void Write<T>(in T v, byte[] into) where T : struct => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in v)).CopyTo(into);

	public Rid Buffer(RidGroup owner, long bytes, byte[] data = null, RenderingDevice.StorageBufferUsage usage = 0) =>
		owner.Add(Rd.StorageBufferCreate((uint)bytes, data, usage));

	public void Timestamp(string name) => Rd.CaptureTimestamp(name);

	// Previous frame's timestamps whose name starts with prefix, GPU ns.
	public Dictionary<string, ulong> ReadTimestamps(string prefix)
	{
		var t = new Dictionary<string, ulong>();
		uint n = Rd.GetCapturedTimestampsCount();
		for (uint i = 0; i < n; i++)
		{
			string name = Rd.GetCapturedTimestampName(i);
			if (name.StartsWith(prefix, StringComparison.Ordinal)) t[name] = Rd.GetCapturedTimestampGpuTime(i);
		}
		return t;
	}

	// Once per frame (Renderer.RenderFrame): the previous frame's timestamps into Captured, read by the passes and the world (Godot still makes a string per name).
	public void CaptureTimestamps()
	{
		Captured.Clear();
		uint n = Rd.GetCapturedTimestampsCount();
		for (uint i = 0; i < n; i++) Captured[Rd.GetCapturedTimestampName(i)] = Rd.GetCapturedTimestampGpuTime(i);
	}
	public readonly Dictionary<string, ulong> Captured = new();
}
