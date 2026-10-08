using Godot;

// Fluid/fluid (docs/v2.md §5.24): a cheap liquid for every liquid (registry kind: liquid, water, lava and future ones). Primary with Liquid goes straight through a translucent liquid (Liquid: transmittance and first surface, LiquidId: its variant); this pass composes the lit bottom with Beer-Lambert absorption, the deep tint and the camera-distance fade, an analytic v1 wave Fresnel sky reflection (no ray trace) and lava's emission. The legacy reflect-refract keeps the traced water look.
[Variant("fluid", Reads = new[] { TargetId.Liquid, TargetId.LiquidId, TargetId.HitId, TargetId.Albedo, TargetId.Color }, Writes = new[] { TargetId.Color })]
public sealed class Fluid : FluidPass
{
	[Tune("Fluid", 0, 512, 8, Label = "Fade start (blocks, clear)")] public static float FluidFadeStart = 48f;
	[Tune("Fluid", 8, 4096, 8, Label = "Fade end (blocks, opaque)")] public static float FluidFadeEnd = 512f;
	[Tune("Fluid", 0, 1, Label = "Reflectance (F0)")] public static float FluidReflectance = 0.02f;
	[Tune("Fluid", 0, 4, Label = "Wave height")] public static float FluidWaves = 1f;
	[Tune("Fluid", 0, 4, Label = "Wave speed (game clock)")] public static float FluidWaveSpeed = 1f;
	[Tune("Fluid", 0, 2, Label = "Deep brightness")] public static float FluidDeep = 0.4f;
	[Tune("Fluid", 0, 2, Label = "Sun in deep")] public static float FluidDeepSun = 0.25f;
	[Tune("Fluid", 0, 20, Label = "Sun glint")] public static float FluidGlint = 4f;
	[Tune("Fluid", 10, 2000, 10, Label = "Sun glint sharpness")] public static float FluidGlintFocus = 400f;
	[Tune("Fluid", 0, 50, Label = "Emission (lava, x the fluid albedo)")] public static float FluidEmission = 12f;
	const int ParamFloats = 12;

	Kernel kernel;
	Rid set, paramsBuffer;
	readonly float[] values = new float[ParamFloats];
	readonly byte[] packed = new byte[ParamFloats * 4];

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		paramsBuffer = gpu.Buffer(gpu.Persistent, packed.Length);
		kernel = gpu.Compile("res://core/render/passes/fluid/fluid.compute", 8, 8, Renderer.Defines);
	}

	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		set = Gpu.Set(kernel.Shader, (0, paramsBuffer), (1, Renderer.CameraBuffer), (2, t[TargetId.Color]), (3, t[TargetId.Liquid]), (4, t[TargetId.LiquidId]), (5, t[TargetId.HitId]), (6, t[TargetId.Albedo]));
	}

	public override void Record(Frame f)
	{
		values[0] = FluidFadeStart; values[1] = FluidFadeEnd; values[2] = FluidReflectance; values[3] = FluidWaves;
		values[4] = (float)(Renderer.World.Clock * FluidWaveSpeed); values[5] = FluidDeep; values[6] = FluidDeepSun; values[7] = FluidGlint;
		values[8] = FluidGlintFocus; values[9] = FluidEmission; values[10] = 0; values[11] = 0;
		System.Buffer.BlockCopy(values, 0, packed, 0, packed.Length);
		Gpu.Rd.BufferUpdate(paramsBuffer, 0, (uint)packed.Length, packed);
		Dispatch(kernel, set, f.Size);
	}
}
