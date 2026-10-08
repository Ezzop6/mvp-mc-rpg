using Godot;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Renesance.Content;
using UniformType = Godot.RenderingDevice.UniformType;

// Atmosphere passes (docs/v2.md §5.12): Params 0 from instance fields, camera 1, Color 2 in place, HitId 3, Depth 4, extras from 5.
public abstract class AtmosphereVariant : Variant
{
	protected abstract string Shader { get; }
	(FieldInfo Info, Type Owner)[] fields;
	Kernel kernel;
	Rid set, paramBuf, sampler;
	byte[] packed;

	// [Tune(Shader = true)] fields (TuneSet sets them before Record) and per-frame Vector4 fields; 16-byte values first for std430.
	static bool IsParam(FieldInfo f) => f.FieldType == typeof(Vector4) || f.GetCustomAttribute<TuneAttribute>()?.Shader == true;

	static int Size(FieldInfo f) => f.FieldType == typeof(float) ? 4 : 16;

	// Types whose param fields make up Params (the first of a name wins) and the instance each is read from, null = zeros (AtmosphereFused).
	protected virtual Type[] ParamTypes => [GetType()];
	protected virtual object ParamOwner(Type t) => this;

	public override void Init(Gpu gpu)
	{
		base.Init(gpu);
		fields = ParamTypes.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(IsParam).Select(f => (f, t))).DistinctBy(x => x.f.Name).OrderBy(x => Size(x.f) == 4).ToArray();
		packed = new byte[Math.Max(16, (fields.Sum(x => Size(x.Info)) + 15) & ~15)];
		paramBuf = gpu.Buffer(gpu.Persistent, packed.Length);
		sampler = gpu.Persistent.Add(gpu.Rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear }));
		kernel = Compile(Shader);
	}

	protected Kernel Compile(string path) =>
		Gpu.Compile(path, 8, 8, [.. Renderer.Defines, "PARAMS " + string.Concat(fields.Select(x => $"{(Size(x.Info) == 4 ? "float" : "vec4")} {(Gpu.Snake(x.Info.Name).ToLowerInvariant())}; "))]);

	public override void Resize(Vector2I size) => set = Bind(kernel, Extra());

	protected virtual (int Binding, Rid Rid, bool Sampled)[] Extra() => [];

	protected Rid Bind(Kernel k, params (int Binding, Rid Rid, bool Sampled)[] extra)
	{
		var t = Renderer.Targets;
		var list = new Godot.Collections.Array<RDUniform>();
		foreach (var (b, rid, sampled) in new[] { (0, paramBuf, false), (1, Renderer.CameraBuffer, false), (2, t[TargetId.Color], false), (3, t[TargetId.HitId], false), (4, t[TargetId.Depth], false) }.Concat(extra))
		{
			var u = new RDUniform { Binding = b, UniformType = sampled ? UniformType.SamplerWithTexture : Gpu.Rd.TextureIsValid(rid) ? UniformType.Image : UniformType.StorageBuffer };
			if (sampled) u.AddId(sampler);
			u.AddId(rid);
			list.Add(u);
		}
		return Gpu.Rd.UniformSetCreate(list, k.Shader, 0);
	}

	protected virtual void Update(Frame f) { }

	// Render thread: the per-frame values without recording, for a pass AtmosphereFused computes.
	public void Prepare(Frame f) => Update(f);

	public override void Record(Frame f)
	{
		Update(f);
		int o = 0;
		foreach (var (fi, type) in fields)
		{
			var v = ParamOwner(type) is { } owner ? fi.GetValue(owner) switch { float x => new[] { x }, Color c => new[] { c.R, c.G, c.B, c.A }, Vector4 q => new[] { q.X, q.Y, q.Z, q.W }, _ => throw new InvalidOperationException(fi.Name) } : new float[Size(fi) / 4];
			for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(packed.AsSpan(o + 4 * i), v[i]);
			o += Size(fi);
		}
		Gpu.Rd.BufferUpdate(paramBuf, 0, (uint)packed.Length, packed);
		Run(f);
	}

	protected virtual void Run(Frame f) => Dispatch(kernel, set, f.Size);

	// Sun and moon textures from the pack.
	protected Rid SkyTexture(string name)
	{
		var img = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://{ContentPaths.Textures}/{ContentPaths.TextureFile(name)}"))
			?? throw new FileNotFoundException($"sky texture {name} (dotnet run --project tools/PackImport)");
		img.Convert(Image.Format.Rgba8);
		var format = new RDTextureFormat { Format = RenderingDevice.DataFormat.R8G8B8A8Srgb, Width = (uint)img.GetWidth(), Height = (uint)img.GetHeight(), UsageBits = RenderingDevice.TextureUsageBits.SamplingBit };
		return Gpu.Persistent.Add(Gpu.Rd.TextureCreate(format, new RDTextureView(), [img.GetData()]));
	}

	// w = the sky clock (World.Clock, s).
	protected Vector4 SkySunVector()
	{
		var d = Renderer.SunDirection(Renderer.World.TimeOfDay);
		return new Vector4(d.X, d.Y, d.Z, (float)Renderer.World.Clock);
	}
}
