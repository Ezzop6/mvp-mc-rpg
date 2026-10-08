using Godot;
using System;

[Pass(PassId.GI, PassStage.Lighting, Order = 2, Enabled = false, Default = "cache", Reads = new[] { TargetId.Depth, TargetId.HitId }, Writes = new[] { TargetId.Gi })]
public abstract class GiPass : Variant { }

// gi_cache.compute steps (GI_STEP_*), in dispatch order.
public enum GiStep : uint { Reset, Sample, Resolve, Evict, Gather }

// v1 one-bounce GI cached in world space per (cell, level, face): a bounce ray per n x n pixels a frame into the cell, every pixel reads its cells bilinearly; a cell whose L0 chunk changed (edit, streaming) starts again.
[Variant("cache")]
public sealed class Cache : GiPass
{
	public const int Cells = 1 << 21, EvictSlices = 64; // Cells: a power of two
	const int CellBytes = 32;
	[Tune("", 0, 4, 0.05f, Label = "GI strength", Shader = true)] public float GiStrength = 1f;
	[Tune("", 1, 64, 0.5f, Label = "Bounce range (m)", Shader = true)] public float GiRange = 16f;
	[Tune("", 0, 128, 1, Label = "Sun shadow at bounce hits up to (m, 0 = unshadowed)", Shader = true)] public float GiShadowDistance = 32f;
	[Tune("", 0, 4, 0.05f, Label = "Bounce strength (light from surfaces; the sky stays)", Shader = true)] public float GiBounce = 1.25f;
	[Tune("", 0, 1, 0.01f, Label = "Ambient on bounce hits (further bounces, caves not black)", Shader = true)] public float GiBounceAmbient = 0.3f;
	[Tune("", 0, 1, 0.01f, Label = "Ambient floor (x the flat ambient, added: never fully dark)", Shader = true)] public float GiAmbient = 0.1f;
	[Tune("", 0, 20, 0.1f, Label = "Block emission in the bounce (face emission 1, x the albedo)", Shader = true)] public float GiEmission = 4f;
	[Tune("", 0.25f, 16, 0.25f, Label = "Bounce clamp (fireflies)", Shader = true)] public float GiClamp = 4f;
	[Tune("Look", 0, 2, 0.01f, Label = "GI saturation (bounced colour)", Shader = true)] public float GiSaturation = 1.5f;
	[Tune("Look", Label = "GI tint (x)", Shader = true)] public Color GiTint = new(1, 1, 1);
	[Tune("Cache", 2, 16, 1, Label = "Sample spacing (px: a bounce ray per n x n pixels a frame, power of two)", Shader = true)] public float GiSamplePx = 8f;
	[Tune("Cache", 2, 32, 1, Label = "Cell size on screen (px)", Shader = true)] public float GiCellPx = 8f;
	[Tune("Cache", 4, 256, 1, Label = "History (frames, fewer react faster but are noisier)", Shader = true)] public float GiHistory = 128f;

	readonly System.Collections.Generic.Dictionary<string, Kernel> kernels = new();
	readonly RidGroup owned = new();
	Kernel kernel;
	Rid paramsBuffer, cells, keys, touched, set;
	int touchedCap, resets = -1;
	uint lastFrame = uint.MaxValue - 1;
	(Rid Map, Rid Meta, bool Tinted)? boundLights;
	static (Rid Map, Rid Meta, bool Tinted)? LightsKey(Cubemaps l) => l == null ? null : (l.MapData, l.Meta, l.Tinted);
	bool bound;

