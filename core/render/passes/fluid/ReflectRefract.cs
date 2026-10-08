using Godot;

[Pass(PassId.Fluid, PassStage.Atmosphere, Order = 5, Enabled = false, Default = "reflect-refract", Reads = new[] { TargetId.Water, TargetId.HitPos, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class FluidPass : Variant { }

// Fluid as v1 (docs/v2.md §5.24): Primary refracts at the surface and goes on under water (WATER while the Water target exists), the lighting passes light what it found; this pass adds absorption, deep colour and the Fresnel reflection (one trace) over Color after the sky.
[Variant("reflect-refract")]
public sealed class ReflectRefract : FluidPass
{
	[Tune("Water", 0, 4, Label = "Water absorption")] public static float WaterAbsorption = 0.6f;
	[Tune("Water", 0, 1, Label = "Water reflectance (F0)")] public static float WaterReflectance = 0.02f;
	[Tune("Water", 1, 2, Label = "Water index of refraction")] public static float WaterIor = 1.33f;
	[Tune("Water", 0, 4, Label = "Wave height")] public static float WaterWaves = 1f;
	[Tune("Water", 0, 4, Label = "Wave speed (game clock)")] public static float WaterWaveSpeed = 1f;
	[Tune("Water", 0, 2, Label = "Deep water brightness")] public static float WaterDeep = 0.4f;
	[Tune("Water", 0, 2, Label = "Sun in deep water")] public static float WaterDeepSun = 0.25f;
	[Tune("Water", Label = "Deep water tint (x the water colour)")] public static Color WaterDeepTint = new(1, 1, 1);
	[Tune("Water", 0, 20, Label = "Sun glint")] public static float WaterGlint = 4f;
	[Tune("Water", 10, 2000, 10, Label = "Sun glint sharpness")] public static float WaterGlintFocus = 400f;

	Kernel kernel;
	Rid set;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		kernel = gpu.Compile("res://core/render/passes/fluid/water_reflect_refract.compute", 8, 8, Renderer.Defines);
	}

	public override void Resize(Vector2I size)
	{
		var t = Renderer.Targets;
		set = Gpu.Set(kernel.Shader, (1, Renderer.CameraBuffer), (2, t[TargetId.Color]), (3, t[TargetId.Water]), (4, t[TargetId.HitPos]));
	}

	public override void Record(Frame f) => Dispatch(kernel, set, f.Size);
}
