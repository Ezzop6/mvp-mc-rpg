using Godot;
using System;
using System.Runtime.InteropServices;
using Renesance.Content;

[Pass(PassId.Animation, PassStage.Primary, Order = -2, Enabled = false, Default = "mcmeta")]
public abstract class AnimationPass : Variant { }

// v1 anim.compute: each animated texture's live layer = its .mcmeta frame (blended into the next one when it interpolates) at World.Clock; only when the frame changes.
[Variant("mcmeta")]
public sealed class McmetaFrames : AnimationPass
{
	[Tune("", 0, 4, Label = "Texture animation speed (1 = 20 ticks per second)")] public float AnimationSpeed = 1f;

	Kernel kernel;
	Rid states, set;
	(int Live, int Frames, TextureAnimation Animation)[] anims;
	byte[] state;
	double ticks = double.NaN;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		anims = Renderer.World.Textures.Animations;
		state = new byte[Math.Max(16, anims.Length * 16)];
		kernel = gpu.Compile("res://core/render/passes/animation/animation_mcmeta.compute", 64, 1);
		states = gpu.Buffer(gpu.Persistent, state.Length);
		set = gpu.Persistent.Add(gpu.Set(kernel.Shader, (0, Renderer.World.TexBuffer), (1, states)));
	}

	public override void Record(Frame f)
	{
		double now = Renderer.World.Clock * TextureAnimation.TicksPerSecond * AnimationSpeed;
		if (anims.Length == 0 || now == ticks) return;
		ticks = now;
		var words = MemoryMarshal.Cast<byte, uint>(state.AsSpan());
		for (int i = 0; i < anims.Length; i++)
		{
			var (live, frames, a) = anims[i];
			var (frame, next, blend) = a.At(ticks);
			(words[4 * i], words[4 * i + 1], words[4 * i + 2], words[4 * i + 3]) = ((uint)live, (uint)(frames + frame), (uint)(frames + next), BitConverter.SingleToUInt32Bits(blend));
		}
		var rd = Gpu.Rd;
		rd.BufferUpdate(states, 0, (uint)state.Length, state);
		long cl = Gpu.Begin(kernel);
		rd.ComputeListBindUniformSet(cl, set, 0);
		rd.ComputeListDispatch(cl, (uint)((GpuBlockTable.TexelsPerLayer + kernel.X - 1) / kernel.X), (uint)anims.Length, 1);
		rd.ComputeListEnd();
	}
}
