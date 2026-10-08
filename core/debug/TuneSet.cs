using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// [Tune] on instance fields of pass variants, on static fields of any type (key = the field name) and the passes' presets (docs/v2.md §5.3): day / dusk / night blended by the sun, the menu edits one preset.
public static class TuneSet
{
	public const string JsonPath = "res://core/render/tuning.json", PassesKey = "Passes", EnabledKey = "Enabled", Day = "day", Dusk = "dusk", Night = "night";

	sealed record Field(Tunable Tunable, FieldInfo Info);

	static readonly Pass[] passes = Pass.Discover();
	static readonly Field[] fields = Find().ToArray();
	public static readonly Tunable[] All = fields.Select(f => f.Tunable).ToArray();
	static readonly Dictionary<string, Tunable> byKey = All.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
	static readonly Dictionary<Type, Field[]> byType = fields.Where(f => !f.Info.IsStatic).GroupBy(f => f.Info.ReflectedType).ToDictionary(g => g.Key, g => g.ToArray());

	public static Dictionary<string, Dictionary<string, float[]>> Presets = new();
	public static Dictionary<string, Dictionary<PassId, string>> PassPresets = new(); // variant, null = off
	public static Dictionary<PassId, bool> Enabled = new(); // tuning.json's pass switches over [Pass] Enabled
	public static string Edit; // the preset the menu edits and shows alone, null = blend
	public static Dictionary<string, float> Weights = new();
	static readonly Dictionary<string, float> editWeights = new(), appliedWeights = new();
	static int version, applied = -1; // Set, SetVariant, Load and Restore bump version; Apply recomputes only after one or another weight / Edit
	static string appliedEdit;
	public static IReadOnlyDictionary<string, float[]> Values { get; private set; } = new Dictionary<string, float[]>(); // replaced as a whole, the render thread keeps a frame's

	static TuneSet() => Load();

	public static string Key(VariantId v, string field) => $"{v.Pass}.{v.Name}.{field}";

