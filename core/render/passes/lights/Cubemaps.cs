using Godot;
using System;

[Pass(PassId.Lights, PassStage.Lighting, Order = 1, Enabled = false, Default = "cubemaps", Reads = new[] { TargetId.Depth, TargetId.HitId }, Writes = new[] { TargetId.Lamp })]
public abstract class LightsPass : Variant { }

// lights_maps.compute steps (LM_STEP_*), in dispatch order.
public enum LightMapStep : uint { Changes, Assign, Build }

// v1 block lights: the light lists of the chunks around each hit, a cached 6 x 64^2 depth cube map per light from an LRU pool, 3x3 PCF (docs/v2.md §5.22).
[Variant("cubemaps")]
public sealed class Cubemaps : LightsPass
{
	public const int Slots = 512, Keys = 4096, Builds = 16, MaxSize = 64;
	const int MetaBytes = 16 + Builds * 16 + Slots * 4 + Keys * 4 + Keys * 32; // light_maps.glsli LightMapMeta
	[Tune("", 0, 10, Shader = true)] public float LightIntensity = 1;
	[Tune("", 0.5f, 64, Label = "Light sample clamp (fireflies)", Shader = true)] public float LightClamp = 8;
	[Tune("", 0.5f, 6, Label = "Falloff exponent ((1 - distance / range)^x)", Shader = true)] public float LightFalloff = 2;
	[Tune("", 0, 1, Label = "Wrap (lights grazing surfaces, 0 = Lambert)", Shader = true)] public float LightWrap = 0.5f;
	[Tune("Look", 0, 2, 0.01f, Label = "Light saturation (block light colours)", Shader = true)] public float LightSaturation = 1f;
	[Tune("Look", Label = "Light tint (x, warm or cold)", Shader = true)] public Color LightTint = new(1, 1, 1);
	[Tune("Shadow maps", 8, MaxSize, 8, Label = "Map texels per cube face edge (change rebuilds every map)", Shader = true)] public float LightMapSize = 64;
	[Tune("Shadow maps", 1, Builds, 1, Label = "Maps built per frame", Shader = true)] public float LightMapsPerFrame = 8;
	[Tune("Shadow maps", 8, 256, 1, Label = "Shadowed lights up to (m from the camera, past it unshadowed unless mapped)", Shader = true)] public float LightShadowDistance = 64;
	[Tune("Shadow maps", 0, 4, Label = "Depth bias (texels, slope-scaled)", Shader = true)] public float LightMapBias = 1;
	[Tune("Shadow maps", 0, 3, Label = "Edge softness (PCF tap spacing, texels)", Shader = true)] public float LightMapSoftness = 1;
	[Tune("Shadow maps", 1, 9, 1, Label = "PCF taps (1, 2x2 = 4 or v1 3x3 = 9; 4 bilinear reads each)", Shader = true)] public float LightMapTaps = 4;

	static readonly bool stats = OS.HasEnvironment("LIGHTS_STATS");
	readonly RidGroup owned = new();
	readonly System.Collections.Generic.Dictionary<(bool, bool, bool), (Kernel Maps, Kernel Shade)> kernels = new();
	Kernel maps, shade;
	Rid paramsBuffer, meta, mapData, args, assignSet, buildSet, shadeSet;
	int builtSize, resets = -1;
	bool see, water, bent; // with Transparency (Trans target): maps through glass and fog with a second plane of transmittance; with Water or Liquid (Water/Liquid target) through liquid (Bent: HitPos exists, bent rays' hits from HitPos)
	Vector4 builtSee;
	uint lastFrame = uint.MaxValue - 1;
	long statFrames, statBuilds, statMax;
	string statLine;