	int SamplePx => 1 << System.Numerics.BitOperations.Log2((uint)Math.Clamp((int)(GiSamplePx + 0.5f), 2, 16)); // as gi_cache.compute: rounded down to a power of two

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		var rd = gpu.Rd;
		paramsBuffer = owned.Add(rd.StorageBufferCreate((uint)ParamsBytes().Length));
		cells = owned.Add(rd.StorageBufferCreate((uint)Cells * CellBytes));
		keys = owned.Add(rd.StorageBufferCreate((uint)Cells * 4));
	}

	// Touched cells: two per sample at the densest spacing; the set is made in Record, which knows whether Lights records.
	public override void Resize(Vector2I size)
	{
		if (touched.IsValid) Gpu.Rd.FreeRid(touched);
		touchedCap = 2 * ((size.X + 1) / 2) * ((size.Y + 1) / 2);
		touched = Gpu.Rd.StorageBufferCreate((uint)(touchedCap + 4) * 4);
		bound = false;
	}

	// With Water (its HitPos target exists) bent rays' hits come from HitPos, with Transparency (Trans) rays pass see-through blocks, with Liquid (Liquid target) rays pass and attenuate through liquid, with Lights recording its maps light the bounce hits.
	void Bind(Cubemaps lights)
	{
		var t = Renderer.Targets;
		bool bent = t.Has(TargetId.HitPos), see = t.Has(TargetId.Trans), liquid = t.Has(TargetId.Liquid);
		string[] defines = [ParamsDefine(), $"GI_CELLS {Cells}u", $"GI_EVICT_SLICES {EvictSlices}u", .. Array.ConvertAll(Enum.GetValues<GiStep>(), v => $"GI_STEP_{Gpu.Snake(v.ToString())} {(uint)v}u"),
			.. bent ? new[] { "BENT" } : [], .. see ? new[] { "SEE_THROUGH" } : [], .. liquid ? new[] { "SEE_WATER" } : [], .. lights != null ? ["LAMP", .. lights.ShadeDefines] : Array.Empty<string>()];
		string key = string.Join("|", defines);
		if (!kernels.TryGetValue(key, out kernel))
			kernels[key] = kernel = Gpu.Compile("res://core/render/passes/gi/gi_cache.compute", 8, 8, defines);
		set = Gpu.Set(kernel.Shader, [(0, paramsBuffer), (1, Renderer.CameraBuffer), (2, t[TargetId.Depth]), (3, t[TargetId.HitId]), (4, t[TargetId.Gi]), (10, keys), (11, cells), (12, touched),
			.. bent ? new[] { (8, t[TargetId.HitPos]) } : [], .. lights != null ? new[] { (5, lights.Meta), (6, lights.MapData), (9, lights.Params) } : []]);
		(boundLights, bound) = (LightsKey(lights), true);
	}

	public override void Record(Frame f)
	{
		var rd = Gpu.Rd;
		var lights = Renderer.Recording(PassId.Lights) as Cubemaps;
		if (lights is not { Initialized: true } || !lights.MapData.IsValid) lights = null;
		if (!bound || boundLights != LightsKey(lights) || !rd.UniformSetIsValid(set)) Bind(lights);
		if (Renderer.World.RingResets != resets || f.Index != lastFrame + 1) { rd.BufferClear(keys, 0, (uint)Cells * 4); rd.BufferClear(cells, 0, (uint)Cells * CellBytes); resets = Renderer.World.RingResets; } // a new world, a restart or frames without the pass: the light may have changed
		lastFrame = f.Index;
		var p = ParamsBytes();
		rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p);
		int s = SamplePx;
		var blocks = new Vector2I((f.Size.X + s - 1) / s, (f.Size.Y + s - 1) / s);
		long cl = Gpu.Begin(kernel);
		rd.ComputeListBindUniformSet(cl, set, 0);
		Gpu.Dispatch(cl, kernel, (uint)GiStep.Reset, 1);
		Gpu.Dispatch(cl, kernel, (uint)GiStep.Sample, blocks.X * blocks.Y, blocks.X);
		Gpu.Dispatch(cl, kernel, (uint)GiStep.Resolve, Math.Min(2 * blocks.X * blocks.Y, touchedCap));
		Gpu.Dispatch(cl, kernel, (uint)GiStep.Evict, Cells / EvictSlices);
		rd.ComputeListSetPushConstant(cl, Gpu.Push((int)GiStep.Gather), Gpu.PushBytes);
		var (x, y) = Gpu.Groups((f.Size + Vector2I.One) / 2, kernel);
		rd.ComputeListDispatch(cl, x, y, 1);
		rd.ComputeListEnd();
	}

	public override void Free()
	{
		if (touched.IsValid) Gpu.Rd.FreeRid(touched);
		owned.Free(Gpu.Rd);
	}
}
