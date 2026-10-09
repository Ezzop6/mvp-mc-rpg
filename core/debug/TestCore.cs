using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Renesance.Content;
using Renesance.Save;

public enum TestIssue { NoReference, ImageDiff, SizeMismatch, WrongResolution, NotSettled, Slower, PoolOverflow, Hitch, NearMissing, Degraded, Timeout, CameraMoved, UnknownView, WrongPosition }

public enum BookmarkStatus { Open, Resolved }

public enum Captured { Shot, PassMs, GenMs, Pools, Shimmer }

// Exact camera: the anchor chunk, the position within it (m) and the look (radians).
public record struct Pose(Vec3I Anchor, Vec3 Local, float Yaw, float Pitch);

// tests/bookmarks/<name>/: a world save with every edit plus bookmark.json, the camera, time, window and the engine's render state (docs/testing.md, Záložky).
public sealed class Snapshot
{
	public const string FileName = "bookmark.json";
	public static string Root => ProjectSettings.GlobalizePath("res://tests/bookmarks"); // also the worlds of flights
	[JsonIgnore] public string Name { get; set; }
	public string Note { get; set; }
	public string Created { get; set; }
	public string Commit { get; set; }
	public BookmarkStatus Status { get; set; }
	public string Redirect { get; set; } // another bookmark loaded in this one's place
	public Pose Camera { get; set; }
	public int? ChunkSize { get; set; } // of Camera.Anchor, null = 25 (saved before the field)
	public MoveMode Move { get; set; }
	public float TimeOfDay { get; set; }
	public int Day { get; set; }
	public bool TimePaused { get; set; } = true; // tests stay deterministic unless the bookmark runs the clock
	public double Clock { get; set; } // World.Clock: sky and texture animation
	public int Width { get; set; } = 1920;
	public int Height { get; set; } = 1080;
	public JsonObject Render { get; set; } // the engine's dump, null = its defaults
	[JsonIgnore] public string Dir => Path.Combine(Root, Name);

	// Camera with the anchor in chunks of Coords.ChunkSize (the same point in metres).
	[JsonIgnore] public Pose CameraNow
	{
		get
		{
			int cs = ChunkSize ?? 25;
			if (cs == Renesance.World.Coords.ChunkSize) return Camera;
			double m = cs * Renesance.World.Coords.BlockMeters, cm = Renesance.World.Coords.ChunkSize * Renesance.World.Coords.BlockMeters;
			(int, float) Axis(int a, float l) { double p = a * m + l; int c = (int)Math.Floor(p / cm); return (c, (float)(p - c * cm)); }
			var ((x, lx), (y, ly), (z, lz)) = (Axis(Camera.Anchor.X, Camera.Local.X), Axis(Camera.Anchor.Y, Camera.Local.Y), Axis(Camera.Anchor.Z, Camera.Local.Z));
			return Camera with { Anchor = new Vec3I(x, y, z), Local = new Vec3(lx, ly, lz) };
		}
	}

	public static bool Exists(string name) => WorldSave.ValidName(name) && File.Exists(Path.Combine(Root, name, FileName));

	public static IEnumerable<string> List() => WorldSave.List(Root).Where(Exists);

	public static Snapshot Load(string name)
	{
		var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(Root, name, FileName)), TestCore.Json);
		s.Name = name;
		return s;
	}

	// Follows Redirect, a cycle stops at its first repeat.
	public static Snapshot Resolve(string name)
	{
		var seen = new HashSet<string>();
		var s = Load(name);
		while (s.Redirect != null && seen.Add(s.Name)) s = Load(s.Redirect);
		return s;
	}

	public void Save()
	{
		Directory.CreateDirectory(Dir);
		File.WriteAllText(Path.Combine(Dir, FileName), JsonSerializer.Serialize(this, TestCore.Json));
	}

	// Redirects to the old name follow it.
	public static void Rename(string name, string newName)
	{
		WorldSave.Rename(Root, name, newName);
		foreach (var s in List().Select(Load).Where(s => s.Redirect == name).ToList()) { s.Redirect = newName; s.Save(); }
	}

	public static void Delete(string name) => WorldSave.Delete(Root, name);

	public static string FreeName(string prefix)
	{
		string name = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}";
		return Enumerable.Range(1, 100).Select(i => i == 1 ? name : $"{name}-{i}").First(n => !Directory.Exists(Path.Combine(Root, n)));
	}
}

