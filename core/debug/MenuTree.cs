using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// Tabs of the debug menu in this order (docs/debug-menu.md).
public enum MenuTab { Passes, Look, World, Generator, Stats }

// A field blended by the look presets (TuneSet): pass variants' fields show in their pass, static fields in the Look tab.
[AttributeUsage(AttributeTargets.Field)]
public sealed class TuneAttribute : Attribute
{
	public readonly string Section;
	public readonly float Min, Max, Step;
	public string Label;
	public bool Shader;

	public TuneAttribute(string section = "", float min = 0, float max = 1, float step = 0)
	{
		Section = section; Min = min; Max = max; Step = step > 0 ? step : (max - min) / 200;
	}
}

// A menu row of a plain value outside the presets: fields and properties of the scene's nodes and of generator parameters.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SettingAttribute(MenuTab tab, string section, float min = 0, float max = 1, float step = 0) : Attribute
{
	public readonly MenuTab Tab = tab;
	public readonly string Section = section;
	public readonly float Min = min, Max = max, Step = step > 0 ? step : (max - min) / 200;
	public string Label;
	public bool Regenerate; // the world is generated again after the change (on mouse release)
}

public sealed class Tunable
{
	public string Key, Label;
	public TuneAttribute Attr;
	public int Size;
	public bool Static, IsBool, IsColor;
	public float[] Default;
	public Func<float[]> Current;
}

// Debug menu panel: slides down from the top, a header, one page per MenuTab, sections one level deep; rows refresh every frame while open.
public sealed class MenuTree
{
	public sealed class Section
	{
		internal string Name;
		internal Control Root;
		internal Button Head;
		internal Container Body;
		internal bool Stacked;
		internal Func<bool> Shown;
		internal bool Open = true;
		internal int Hits;
	}

	sealed record Entry(Control Control, string Text, Section Section, Func<bool> Shown);

	const float SlideSeconds = 0.15f, MinHeight = 0.2f, MaxHeight = 0.9f, CellWidth = 340, NameWidth = 170, ValueWidth = 56, HeaderCellWidth = 220;
	readonly PanelContainer panel;
	public readonly HBoxContainer Header;
	public readonly Section Top; // rows in the header: always shown
	readonly TabContainer tabs;
	readonly LineEdit search;
	readonly Dictionary<MenuTab, VBoxContainer> pages = new();
	readonly Dictionary<(Container, string), Section> sections = new();
	readonly List<Section> sectionList = new();
	readonly List<Entry> rows = new();
	readonly List<Action> refresh = new();
	float height = 0.45f, slide;
	bool open;

	// Public [Setting] instance members of types outside the scene's nodes: generator parameters.
	public static readonly MemberInfo[] ParamMembers = typeof(MenuTree).Assembly.GetTypes().Where(t => !t.IsSubclassOf(typeof(Node)))
		.SelectMany(SettingMembers).ToArray();

	public static IEnumerable<MemberInfo> SettingMembers(Type t) =>
		t.GetMembers(BindingFlags.Public | BindingFlags.Instance).Where(m => m is FieldInfo or PropertyInfo && m.IsDefined(typeof(SettingAttribute))).OrderBy(m => m.MetadataToken);

