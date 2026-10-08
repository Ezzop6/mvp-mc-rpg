using Godot;

[Pass(PassId.Shafts, PassStage.Atmosphere, Order = 9, Enabled = false, Default = "quarter-res", Reads = new[] { TargetId.Depth, TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class ShaftsPass : AtmosphereVariant { }

// v1 shafts: marched into an own texture at a quarter of the render size per axis, then added to Color.
[Variant("quarter-res")]
public sealed class ShaftsQuarter : ShaftsPass
{
	[Tune("", 0, 2, Shader = true)] public float ShaftIntensity = 0.35f;
	[Tune("", 0.5f, 1, 0.005f, Shader = true)] public float ShaftDecay = 0.93f;
	[Tune("", 1, 32, Label = "Shaft sharpness", Shader = true)] public float ShaftFocus = 6f;
	[Tune("", 0, 1, Label = "Clouds block shafts", Shader = true)] public float ShaftClouds = 1f;
	public Vector4 CloudCover, CloudDrift; // Clouds' layer this frame: coverage (0 without the pass), softness, altitude, scale; wind offset xz (m), density x thickness, fade
	Kernel march;
	Rid quarter, marchSet;
	Vector2I quarterSize;

	protected override string Shader => "res://core/render/passes/shafts/shafts_add.compute";

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		march = Compile("res://core/render/passes/shafts/shafts_march.compute");
	}

	public override void Resize(Vector2I size)
	{
		Free();
		quarterSize = new Vector2I((size.X + 3) / 4, (size.Y + 3) / 4);
		quarter = Gpu.Rd.TextureCreate(new RDTextureFormat
		{
			Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = (uint)quarterSize.X, Height = (uint)quarterSize.Y,
			UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit,
		}, new RDTextureView());
		marchSet = Bind(march, (6, quarter, false));
		base.Resize(size);
	}

	protected override (int, Rid, bool)[] Extra() => [(5, quarter, true)];

	protected override void Update(Frame f)
	{
		if (Renderer.Recording(PassId.Clouds) is not CloudLayer c || ShaftClouds <= 0) { CloudCover = default; return; }
		float wind = Mathf.DegToRad(c.CloudWindAngle), drift = c.CloudSpeed * (float)Renderer.World.Clock;
		CloudCover = new Vector4(c.CloudCoverage, c.CloudSoftness, c.CloudAltitude, c.CloudScale);
		CloudDrift = new Vector4(Mathf.Cos(wind) * drift, Mathf.Sin(wind) * drift, c.CloudDensity * c.CloudThickness, c.CloudFade);
	}

	protected override void Run(Frame f)
	{
		Dispatch(march, marchSet, quarterSize);
		base.Run(f);
	}

	public override void Free()
	{
		if (quarter.IsValid) Gpu.Rd.FreeRid(quarter);
		quarter = default;
	}
}