// tests/lists/<name>.json: bookmarks in order and what is captured of each (docs/testing.md, Seznamy).
public sealed class TestList
{
	public static string Root => ProjectSettings.GlobalizePath("res://tests/lists");
	public string Note { get; set; }
	public List<string> Bookmarks { get; set; } = new();
	public HashSet<Captured> Capture { get; set; } = Enum.GetValues<Captured>().ToHashSet();
	public List<string> Views { get; set; } = new(); // debug views captured besides the shot

	static string PathOf(string name) => Path.Combine(Root, name + ".json");
	public static bool Exists(string name) => WorldSave.ValidName(name) && File.Exists(PathOf(name));
	public static IEnumerable<string> Names() => Directory.Exists(Root) ? Directory.GetFiles(Root, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal) : Enumerable.Empty<string>();
	public static TestList Load(string name) => JsonSerializer.Deserialize<TestList>(File.ReadAllText(PathOf(name)), TestCore.Json);
}

// Tests: bookmark lists of tests/lists and flights of tests/flights, results and history (docs/testing.md).
public sealed class TestCore
{
	// Thresholds and frame counts, docs/testing.md (Prahy).
	public const int PixelTolerance = 8, DiffGain = 8;
	public const double MaxMeanError = 0.5, MaxDifferentShare = 0.002;
	const double SlowerRatio = 1.15, SlowerMs = 0.3;
	public const int SettleFrames = 10, SettleTimeout = 1800;
	const int ConvergeFrames = 16, ViewFrames = 16, ShimmerFrames = 16, FlickerLevel = 2;
	public static ulong ItemMs = 5000; // --item-ms for measurement runs that need the whole world in
	public const string ShotKey = "shot";

	public sealed record ImageDiff(double MeanError, int MaxError, double DifferentShare);
	public sealed record Slowdown(double Ms, double Previous);
	public sealed record Previous(string Stamp, Dictionary<string, ImageDiff> Images, Dictionary<string, Slowdown> Slower);
	public sealed record ItemResult(string List, string Bookmark, string Loaded, string Note, BookmarkStatus Status, int[] Size, bool Passed, List<TestIssue> Issues,
		double LoadMs, bool Settled, double? GenMs, Dictionary<string, double> PassMs, Dictionary<string, double> FrameMs, Dictionary<string, double> Shimmer, Dictionary<string, double> PoolMB, Dictionary<string, int> Overflow,
		Dictionary<string, string> Images, Dictionary<string, ImageDiff> Reference, Previous Previous, string Variant = null, Dictionary<string, ImageDiff> VariantDiff = null,
		double? Pressure = null, double? FarRadiusM = null); // Variant, VariantDiff: --compare-variant, the images against the first variant's
	public sealed record TestRun(string Time, string Commit, string Gpu, string Driver, string Api, bool Passed, List<ItemResult> Results, List<FlightResult> Flights, bool Optimized); // Optimized: the game's C# runs JIT-optimized (Release-like build, docs/testing.md)

	sealed record Item(string List, TestList Spec, string Name, VariantPick? Pick = null, VariantPick? Against = null);

	public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
	static readonly JsonSerializerOptions JsonLine = new() { Converters = { new JsonStringEnumConverter() } };
	public static string OutRoot => ProjectSettings.GlobalizePath("res://tests/out");
	static string HistoryPath => Path.Combine(OutRoot, "history.jsonl");

	public readonly Main Host;
	public readonly string Stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
	public bool Running { get; private set; } = true;
	readonly Queue<Item> items = new();
	readonly Queue<Flight> flights = new();
	readonly List<ItemResult> results = new();
	readonly List<FlightResult> flightResults = new();
	FlightPlayer flight;
	Item item;
	Snapshot snap;
	List<TestIssue> issues;
	Dictionary<string, string> images;
	Dictionary<string, ImageDiff> reference, variantDiff;
	Dictionary<string, double> passMs, frameMs, poolMB, shimmer;
	float[] lumaSum, lumaSq, lumaLast;
	double change;
	int shimmerFrame = -1;
	Dictionary<string, int> overflow;
	ulong started;
	double loadMs, genMs;
	int frames, idle, view;
	bool waiting, settled;

