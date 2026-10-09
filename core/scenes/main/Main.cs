using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Renesance.Save;
using Renesance.World;

// Scene root: command line, HUD with per-pass GPU ms and the world's pools, loading screen, keys, host of TestCore (docs/v2.md §6).
public partial class Main : Node3D
{
	[Export] public World World;
	[Export] public Renderer Renderer;
	[Export] public Player Player;
	[Export] public WorldEnvironment Env;
	[Tune("Light", 0.05f, 4, 0.01f)] public static float Exposure = 0.8f;

	const float TeleportMeters = 5000, EditReach = 8;
	const string EditorKeys = "WASD, Space/Shift up/down, Ctrl fast, Alt Build/Select, LMB break / corner A, RMB place / corner B, MMB pick, 1-9 / wheel slot, E inventory, K bookmark the view, Ctrl+wheel cursor reach, Ctrl+C copy, Ctrl+V paste, R rotate, T paste air, Ctrl+Z undo, Esc clear selection / menu (save, load, bookmarks)";
	Label hud, loadingLabel;
	Control loading;
	string shotPath, testFilter, startBookmark, saveBookmark;
	bool testing, flightsOnly, ui, editorChecked, hudFull;
	string[] uiShots; // --ui: menu tabs (MenuTab names) or "hud", one shot each
	const int ShotSpacing = 10;
	readonly Dictionary<PassId, bool> argPasses = new(); // --on / --off, over bookmarks
	readonly List<VariantPick> argVariants = new(); // --variant, over bookmarks
	VariantPick[] compareVariants; // --compare-variant: every test item with each
	TestCore tests;
	int shotFrames = 60, frames;
	DebugView defaultView;
	Godot.Environment.ToneMapper tonemap;
	readonly List<(string Key, string Value)> args = new();

	// Before the world's _Ready: its generator comes from the command line or the bookmark of --world.
	public override void _EnterTree()
	{
		foreach (var arg in OS.GetCmdlineUserArgs())
			args.Add(arg.IndexOf('=') is var i and > 0 ? (arg[..i], arg[(i + 1)..]) : (arg, ""));
		foreach (var (key, value) in args)
			switch (key)
			{
				case "--world":
					startBookmark = Snapshot.Resolve(value).Name;
					var h = WorldSave.Open(Path.Combine(Snapshot.Root, startBookmark)).Header;
					(World.Generator, World.GeneratorParams) = (h.Dimensions[h.Player.Dimension].Generator, h.Dimensions[h.Player.Dimension].Params);
					break;
				case "--save-bookmark": saveBookmark = value; break;
				case "--gen": World.Generator = value; break;
				case "--view": World.View = Enum.Parse<ViewPreset>(value, true); break;
				case "--render-scale": Renderer.RenderScale = float.Parse(value, CultureInfo.InvariantCulture); break;
				case "--gen-budget": World.FixedBudget = value == "fixed"; break;
				case "--gen-ms": World.GenTargetMs = float.Parse(value, CultureInfo.InvariantCulture); break;
				case "--world-mb": World.WorldMB = WorldMBArg = int.Parse(value); break;
				case "--item-ms": TestCore.ItemMs = ulong.Parse(value); break;
				case "--debug-view": defaultView = Enum.Parse<DebugView>(value, true); break;
				case "--test": (testing, testFilter) = (true, value); break;
				case "--flight": (testing, testFilter, flightsOnly) = (true, value, true); break;
			}
	}

