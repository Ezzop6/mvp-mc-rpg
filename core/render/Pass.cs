using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public enum PassStage { Primary, Lighting, Compose, Atmosphere, Temporal, Post, Output }

// Same names as v1 RenderOptions where it is the same effect (docs/v2.md §5.2).
public enum PassId { Primary, Transparency, Shadows, Lights, AO, GI, Fluid, Shade, Sky, Stars, Moon, Sun, Clouds, HeightFog, Haze, Shafts, Temporal, Sharpen, DebugView, Present, Raster, Animation, Grade, Atmosphere }

public readonly record struct VariantId(PassId Pass, string Name)
{
	public override string ToString() => $"{Pass}/{Name}";
}

// A pass's variant in a preset, Variant null = the pass off; "Pass:name" or "Pass:off" on the command line and in test results.
public readonly record struct VariantPick(PassId Pass, string Variant)
{
	public const string Off = "off";
	public string Name => Variant ?? Off;
	public static VariantPick Of(PassId pass, string name) => new(pass, name == Off ? null : name);
	public override string ToString() => $"{Pass}:{Name}";

	public static VariantPick Parse(IEnumerable<Pass> passes, string pass, string name)
	{
		var p = passes.Single(x => x.Id == Enum.Parse<PassId>(pass, true));
		if (name.Equals(Off, StringComparison.OrdinalIgnoreCase))
			return p.Info.Required ? throw new ArgumentException($"pass {p.Id} is required, it can't be off") : new(p.Id, null);
		return new(p.Id, p.Variants.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"pass {p.Id} has no variant '{name}' (known: {string.Join(", ", p.Variants.Keys)}, {Off})"));
	}
}

// On the abstract base class of a pass's variants.
[AttributeUsage(AttributeTargets.Class)]
public sealed class PassAttribute(PassId id, PassStage stage) : Attribute
{
	public readonly PassId Id = id;
	public readonly PassStage Stage = stage;
	public int Order;
	public bool Required, Enabled = true, Effect = true; // Effect: "All effects" masks it
	public string Default;
	public TargetId[] Reads = [], Writes = [];
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class VariantAttribute(string name) : Attribute
{
	public readonly string Name = name;
	public TargetId[] Reads, Writes; // null = the [Pass] level's, so a variant only declares when it differs
}

public sealed class Frame
{
	public Gpu Gpu;
	public GBuffer Targets;
	public Vector2I Size, Output;
	public uint Index;
}

// Render thread: Init once, Resize after the G-buffer was (re)allocated, Record every frame the pass is enabled.
public abstract class Variant
{
	VariantId id;
	public VariantId Id
	{
		get => id;
		internal set => (id, TimestampBegin, TimestampEnd) = (value, $"{value.Pass.ToString().ToLowerInvariant()}_{value.Name}_begin", $"{value.Pass.ToString().ToLowerInvariant()}_{value.Name}_end");
	}
	public string TimestampBegin { get; private set; } // GPU timestamp names, made once
	public string TimestampEnd { get; private set; }
	public Renderer Renderer { get; internal set; }
	protected Gpu Gpu { get; private set; }
	public bool Initialized => Gpu != null;

	public virtual void Init(Gpu gpu) => Gpu = gpu;
	public virtual void Resize(Vector2I size) { }
	public abstract void Record(Frame f);
	public virtual void Free() { }
	public virtual void Adopt(Variant old) { } // after Init: GPU state worth keeping from the instance of the same variant this one replaces
	public virtual string DebugStats => null; // HUD line of a debug compile

	// [Tune(Shader = true)] float and Color fields of the variant as `PARAMS` float members (P.snake_name, a Color as P.snake_name_r, _g, _b) and their bytes, until TuneSet (docs/v2.md §5.3).
	FieldInfo[] ShaderTunes => GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.GetCustomAttribute<TuneAttribute>() is { Shader: true }).OrderBy(f => f.MetadataToken).ToArray();
	static readonly string[] rgb = ["_r", "_g", "_b"];
	protected string ParamsDefine() => "PARAMS " + string.Concat(ShaderTunes.SelectMany(f => (f.FieldType == typeof(Color) ? rgb : [""]).Select(c => $"float {Gpu.Snake(f.Name).ToLowerInvariant()}{c}; ")));
	protected byte[] ParamsBytes()
	{
		var v = ShaderTunes.SelectMany(f => f.GetValue(this) is Color c ? new[] { c.R, c.G, c.B } : [(float)f.GetValue(this)]).ToArray();
		var bytes = new byte[Math.Max(16, v.Length * 4)];
		System.Buffer.BlockCopy(v, 0, bytes, 0, v.Length * 4);
		return bytes;
	}