	// filter: `;`-separated test lists, bookmarks or flights, null = every list and flight; flightsOnly takes flights only; compare: two variants each list item runs with.
	public TestCore(Main host, string filter, bool flightsOnly, VariantPick[] compare = null)
	{
		Host = host;
		var missing = new[] { ContentPaths.Textures, ContentPaths.Mod(BlockId.BaseMod) }.Select(d => ProjectSettings.GlobalizePath("res://" + d)).Where(d => !Directory.Exists(d)).ToList();
		if (missing.Count > 0)
		{
			GD.PrintErr($"[test] missing {string.Join(", ", missing)}: link the main checkout's assets (docs/testing.md)");
			Quit(2);
			return;
		}
		var all = Flight.All().ToDictionary(f => f.Name);
		var tokens = filter?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? (flightsOnly ? all.Keys : TestList.Names().Concat(all.Keys)).ToArray();
		var unknown = new List<string>();
		foreach (var t in tokens)
			if (all.TryGetValue(t, out var f))
			{
				if (Host.Supports(f)) flights.Enqueue(f);
				else GD.Print($"[flight] {f.Name}: skipped, not supported");
			}
			else if (!flightsOnly && TestList.Exists(t)) { var list = TestList.Load(t); foreach (var b in list.Bookmarks) items.Enqueue(new Item(t, list, b)); }
			else if (!flightsOnly && Snapshot.Exists(t)) items.Enqueue(new Item(t, new TestList(), t));
			else unknown.Add(t);
		foreach (var i in items.Where(i => !Resolves(i.Name))) unknown.Add($"{i.List}/{i.Name}");
		if (compare != null)
			foreach (var i in items.ToArray()) { items.Dequeue(); items.Enqueue(i with { Pick = compare[0] }); items.Enqueue(i with { Pick = compare[1], Against = compare[0] }); }
		if (unknown.Count > 0 || items.Count + flights.Count == 0)
		{
			GD.PrintErr($"[test] no test list, bookmark or flight (or a broken redirect): {string.Join(", ", unknown.DefaultIfEmpty(filter ?? "anything"))} in {TestList.Root}, {Snapshot.Root} or {Flight.Root}");
			Quit(1);
		}
	}

	static bool Resolves(string name)
	{
		try { Snapshot.Resolve(name); return true; }
		catch (Exception e) when (e is IOException or JsonException) { return false; }
	}

	void Quit(int code)
	{
		Running = false;
		Host.Quit(code, "tests finished");
	}

	public void Step(double delta)
	{
		if (!Running || !Host.Loaded) return;
		if (flight != null)
		{
			if (flight.Step(delta) is FlightResult r) { flightResults.Add(r); flight = null; }
			return;
		}
		if (item == null)
		{
			if (items.TryDequeue(out var next)) Begin(next);
			else if (flights.TryDequeue(out var f)) flight = new FlightPlayer(this, f);
			else Finish();
			return;
		}
		if (waiting) { Wait(); return; }
		if (shimmerFrame >= 0) { Shimmer(); return; }
		if (++frames < (view < 0 ? ConvergeFrames : ViewFrames)) return;
		string key = view < 0 ? ShotKey : item.Spec.Views[view];
		if (view < 0) Values();
		if (view >= 0 || item.Spec.Capture.Contains(Captured.Shot)) Shot(key);
		if (view < 0 && item.Spec.Capture.Contains(Captured.Shimmer)) shimmerFrame = 0;
		else NextView();
	}

	void NextView()
	{
		while (++view < item.Spec.Views.Count && !Host.SetDebugView(item.Spec.Views[view])) issues.Add(TestIssue.UnknownView);
		if (view < item.Spec.Views.Count) frames = 0;
		else End();
	}

	Vector2I size;