	static IEnumerable<Field> Find()
	{
		foreach (var p in passes)
			foreach (var (name, type) in p.Variants)
			{
				var found = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(f => f.IsDefined(typeof(TuneAttribute))).OrderBy(f => f.MetadataToken).ToArray();
				if (found.Length == 0) continue;
				var probe = Activator.CreateInstance(type);
				foreach (var f in found) yield return New(Key(new VariantId(p.Id, name), f.Name), f, probe);
			}
		foreach (var f in typeof(TuneSet).Assembly.GetTypes().OrderBy(t => t.FullName)
			.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.MetadataToken)).Where(f => f.IsDefined(typeof(TuneAttribute))))
			yield return New(f.Name, f, null);
	}

	static Field New(string key, FieldInfo f, object probe)
	{
		var a = f.GetCustomAttribute<TuneAttribute>();
		if (f.FieldType != typeof(float) && f.FieldType != typeof(bool) && f.FieldType != typeof(Color)) throw new InvalidOperationException($"[Tune] {f.DeclaringType.Name}.{f.Name}: float, bool or Color");
		var t = new Tunable
		{
			Key = key, Attr = a, Label = a.Label ?? MenuTree.Words(f.Name),
			Static = f.IsStatic, IsBool = f.FieldType == typeof(bool), IsColor = f.FieldType == typeof(Color), Default = Get(f, probe),
		};
		t.Size = t.Default.Length;
		t.Current = () => Values.TryGetValue(t.Key, out var v) ? v : t.Default;
		return new Field(t, f);
	}

	static float[] Get(FieldInfo f, object o) => f.GetValue(o) switch { bool b => new[] { b ? 1f : 0f }, Color c => new[] { c.R, c.G, c.B }, var x => new[] { (float)x } };

	static void Put(FieldInfo f, object o, float[] v) =>
		f.SetValue(o, f.FieldType == typeof(bool) ? v[0] > 0.5f : f.FieldType == typeof(Color) ? new Color(v[0], v[1], v[2]) : v[0]);

	// Render thread, before Record: a frame's values into the variant's fields (the shader's Params are packed from them, AtmosphereVariant).
	public static void Write(Variant v, IReadOnlyDictionary<string, float[]> values)
	{
		if (!byType.TryGetValue(v.GetType(), out var of)) return;
		foreach (var (t, f) in of) Put(f, v, values.TryGetValue(t.Key, out var x) ? x : t.Default);
	}

	public static float[] Value(string preset, Tunable t) =>
		Presets.TryGetValue(preset, out var p) && p.TryGetValue(t.Key, out var v) ? v : preset != Day ? Value(Day, t) : t.Default;

	public static void Set(string preset, Tunable t, float[] v) { Presets[preset][t.Key] = v; version++; }

	// The preset's variant of the pass, null when it turns the pass off.
	public static string Variant(string preset, Pass p) =>
		PassPresets.TryGetValue(preset, out var ps) && ps.TryGetValue(p.Id, out var v) ? v : preset != Day ? Variant(Day, p) : p.DefaultVariant;

	public static void SetVariant(string preset, Pass p, string variant) { PassPresets[preset][p.Id] = variant; version++; }

	// Main thread: numbers blend by weight (static fields take theirs); switches and variants come from the heaviest preset. Nothing changed (weights, Edit, presets): no new Values, no allocation.
	public static void Apply(Pass[] onto, Dictionary<string, float> weights)
	{
		if (Edit != null)
		{
			if (editWeights.Count != 1 || !editWeights.ContainsKey(Edit)) { editWeights.Clear(); editWeights[Edit] = 1; }
			weights = editWeights;
		}
		if (applied == version && appliedEdit == Edit && SameWeights(weights)) return;
		(applied, appliedEdit) = (version, Edit);
		appliedWeights.Clear();
		foreach (var (k, w) in weights) appliedWeights[k] = w;
		Weights = weights;
		float total = weights.Values.Sum();
		string top = weights.MaxBy(w => w.Value).Key;
		var values = new Dictionary<string, float[]>(All.Length);
		foreach (var t in All)
		{
			if (t.IsBool) { values[t.Key] = Value(top, t); continue; }
			var r = new float[t.Size];
			foreach (var (name, w) in weights)
			{
				if (w <= 0) continue;
				var v = Value(name, t);
				for (int i = 0; i < r.Length; i++) r[i] += v[i] * (w / total);
			}
			values[t.Key] = r;
		}
		Values = values;
		foreach (var (t, f) in fields.Where(f => f.Info.IsStatic)) Put(f, null, values[t.Key]);
		foreach (var p in onto)
		{
			var v = Variant(top, p);
			p.Tuned = v != null;
			if (v != null) p.Selected = v;
		}
	}

	static bool SameWeights(Dictionary<string, float> weights)
	{
		if (weights.Count != appliedWeights.Count) return false;
		foreach (var (k, w) in weights) if (!appliedWeights.TryGetValue(k, out var a) || a != w) return false;
		return true;
	}

	public static void Load() { (Presets, PassPresets, Enabled) = Read(); version++; }

	public static bool DefaultEnabled(Pass p) => p.Info.Required || Enabled.GetValueOrDefault(p.Id, p.Info.Enabled);

	static (Dictionary<string, Dictionary<string, float[]>>, Dictionary<string, Dictionary<PassId, string>>, Dictionary<PassId, bool>) Read()
	{
		var presets = new Dictionary<string, Dictionary<string, float[]>> { [Day] = new(), [Dusk] = new(), [Night] = new() };
		var passPresets = presets.Keys.ToDictionary(k => k, _ => new Dictionary<PassId, string>());
		var enabled = new Dictionary<PassId, bool>();
		if (!Godot.FileAccess.FileExists(JsonPath)) return (presets, passPresets, enabled);
		foreach (var (name, values) in JsonNode.Parse(Godot.FileAccess.GetFileAsString(JsonPath)).AsObject())
		{
			if (name == EnabledKey) { LoadEnabled(values.AsObject(), enabled); continue; }
			var p = presets[name] = new();
			var ps = passPresets[name] = new();
			foreach (var (key, node) in values.AsObject())
			{
				if (key == PassesKey) { LoadPasses(name, node.AsObject(), ps); continue; }
				var v = node is JsonArray a ? a.Select(x => x.GetValue<float>()).ToArray()
					: node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? new[] { node.GetValue<bool>() ? 1f : 0f } : new[] { node.GetValue<float>() };
				if (byKey.TryGetValue(key, out var t) && v.Length == t.Size) p[t.Key] = v;
				else GD.PushWarning($"{JsonPath}: {name}.{key}: no such tunable, or the wrong number of values");
			}
		}
		return (presets, passPresets, enabled);
	}

	public sealed record Changes(Dictionary<string, Dictionary<string, float[]>> Values, Dictionary<string, Dictionary<PassId, string>> Variants);

	// Values and pass variants (null = off) of each preset that differ from tuning.json, as the menu left them.
	public static Changes Overrides()
	{
		var (values, variants, _) = Read();
		static Dictionary<string, Dictionary<K, V>> Diff<K, V>(Dictionary<string, Dictionary<K, V>> now, Dictionary<string, Dictionary<K, V>> disk, Func<V, V, bool> same) =>
			now.Select(p => (p.Key, Changed: p.Value.Where(kv => !(disk.TryGetValue(p.Key, out var d) && d.TryGetValue(kv.Key, out var dv) && same(dv, kv.Value))).ToDictionary(kv => kv.Key, kv => kv.Value)))
				.Where(p => p.Changed.Count > 0).ToDictionary(p => p.Key, p => p.Changed);
		return new Changes(Diff(Presets, values, (a, b) => a.SequenceEqual(b)), Diff(PassPresets, variants, (a, b) => a == b));
	}

	// tuning.json, then the changes over it.
	public static void Restore(Changes c)
	{
		Load();
		version++;
		foreach (var (name, values) in c?.Values ?? new())
			foreach (var (key, v) in values) (Presets.TryGetValue(name, out var p) ? p : Presets[name] = new())[key] = v;
		foreach (var (name, variants) in c?.Variants ?? new())
			foreach (var (id, v) in variants) (PassPresets.TryGetValue(name, out var p) ? p : PassPresets[name] = new())[id] = v;
	}

	static void LoadPasses(string preset, JsonObject o, Dictionary<PassId, string> into)
	{
		foreach (var (key, node) in o)
		{
			var pass = passes.FirstOrDefault(p => p.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase));
			string variant = node.GetValueKind() == JsonValueKind.False ? null : node.GetValue<string>();
			if (pass != null && (variant == null ? !pass.Info.Required : pass.Variants.ContainsKey(variant))) into[pass.Id] = variant;
			else GD.PushWarning($"{JsonPath}: {preset}.{PassesKey}.{key}: no such pass or variant, or a required pass turned off");
		}
	}

	static void LoadEnabled(JsonObject o, Dictionary<PassId, bool> into)
	{
		foreach (var (key, node) in o)
		{
			var pass = passes.FirstOrDefault(p => p.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase));
			if (pass != null && !pass.Info.Required && node.GetValueKind() is JsonValueKind.True or JsonValueKind.False) into[pass.Id] = node.GetValue<bool>();
			else GD.PushWarning($"{JsonPath}: {EnabledKey}.{key}: no such pass, a required pass, or not true / false");
		}
	}

	// The live passes' switches (not the required ones) first; day keeps every value, the other presets only theirs.
	public static void Save(IEnumerable<Pass> live)
	{
		Enabled = live.Where(p => !p.Info.Required).ToDictionary(p => p.Id, p => p.Enabled);
		var sb = new StringBuilder($"{{\n\t\"{EnabledKey}\": {{\n");
		sb.Append(string.Join(",\n", Enabled.Select(e => $"\t\t\"{e.Key}\": {(e.Value ? "true" : "false")}"))).Append("\n\t},\n");
		var names = Presets.Keys.OrderBy(n => n != Day).ToList();
		foreach (var name in names)
		{
			bool all = name == Day;
			var ps = passes.Where(p => all || PassPresets[name].ContainsKey(p.Id)).Select(p => $"\t\t\t\"{p.Id}\": {(Variant(name, p) is { } v ? $"\"{v}\"" : "false")}").ToList();
			var lines = All.Where(t => all || Presets[name].ContainsKey(t.Key)).Select(t => $"\t\t\"{t.Key}\": {Json(t, Value(name, t))}").ToList();
			if (ps.Count > 0) lines.Insert(0, $"\t\t\"{PassesKey}\": {{\n{string.Join(",\n", ps)}\n\t\t}}");
			sb.Append($"\t\"{name}\": {{\n{string.Join(",\n", lines)}{(lines.Count > 0 ? "\n" : "")}\t}}{(name == names[^1] ? "" : ",")}\n");
		}
		System.IO.File.WriteAllText(ProjectSettings.GlobalizePath(JsonPath), sb.Append("}\n").ToString());
	}

	static string Json(Tunable t, float[] v) =>
		t.IsBool ? (v[0] > 0.5f ? "true" : "false")
		: t.IsColor ? $"[{string.Join(", ", v.Select(F))}]" : F(v[0]);

	static string F(float x) => x.ToString(CultureInfo.InvariantCulture);
}
