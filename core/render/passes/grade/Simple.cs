using Godot;

[Pass(PassId.Grade, PassStage.Post, Order = 1, Enabled = false, Default = "simple", Reads = new[] { TargetId.Color }, Writes = new[] { TargetId.Color })]
public abstract class GradePass : Variant { }

// Colour grading of the linear image before Godot's tonemap, in place on what Present shows (Output with Temporal, else Color); skipped in debug views.
[Variant("simple")]
public sealed class Simple : GradePass
{
	[Tune("", 0, 3, 0.01f, Label = "Brightness (x)", Shader = true)] public float Brightness = 1f;
	[Tune("", 0.5f, 2, 0.01f, Label = "Contrast (around mid grey 0.18)", Shader = true)] public float Contrast = 1f;
	[Tune("", 0.5f, 2, 0.01f, Label = "Gamma (> 1 brighter mid tones)", Shader = true)] public float Gamma = 1f;
	[Tune("", 0, 2, 0.01f, Label = "Saturation", Shader = true)] public float Saturation = 1f;
	[Tune("", -1, 1, 0.01f, Label = "Vibrance (saturates dull colours more)", Shader = true)] public float Vibrance = 0f;
	[Tune("White balance", -1, 1, 0.01f, Label = "Temperature (- cold, + warm)", Shader = true)] public float Temperature = 0f;
	[Tune("White balance", -1, 1, 0.01f, Label = "Tint (- green, + magenta)", Shader = true)] public float Tint = 0f;
	[Tune("Split toning", Label = "Shadows tint (x, dark tones)", Shader = true)] public Color ShadowsTint = new(1, 1, 1);
	[Tune("Split toning", Label = "Highlights tint (x, bright tones)", Shader = true)] public Color HighlightsTint = new(1, 1, 1);

	Kernel kernel;
	Rid paramsBuffer, set;
	Vector2I size;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		kernel = gpu.Compile("res://core/render/passes/grade/grade_simple.compute", 8, 8, ParamsDefine());
		paramsBuffer = gpu.Buffer(gpu.Persistent, ParamsBytes().Length);
	}

	public override void Resize(Vector2I _)
	{
		var t = Renderer.Targets;
		var target = t.Has(TargetId.Output) ? TargetId.Output : TargetId.Color;
		size = t.SizeOf(target);
		if (Gpu.Rd.UniformSetIsValid(set)) Gpu.Rd.FreeRid(set);
		set = Gpu.Set(kernel.Shader, (0, paramsBuffer), (2, t[target]));
	}

	public override void Record(Frame f)
	{
		if (Renderer.View != DebugView.None) return;
		var p = ParamsBytes();
		Gpu.Rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p);
		Dispatch(kernel, set, size);
	}
}