	// Window of the given resolution, centred on its screen, not resizable so tiling window managers leave it floating.
	public void Resize(string resolution, string who)
	{
		if (!Bookmark.Resolutions.TryGetValue(resolution ?? Bookmark.DefaultResolution, out var r))
		{
			GD.PushWarning($"[test] {who}: unknown resolution {resolution}, using {Bookmark.DefaultResolution}");
			r = Bookmark.Resolutions[Bookmark.DefaultResolution];
		}
		Resize(new Vector2I(r.Width, r.Height));
	}

	void Resize(Vector2I to)
	{
		size = to;
		var w = Host.GetWindow();
		(w.Mode, w.Unresizable, w.Size) = (Window.ModeEnum.Windowed, true, size);
		w.Position = DisplayServer.ScreenGetPosition(w.CurrentScreen) + ((DisplayServer.ScreenGetSize(w.CurrentScreen) - size) / 2).Max(Vector2I.Zero);
	}

	// True once sized and idle for SettleFrames (issue null), or after SettleTimeout (issue set).
	public bool Settle(ref int count, ref int waited, out TestIssue? issue)
	{
		bool sized = Host.GetWindow().Size == size;
		count = sized && Host.Idle ? count : 0;
		issue = ++waited > SettleTimeout && count < SettleFrames ? sized ? TestIssue.NotSettled : TestIssue.WrongResolution : null;
		return count >= SettleFrames || issue != null;
	}

	public int[] WindowSize { get { var s = Host.GetWindow().Size; return new[] { s.X, s.Y }; } }

	// The bookmark's whole state, world included, so the result doesn't depend on the order or the list.
	void Begin(Item next)
	{
		item = next;
		snap = Snapshot.Resolve(item.Name);
		if (snap.Name != item.Name) GD.Print($"[test] {item.Name}: redirected to {snap.Name}");
		(issues, images, reference, variantDiff, waiting, idle, genMs, view, started, shimmer, shimmerFrame) = (new(), new(), new(), new(), true, 0, 0, -1, Time.GetTicksMsec(), null, -1);
		Host.LoadBookmark(snap);
		if (item.Pick is { } pick) Host.SelectVariant(pick);
		Resize(new Vector2I(snap.Width, snap.Height));
	}

	// SettleFrames idle frames in the bookmark's window size, or after ItemMs from the load at least the near part (else Timeout).
	void Wait()
	{
		genMs += Host.GenMs;
		bool sized = Host.GetWindow().Size == size, late = Time.GetTicksMsec() - started > ItemMs;
		idle = sized && Host.Idle ? idle + 1 : 0;
		if (idle < SettleFrames && !late) return;
		if (late && !(sized && Host.NearReady)) issues.Add(sized ? TestIssue.Timeout : TestIssue.WrongResolution);
		(loadMs, settled, waiting, frames) = (Time.GetTicksMsec() - started, idle >= SettleFrames, false, 0);
	}

	void Values()
	{
		var c = item.Spec.Capture;
		if (Host.CameraPose is var cam && cam != snap.CameraNow) { issues.Add(TestIssue.CameraMoved); GD.Print($"[test] {snap.Name}: camera {cam}, bookmark {snap.CameraNow}"); }
		passMs = c.Contains(Captured.PassMs) ? Host.PassSamples().ToDictionary(p => p.name, p => Math.Round(p.ms, 3)) : null;
		frameMs = c.Contains(Captured.PassMs) ? Host.FrameTimes().ToDictionary(p => p.name, p => Math.Round(p.ms, 3)) : null;
		poolMB = c.Contains(Captured.Pools) ? Host.PoolMB : null;
		overflow = Overflow();
		if (overflow != null) issues.Add(TestIssue.PoolOverflow);
	}