	public MenuTree(CanvasLayer layer)
	{
		panel = new PanelContainer { Visible = false, AnchorRight = 1, Theme = VisibleCheckBoxes() };
		var box = new VBoxContainer();
		panel.AddChild(box);
		layer.AddChild(panel);
		Header = new HBoxContainer();
		Top = new Section { Name = "", Body = Header, Root = Header };
		box.AddChild(Header);
		search = new LineEdit { PlaceholderText = "Search (Ctrl+F)", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(200, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		box.AddChild(tabs);
		foreach (var t in Enum.GetValues<MenuTab>())
		{
			var scroll = new ScrollContainer { Name = Words(t.ToString()), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			var page = pages[t] = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			page.AddThemeConstantOverride("separation", 2);
			scroll.AddChild(page);
			tabs.AddChild(scroll);
		}
		var grip = new ColorRect { Color = new Color(1, 1, 1, 0.25f), CustomMinimumSize = new Vector2(0, 6), MouseDefaultCursorShape = Control.CursorShape.Vsize };
		grip.GuiInput += e =>
		{
			if (e is InputEventMouseMotion m && m.ButtonMask.HasFlag(MouseButtonMask.Left))
				height = Math.Clamp(height + m.Relative.Y / panel.GetViewportRect().Size.Y, MinHeight, MaxHeight);
		};
		box.AddChild(grip);
	}

	public void AddSearch() => Header.AddChild(search);

	public bool Visible => open;

	public bool Searching => search.Text.Trim() != "";

	public MenuTab Tab { get => (MenuTab)tabs.CurrentTab; set => tabs.CurrentTab = (int)value; }

	public void FocusSearch() => search.GrabFocus();

	// Slides toward open; instant for shots.
	public void Show(bool open, bool instant = false)
	{
		this.open = open;
		if (instant) slide = open ? 1 : 0;
		if (!open) panel.GetViewport()?.GuiReleaseFocus();
	}

	public VBoxContainer Page(MenuTab tab) => pages[tab];

	// A page whose parts scroll on their own (a list beside its detail): the tab itself does not scroll.
	public VBoxContainer OwnScroll(MenuTab tab)
	{
		var page = pages[tab];
		page.GetParent<ScrollContainer>().VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
		page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		return page;
	}

	public static string Words(string camel)
	{
		string s = Gpu.Snake(camel).Replace('_', ' ').ToLowerInvariant();
		return char.ToUpperInvariant(s[0]) + s[1..];
	}

	public void OnRefresh(Action a) => refresh.Add(a);

	// Every frame: the slide, then (while open) values and what is shown.
	public void Step(double delta)
	{
		slide = Math.Clamp(slide + (open ? 1 : -1) * (float)delta / SlideSeconds, 0, 1);
		panel.Visible = slide > 0;
		float h = height * panel.GetViewportRect().Size.Y;
		(panel.OffsetTop, panel.OffsetBottom) = (-h * (1 - slide), h * slide);
		if (!panel.Visible) return;
		foreach (var r in refresh) r();
		Filter();
	}

	// One level: a heading (none for "") over a grid of rows; shown() gates the whole section.
	public Section SectionOf(Container parent, string name, bool stacked = false, Func<bool> shown = null)
	{
		if (sections.TryGetValue((parent, name), out var s)) return s;
		s = sections[(parent, name)] = new Section { Name = name, Shown = shown, Stacked = stacked };
		var root = new VBoxContainer();
		root.AddThemeConstantOverride("separation", 0);
		if (name != "")
		{
			s.Head = new Button { Flat = true, Alignment = HorizontalAlignment.Left };
			s.Head.Pressed += () => s.Open = !s.Open;
			root.AddChild(s.Head);
		}
		s.Body = stacked ? new VBoxContainer() : new HFlowContainer();
		s.Body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		s.Body.AddThemeConstantOverride("separation", 0);
		s.Body.AddThemeConstantOverride("h_separation", 20);
		s.Body.AddThemeConstantOverride("v_separation", 0);
		root.AddChild(s.Body);
		s.Root = root;
		parent.AddChild(root);
		sectionList.Add(s);
		return s;
	}

	public Section SectionOf(MenuTab tab, string name, bool stacked = false, Func<bool> shown = null) => SectionOf(pages[tab], name, stacked, shown);

	void Filter()
	{
		string q = search.Text.Trim().ToLowerInvariant();
		foreach (var s in sectionList) s.Hits = 0;
		foreach (var r in rows)
		{
			bool shown = r.Section == Top || (r.Shown?.Invoke() ?? true) && (q == "" || r.Text.Contains(q));
			r.Control.Visible = shown;
			if (shown) r.Section.Hits++;
		}
		foreach (var s in sectionList)
		{
			s.Root.Visible = s.Hits > 0 && (s.Shown?.Invoke() ?? true);
			s.Body.Visible = s.Open || q != "";
			if (s.Head != null) s.Head.Text = (s.Body.Visible ? "- " : "+ ") + s.Name;
		}
		foreach (var (t, page) in pages)
		{
			int hits = q == "" ? 0 : sectionList.Where(s => s.Root.Visible && s.Root.GetParent() is Node p && (p == page || page.IsAncestorOf(p))).Sum(s => s.Hits);
			tabs.SetTabTitle((int)t, Words(t.ToString()) + (q == "" ? "" : $" ({hits})"));
		}
	}

	public T Row<T>(Section s, string text, T c, Func<bool> shown = null) where T : Control
	{
		if (s.Stacked) c.SizeFlagsHorizontal |= Control.SizeFlags.ExpandFill;
		s.Body.AddChild(c);
		rows.Add(new Entry(c, $"{s.Name} {text}".ToLowerInvariant(), s, shown));
		return c;
	}

	public static Button NewButton(string text, Action pressed)
	{
		var b = new Button { Text = text };
		b.Pressed += pressed;
		return b;
	}

	// The name shown in a cell: the label up to its first parenthesis (the whole label is the tooltip).
	public static string Short(string label) => label.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? label[..i] : label;

	// Godot's unchecked box is nearly invisible on the panel: an outlined one.
	static Theme VisibleCheckBoxes()
	{
		ImageTexture Box(float alpha)
		{
			const int n = 16;
			var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
			for (int y = 1; y < n - 1; y++)
				for (int x = 1; x < n - 1; x++)
					img.SetPixel(x, y, x < 3 || y < 3 || x > n - 4 || y > n - 4 ? new Color(1, 1, 1, alpha) : new Color(0, 0, 0, 0.35f * alpha));
			return ImageTexture.CreateFromImage(img);
		}
		var theme = new Theme();
		theme.SetIcon("unchecked", "CheckBox", Box(0.85f));
		theme.SetIcon("unchecked_disabled", "CheckBox", Box(0.35f));
		return theme;
	}

	// A fixed-width cell: the name (clipped, whole in the tooltip) and the control beside it.
	static HBoxContainer Cell(string text, Control c, float nameWidth = NameWidth)
	{
		var row = new HBoxContainer { CustomMinimumSize = new Vector2(CellWidth, 0) };
		row.AddChild(new Label { Text = Short(text), CustomMinimumSize = new Vector2(nameWidth, 0), TooltipText = text, MouseFilter = Control.MouseFilterEnum.Pass });
		c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		row.AddChild(c);
		return row;
	}

	public void Readout(Section s, string text, Func<string> get, Func<bool> shown = null)
	{
		var l = new Label { AutowrapMode = s.Stacked ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
		refresh.Add(() => { if (l.IsVisibleInTree()) l.Text = get(); });
		Row(s, text, l, shown);
	}

	public void Check(Section s, string text, Func<bool> get, Action<bool> set, Func<bool> enabled = null, Func<bool> shown = null)
	{
		var c = new CheckBox { Text = Short(text), ButtonPressed = get(), TooltipText = text, CustomMinimumSize = new Vector2(s == Top ? 0 : CellWidth, 0) };
		c.Toggled += v => set(v);
		refresh.Add(() =>
		{
			c.SetPressedNoSignal(get());
			if (enabled != null) c.Disabled = !enabled();
		});
		Row(s, text, c, shown);
	}

	public void Choice(Section s, string text, string[] items, Func<int> get, Action<int> set, Func<bool> enabled = null, Func<bool> shown = null)
	{
		var o = new OptionButton { ClipText = true };
		foreach (var item in items) o.AddItem(item);
		o.Select(get());
		o.ItemSelected += i => set((int)i);
		refresh.Add(() =>
		{
			if (o.Selected != get()) o.Select(get());
			if (enabled != null) o.Disabled = !enabled();
		});
		Row(s, text, Cell(text, o), shown);
	}

	public void Slider(Section s, string text, double min, double max, double step, Func<double> get, Action<double> set, Func<bool> enabled = null, Func<bool> shown = null)
	{
		var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, AllowGreater = true, AllowLesser = true, Value = get(), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
		var value = new Label { CustomMinimumSize = new Vector2(ValueWidth, 0), ClipText = true };
		void Show(double v) => value.Text = $"{v:0.#####}";
		Show(sl.Value);
		sl.ValueChanged += v => { set(v); Show(v); };
		refresh.Add(() =>
		{
			if (!sl.HasFocus()) { sl.SetValueNoSignal(get()); Show(sl.Value); }
			if (enabled != null) sl.Editable = enabled();
		});
		var row = Cell(text, sl, s == Top ? 0 : NameWidth);
		if (s == Top) row.CustomMinimumSize = new Vector2(HeaderCellWidth, 0);
		row.AddChild(value);
		Row(s, text, row, shown);
	}

	public void Number(Section s, string text, Func<double> get, Action<double> set)
	{
		var n = new SpinBox { MinValue = 0, MaxValue = 1e6, Step = 1, AllowGreater = true, Value = get(), TooltipText = text };
		n.ValueChanged += v => set(v);
		refresh.Add(() => { if (!n.GetLineEdit().HasFocus()) n.SetValueNoSignal(get()); });
		var row = new HBoxContainer { TooltipText = text };
		row.AddChild(new Label { Text = Short(text), TooltipText = text, MouseFilter = Control.MouseFilterEnum.Pass });
		row.AddChild(n);
		Row(s, text, row);
	}

	public void Text(Section s, string text, Func<string> get, Action<string> set, Func<bool> shown = null)
	{
		var e = new LineEdit { Text = get() };
		e.TextSubmitted += v => set(v);
		refresh.Add(() => { if (!e.HasFocus()) e.Text = get(); });
		Row(s, text, Cell(text, e), shown);
	}

	public void Picker(Section s, string text, Func<Color> get, Action<Color> set, Func<bool> enabled, Func<bool> shown = null)
	{
		var p = new ColorPickerButton { Color = get(), EditAlpha = false };
		p.ColorChanged += c => set(c);
		refresh.Add(() =>
		{
			if (!p.GetPopup().Visible) p.Color = get();
			p.Disabled = !enabled();
		});
		Row(s, text, Cell(text, p), shown);
	}

	// A tunable as a check box, colour picker or slider over get / put (a preset's value).
	public void Tune(Section s, Tunable t, Func<float[]> get, Action<float[]> put, Func<bool> enabled, Func<bool> shown = null)
	{
		if (t.IsBool) Check(s, t.Label, () => get()[0] > 0.5f, v => put(new[] { v ? 1f : 0f }), enabled, shown);
		else if (t.IsColor) Picker(s, t.Label, () => { var v = get(); return new Color(v[0], v[1], v[2]); }, c => put(new[] { c.R, c.G, c.B }), enabled, shown);
		else Slider(s, t.Label, t.Attr.Min, t.Attr.Max, t.Attr.Step, () => get()[0], v => put(new[] { (float)v }), enabled, shown);
	}

	// A [Setting] member in its tab and section over target() (null = not shown: e.g. another generator's parameters), changed after each edit.
	public void Setting(MemberInfo m, Func<object> target, Action<SettingAttribute> changed)
	{
		var a = m.GetCustomAttribute<SettingAttribute>();
		var s = SectionOf(a.Tab, a.Section);
		string label = a.Label ?? Words(m.Name);
		var (type, get, set) = m switch
		{
			FieldInfo f => (f.FieldType, (Func<object, object>)f.GetValue, (Action<object, object>)f.SetValue),
			PropertyInfo p => (p.PropertyType, p.GetValue, p.SetValue),
			_ => throw new InvalidOperationException(),
		};
		bool Shown() => target() != null;
		void Put(object v)
		{
			if (target() is not { } o) return;
			set(o, v);
			changed(a);
		}
		if (type == typeof(bool)) Check(s, label, () => target() is { } o && (bool)get(o), v => Put(v), shown: Shown);
		else if (type == typeof(string)) Text(s, label, () => target() is { } o ? (string)get(o) : "", v => Put(v), Shown);
		else if (type.IsEnum)
		{
			var values = Enum.GetValues(type).Cast<object>().ToArray();
			Choice(s, label, values.Select(v => v.ToString()).ToArray(), () => target() is { } o ? Array.IndexOf(values, get(o)) : 0, i => Put(values[i]), shown: Shown);
		}
		else Slider(s, label, a.Min, a.Max, a.Step, () => target() is { } o ? Convert.ToDouble(get(o)) : 0, v => Put(Convert.ChangeType(v, type)), shown: Shown);
	}
}