	// For GI's bounce hits (light_shade.glsli): the buffers, the params block as LIGHT_PARAMS and the map constants; Tinted = maps with the see-through plane (LM_TINT_PLANE).
	public Rid Meta => meta;
	public Rid MapData => mapData;
	public Rid Params => paramsBuffer;
	public bool Tinted => see || water;
	public string[] ShadeDefines => [$"LIGHT_{ParamsDefine()}", $"LIGHT_MAP_SLOTS {Slots}u", $"LIGHT_MAP_KEYS {Keys}u", $"LIGHT_MAP_BUILDS {Builds}u", $"LIGHT_MAP_MAX {MaxSize}", .. Tinted ? new[] { "LM_TINT_PLANE" } : []];

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		var rd = gpu.Rd;
		paramsBuffer = owned.Add(rd.StorageBufferCreate((uint)ParamsBytes().Length));
		meta = owned.Add(rd.StorageBufferCreate(MetaBytes));
		args = owned.Add(rd.StorageBufferCreate(12, null, RenderingDevice.StorageBufferUsage.Indirect));
	}

	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		bool wantSee = t.Has(TargetId.Trans), wantWater = t.Has(TargetId.Water) || t.Has(TargetId.Liquid), wantBent = t.Has(TargetId.HitPos);
		if (wantSee != see || wantWater != water || wantBent != bent || !mapData.IsValid)
		{
			(see, water, bent) = (wantSee, wantWater, wantBent);
			if (mapData.IsValid) Gpu.Rd.FreeRid(mapData);
			mapData = Gpu.Rd.StorageBufferCreate((uint)Slots * 6 * MaxSize * MaxSize * 4 * (see || water ? 2u : 1u));
			Gpu.Rd.BufferClear(meta, 0, MetaBytes);
			if (!kernels.TryGetValue((see, water, bent), out var k))
			{
				string[] defines = [ParamsDefine(), $"LIGHT_MAP_SLOTS {Slots}u", $"LIGHT_MAP_KEYS {Keys}u", $"LIGHT_MAP_BUILDS {Builds}u", $"LIGHT_MAP_MAX {MaxSize}",
					.. Array.ConvertAll(Enum.GetValues<LightMapStep>(), v => $"LM_STEP_{Gpu.Snake(v.ToString())} {(uint)v}u"), .. see ? new[] { "SEE_THROUGH" } : [], .. water ? new[] { "SEE_WATER" } : [], .. bent ? new[] { "BENT" } : []];
				kernels[(see, water, bent)] = k = (Gpu.Compile("res://core/render/passes/lights/lights_maps.compute", 64, 1, defines), Gpu.Compile("res://core/render/passes/lights/lights_cubemaps.compute", 8, 8, defines));
			}
			(maps, shade) = k;
			assignSet = Gpu.Set(maps.Shader, (0, paramsBuffer), (1, Renderer.CameraBuffer), (5, meta), (6, mapData), (7, args)); // sets go with their buffers or shaders
			buildSet = Gpu.Set(maps.Shader, (0, paramsBuffer), (1, Renderer.CameraBuffer), (5, meta), (6, mapData), (7, meta)); // indirect args must not be bound while read
		}
		shadeSet = Gpu.Set(shade.Shader, [(0, paramsBuffer), (1, Renderer.CameraBuffer), (2, t[TargetId.Depth]), (3, t[TargetId.HitId]), (4, t[TargetId.Lamp]), (5, meta), (6, mapData), .. bent ? new[] { (7, t[TargetId.HitPos]) } : []]);
	}

	public override void Record(Frame f)
	{
		var rd = Gpu.Rd;
		int size = Math.Clamp((int)(LightMapSize + 0.5f), 8, MaxSize);
		var seeKey = see || water ? new Vector4(Segment.GlassTint, ReflectRefract.WaterAbsorption, Segment.VolumeDensity, see ? 1 : 2) : default;
		if (size != builtSize || Renderer.World.RingResets != resets || f.Index != lastFrame + 1 || seeKey != builtSee) { rd.BufferClear(meta, 0, MetaBytes); (builtSize, resets, builtSee) = (size, Renderer.World.RingResets, seeKey); } // a frame without the pass (off, restart), other see-through tuning: maps may be stale
		lastFrame = f.Index;
		var p = ParamsBytes();
		rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p);
		int frame = (int)f.Index;
		long cl = Gpu.Begin(maps);
		rd.ComputeListBindUniformSet(cl, assignSet, 0);
		Gpu.Dispatch(cl, maps, (uint)LightMapStep.Changes, Math.Max(World.LightChanges * World.LightNeighbours, Keys), frame);
		Gpu.Dispatch(cl, maps, (uint)LightMapStep.Assign, maps.X, frame);
		rd.ComputeListEnd();
		cl = Gpu.Begin(maps);
		rd.ComputeListBindUniformSet(cl, buildSet, 0);
		rd.ComputeListSetPushConstant(cl, Gpu.Push((int)LightMapStep.Build, 0, 0, frame), Gpu.PushBytes);
		rd.ComputeListDispatchIndirect(cl, args, 0);
		rd.ComputeListEnd();
		cl = Gpu.Begin(shade);
		rd.ComputeListBindUniformSet(cl, shadeSet, 0);
		rd.ComputeListSetPushConstant(cl, Gpu.Push(frame), Gpu.PushBytes);
		var (x, y) = Gpu.Groups(f.Size, shade);
		rd.ComputeListDispatch(cl, x, y, 1);
		rd.ComputeListEnd();
		if (!stats) return;
		rd.BufferGetDataAsync(meta, Callable.From<byte[]>(Stat), 0, 4);
		if (statLine is not { } line) return;
		statLine = null;
		rd.BufferGetDataAsync(Renderer.World.LightBuffer, Callable.From<byte[]>(h =>
			GD.Print($"{line}, light lists {BitConverter.ToUInt32(h, 4)} / {BitConverter.ToUInt32(h, 8)} (free {Math.Max(BitConverter.ToInt32(h, 0), 0)}, fail {BitConverter.ToUInt32(h, 12)})")), 0, 16);
	}

	// LIGHTS_STATS: maps built per frame over 120 frames, printed with the light list pool (read in the next Record: no reads inside a read's callback).
	void Stat(byte[] d)
	{
		uint n = BitConverter.ToUInt32(d, 0);
		(statBuilds, statMax) = (statBuilds + n, Math.Max(statMax, n));
		if (++statFrames % 120 != 0) return;
		statLine = $"[lights] maps built {statBuilds / 120.0:0.00} per frame (max {statMax})";
		(statBuilds, statMax) = (0, 0);
	}

	public override void Free()
	{
		if (mapData.IsValid) Gpu.Rd.FreeRid(mapData);
		owned.Free(Gpu.Rd);
	}
}
