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

public enum FlightAction { Teleport, Regenerate, Edit, Turn, Walk, Expect }

public sealed record FlightPoint(double T, Vec3 Pos, float Yaw, float Pitch);

// Teleport: By (m); Regenerate: Params over the current ones; Edit: Edits (--edits syntax); Turn: Yaw (deg) over Seconds;
// Walk (Move: Walk): input from now on, Move.X right, Move.Z forward (-1..1), Jump, Sprint; Expect: the eye at Pos (m) within Tolerance, else WrongPosition.
public sealed record FlightEvent(double T, FlightAction Kind, Vec3 By, JsonObject Params, string Edits, float Yaw, double Seconds,
	Vec3 Move = default, bool Jump = false, bool Sprint = false, Vec3 Pos = default, float Tolerance = FlightEvent.DefaultTolerance)
{
	public const float DefaultTolerance = 0.02f;
}

public sealed record FrameStats(double Mean, double P99, double Worst);
public sealed record Hitch(double T, double Ms);
public sealed record Drift(double QueueStart, double QueueEnd, double FarFillStart, double FarFillEnd);
public sealed record Memory(int BudgetMB, double PressureMax, double PressureMean, double FarMinM, double FarEndM, double FullM, int Shrinks, int Grows, int Evicted = 0); // the pool regulator while flying (docs/v2.md §3.6)
public sealed record FlightResult(string Flight, string Resolution, int[] Size, bool Passed, List<TestIssue> Issues, int Frames, FrameStats Frame, FrameStats Gpu, FrameStats Gen,
	int Hitches, int HitchesOverMs, List<Hitch> HitchList, Dictionary<string, int> Overflow, int? NearMissing, int? MidMissing, int MissingFrames,
	Dictionary<string, TestCore.ImageDiff> Shots, Drift Drift, int[] Collections, double AllocatedMB, Memory Memory = null); // Collections: GCs per generation while flying, AllocatedMB: managed bytes allocated meanwhile

// A dynamic test, tests/flights/<name>.json (docs/testing.md, Lety): the camera pose is a function of the frame number.
public sealed class Flight
{
	public static string Root => ProjectSettings.GlobalizePath("res://tests/flights");
	public string Name { get; set; }
	public string Scene { get; set; }
	public string Generator { get; set; }
	public JsonElement? Params { get; set; }
	public string Resolution { get; set; } = Bookmark.DefaultResolution;
	public string Preset { get; set; } = Bookmark.AutoPreset;
	public string[] Off { get; set; }
	public string[] On { get; set; }
	public float TimeOfDay { get; set; } = 12;
	public MoveMode Move { get; set; } // Walk: Path gives the start and the look, the player walks by Walk actions
	public bool TimePaused { get; set; } = true;
	public float? RenderScale { get; set; } // null = the start's (--render-scale or Renderer.DefaultScale)
	public string DebugView { get; set; } // DebugView name, null = none
	public double Fps { get; set; } = 120;
	public int? WorldMB { get; set; }
	public ViewPreset? ViewPreset { get; set; } // null = the current one
	public bool NoWait { get; set; } // start once the near sphere is in (QuickLoad), not when the world settled
	public float NearRadius { get; set; } = 64;
	public double HitchMs { get; set; } = 16.7;
	public int GraceFrames { get; set; } = 120;
	public double? DriftWindow { get; set; } // s: mean queue and far fill of the second such window vs the last one, null = no check
	public List<FlightPoint> Path { get; set; } = new();
	public List<FlightEvent> Actions { get; set; } = new();
	public List<double> Shots { get; set; } = new();
	public string Note { get; set; }

	public static IEnumerable<Flight> All() => Directory.Exists(Root)
		? Directory.GetFiles(Root, "*.json").Order(StringComparer.Ordinal).Select(Load)
		: Enumerable.Empty<Flight>();

	static Flight Load(string file)
	{
		var f = JsonSerializer.Deserialize<Flight>(File.ReadAllText(file), TestCore.Json);
		f.Name = System.IO.Path.GetFileNameWithoutExtension(file);
		return f;
	}

	public double End => Path.Select(p => p.T).Concat(Shots).Concat(Actions.Select(a => a.T + a.Seconds)).DefaultIfEmpty(0).Max();

