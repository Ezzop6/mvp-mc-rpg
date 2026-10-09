using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DataFormat = Godot.RenderingDevice.DataFormat;
using Usage = Godot.RenderingDevice.TextureUsageBits;

public enum TargetId { Depth, HitId, Albedo, Color, History0, History1, Output, Start, Ao, SunVis, Lamp, Trans, Inscatter, Water, HitPos, Liquid, LiquidId, Gi }

// HitId bit layout (docs/v2.md §5.1); the prelude gets them as HIT_<NAME>.
public static class HitBits
{
	public const uint FaceMask = 7, Sky = 7;
	public const int VariantShift = 5;
	public const uint VariantMask = (1u << 22) - 1;
	public const uint Model = 1u << 27, Fluid = 1u << 28, Transparent = 1u << 29, Bent = 1u << 31;
}

// Render-size and output-size targets, allocated only when declared (Pass.Declared).
public sealed class GBuffer
{
	static readonly Dictionary<TargetId, (DataFormat Format, Color? Default, bool Output)> spec = new()
	{
		[TargetId.Depth] = (DataFormat.R32Sfloat, null, false),
		[TargetId.HitId] = (DataFormat.R32Uint, null, false),
		[TargetId.Albedo] = (DataFormat.R8G8B8A8Unorm, null, false),
		[TargetId.Color] = (DataFormat.R16G16B16A16Sfloat, new Color(0, 0, 0, 0), false),
		[TargetId.History0] = (DataFormat.R16G16B16A16Sfloat, null, true),
		[TargetId.History1] = (DataFormat.R16G16B16A16Sfloat, null, true),
		[TargetId.Output] = (DataFormat.R16G16B16A16Sfloat, null, true),
		[TargetId.Start] = (DataFormat.R32Sfloat, new Color(0, 0, 0, 0), false), // t the primary ray starts at, 0 = the camera
		[TargetId.Ao] = (DataFormat.R8Unorm, new Color(1, 1, 1, 1), false), // ambient factor of AO, 1 = open
		[TargetId.SunVis] = (DataFormat.R8G8B8A8Unorm, new Color(1, 1, 1, 1), false), // sun transmittance (tinted by glass, water, fog), a = of the fog in front (Transparency)
		[TargetId.Lamp] = (DataFormat.R16G16B16A16Sfloat, new Color(0, 0, 0, 0), false), // block light, a = lights in reach (LightCount view)
		[TargetId.Trans] = (DataFormat.R16G16B16A16Sfloat, new Color(1, 1, 1, 0), false), // transmittance in front of the hit, a = distance of the fog's in-scatter (blocks, 0 = none); Primary with Transparency
		[TargetId.Inscatter] = (DataFormat.R16G16B16A16Sfloat, new Color(0, 0, 0, 0), false), // light added in front of the hit, a = the sun's share in fog
		[TargetId.Water] = (DataFormat.R16G16B16A16Sfloat, new Color(1, 1, 1, 0), false), // transmittance of the ray's path in water, a = distance of its first water surface (> 0 from above, < 0 from below); Primary with Water
		[TargetId.HitPos] = (DataFormat.R32G32B32A32Sfloat, new Color(0, 0, 0, 0), false), // the hit (anchor-relative blocks) of a ray Water bent (HitId Bent) or one through water, w = the water's variant
		[TargetId.Liquid] = (DataFormat.R16G16B16A16Sfloat, new Color(1, 1, 1, 0), false), // transmittance of the ray's translucent-liquid path (Beer-Lambert, 1 = none), a = distance of the first surface (> 0 from above, < 0 from below); Primary with Liquid
		[TargetId.LiquidId] = (DataFormat.R32Uint, new Color(0, 0, 0, 0), false), // variant of the first liquid surface, 0 = none; Primary with Liquid (8 + 4 B/px against HitPos's 16)
		[TargetId.Gi] = (DataFormat.R16G16B16A16Sfloat, new Color(0, 0, 0, 0), false), // ambient light from the GI cache, a = its weight over Shade's flat ambient
	};

	public Vector2I Size { get; private set; }
	public Vector2I OutputSize { get; private set; }
	readonly Dictionary<TargetId, Rid> rids = new();

	public static Color? Default(TargetId t) => spec[t].Default;

	public Rid this[TargetId t] => rids.TryGetValue(t, out var r) ? r : throw new InvalidOperationException($"target {t} is not declared by any enabled pass");

	public bool Has(TargetId t) => rids.ContainsKey(t);

	public Vector2I SizeOf(TargetId t) => spec[t].Output ? OutputSize : Size;

	// Output-size targets (the temporal history) survive a change of the render size.
	public void Allocate(RenderingDevice rd, Vector2I size, Vector2I output, IEnumerable<TargetId> declared)
	{
		var want = declared.ToHashSet();
		foreach (var t in rids.Keys.ToArray())
			if (!want.Contains(t) || !spec[t].Output || output != OutputSize) { rd.FreeRid(rids[t]); rids.Remove(t); }
		(Size, OutputSize) = (size, output);
		foreach (var t in want.Where(t => !rids.ContainsKey(t)))
			rids[t] = rd.TextureCreate(new RDTextureFormat
			{
				Format = spec[t].Format, Width = (uint)SizeOf(t).X, Height = (uint)SizeOf(t).Y,
				UsageBits = Usage.StorageBit | Usage.SamplingBit | Usage.CanCopyToBit | Usage.CanCopyFromBit,
			}, new RDTextureView());
	}

	// What a disabled pass leaves in the targets it would write.
	public void Fill(RenderingDevice rd, TargetId t)
	{
		if (Has(t) && Default(t) is { } c) rd.TextureClear(rids[t], c, 0, 1, 0, 1);
	}

	public void Free(RenderingDevice rd)
	{
		foreach (var r in rids.Values) rd.FreeRid(r);
		rids.Clear();
	}

	public static string Prelude() =>
		string.Concat(typeof(HitBits).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => $"const uint HIT_{Gpu.Snake(f.Name)} = {Convert.ToUInt32(f.GetValue(null))}u;\n")) +
		string.Concat(Enum.GetValues<TargetId>().Select(t => $"const uint TARGET_{Gpu.Snake(t.ToString())} = {(int)t}u;\n"));
}
