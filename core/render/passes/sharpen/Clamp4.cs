using Godot;
using Usage = Godot.RenderingDevice.TextureUsageBits;

[Pass(PassId.Sharpen, PassStage.Post, Enabled = false, Default = "clamp4", Reads = new[] { TargetId.Output }, Writes = new[] { TargetId.Output })]
public abstract class SharpenPass : Variant { }

// v1 present.gdshader sharpen on Temporal's Output, from a copy of it (after the history: in it, sharpening would build up).
[Variant("clamp4")]
public sealed class Clamp4 : SharpenPass
{
	[Tune("", 0, 1, 0.05f, Shader = true)] public float Sharpness = 0.5f;

	Kernel kernel;
	Rid paramsBuffer, source, set;
	Vector2I size;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		kernel = gpu.Compile("res://core/render/passes/sharpen/sharpen_clamp4.compute", 8, 8, ParamsDefine());
		paramsBuffer = gpu.Buffer(gpu.Persistent, ParamsBytes().Length);
	}

	public override void Resize(Vector2I _)
	{
		var rd = Gpu.Rd;
		if (rd.UniformSetIsValid(set)) rd.FreeRid(set);
		if (Renderer.Targets.OutputSize != size)
		{
			Free();
			size = Renderer.Targets.OutputSize;
			source = rd.TextureCreate(new RDTextureFormat
			{
				Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = (uint)size.X, Height = (uint)size.Y, UsageBits = Usage.StorageBit | Usage.CanCopyToBit,
			}, new RDTextureView());
		}
		set = Gpu.Set(kernel.Shader, (0, paramsBuffer), (2, source), (3, Renderer.Targets[TargetId.Output]));
	}

	public override void Record(Frame f)
	{
		var rd = Gpu.Rd;
		rd.TextureCopy(Renderer.Targets[TargetId.Output], source, Vector3.Zero, Vector3.Zero, new Vector3(size.X, size.Y, 1), 0, 0, 0, 0);
		var p = ParamsBytes();
		rd.BufferUpdate(paramsBuffer, 0, (uint)p.Length, p);
		Dispatch(kernel, set, size);
	}

	public override void Free()
	{
		if (Gpu.Rd.TextureIsValid(source)) Gpu.Rd.FreeRid(source);
	}
}