	// Catmull-Rom over time (tangent = velocity between the neighbours), held after the last point.
	public (Vector3 pos, float yaw, float pitch) Pose(double t)
	{
		int i = Math.Max(0, Path.FindLastIndex(p => p.T <= t));
		if (i >= Path.Count - 1) return (V(Path[^1].Pos), Path[^1].Yaw, Path[^1].Pitch);
		var (p0, p1, p2, p3) = (Path[Math.Max(i - 1, 0)], Path[i], Path[i + 1], Path[Math.Min(i + 2, Path.Count - 1)]);
		float h = (float)(p2.T - p1.T), u = (float)Math.Clamp((t - p1.T) / h, 0, 1), k0 = h / (float)(p2.T - p0.T), k1 = h / (float)(p3.T - p1.T);
		float C(float a, float b, float c, float d) =>
			(2 * u * u * u - 3 * u * u + 1) * b + (u * u * u - 2 * u * u + u) * (c - a) * k0 + (3 * u * u - 2 * u * u * u) * c + (u * u * u - u * u) * (d - b) * k1;
		var pos = new Vector3(C(p0.Pos.X, p1.Pos.X, p2.Pos.X, p3.Pos.X), C(p0.Pos.Y, p1.Pos.Y, p2.Pos.Y, p3.Pos.Y), C(p0.Pos.Z, p1.Pos.Z, p2.Pos.Z, p3.Pos.Z));
		return (pos, C(p0.Yaw, p1.Yaw, p2.Yaw, p3.Yaw), C(p0.Pitch, p1.Pitch, p2.Pitch, p3.Pitch));
	}

	static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
}

// Plays one flight through the host and measures it; Step returns the result after the last frame.
public sealed class FlightPlayer
{
	const int ShotShadow = 2, FlushLines = 600; // frames after a capture that its stall spoils; frame log lines kept before they are appended to the file
	const double DriftQueue = 1.5, DriftQueueSlack = 5000, DriftFarFill = 0.85; // Degraded: end queue over start * DriftQueue + slack, or end far fill under start * DriftFarFill while over slack is still queued (a short queue fills less)
	readonly TestCore core;
	readonly Main host;
	readonly Flight f;
	readonly List<FlightEvent> pending;
	readonly List<TestIssue> issues = new();
	readonly List<(int frame, double ms)> frame = new();
	readonly List<double> gpu = new(), gen = new(), pressure = new(), farM = new();
	int shrinks0, grows0;
	readonly List<(double T, int Queue, int Far)> stream = new();
	readonly Dictionary<string, int> overflow = new();
	readonly Dictionary<string, TestCore.ImageDiff> shots = new();
	readonly DisplayServer.VSyncMode vsync = DisplayServer.WindowGetVsyncMode();
	int[] gcStart = new int[3];
	long allocStart;
	static int[] Collections() => new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
	bool flying, logged;
	int n, settle, wait, graceUntil, shadowUntil, missingFrames;
	int? nearMissing, midMissing;
	readonly StringBuilder log = new();
	static readonly string[] PoolNames = Enum.GetNames<WorldPool>();
	int logLines;
	Vector3 offset;
	readonly List<FlightEvent> turns = new();
	FlightEvent walk;

