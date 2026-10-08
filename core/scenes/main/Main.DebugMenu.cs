using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Renesance.Save;

// Debug menu (` key, docs/debug-menu.md): tabs generated from the passes, [Tune] and [Setting] members, the info groups of the HUD.
public partial class Main
{
	const int FrameSamples = 300;
	enum GeneratorPreset { Default, Crazy, Flat }
	MenuTree menu;
	OptionButton presetPicker;
	Pass selectedPass;
	ScrollContainer detailScroll;
	bool regenPending; // regenerate on slider release, not on every drag step
	readonly float[] frameMs = new float[FrameSamples];
	int frameSample;
	readonly Dictionary<PassId, (string Last, string Before)> picked = new(); // A/B: names of the pass's two last picks
	PassId? abPass;

	public bool MenuOpen => menu?.Visible == true;

	[Setting(MenuTab.World, "Render")]
	public bool Fullscreen
	{
		get => DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
		set => DisplayServer.WindowSetMode(value ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
	}

	static string EditLine() => TuneSet.Edit == null
		? "Preset Auto (blend by time of day): read-only, pick a preset in the header to edit"
		: $"Editing preset {TuneSet.Edit}";

	void BuildMenu(CanvasLayer layer)
	{
		var m = menu = new MenuTree(layer);
		var effects = new CheckBox { Text = "All effects", ButtonPressed = !Pass.EffectsOff };
		effects.Toggled += v => Pass.EffectsOff = !v;
		m.Header.AddChild(effects);
		var views = Enum.GetValues<DebugView>();
		var view = new OptionButton { TooltipText = "Debug view" };
		foreach (var v in views) view.AddItem(v == DebugView.None ? "No debug view" : $"View {v}");
		view.ItemSelected += i => SetView(views[i]);
		m.Header.AddChild(view);
		var vsync = new CheckBox { Text = "VSync (F6)" };
		vsync.Toggled += v => DisplayServer.WindowSetVsyncMode(v ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
		m.Header.AddChild(vsync);
		m.Header.AddChild(new VSeparator());
		presetPicker = new OptionButton { TooltipText = "Look preset: Auto blends day, dusk and night by the time of day; a named one is edited alone" };
		presetPicker.ItemSelected += i => TuneSet.Edit = i == 0 ? null : presetPicker.GetItemText((int)i);
		m.Header.AddChild(presetPicker);
		m.Header.AddChild(MenuTree.NewButton("Save", () => TuneSet.Save(Renderer.Passes)));
		m.Header.AddChild(MenuTree.NewButton("Revert", () => { TuneSet.Load(); DefaultPasses(); FillPresets(); }));
		FillPresets();
		m.Header.AddChild(new VSeparator());
		m.Slider(m.Top, "Time (h)", 0, 24, 0.25, () => World.TimeOfDay, v => World.TimeOfDay = (float)v);
		m.Check(m.Top, "Paused", () => World.TimePaused, v => World.TimePaused = v);
		m.Number(m.Top, "Day (moon phase = day % 8)", () => World.Day, v => World.Day = (int)v);
		m.Check(m.Top, "Walk (G)", () => Player.Mode == MoveMode.Walk, v => Player.Mode = v ? MoveMode.Walk : MoveMode.Fly);
		m.AddSearch();
		m.OnRefresh(() =>
		{
			effects.SetPressedNoSignal(!Pass.EffectsOff);
			if (view.Selected != Array.IndexOf(views, Renderer.View)) view.Select(Array.IndexOf(views, Renderer.View));
			vsync.SetPressedNoSignal(DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled);
		});

		var columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		var list = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
		var detail = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var listScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(440, 0) };
		detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		listScroll.AddChild(list);
		detailScroll.AddChild(detail);
		columns.AddChild(listScroll);
		columns.AddChild(new VSeparator());
		columns.AddChild(detailScroll);
		m.OwnScroll(MenuTab.Passes).AddChild(columns);
		var edit = new Label();
		detail.AddChild(edit);
		m.OnRefresh(() => edit.Text = EditLine());
		var group = new ButtonGroup();
		foreach (var p in Renderer.Passes) PassRows(p, list, detail, group);

		var look = m.SectionOf(MenuTab.Look, "Preset");
		m.Readout(look, "Edit", EditLine);
		m.Readout(look, "Blend weights", () => "Blend: " + string.Join("  ", TuneSet.Weights.Where(w => w.Value > 0.005f).Select(w => $"{w.Key} {w.Value.ToString("0.00", CultureInfo.InvariantCulture)}")));

		var gen = m.SectionOf(MenuTab.Generator, "Presets");
		var looks = new HBoxContainer();
		foreach (var g in Enum.GetValues<GeneratorPreset>()) looks.AddChild(MenuTree.NewButton(g.ToString(), () => LoadPreset(g)));
		looks.AddChild(MenuTree.NewButton("Regenerate (F5)", World.Regenerate));
		m.Row(gen, "Presets default crazy flat regenerate", looks);
		m.Readout(gen, "Active generator", () => $"Active generator: {World.Generator}");

		foreach (var target in new Node[] { World, Renderer, Player, this })
			foreach (var member in MenuTree.SettingMembers(target.GetType()))
				m.Setting(member, () => target, a => regenPending |= a.Regenerate);
		foreach (var member in MenuTree.ParamMembers)
			m.Setting(member, () => World.GeneratorInstance?.Settings is { } s && s.GetType() == member.DeclaringType ? s : null, _ =>
			{
				var s = World.GeneratorInstance.Settings;
				World.GeneratorParams = JsonSerializer.SerializeToElement(s, s.GetType(), WorldHeader.Json);
				regenPending = true;
			});
		foreach (var t in TuneSet.All.Where(t => t.Static))
			m.Tune(m.SectionOf(MenuTab.Look, t.Attr.Section), t, () => TuneValue(t), v => SetTune(t, v), () => TuneSet.Edit != null);

		foreach (var g in Enum.GetValues<InfoGroup>())
			m.Readout(m.SectionOf(MenuTab.Stats, g.ToString(), true), g.ToString(), () => info.GetValueOrDefault(g, ""));
	}

	static float[] TuneValue(Tunable t) => TuneSet.Edit == null ? t.Current() : TuneSet.Value(TuneSet.Edit, t);

	static void SetTune(Tunable t, float[] v) { if (TuneSet.Edit != null) TuneSet.Set(TuneSet.Edit, t, v); }

	static string OffReason(Pass p) => !p.Enabled ? "off" : !p.Tuned ? "off (preset)" : "off (All effects)";

	// Left: switch, name (selects the detail) and ms or why it is off, in its stage; right: the variant and its [Tune] fields.
	void PassRows(Pass p, VBoxContainer list, VBoxContainer detail, ButtonGroup group)
	{
		var stage = menu.SectionOf(list, MenuTree.Words(p.Info.Stage.ToString()), true);
		var on = new CheckBox { ButtonPressed = p.Enabled, Disabled = p.Info.Required, TooltipText = p.Info.Required ? "Required" : "Enabled" };
		on.Toggled += v => p.Enabled = v;
		var name = new Button { Text = p.Id.ToString(), ToggleMode = true, ButtonGroup = group, Flat = true, Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		name.Toggled += v => { if (v) (selectedPass, detailScroll.ScrollVertical) = (p, 0); };
		if (selectedPass == null) { selectedPass = p; name.ButtonPressed = true; }
		var status = new Label { CustomMinimumSize = new Vector2(150, 0), HorizontalAlignment = HorizontalAlignment.Right };
		var row = new HBoxContainer();
		row.AddChild(on);
		row.AddChild(name);
		row.AddChild(status);
		menu.OnRefresh(() =>
		{
			on.SetPressedNoSignal(p.Enabled);
			status.Text = p.Active ? $"{Renderer.PassMs(p.Id).ToString("0.000", CultureInfo.InvariantCulture)} ms" : OffReason(p);
			status.Modulate = p.Active ? Colors.White : new Color(1, 1, 1, 0.5f);
		});
		menu.Row(stage, p.Id.ToString(), row);

		bool Shown() => selectedPass == p || menu.Searching;
		var head = menu.SectionOf(detail, p.Id.ToString(), false, Shown);
		var names = p.Variants.Keys.Concat(p.Info.Required ? Array.Empty<string>() : new[] { VariantPick.Off }).ToArray();
		menu.Choice(head, "Variant", names, () => p.Tuned ? Array.IndexOf(names, p.Selected) : names.Length - 1,
			i => { if (TuneSet.Edit != null) SelectVariant(VariantPick.Of(p.Id, names[i])); }, () => TuneSet.Edit != null);
		menu.Readout(head, "State", () => p.Info.Required ? "Required" : p.Active ? "On" : OffReason(p));
		foreach (var (variant, _) in p.Variants)
		{
			string v = variant, prefix = TuneSet.Key(new VariantId(p.Id, variant), "");
			foreach (var t in TuneSet.All.Where(t => !t.Static && t.Key.StartsWith(prefix, StringComparison.Ordinal)))
				menu.Tune(t.Attr.Section == "" ? head : menu.SectionOf(detail, $"{p.Id}: {t.Attr.Section}", false, Shown), t, () => TuneValue(t), x => SetTune(t, x),
					() => TuneSet.Edit != null, () => p.Selected == v);
		}
	}

	// The generator and parameters of a known look, generated again (edits stay).
	void LoadPreset(GeneratorPreset g)
	{
		var (id, settings) = g switch { GeneratorPreset.Crazy => ("random", RandomParams.Crazy), GeneratorPreset.Flat => ("flat", null), _ => ("random", (object)null) };
		(World.Generator, World.GeneratorParams) = (id, settings == null ? null : JsonSerializer.SerializeToElement(settings, settings.GetType(), WorldHeader.Json));
		World.Regenerate();
	}

	// Into the edited preset, every preset while blending (menu, A/B key, --variant, --compare-variant).
	public void SelectVariant(VariantPick v)
	{
		var p = Renderer.Passes.Single(x => x.Id == v.Pass);
		var had = picked.GetValueOrDefault(v.Pass, (p.Tuned ? p.Selected : VariantPick.Off, null));
		picked[v.Pass] = had.Last == v.Name ? had : (v.Name, had.Last);
		abPass = v.Pass;
		foreach (var preset in TuneSet.Edit != null ? new[] { TuneSet.Edit } : TuneSet.PassPresets.Keys.ToArray()) TuneSet.SetVariant(preset, p, v.Variant);
	}

	// A/B: back to the other of the last two picks of the last picked pass.
	void ToggleVariant()
	{
		if (abPass is { } id && picked[id].Before is { } before) SelectVariant(VariantPick.Of(id, before));
	}

	void FillPresets()
	{
		presetPicker.Clear();
		presetPicker.AddItem("Auto (blend by time of day)");
		foreach (var name in TuneSet.Presets.Keys) presetPicker.AddItem(name);
		if (TuneSet.Edit != null && !TuneSet.Presets.ContainsKey(TuneSet.Edit)) TuneSet.Edit = null;
		presetPicker.Select(TuneSet.Edit == null ? 0 : TuneSet.Presets.Keys.ToList().IndexOf(TuneSet.Edit) + 1);
	}

	void ShowMenu(bool open, bool instant = false)
	{
		menu.Show(open, instant);
		Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
	}

	void UpdateMenu(double delta)
	{
		frameMs[frameSample++ % FrameSamples] = (float)(delta * 1000);
		if (regenPending && !Input.IsMouseButtonPressed(MouseButton.Left)) { regenPending = false; World.Regenerate(); }
		menu.Step(delta);
	}
}