	// Still camera over ShimmerFrames frames: per-pixel luma (0-255) standard deviation, its mean and the share over FlickerLevel, the mean change between frames; the deviation x16 as <shot>.shimmer.png.
	void Shimmer()
	{
		var img = Host.GetViewport().GetTexture().GetImage();
		img.Convert(Image.Format.Rgb8);
		var d = img.GetData();
		int n = d.Length / 3;
		if (shimmerFrame == 0) (lumaSum, lumaSq, lumaLast, change) = (new float[n], new float[n], new float[n], 0);
		for (int i = 0; i < n; i++)
		{
			float y = 0.299f * d[3 * i] + 0.587f * d[3 * i + 1] + 0.114f * d[3 * i + 2];
			if (shimmerFrame > 0) change += Math.Abs(y - lumaLast[i]);
			(lumaLast[i], lumaSum[i], lumaSq[i]) = (y, lumaSum[i] + y, lumaSq[i] + y * y);
		}
		if (++shimmerFrame < ShimmerFrames) return;
		double sum = 0;
		int over = 0;
		var map = new byte[d.Length];
		for (int i = 0; i < n; i++)
		{
			float m = lumaSum[i] / ShimmerFrames, sd = MathF.Sqrt(Math.Max(0, lumaSq[i] / ShimmerFrames - m * m));
			sum += sd;
			if (sd > FlickerLevel) over++;
			map[3 * i] = map[3 * i + 1] = map[3 * i + 2] = (byte)Math.Min(255, sd * 16);
		}
		shimmer = new() { ["MeanStd"] = Math.Round(sum / n, 3), ["FlickerShare"] = Math.Round((double)over / n, 5), ["MeanChange"] = Math.Round(change / n / (ShimmerFrames - 1), 3) };
		string dir = Path.Combine(OutRoot, Stamp, item.List);
		Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgb8, map).SavePng(Path.Combine(dir, ShotFile(item.Pick, ShotKey) + ".shimmer.png"));
		(lumaSum, lumaSq, lumaLast, shimmerFrame) = (null, null, null, -1);
		NextView();
	}

	string ShotFile(VariantPick? pick, string key) => FileName(snap.Name) + (pick is { } p ? "." + FileName(p.ToString()) : "") + (key == ShotKey ? "" : "." + FileName(key));

	// A compared variant against the first one's image instead of the reference (the first one: none).
	void Shot(string key)
	{
		string dir = Path.Combine(OutRoot, Stamp, item.List), file = ShotFile(item.Pick, key);
		if (item.Pick == null && Capture(dir, file, Path.Combine(snap.Dir, "ref", FileName(key) + ".png"), issues) is { } d) reference[key] = d;
		if (item.Pick != null && Capture(dir, file, item.Against is { } a ? Path.Combine(dir, ShotFile(a, key) + ".png") : null, new()) is { } vd) variantDiff[key] = vd;
		images[key] = Path.Combine(item.List, file + ".png");
	}

	// Saves the frame as dir/file.png and its difference to refPath as file.diff.png; image issues go to `into`.
	public ImageDiff Capture(string dir, string file, string refPath, List<TestIssue> into)
	{
		var img = Host.GetViewport().GetTexture().GetImage();
		img.Convert(Image.Format.Rgb8);
		Directory.CreateDirectory(dir);
		img.SavePng(Path.Combine(dir, file + ".png"));
		if (refPath == null) return null;
		if (!File.Exists(refPath)) { into.Add(TestIssue.NoReference); return null; }
		if (Diff(img, refPath) is not { } diff) { into.Add(TestIssue.SizeMismatch); return null; }
		var (d, map) = diff;
		Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgb8, map).SavePng(Path.Combine(dir, file + ".diff.png"));
		if (d.MeanError > MaxMeanError || d.DifferentShare > MaxDifferentShare) into.Add(TestIssue.ImageDiff);
		return d;
	}

	// Null when the sizes differ.
	static (ImageDiff, byte[])? Diff(Image img, string path)
	{
		var other = Image.LoadFromFile(path);
		other.Convert(Image.Format.Rgb8);
		return other.GetSize() == img.GetSize() ? CompareImages(img.GetData(), other.GetData()) : null;
	}

	// Per channel 0-255; a pixel differs when any channel is off by more than PixelTolerance.
	public static (ImageDiff, byte[]) CompareImages(byte[] a, byte[] b)
	{
		var diff = new byte[a.Length];
		long sum = 0;
		int max = 0, over = 0;
		for (int i = 0; i < a.Length; i += 3)
		{
			int m = 0;
			for (int c = i; c < i + 3; c++)
			{
				int d = Math.Abs(a[c] - b[c]);
				sum += d;
				m = Math.Max(m, d);
				diff[c] = (byte)Math.Min(255, d * DiffGain);
			}
			max = Math.Max(max, m);
			if (m > PixelTolerance) over++;
		}
		return (new ImageDiff((double)sum / a.Length, max, 3.0 * over / a.Length), diff);
	}

	// Pools that overflowed, null when none did.
	public Dictionary<string, int> Overflow()
	{
		var o = Host.PoolFails.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
		return o.Count > 0 ? o : null;
	}

	public static string OverflowText(Dictionary<string, int> o) => o == null ? "" : "  overflow " + string.Join(", ", o.Select(p => $"{p.Key} {p.Value}"));

	void End()
	{
		var r = new ItemResult(item.List, item.Name, snap.Name, snap.Note, snap.Status, WindowSize, false, issues, loadMs, settled,
			item.Spec.Capture.Contains(Captured.GenMs) ? Math.Round(genMs, 3) : null, passMs, frameMs, shimmer, poolMB, overflow, images, reference.Count > 0 ? reference : null, null,
			item.Pick?.ToString(), variantDiff.Count > 0 ? variantDiff : null, poolMB == null ? null : Math.Round(Host.Pressure, 3), poolMB == null ? null : Math.Round(Host.FarRadiusM));
		var prev = PreviousOf(r);
		if (prev?.Slower != null) issues.Add(TestIssue.Slower);
		r = r with { Issues = issues.Distinct().ToList(), Previous = prev, Passed = issues.All(x => x is TestIssue.NoReference or TestIssue.UnknownView or TestIssue.Slower) };
		results.Add(r);
		string Diffs(Dictionary<string, ImageDiff> d) => d == null ? "" : string.Join(" ", d.Select(p => $"{p.Key} {p.Value.MeanError:0.000}/{100 * p.Value.DifferentShare:0.00}%"));
		GD.Print($"[test] {r.List}/{r.Bookmark}{(r.Variant == null ? "" : " " + r.Variant)}: {(r.Passed ? "ok" : "FAIL")} {string.Join(" ", r.Issues)}  load {loadMs:0} ms{(settled ? "" : " (near only)")}  gen {r.GenMs:0.0} ms" +
			(passMs?.GetValueOrDefault("voxel") is double v ? $"  voxel {v:0.00} ms" : "") + (r.Pressure is double pr ? $"  mem {pr:0.00} far {r.FarRadiusM:0} m" : "") +
			(frameMs == null ? "" : "  " + string.Join(" ", frameMs.Select(p => $"{p.Key} {p.Value:0.00}"))) +
			(shimmer == null ? "" : "  shimmer " + string.Join(" ", shimmer.Select(p => $"{p.Key} {p.Value}"))) + OverflowText(overflow) +
			(Host.Renderer.View == DebugView.None ? "" : "  " + Host.Renderer.DebugStats) +
			(r.Reference == null ? "" : $"  ref {Diffs(r.Reference)}") + (r.VariantDiff == null ? "" : $"  vs {item.Against} {Diffs(r.VariantDiff)}") + (prev == null ? "" : $"  vs {prev.Stamp} {Diffs(prev.Images)}"));
		item = null;
	}

	// The last run of the same list item on this GPU: image differences and, when both settled, passes slower past both thresholds (reported, not failing).
	Previous PreviousOf(ItemResult r)
	{
		string gpu = RenderingServer.GetVideoAdapterName();
		var run = History().LastOrDefault(h => h.Gpu == gpu && h.Results?.Any(p => p.List == r.List && p.Bookmark == r.Bookmark && p.Variant == r.Variant && p.Images != null) == true);
		if (run == null) return null;
		var p = run.Results.Last(p => p.List == r.List && p.Bookmark == r.Bookmark && p.Variant == r.Variant && p.Images != null);
		var diffs = new Dictionary<string, ImageDiff>();
		foreach (var (key, file) in r.Images)
		{
			string old = p.Images.GetValueOrDefault(key) is { } f ? Path.Combine(OutRoot, run.Time, f) : null;
			if (old == null || !File.Exists(old)) continue;
			var img = Image.LoadFromFile(Path.Combine(OutRoot, Stamp, file));
			img.Convert(Image.Format.Rgb8);
			if (Diff(img, old) is { } d) diffs[key] = d.Item1;
		}
		var slower = !r.Settled || !p.Settled ? null : r.PassMs?.Where(t => p.PassMs != null && p.PassMs.TryGetValue(t.Key, out var b) && t.Value > b * SlowerRatio && t.Value - b > SlowerMs)
			.ToDictionary(t => t.Key, t => new Slowdown(t.Value, p.PassMs[t.Key]));
		return new Previous(run.Time, diffs, slower?.Count > 0 ? slower : null);
	}

	// Lines of older formats are skipped.
	static IEnumerable<TestRun> History()
	{
		if (!File.Exists(HistoryPath)) yield break;
		foreach (var line in File.ReadLines(HistoryPath))
		{
			TestRun run = null;
			try { run = JsonSerializer.Deserialize<TestRun>(line, JsonLine); } catch (JsonException) { }
			if (run != null) yield return run;
		}
	}

	public static string Commit()
	{
		var output = new Godot.Collections.Array();
		return OS.Execute("git", new[] { "-C", ProjectSettings.GlobalizePath("res://"), "describe", "--always", "--dirty" }, output) == 0 ? ((string)output[0]).Trim() : "unknown";
	}

	void Finish()
	{
		var run = new TestRun(Stamp, Commit(), RenderingServer.GetVideoAdapterName(), string.Join(" ", OS.GetVideoAdapterDriverInfo()),
			RenderingServer.GetVideoAdapterApiVersion(), results.All(r => r.Passed) && flightResults.All(r => r.Passed), results, flightResults.Count > 0 ? flightResults : null,
			typeof(TestCore).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled != true);
		string dir = Path.Combine(OutRoot, Stamp), latest = Path.Combine(OutRoot, "latest");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "results.json"), JsonSerializer.Serialize(run, Json));
		File.AppendAllText(HistoryPath, JsonSerializer.Serialize(run, JsonLine) + "\n");
		if (new FileInfo(latest).LinkTarget != null) File.Delete(latest);
		File.CreateSymbolicLink(latest, Stamp);
		GD.Print($"[test] {results.Count(r => r.Passed)} / {results.Count} passed, flights {flightResults.Count(r => r.Passed)} / {flightResults.Count}, {dir}");
		Quit(run.Passed ? 0 : 1);
	}

	// The newest captured images of each bookmark of the matching lists or bookmarks become its references in tests/bookmarks/<b>/ref, never flight references.
	public static bool AcceptReferences(string filter)
	{
		var names = (filter ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
		var newest = new Dictionary<(string Bookmark, string Key), string>();
		var runs = Directory.Exists(OutRoot) ? Directory.GetDirectories(OutRoot).Where(d => Path.GetFileName(d) != "latest").Order(StringComparer.Ordinal) : Enumerable.Empty<string>();
		foreach (var run in runs.Where(d => File.Exists(Path.Combine(d, "results.json"))))
		{
			TestRun r = null;
			try { r = JsonSerializer.Deserialize<TestRun>(File.ReadAllText(Path.Combine(run, "results.json")), Json); } catch (JsonException) { }
			foreach (var i in r?.Results?.Where(i => i.Images != null && i.Variant == null && (names.Count == 0 || names.Contains(i.List) || names.Contains(i.Bookmark) || names.Contains(i.Loaded))) ?? [])
				foreach (var (key, file) in i.Images) newest[(i.Loaded, key)] = Path.Combine(run, file);
		}
		int accepted = 0;
		foreach (var ((bookmark, key), src) in newest.Where(n => Snapshot.Exists(n.Key.Bookmark) && File.Exists(n.Value)))
		{
			string dst = Path.Combine(Snapshot.Root, bookmark, "ref", FileName(key) + ".png");
			Directory.CreateDirectory(Path.GetDirectoryName(dst));
			File.Copy(src, dst, overwrite: true);
			GD.Print($"[test-accept] {src} -> {dst}");
			accepted++;
		}
		if (accepted == 0) GD.PrintErr($"[test-accept] no captured image matches '{filter}' in {OutRoot}");
		return accepted > 0;
	}

	public static string FileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