	public FlightPlayer(TestCore core, Flight f)
	{
		(this.core, host, this.f) = (core, core.Host, f);
		pending = f.Actions.OrderBy(a => a.T).ToList();
		host.World.WorldMB = f.WorldMB ?? host.WorldMBArg;
		host.World.QuickLoad = f.NoWait;
		host.StartView(f.ViewPreset);
		if (f.Scene != null) host.Load(WorldSave.Open(System.IO.Path.Combine(Snapshot.Root, f.Scene)));
		else host.LoadGenerator(f.Generator, f.Params);
		host.ApplyBookmark(new Bookmark { Name = f.Name, View = View(0), TimeOfDay = f.TimeOfDay, Preset = f.Preset, Off = f.Off, On = f.On, Resolution = f.Resolution, DebugView = f.DebugView }, null);
		(host.World.TimePaused, host.World.Clock, host.World.Day) = (f.TimePaused, 0, 0);
		host.Player.Mode = f.Move;
		if (f.RenderScale is { } scale) host.Renderer.RenderScale = scale;
		core.Resize(f.Resolution, f.Name);
		DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); // frame times are the cost, not the refresh
		GD.Print($"[flight] {f.Name}: {f.End:0.##} s at {f.Fps} virtual fps");
	}

	View View(double t)
	{
		var (p, yaw, pitch) = f.Pose(t);
		p += offset;
		yaw += turns.Sum(a => a.Yaw * (float)Math.Clamp((t - a.T) / Math.Max(a.Seconds, 1e-6), 0, 1));
		return new View(WorldHeader.DefaultDimension, new Vec3(p.X, p.Y, p.Z), yaw, pitch);
	}

	public FlightResult Step(double delta)
	{
		if (!flying)
		{
			host.ApplyView(View(0));
			settle++;
			TestIssue? issue = null;
			if (f.NoWait ? settle < TestCore.SettleFrames || !host.NearReady : !core.Settle(ref settle, ref wait, out issue)) return null;
			if (issue is TestIssue i) { issues.Add(i); return Result(); }
			host.Renderer.Restart();
			SeenFeedback.Request(null);
			(flying, graceUntil, gcStart, allocStart, shrinks0, grows0) = (true, f.GraceFrames, Collections(), GC.GetTotalAllocatedBytes(), host.World.Shrinks, host.World.Grows);
			return null;
		}
		double t = n / f.Fps;
		while (pending.Count > 0 && pending[0].T <= t) Act(pending[0]);
		if (f.Move == MoveMode.Walk)
		{
			var v = View(t);
			host.Player.Look = (Mathf.DegToRad(v.Yaw), Mathf.DegToRad(v.Pitch));
			host.Player.Walk(1 / f.Fps, walk == null ? Vector2.Zero : new Vector2(walk.Move.X, walk.Move.Z), walk?.Jump == true, walk?.Sprint == true);
		}
		else host.ApplyView(View(t));
		if (logLines >= FlushLines) FlushLog();
		log.Append(CultureInfo.InvariantCulture, $"{n},{delta * 1000:0.###},{(n > graceUntil ? 1 : 0)},");
		host.FrameLog(log);
		logLines++;
		if (n > graceUntil && n > shadowUntil) Sample(delta);
		foreach (var s in f.Shots) if ((int)Math.Round(s * f.Fps) == n) Shot(s);
		return ++n > f.End * f.Fps ? Result() : null;
	}

	void Act(FlightEvent a)
	{
		pending.RemoveAt(0);
		switch (a.Kind)
		{
			case FlightAction.Teleport: offset += new Vector3(a.By.X, a.By.Y, a.By.Z); graceUntil = n + f.GraceFrames; break;
			case FlightAction.Regenerate: host.Regenerate(a.Params ?? new()); graceUntil = n + f.GraceFrames; break;
			case FlightAction.Edit: host.Edit(a.Edits); break;
			case FlightAction.Turn: turns.Add(a); break;
			case FlightAction.Walk: walk = a; break;
			case FlightAction.Expect: Expect(a); break;
		}
	}

	void Sample(double delta)
	{
		frame.Add((n, delta * 1000));
		gpu.Add(host.GpuMs);
		gen.Add(host.GenMs);
		pressure.Add(host.Pressure);
		farM.Add(host.FarRadiusM);
		var fails = host.PoolFailCounts;
		for (int p = 0; p < fails.Length; p++) if (fails[p] > 0) overflow[PoolNames[p]] = Math.Max((int)fails[p], overflow.GetValueOrDefault(PoolNames[p]));
		nearMissing = Max(nearMissing, host.NearMissing);
		midMissing = Max(midMissing, host.MidMissing);
		if (host.NearMissing > 0 || host.MidMissing > 0) missingFrames++;
		if (host.Stream is var (q, far)) stream.Add((n / f.Fps, q, far));
	}

	void Expect(FlightEvent a)
	{
		var (p, want) = (host.World.CameraPosition, new Vector3(a.Pos.X, a.Pos.Y, a.Pos.Z));
		var e = (p - want).Abs();
		bool ok = Math.Max(e.X, Math.Max(e.Y, e.Z)) <= a.Tolerance;
		if (!ok) issues.Add(TestIssue.WrongPosition);
		GD.Print($"[flight] {f.Name} at {a.T:0.##} s: eye {p.X:0.000} {p.Y:0.000} {p.Z:0.000}, expected {want.X:0.000} {want.Y:0.000} {want.Z:0.000} within {a.Tolerance}: {(ok ? "ok" : "WRONG")}");
	}

	static int? Max(int? a, int? b) => b == null ? a : Math.Max(a ?? 0, b.Value);

	void Shot(double t)
	{
		string file = $"{f.Name}.{t.ToString("0.###", CultureInfo.InvariantCulture)}";
		var d = core.Capture(System.IO.Path.Combine(TestCore.OutRoot, core.Stamp, "flights"), file,
			System.IO.Path.Combine(Flight.Root, "ref", file + ".png"), issues);
		shots[t.ToString(CultureInfo.InvariantCulture)] = d;
		SeenFeedback.Request($"{f.Name}@{t.ToString(CultureInfo.InvariantCulture)}");
		shadowUntil = n + ShotShadow;
	}

	// The frame log goes out in parts, so a run that dies keeps what it measured.
	void FlushLog()
	{
		if (logLines == 0) return;
		string dir = System.IO.Path.Combine(TestCore.OutRoot, core.Stamp, "flights"), file = System.IO.Path.Combine(dir, f.Name + ".frames.csv");
		Directory.CreateDirectory(dir);
		File.AppendAllText(file, (logged ? "" : "frame,ms,measured," + host.FrameLogHeader + "\n") + log);
		logged = true;
		(log.Length, logLines) = (0, 0);
	}

	static FrameStats Stats(IEnumerable<double> values)
	{
		var s = values.Order().ToList();
		return s.Count == 0 ? null : new FrameStats(Math.Round(s.Average(), 3), Math.Round(s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))], 3), Math.Round(s[^1], 3));
	}

	FlightResult Result()
	{
		DisplayServer.WindowSetVsyncMode(vsync);
		host.Player.Mode = MoveMode.Fly;
		var world = host.World;
		var memory = pressure.Count == 0 ? null : new Memory(world.BudgetMB, Math.Round(pressure.Max(), 3), Math.Round(pressure.Average(), 3), Math.Round(farM.Min()), Math.Round(farM[^1]), Math.Round(world.FullRadiusM),
			world.Shrinks - shrinks0, world.Grows - grows0, world.Evicted);
		(world.WorldMB, world.QuickLoad) = (host.WorldMBArg, false);
		double median = frame.Select(x => x.ms).Order().ElementAtOrDefault(frame.Count / 2);
		FlushLog();
		var hitches = frame.Where(x => x.ms > f.HitchMs || x.ms > 2 * median).Select(x => new Hitch(Math.Round(x.frame / f.Fps, 3), Math.Round(x.ms, 1))).ToList();
		if (hitches.Count > 0 && f.Move == MoveMode.Fly) issues.Add(TestIssue.Hitch); // walking tests movement, not frame times
		var pools = overflow.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
		if (pools.Count > 0) issues.Add(TestIssue.PoolOverflow);
		if (nearMissing > 0 || midMissing > 0) issues.Add(TestIssue.NearMissing);
		Drift drift = null;
		if (f.DriftWindow is double w && stream.Count > 0)
		{
			var (a, b) = (stream.Where(s => s.T >= w && s.T < 2 * w).ToList(), stream.Where(s => s.T >= f.End - w).ToList());
			drift = new Drift(Math.Round(a.Average(s => s.Queue)), Math.Round(b.Average(s => s.Queue)), Math.Round(a.Average(s => s.Far), 1), Math.Round(b.Average(s => s.Far), 1));
			if (drift.QueueEnd > drift.QueueStart * DriftQueue + DriftQueueSlack || drift.FarFillEnd < drift.FarFillStart * DriftFarFill && drift.QueueEnd > DriftQueueSlack) issues.Add(TestIssue.Degraded);
		}
		var r = new FlightResult(f.Name, f.Resolution, core.WindowSize, issues.All(i => i == TestIssue.NoReference), issues, frame.Count,
			Stats(frame.Select(x => x.ms)), Stats(gpu), Stats(gen), hitches.Count, hitches.Count(h => h.Ms > f.HitchMs), hitches,
			pools.Count > 0 ? pools : null, nearMissing, midMissing, missingFrames, shots.Count > 0 ? shots : null, drift,
			Collections().Zip(gcStart, (a, b) => a - b).ToArray(), Math.Round((GC.GetTotalAllocatedBytes() - allocStart) / 1048576.0, 1), memory);
		static string S(FrameStats s) => s == null ? "n/a" : $"mean {s.Mean:0.00} p99 {s.P99:0.00} worst {s.Worst:0.0}";
		GD.Print($"[flight] {f.Name}: {(r.Passed ? "ok" : "FAIL")} {string.Join(" ", issues.Distinct())}  frames {r.Frames}\n" +
			$"  frame ms {S(r.Frame)}  (median {median:0.00})\n  gpu ms {S(r.Gpu)}\n  gen ms {S(r.Gen)}\n" +
			$"  hitches {r.Hitches}, {r.HitchesOverMs} over {f.HitchMs} ms (rule: > {f.HitchMs} ms or > 2x median, outside {f.GraceFrames} grace frames){TestCore.OverflowText(r.Overflow)}\n" +
			(memory == null ? "" : $"  memory {memory.BudgetMB} MB: pressure max {memory.PressureMax:0.00} mean {memory.PressureMean:0.00}, far radius min {memory.FarMinM} end {memory.FarEndM} of {memory.FullM} m, shrinks {memory.Shrinks} grows {memory.Grows}, evicted unseen {memory.Evicted}\n") +
			$"  near missing {r.NearMissing?.ToString() ?? "n/a"}  mid missing {r.MidMissing?.ToString() ?? "n/a"}  frames missing {r.MissingFrames}\n" +
			(drift == null ? "" : $"  drift (window {f.DriftWindow} s, second vs last): queue {drift.QueueStart} -> {drift.QueueEnd}, far filled/frame {drift.FarFillStart} -> {drift.FarFillEnd}\n") +
			(hitches.Count == 0 ? "" : $"  worst hitches (s: ms): {string.Join("  ", r.HitchList.OrderByDescending(h => h.Ms).Take(12).Select(h => $"{h.T:0.00}: {h.Ms:0}"))}"));
		return r;
	}
}