	public override void _Ready()
	{
		GetWindow().ContentScaleFactor = 0.5f;
		World.QuickLoad = !testing;
		InitQuit();
		var layer = new CanvasLayer();
		hud = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, AnchorRight = 1, OffsetLeft = 10, OffsetTop = 10, OffsetRight = -10 };
		loading = new ColorRect { Color = new Color(0.05f, 0.06f, 0.08f) };
		loading.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		loadingLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		loadingLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		loading.AddChild(loadingLabel);
		layer.AddChild(loading);
		layer.AddChild(hud);
		AddChild(layer);
		BuildMenu(layer);
		tonemap = Env.Environment.TonemapMode;
		SetView(defaultView);
		DefaultPasses();
		startup = CaptureRender();
		if (startBookmark != null) LoadBookmark(Snapshot.Load(startBookmark));
		else Player.Spawn();
		ApplyArgs();
		if (!testing) Input.MouseMode = Input.MouseModeEnum.Captured;
		if (EditorWorld is { } ew) Player.ReadBox = ew.ReadBox;
		if (testing || (shotPath != null && !ui)) { GetNodeOrNull<CanvasLayer>("Editor")?.Hide(); if (EditorWorld is { } w) w.Picking = false; }
		if (!testing) return;
		Player.Autopilot = true;
		tests = new TestCore(this, string.IsNullOrEmpty(testFilter) ? null : testFilter, flightsOnly, compareVariants);
	}

	// Flights and world headers: passes are their defaults plus the bookmark's On less its Off (--on / --off win), the start's render scale, its preset is edited alone.
	public void ApplyBookmark(Bookmark b, WorldHeader header)
	{
		ApplyView(b?.View ?? header?.Player);
		World.TimeOfDay = b?.TimeOfDay ?? header?.TimeOfDay ?? World.TimeOfDay;
		World.Day = b?.Day ?? header?.Day ?? World.Day;
		SetView(b?.DebugView == null ? defaultView : Enum.TryParse<DebugView>(b.DebugView, true, out var dv) ? dv : DebugView.None);
		if (b == null) return;
		Renderer.RenderScale = startup.RenderScale;
		TuneSet.Edit = b.Preset is { } name && name != Bookmark.AutoPreset && TuneSet.Presets.ContainsKey(name) ? name : null;
		static bool Named(string[] names, Pass p) => names?.Any(n => n.Equals(p.Id.ToString(), StringComparison.OrdinalIgnoreCase)) == true;
		foreach (var p in Renderer.Passes)
			p.Enabled = p.Info.Required || (argPasses.TryGetValue(p.Id, out var on) ? on : !Named(b.Off, p) && (TuneSet.DefaultEnabled(p) || Named(b.On, p)));
	}

	// tuning.json's switches over the [Pass] defaults, --on / --off over both.
	void DefaultPasses()
	{
		foreach (var p in Renderer.Passes) p.Enabled = argPasses.TryGetValue(p.Id, out var on) ? on : TuneSet.DefaultEnabled(p);
	}

	public void ApplyView(View? view)
	{
		if (view is not { } v) return;
		World.CameraPosition = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
		Player.Look = (Mathf.DegToRad(v.Yaw), Mathf.DegToRad(v.Pitch));
	}

	// Debug views show their values as they are: linear tone mapping, exposure 1.
	public void SetView(DebugView v)
	{
		Renderer.View = v;
		bool debug = v != DebugView.None;
		Env.Environment.TonemapMode = debug ? Godot.Environment.ToneMapper.Linear : tonemap;
		Env.Environment.TonemapExposure = debug ? 1 : Exposure;
	}

	// --world=bookmark --save-bookmark=name (after the world settled, then quits unless --shot) --gen=id --view=low..ultra --render-scale=f (0 = auto) --gen-budget=fixed --gen-ms=f --world-mb=n (pools for n MB when under the view's budget: memory pressure) --debug-view=steps|albedo|cost|modellevel|temporal --test[=filter] --flight=name --pos=x,y,z (m) --look=yaw,pitch (deg) --time=h --edits=x,y,z[..x,y,z]=block;.. --off=Pass,.. --on=Pass,.. --variant=Pass:name|off,.. --compare-variant=Pass:a,b (tests: each item with both, ms and the image difference in the results) --freeze-streaming --walk --no-vsync --shot=file.png [--frames=n after the world settled] [--ui[=Tab,..,hud]: the menu on each tab (or the full HUD), a shot each as file-tab.png, and the editor] --test-accept[=filter] (captured images become references, then quits)
	void ApplyArgs()
	{
		foreach (var (key, value) in args)
		{
			float[] F() => value.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
			switch (key)
			{
				case "--world": case "--save-bookmark": case "--gen": case "--view": case "--render-scale": case "--gen-budget": case "--gen-ms": case "--world-mb": case "--item-ms": case "--debug-view": case "--test": case "--flight": break;
				case "--pos": var p = F(); World.CameraPosition = new Vector3(p[0], p[1], p[2]); break;
				case "--look": var l = F(); Player.Look = (Mathf.DegToRad(l[0]), Mathf.DegToRad(l[1])); break;
				case "--time": World.TimeOfDay = F()[0]; break;
				case "--edits": Edit(value); break;
				case "--off": case "--on":
					foreach (var n in value.Split(',')) { var id = Enum.Parse<PassId>(n, true); argPasses[id] = key == "--on"; Renderer.Passes.Single(x => x.Id == id).Enabled = key == "--on"; }
					break;
				case "--variant":
					foreach (var pv in value.Split(',')) { var v = VariantPick.Parse(Renderer.Passes, pv[..pv.IndexOf(':')], pv[(pv.IndexOf(':') + 1)..]); argVariants.Add(v); SelectVariant(v); }
					break;
				case "--compare-variant":
					var (pass, names) = (value[..value.IndexOf(':')], value[(value.IndexOf(':') + 1)..].Split(','));
					compareVariants = names.Length == 2 ? names.Select(n => VariantPick.Parse(Renderer.Passes, pass, n)).ToArray() : throw new ArgumentException("--compare-variant=Pass:a,b");
					break;
				case "--freeze-streaming": World.FreezeStreaming = true; break;
				case "--walk": Player.Mode = MoveMode.Walk; break;
				case "--shot": shotPath = value; break;
				case "--ui": (ui, uiShots) = (true, value == "" ? null : value.Split(',')); break;
				case "--frames": shotFrames = int.Parse(value); break;
				case "--no-vsync": DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); break;
				case "--test-accept": Quit(TestCore.AcceptReferences(value) ? 0 : 1, "test-accept"); break;
				default: GD.PushWarning($"unknown argument {key}"); break;
			}
		}
	}

	// ` menu; while it is open Esc closes it, Ctrl+F searches, Ctrl+1.. picks a tab (before the editor and the menu's controls).
	public override void _Input(InputEvent e)
	{
		if (testing || e is not InputEventKey { Pressed: true, Echo: false } k) return;
		int tab = (int)(k.Keycode - Key.Key1);
		if (k.Keycode == Key.Quoteleft) ShowMenu(!MenuOpen);
		else if (!MenuOpen) return;
		else if (k.Keycode == Key.Escape) ShowMenu(false);
		else if (k.CtrlPressed && k.Keycode == Key.F) menu.FocusSearch();
		else if (k.CtrlPressed && tab >= 0 && tab < Enum.GetValues<MenuTab>().Length) menu.Tab = (MenuTab)tab;
		else return;
		GetViewport().SetInputAsHandled();
	}

	// Ctrl+Q quits, G walk / fly, F3 the full HUD, F5 generates the world again, F6 toggles VSync, F7 teleports ahead, F8 A/B of the last two variants picked, B places and X digs a 3^3 box in front (each with the loading screen or the edit path).
	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !MenuOpen) Input.MouseMode = Input.MouseModeEnum.Captured;
		if (e is not InputEventKey { Pressed: true, Echo: false } k || testing) return;
		var ahead = -Player.GlobalBasis.Z;
		switch (k.Keycode)
		{
			case Key.Q when k.CtrlPressed: QuitByUser("Ctrl+Q"); break;
			case Key.G: ToggleWalk(); break;
			case Key.F3: hudFull = !hudFull; break;
			case Key.Escape: Input.MouseMode = Input.MouseModeEnum.Visible; break;
			case Key.F5: World.Regenerate(); break;
			case Key.F6: DisplayServer.WindowSetVsyncMode(DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Disabled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled); break;
			case Key.F8: ToggleVariant(); break;
			case Key.F7: World.CameraPosition += new Vector3(ahead.X, 0, ahead.Z).Normalized() * TeleportMeters; break;
			case Key.B: case Key.X:
				Action<BlockPos, BlockState> set = EditorWorld is { } w ? w.SetBlock : World.SetBlock;
				var state = k.Keycode == Key.B ? World.Registry["base:stone"].Default : BlockState.Air;
				var c = (Vector3I)((World.CameraPosition + ahead * EditReach) / Voxel.BlockSize).Floor();
				for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) set(new BlockPos(c.X + x, c.Y + y, c.Z + z), state);
				break;
		}
	}

	void ToggleWalk() => Player.Mode = Player.Mode == MoveMode.Walk ? MoveMode.Fly : MoveMode.Walk;

	static string Mb(long bytes) => (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);

	string WorldLine()
	{
		var pools = Enum.GetValues<WorldPool>().Select(p => $"{p} {Mb(World.PoolBytes(p))}/{Mb(World.PoolBytes(p, true))}");
		long used = Enum.GetValues<WorldPool>().Sum(p => World.PoolBytes(p)), cap = Enum.GetValues<WorldPool>().Sum(p => World.PoolBytes(p, true));
		string near = World.NearMissing == 0 ? "near OK" : $"near MISSING {World.NearMissing}", mid = World.MidMissing == 0 ? "mid OK" : $"mid MISSING {World.MidMissing}";
		string budget = World.FixedBudget ? "fixed" : $"x{World.Budget.ToString("0.00", CultureInfo.InvariantCulture)}";
		string far = World.FarRadiusM < World.FullRadiusM ? $"far {World.FarRadiusM:0}/{World.FullRadiusM:0} m" : $"far {World.FarRadiusM:0} m";
		return $"world {World.Generator} {World.View}  mem {World.Pressure.ToString("0.00", CultureInfo.InvariantCulture)} of {World.BudgetMB} MB  {far} (shrinks {World.Shrinks}, grows {World.Grows})  queue {World.Queued}  sent {World.SentLastFrame}/frame (budget {budget}){(World.Settled ? "  settled" : "")}  gen {World.GenMs.ToString("0.000", CultureInfo.InvariantCulture)} ms  {near}  {mid}\n" +
			$"pools MB used/cap: {string.Join("  ", pools)}  used {Mb(used)} free {Mb(cap - used)}  fail {string.Join(",", World.PoolFail)}  holes {World.Frame.Holes} (retry {World.Frame.Retrying})  incoming {World.Frame.Incoming}  features dropped {World.FeaturesDropped}  evicted {World.Evicted}  fixed {Mb(World.FixedBytes)} MB";
	}

	public override void _Process(double delta)
	{
		Renderer.ProcessStarted = Time.GetTicksUsec();
		hud.Visible = (shotPath == null || ui) && tests == null && !MenuOpen;
		bool stats = MenuOpen && menu.Tab == MenuTab.Stats;
		if ((hud.Visible || stats) && (hudAge += delta) >= HudSeconds)
		{
			hudAge = 0;
			if (hudFull || stats) foreach (var g in Enum.GetValues<InfoGroup>()) info[g] = Info(g);
			hud.Text = hudFull ? HudText() : $"FPS {Engine.GetFramesPerSecond()}  (F3 info)";
		}
		UpdateMenu(delta);
		if (Renderer.View == DebugView.None) Env.Environment.TonemapExposure = Exposure; // the preset's, blended by Renderer.Sun
		loading.Visible = World.Loading;
		if (loading.Visible)
		{
			int missing = Math.Max(World.NearMissing, 0), total = Math.Max(World.NearTotal, 1), done = total - Math.Min(missing, total);
			if (done != loadingShown) (loadingShown, loadingLabel.Text) = (done, $"Loading world  {100 * done / total} %\nnear chunks {done} / {total}");
		}
		if (tests != null) { tests.Step(delta); return; }
		frames = World.Settled ? frames + 1 : 0;
		if (ui && frames == shotFrames / 2 && !editorChecked) EditorCheck();
		if (frames == shotFrames && saveBookmark != null) GD.Print($"bookmark saved: {SaveBookmark(saveBookmark).Dir}");
		if (frames == shotFrames && shotPath == null && saveBookmark != null) Quit(0, "bookmark saved");
		if (frames >= shotFrames && shotPath != null && (frames - shotFrames) % ShotSpacing == 0) ShotStep((frames - shotFrames) / ShotSpacing);
	}

	// Shot i - 1 is taken, then shot i (a menu tab or the full HUD) is shown.
	void ShotStep(int i)
	{
		var shots = uiShots ?? [null];
		if (i > 0) Shot(shots.Length == 1 ? shotPath : $"{System.IO.Path.ChangeExtension(shotPath, null)}-{shots[i - 1].ToLowerInvariant()}.png");
		foreach (var g in Enum.GetValues<InfoGroup>()) info[g] = Info(g);
		if (i == shots.Length) { GD.Print(HudText().Replace('\n', ' ')); Quit(0, "shot done"); return; }
		if (shots[i] == null) return;
		hudFull = shots[i] == "hud";
		if (!hudFull) menu.Tab = Enum.Parse<MenuTab>(shots[i], true);
		ShowMenu(!hudFull, true);
		hud.Text = HudText();
	}

	const double HudSeconds = 0.25; // the HUD text is rebuilt this often (building it allocates)
	double hudAge = HudSeconds;
	int loadingShown = -1;

	enum InfoGroup { Frame, Passes, World, Camera, Keys }
	readonly Dictionary<InfoGroup, string> info = new();

	// The HUD's (F3) and the Stats tab's lines, by group.
	string Info(InfoGroup g)
	{
		var ic = CultureInfo.InvariantCulture;
		var pos = World.CameraPosition;
		return g switch
		{
			InfoGroup.Frame => $"FPS {Engine.GetFramesPerSecond()}  frame P99 {frameMs.Order().ElementAt(FrameSamples * 99 / 100).ToString("0.0", ic)} ms  GPU voxel {Renderer.TotalMs.ToString("0.000", ic)} ms  " +
				$"output {Renderer.OutputSize.X}x{Renderer.OutputSize.Y}  render {Renderer.RenderSize.X}x{Renderer.RenderSize.Y} (scale {Renderer.EffectiveScale.ToString("0.###", ic)}{(Renderer.RenderScale > 0 ? "" : " auto")}){(Renderer.View != DebugView.None ? $"  debug view {Renderer.View}" : "")}\n" +
				$"frame ms: {string.Join("  ", FrameTimes().Select(s => $"{s.name} {s.ms.ToString("0.000", ic)}"))}",
			InfoGroup.Passes => $"GPU ms: {string.Join("  ", Renderer.Passes.Select(p => $"{p.Id.ToString().ToLowerInvariant()} {(p.Active ? Renderer.PassMs(p.Id).ToString("0.000", ic) : "off")}"))}" +
				(Renderer.View != DebugView.None ? "\n" + Renderer.DebugStats : ""),
			InfoGroup.World => WorldLine(),
			InfoGroup.Camera => $"pos {pos.X:0} {pos.Y:0} {pos.Z:0}  {Player.Mode}  speed {Player.Speed:0} m/s (Ctrl x8)  time {World.TimeOfDay.ToString("0.0", ic)} h day {World.Day}{(World.TimePaused ? " (paused)" : "")}",
			_ => (GetNodeOrNull<CanvasLayer>("Editor") != null ? EditorKeys : "WASD, Space/Shift up/down, Ctrl fast, wheel speed") +
				", G walk / fly (walk: WASD, Space jump / swim up, Ctrl sprint), ` menu, F3 info, Ctrl+Q quit, F5 regenerate, F6 VSync, F7 teleport, F8 A/B variant, B/X place/dig a box, Esc release mouse",
		};
	}

	string HudText() => string.Join("\n", Enum.GetValues<InfoGroup>().Select(g => $"[{g}] " + info.GetValueOrDefault(g, "").Replace("\n", "\n    ")));

	// --ui: the editor's pick and a ReadBox around it into the log, then a 3x1x3 gold slab placed on the picked face through Resolve.
	void EditorCheck()
	{
		editorChecked = true;
		if (EditorWorld is not { } w || w.LastPick is not { } p) { GD.Print("editor: no pick"); return; }
		string Name(BlockState s) => World.Registry.Get(s).Name;
		w.ReadBox(new BlockPos(p.Block.X - 1, p.Block.Y - 1, p.Block.Z - 1), Vector3I.One * 3, s =>
			GD.Print($"editor: pick {p.Block.X},{p.Block.Y},{p.Block.Z} {Name(p.State)} normal {p.Normal} {p.Distance:0.00} m; ReadBox centre {(s == null ? "not ready" : Name(s[13]))}, {s?.Count(x => x != BlockState.Air)} of 27 solid"));
		var gold = World.Registry["base:gold_block"].Default;
		w.Resolve(new BlockPos(p.Place.X - 1, p.Place.Y, p.Place.Z - 1), new Vector3I(3, 1, 3), _ => gold, false, changes =>
		{
			GD.Print($"editor: Resolve placed {changes?.Count} blocks");
			foreach (var (pos, s, _) in changes ?? new()) w.SetBlock(pos, s);
		});
	}

	void Shot(string path)
	{
		GetViewport().GetTexture().GetImage().SavePng(path);
		GD.Print($"shot: {path}");
	}

	// --- tests (core/debug/TestCore.cs) ---

	public bool Loaded => World.GpuReady;
	public bool Idle => World.Settled && !World.Loading;
	public void Load(WorldSave save) => World.Load(save);
	public bool Supports(Flight f) => true;
	public int WorldMBArg { get; private set; } // --world-mb, flights without their own WorldMB
	public float Pressure => World.Pressure;
	public float FarRadiusM => World.FarRadiusM;
	public IEnumerable<(string name, float ms)> PassSamples() =>
		Renderer.Passes.Where(p => p.Active).Select(p => (p.Id.ToString().ToLowerInvariant(), Renderer.PassMs(p.Id))).Append(("voxel", Renderer.TotalMs));
	public IEnumerable<(string name, float ms)> FrameTimes() => Enum.GetValues<FrameStat>().Select(s => (Gpu.Snake(s.ToString()).ToLowerInvariant(), Renderer.FrameMs(s)));
	public float GpuMs => Renderer.LastMs;
	public float GenMs => Math.Max(World.GenMs, 0);
	public Dictionary<string, int> PoolFails => Enum.GetValues<WorldPool>().ToDictionary(p => p.ToString(), p => (int)World.PoolFail[(int)p]);
	public uint[] PoolFailCounts => World.PoolFail; // by WorldPool, read in place every flight frame
	public int? NearMissing => World.NearMissing < 0 ? null : World.NearMissing;
	public int? MidMissing => World.MidMissing < 0 ? null : World.MidMissing;
	public (int Queue, int FarFilled)? Stream => (World.Queued, World.Frame.SentFar - World.Frame.RequeuedFar);
	public string FrameLogHeader => "cx,cy,cz,queue,sent,columns,fill_cap,released,entered,requeued,move_ms,near_ms,pop_ms,drain_ms,upload_ms,step_ms,upkeep_ms,gen_ms,gpu_ms,raster_ms,primary_ms,temporal_ms,near_missing,mid_missing,redone,gc0,gc1,gc2,alloc_kb,alloc_step_kb,alloc_renderer_kb,alloc_render_thread_kb,budget,render_scale," +
		"pressure,far_radius,shrinks,grows,failed,holes,incoming,drained,pending,stale,q_near,q_mid,q_far,sent_near,sent_mid,sent_far,requeued_far,far_age,far_age_max," + string.Join(",", Enum.GetNames<WorldPool>().SelectMany(p => new[] { $"used_{p}", $"high_{p}", $"free_{p}", $"fail_{p}" })) + string.Concat(Renderer.Passes.Select(p => $",pass_{p.Id}_ms")) + ",gen_columns_ms,gen_classify_ms,gen_fill_ms,gen_pack_ms";

	// One line of FrameLogHeader's columns, appended without allocating (interpolation handlers).
	public void FrameLog(StringBuilder into)
	{
		var (f, c) = (World.Frame, World.CameraChunk);
		int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
		long alloc = GC.GetTotalAllocatedBytes();
		var ic = CultureInfo.InvariantCulture;
		into.Append(ic, $"{c.X},{c.Y},{c.Z},{f.Queue},{f.Sent},{f.Columns},{f.FillCap},{f.Released},{f.Entered},{f.Requeued},");
		into.Append(ic, $"{f.MoveMs:0.###},{f.NearMs:0.###},{f.PopMs:0.###},{f.DrainMs:0.###},{f.UploadMs:0.###},{f.StepMs:0.###},{World.UpkeepMs:0.###},{World.GenMs:0.###},{Renderer.LastMs:0.###},");
		into.Append(ic, $"{Renderer.LastPassMs(PassId.Raster):0.###},{Renderer.LastPassMs(PassId.Primary):0.###},{Renderer.LastPassMs(PassId.Temporal):0.###},{World.NearMissing},{World.MidMissing},{World.Redone},");
		into.Append(ic, $"{gc0 - gcSeen.Gen0},{gc1 - gcSeen.Gen1},{gc2 - gcSeen.Gen2},{Kb(alloc - gcSeen.Alloc):0.###},{Kb(World.StepAllocated):0.###},{Kb(Renderer.ProcessAllocated):0.###},{Kb(Renderer.TakeRenderThreadAllocated()):0.###},{World.Budget:0.###},{Renderer.EffectiveScale:0.###},");
		into.Append(ic, $"{World.Pressure:0.###},{World.FarRadiusM:0},{World.Shrinks},{World.Grows},{f.Failed},{f.Holes},{f.Incoming},{f.Drained},{f.Pending},{f.Stale},{f.QNear},{f.QMid},{f.QFar},{f.SentNear},{f.SentMid},{f.SentFar},{f.RequeuedFar},{f.FarAge:0.###},{f.FarAgeMax}");
		for (int p = 0; p < 4; p++) into.Append(ic, $",{World.PoolUsed[p]},{World.PoolHigh[p]},{World.PoolFree[p]},{World.PoolFail[p]}");
		foreach (var p in Renderer.Passes) into.Append(ic, $",{Renderer.LastPassMs(p.Id):0.###}");
		foreach (var ms in World.GenPhaseMs) into.Append(ic, $",{ms:0.###}");
		into.Append('\n');
		gcSeen = (gc0, gc1, gc2, alloc);
	}

	(int Gen0, int Gen1, int Gen2, long Alloc) gcSeen;
	static double Kb(long bytes) => bytes / 1024.0;

	public void LoadGenerator(string generator, JsonElement? parameters)
	{
		(World.Generator, World.GeneratorParams) = (generator, parameters);
		World.Edits.Clear();
		World.Regenerate();
	}

	public void Regenerate(JsonObject parameters)
	{
		var p = World.GeneratorParams is { } e ? JsonNode.Parse(e.GetRawText()) as JsonObject ?? new() : new JsonObject();
		foreach (var (k, v) in parameters) p[k] = v?.DeepClone();
		World.GeneratorParams = JsonSerializer.SerializeToElement(p);
		World.Regenerate();
	}

	// --edits syntax: x,y,z[..x,y,z]=block;...
	public void Edit(string script)
	{
		foreach (var entry in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			var state = World.Registry[entry[(entry.LastIndexOf('=') + 1)..].Trim()].Default;
			var corners = entry[..entry.LastIndexOf('=')].Split("..").Select(c => c.Split(',').Select(int.Parse).ToArray()).ToArray();
			int[] a = corners[0], b = corners[^1];
			for (int z = Math.Min(a[2], b[2]); z <= Math.Max(a[2], b[2]); z++)
				for (int y = Math.Min(a[1], b[1]); y <= Math.Max(a[1], b[1]); y++)
					for (int x = Math.Min(a[0], b[0]); x <= Math.Max(a[0], b[0]); x++) World.SetBlock(new BlockPos(x, y, z), state);
		}
	}
}
