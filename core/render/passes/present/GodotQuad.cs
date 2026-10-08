using Godot;
using DataFormat = Godot.RenderingDevice.DataFormat;
using Usage = Godot.RenderingDevice.TextureUsageBits;

[Pass(PassId.Present, PassStage.Output, Required = true, Default = "godot-quad", Reads = new[] { TargetId.Color, TargetId.Depth })]
public abstract class PresentPass : Variant { }

// Copies Color (Output with Temporal) and the view depth into textures the screen quad (present.gdshader) samples.
[Variant("godot-quad")]
public sealed class GodotQuad : PresentPass
{
	static readonly StringName ColorTex = "color_tex", DepthTex = "depth_tex";
	readonly Texture2Drd colorRd = new(), depthRd = new();
	Kernel kernel;
	Rid set;
	Vector2I presentSize;
	RidGroup outputs = new();

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		kernel = gpu.Compile("res://core/render/passes/present/present_godot_quad.compute", 8, 8);
		var mat = (ShaderMaterial)Renderer.Screen.MaterialOverride;
		Callable.From(() => { mat.SetShaderParameter(ColorTex, colorRd); mat.SetShaderParameter(DepthTex, depthRd); }).CallDeferred();
	}

	public override void Resize(Vector2I size)
	{
		var old = outputs;
		outputs = new RidGroup();
		var source = Renderer.Targets.Has(TargetId.Output) ? TargetId.Output : TargetId.Color;
		presentSize = Renderer.Targets.SizeOf(source);
		var color = outputs.Add(Texture(DataFormat.R16G16B16A16Sfloat, presentSize));
		var depth = outputs.Add(Texture(DataFormat.R32Sfloat, presentSize));
		set = Gpu.Set(kernel.Shader, (1, Renderer.CameraBuffer), (2, Renderer.Targets[source]), (3, Renderer.Targets[TargetId.Depth]), (4, color), (5, depth));
		// Texture2Drd drops its view of the old texture when switched: free the old ones only afterwards.
		Callable.From(() =>
		{
			colorRd.TextureRdRid = color; depthRd.TextureRdRid = depth;
			RenderingServer.CallOnRenderThread(Callable.From(() => old.Free(Gpu.Rd)));
		}).CallDeferred();
	}

	Rid Texture(DataFormat format, Vector2I size) =>
		Gpu.Rd.TextureCreate(new RDTextureFormat { Format = format, Width = (uint)size.X, Height = (uint)size.Y, UsageBits = Usage.StorageBit | Usage.SamplingBit }, new RDTextureView());

	public override void Record(Frame f) => Dispatch(kernel, set, presentSize);

	// Main thread, before the textures go: the material must not keep sampling them.
	public void Detach() => (colorRd.TextureRdRid, depthRd.TextureRdRid) = (new Rid(), new Rid());

	// Replaced by a new instance: its Init switched the material (deferred earlier), then these textures go.
	public void Retire() => Callable.From(() => { Detach(); RenderingServer.CallOnRenderThread(Callable.From(Free)); }).CallDeferred();

	public override void Free() => outputs.Free(Gpu.Rd);
}