	protected void Dispatch(Kernel k, Rid set, Vector2I size)
	{
		var rd = Gpu.Rd;
		long cl = Gpu.Begin(k);
		rd.ComputeListBindUniformSet(cl, set, 0);
		var (x, y) = Gpu.Groups(size, k);
		rd.ComputeListDispatch(cl, x, y, 1);
		rd.ComputeListEnd();
	}
}

public sealed class Pass
{
	public readonly PassAttribute Info;
	public readonly Dictionary<string, Type> Variants;
	public readonly string DefaultVariant;
	public bool Enabled;        // [Pass] Enabled, then the menu, bookmarks (On / Off), --on / --off
	public bool Tuned = true;   // the tuning preset (TuneSet)
	public string Selected;     // the tuning preset's variant
	public static bool EffectsOff; // debug menu "All effects": masks every effect pass, Enabled and Tuned stay

	public PassId Id => Info.Id;
	public bool Active => Enabled && (Info.Required || Tuned && !(EffectsOff && Info.Effect));
	// The selected variant's targets, or the [Pass] level's when the variant declares none (unchanged for existing passes).
	public TargetId[] Reads => ReadsOf(Selected);
	public TargetId[] Writes => WritesOf(Selected);
	public TargetId[] ReadsOf(string variant) => VariantAttr(variant).Reads ?? Info.Reads;
	public TargetId[] WritesOf(string variant) => VariantAttr(variant).Writes ?? Info.Writes;

	VariantAttribute VariantAttr(string name) => Variants[name].GetCustomAttribute<VariantAttribute>();

	Pass(PassAttribute info, Dictionary<string, Type> variants)
	{
		(Info, Variants, Selected, Enabled) = (info, variants, DefaultVariant = info.Default ?? variants.Keys.First(), info.Enabled);
		if (!variants.ContainsKey(Selected)) throw new InvalidOperationException($"[Pass] {info.Id}: default variant '{Selected}' does not exist");
	}

	public Variant Create(string variant)
	{
		var v = (Variant)Activator.CreateInstance(Variants[variant]);
		v.Id = new VariantId(Id, variant);
		return v;
	}

	// Every non-abstract [Variant] subclass of Variant, grouped by the [Pass] on a base class, in stage and Order order.
	public static Pass[] Discover() =>
		typeof(Pass).Assembly.GetTypes()
			.Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Variant)) && t.GetCustomAttribute<VariantAttribute>() != null)
			.GroupBy(t => PassOf(t).Id)
			.Select(g => new Pass(PassOf(g.First()), g.ToDictionary(t => t.GetCustomAttribute<VariantAttribute>().Name)))
			.OrderBy(p => p.Info.Stage).ThenBy(p => p.Info.Order)
			.ToArray();

	static PassAttribute PassOf(Type variant)
	{
		for (var t = variant; t != null; t = t.BaseType)
			if (t.GetCustomAttribute<PassAttribute>() is { } a) return a;
		throw new InvalidOperationException($"[Variant] {variant.Name}: no [Pass] on a base class");
	}

	// The required passes must stay on.
	public static void Check(IEnumerable<Pass> ordered)
	{
		foreach (var p in ordered)
			if (!p.Enabled && p.Info.Required) throw new InvalidOperationException($"pass {p.Id} is required");
	}

	// Each read target is written by an earlier runnable pass or has a default; a pass whose inputs are absent is skipped this frame.
	public static bool[] Runnable(Pass[] ordered)
	{
		var written = new HashSet<TargetId>();
		var run = new bool[ordered.Length];
		for (int i = 0; i < ordered.Length; i++)
		{
			var p = ordered[i];
			if (!p.Enabled && p.Info.Required) throw new InvalidOperationException($"pass {p.Id} is required");
			if (!p.Active) continue;
			bool ready = true;
			foreach (var t in p.Reads)
				if (!written.Contains(t) && GBuffer.Default(t) == null) { ready = false; break; }
			if (!ready) continue;
			run[i] = true;
			written.UnionWith(p.Writes);
		}
		return run;
	}
}
