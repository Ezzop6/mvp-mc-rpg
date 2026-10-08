using Renesance.World;

// Self-checks for the Godot-free parts of core/world (ring, streaming bookkeeping, brick packing), exits 1 on the first failure. Run: dotnet run --project tests/World
static void Check(bool ok, string what) { if (!ok) { Console.WriteLine("FAIL: " + what); Environment.Exit(1); } Console.WriteLine("ok   " + what); }

// --- ring ---
Check(RingWindow.Pmod(-1, 4) == 3 && RingWindow.Pmod(-4, 4) == 0 && RingWindow.Pmod(-5, 4) == 3 && RingWindow.Pmod(7, 4) == 3, "pmod of negatives");
var ring = new RingWindow(new ChunkPos(-7, 3, 120), 96, 0.5f);
Check(ring.SizeXZ == 196 && ring.SizeY == 100 && ring.RadiusY == 48, "ultra ring: 196 x 100 x 196 (docs/v2.md §3.1)");
Check(ring.Min == new ChunkPos(-103, -45, 24), "ring min = centre - radius");
var sphere = new List<ChunkPos>();
ring.EachWanted(sphere.Add);
Check(sphere.All(ring.Contains) && sphere.All(ring.Wanted) && sphere.Distinct().Count() == sphere.Count, "sphere chunks are wanted, in the ring, unique");
Check(sphere.Select(ring.SlotIndex).Distinct().Count() == sphere.Count, "no two sphere chunks share a slot");
static double D(ChunkPos a, ChunkPos b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y) + (double)(a.Z - b.Z) * (a.Z - b.Z));
Check(ring.Wanted(new ChunkPos(-7 + 96, 3, 120)) && !ring.Wanted(new ChunkPos(-7 + 97, 3, 120)) && ring.Wanted(new ChunkPos(-7, 3 - 48, 120)) && !ring.Wanted(new ChunkPos(-7, 3 + 49, 120)),
	"ellipsoid: radius across, radius * flatten up");
int expect = 0;
for (int dz = -96; dz <= 96; dz++)
	for (int dy = -48; dy <= 48; dy++)
		for (int dx = -96; dx <= 96; dx++)
			if ((dx * dx + dz * dz) / (96.0 * 96) + dy * dy / (48.0 * 48) <= 1) expect++;
Check(sphere.Count == expect, $"sphere holds every chunk of the ellipsoid ({expect})");
Check(ring.Offset == new ChunkPos(RingWindow.Pmod(-103, 196), RingWindow.Pmod(-45, 100), RingWindow.Pmod(24, 196)) && ring.SizeXZ % 4 == 0 && ring.SizeY % 4 == 0,
	"slot offset, sizes are multiples of 4 (occupancy mip)");
var low = new RingWindow(new ChunkPos(0, 0, 0), 24, 0.35f);
Check(low.SizeXZ == 52 && low.RadiusY == 8 && low.SizeY == 20, "low ring rounds up to multiples of 4");

// --- streaming: shift, release, queue (docs/v2.md §4.1) ---
var small = new RingWindow(new ChunkPos(3, -2, 5), 9, 0.5f);
HashSet<ChunkPos> WantedSet(RingWindow r) { var h = new HashSet<ChunkPos>(); r.EachWanted(c => h.Add(c)); return h; }
bool diffOk = true;
foreach (var d in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (1, -1, 1), (3, 2, -4), (9, 0, 0) })
{
	var to = small.MovedTo(new ChunkPos(small.Center.X + d.Item1, small.Center.Y + d.Item2, small.Center.Z + d.Item3));
	var (left, entered) = (new List<ChunkPos>(), new List<ChunkPos>());
	RingWindow.Diff(small, to, left, entered);
	var (a, b) = (WantedSet(small), WantedSet(to));
	diffOk &= left.ToHashSet().SetEquals(a.Except(b)) && entered.ToHashSet().SetEquals(b.Except(a)) && left.Count == left.Distinct().Count() && entered.Count == entered.Distinct().Count();
}
Check(diffOk, "shift diff = exact set differences of the wanted ellipsoids, every axis and sign");

var stream = new ChunkStream(small, 3);
Check(stream.Count == WantedSet(small).Count && WantedSet(small).All(c => stream.State(c) == SlotState.Queued), "a new stream queues the whole sphere");
var popped = new List<ChunkPos>();
for (int i = 0; i < 40 && stream.TryPop(out var c); i++) popped.Add(c);
Check(popped[0] == small.Center && popped.Zip(popped.Skip(1)).All(p => (int)D(p.First, small.Center) <= (int)D(p.Second, small.Center)), "pops nearest first, by whole chunks");
stream.Requeue(popped[^1]);
Check(stream.State(popped[^1]) == SlotState.Queued && stream.TryPop(out var again) && again == popped[^1], "a deferred chunk goes back and comes out again");

var coned = new ChunkStream(small, 3, System.Numerics.Vector3.UnitX, 0.5f);
var coneOrder = new List<ChunkPos>();
while (coned.TryPop(out var c)) coneOrder.Add(c);
bool InCone(ChunkPos c) => c.X - small.Center.X >= 0.5 * D(c, small.Center);
int lastIn = coneOrder.FindLastIndex(c => (int)D(c, small.Center) <= 3 || InCone(c)), firstOut = coneOrder.FindIndex(c => (int)D(c, small.Center) > 3 && !InCone(c));
Check(coneOrder.Count == WantedSet(small).Count && coneOrder.Distinct().Count() == coneOrder.Count && lastIn < firstOut && firstOut > 0,
	"view cone: the mid sphere and the cone past it before the rest, nothing dropped");
Check(coneOrder.Skip(firstOut).Zip(coneOrder.Skip(firstOut + 1)).All(p => (int)D(p.First, small.Center) <= (int)D(p.Second, small.Center)), "outside the cone nearest first");

var near = new ChunkPos(small.Center.X + 5, small.Center.Y, small.Center.Z);
Check(stream.Claim(near) && stream.State(near) == SlotState.Sent && !stream.Claim(near), "the near pass claims a queued chunk once");
bool poppedAgain = false;
while (stream.TryPop(out var c)) poppedAgain |= c == near;
Check(!poppedAgain, "a claimed chunk's queue entry is skipped");

// GPU mirror: a resident chunk per slot, records from a free stack with bump fallback, as gen.compute take/give.
var resident = new Dictionary<int, ChunkPos>();
var free = new Stack<uint>();
uint highWater = 0;
int sentTwice = 0, slotClash = 0;
void Send(ChunkStream s, ChunkPos c)
{
	int slot = s.Ring.SlotIndex(c);
	if (resident.TryGetValue(slot, out var o)) { if (o == c) sentTwice++; else slotClash++; }
	resident[slot] = c;
	if (!free.TryPop(out _)) highWater++;
}
void Release(ChunkStream s, List<ChunkPos> rel)
{
	foreach (var c in rel)
		if (resident.TryGetValue(s.Ring.SlotIndex(c), out var o) && o == c) { resident.Remove(s.Ring.SlotIndex(c)); free.Push(0); }
}
stream = new ChunkStream(small, 3);
while (stream.TryPop(out var c)) Send(stream, c);
uint full = highWater;
var rng = new Random(5);
var center = small.Center;
bool releasedOk = true;
for (int step = 0; step < 300; step++)
{
	center = new ChunkPos(center.X + rng.Next(-1, 2), center.Y + rng.Next(-1, 2), center.Z + rng.Next(-1, 2));
	var rel = new List<ChunkPos>();
	stream.Move(center, rel);
	stream.Drain(rng.Next(0, 300)); // a crossing's entered chunks queue over several frames
	releasedOk &= rel.All(c => !stream.Ring.Wanted(c) && stream.State(c) != SlotState.Queued);
	Release(stream, rel);
	for (int k = 0; k < 400 && stream.TryPop(out var c); k++) Send(stream, c); // budget: some moves leave work for later
}
stream.Drain(int.MaxValue);
while (stream.TryPop(out var c)) Send(stream, c);
Check(releasedOk, "300 random moves: released chunks are not wanted any more");
var want = WantedSet(stream.Ring);
Check(sentTwice == 0 && slotClash == 0, "no chunk generated twice, no slot taken by two chunks");
Check(resident.Values.ToHashSet().SetEquals(want) && want.All(c => stream.State(c) == SlotState.Sent) && stream.Count == 0, "after the queue drains, exactly the wanted sphere is resident");
Check(highWater == full && resident.Count + free.Count == highWater, $"memory is reused: high water {highWater} for a sphere of {full}, freed + resident = taken");
Check(!stream.HasColumn(center), "column ring starts empty");
stream.SetColumn(center);
Check(stream.HasColumn(center) && !stream.HasColumn(new ChunkPos(center.X + stream.Ring.SizeXZ, 0, center.Z)), "a column slot remembers which column it holds");

// --- priority under backlog, pending count, stale entries (docs/v2.md §4.1, the bugs behind holes at 300 m) ---
bool PendingOk(ChunkStream s) => s.Pending == WantedSet(s.Ring).Count(c => s.State(c) == SlotState.Queued);
stream = new ChunkStream(small, 3);
while (stream.TryPop(out _)) { }
for (int i = 1; i <= 6; i++) { stream.Move(new ChunkPos(small.Center.X + i, small.Center.Y, small.Center.Z), new List<ChunkPos>()); stream.Drain(int.MaxValue); }
var cam6 = new ChunkPos(small.Center.X + 6, small.Center.Y, small.Center.Z);
Check(PendingOk(stream), "pending = queued slots after moves without popping");
var order = new List<ChunkPos>();
while (stream.TryPop(out var c)) order.Add(c);
int lastMid = order.FindLastIndex(c => D(c, cam6) <= 3), firstFar = order.FindIndex(c => D(c, cam6) > 3);
Check(order.Count(c => D(c, cam6) <= 3) > 0 && lastMid < firstFar, "a backlog serves chunks that came into the mid sphere before newer far entries");
var byAge = order.Where(c => D(c, cam6) > 3).ToList();
Check(D(byAge[0], cam6) < D(byAge[^1], cam6), "outside the mid sphere the oldest (now nearest) entries come first");
stream.Requeue(order[^1]);
var midChunk = order.First(c => D(c, cam6) <= 3);
stream.Requeue(midChunk);
Check(stream.TryPop(out var firstBack) && firstBack == midChunk, "a deferred chunk inside the mid sphere goes before the rest");
stream = new ChunkStream(small, 3);
for (int i = 1; i <= 4; i++) { stream.Move(new ChunkPos(small.Center.X - 3 * i, small.Center.Y, small.Center.Z), new List<ChunkPos>()); stream.Drain(int.MaxValue); }
int skips = 5, got = 0;
while (stream.TryPop(out _, ref skips)) got++;
Check(skips <= 0 && stream.Pending > 0 && PendingOk(stream), "stale entries stop a frame's pops after the skip budget, pending stays exact");

// --- ultra ring on the CPU: a crossing's diff and a frame's pops stay small (flight hitches, docs/v2-mereni.md) ---
var ultra = new ChunkStream(new RingWindow(new ChunkPos(0, 0, 0), 96, 0.5f), 24);
while (ultra.TryPop(out _)) { }
var relU = new List<ChunkPos>();
(double Ms, double DrainMs, int In, int Out) Cross(int dx, int dy)
{
	relU.Clear();
	var at = ultra.Ring.Center;
	var sw = System.Diagnostics.Stopwatch.StartNew();
	ultra.Move(new ChunkPos(at.X + dx, at.Y + dy, at.Z), relU);
	double move = sw.Elapsed.TotalMilliseconds;
	ultra.Drain(int.MaxValue);
	return (move, sw.Elapsed.TotalMilliseconds - move, ultra.Pending, relU.Count);
}
var sw = System.Diagnostics.Stopwatch.StartNew();
int pops = 0;
for (int i = 0; i < 3; i++) { Cross(1, 0); Cross(0, 1); while (ultra.TryPop(out _)) pops++; }
double msWarm = sw.Elapsed.TotalMilliseconds;
var xs = Enumerable.Range(0, 10).Select(i => { var r = Cross(1, 0); while (ultra.TryPop(out _)) { } return r; }).ToList();
var ys = Enumerable.Range(0, 10).Select(i => { var r = Cross(0, 1); while (ultra.TryPop(out _)) { } return r; }).ToList();
sw.Restart();
Cross(1, 0); Cross(0, 1); Cross(0, 1);
pops = 0;
double msMoves = sw.Elapsed.TotalMilliseconds;
sw.Restart();
while (pops < 16384 && ultra.TryPop(out _)) pops++;
double msPop = sw.Elapsed.TotalMilliseconds;
Console.WriteLine($"     ultra crossing x: {xs[0].In} in / {xs[0].Out} out, move {xs.Average(r => r.Ms):0.00} ms + drain {xs.Average(r => r.DrainMs):0.00} ms; " +
	$"y: {ys[0].In} in / {ys[0].Out} out, move {ys.Average(r => r.Ms):0.00} + drain {ys.Average(r => r.DrainMs):0.00} ms; 16384 pops {msPop:0.00} ms");
Check(xs.All(r => r.In == r.Out && r.In == xs[0].In) && ys.All(r => r.In == r.Out && r.In == ys[0].In) && pops == 16384, "ultra crossings: as many chunks leave as enter, every time");

// --- crossings spread over frames (ChunkStream.Advance) end in the same state as whole ones ---
{
	var (whole, spread) = (new ChunkStream(small, 3), new ChunkStream(small, 3));
	var (relW, relS) = (new List<ChunkPos>(), new List<ChunkPos>());
	var walk = new Random(9);
	var at = small.Center;
	bool slotsFree = true;
	for (int step = 0; step < 200; step++)
	{
		at = new ChunkPos(at.X + walk.Next(-1, 2), at.Y + walk.Next(-1, 2), at.Z + walk.Next(-1, 2));
		whole.Move(at, relW);
		whole.Drain(int.MaxValue);
		int before = relS.Count;
		spread.Move(at, relS, 40);
		spread.Drain(int.MaxValue);
		slotsFree &= relS.Skip(before).All(c => spread.Ring.Wanted(c) || spread.State(c) != SlotState.Queued);
	}
	while (spread.Shifts > 0) spread.Advance(relS, 1000);
	spread.Drain(int.MaxValue);
	bool sameStates = WantedSet(whole.Ring).All(c => whole.State(c) == spread.State(c)) && whole.Pending == spread.Pending;
	Check(sameStates && slotsFree && relW.ToHashSet().SetEquals(relS.ToHashSet()), "crossings over several frames: same slots queued and released as whole crossings");
}

// --- long flights: the backlog stays bounded when the frame's work is capped (docs/v2.md §5.14, far chunks loading slower and slower) ---
(int Incoming, int Count, int Pending, int Far) Cruise(int pops, bool turn, int frames, out (int Incoming, int Count, int Far) start)
{
	var s = new ChunkStream(new RingWindow(new ChunkPos(0, 0, 0), 40, 0.5f), 10);
	while (s.TryPop(out _)) { }
	double x = 0, z = 0;
	var rel = new List<ChunkPos>();
	int far = 0;
	start = default;
	for (int frame = 0; frame < frames; frame++)
	{
		double a = turn ? frame * 0.002 : 0;
		(x, z) = (x + Math.Cos(a) * 0.4, z + Math.Sin(a) * 0.4);
		var cam = new ChunkPos((int)Math.Floor(x), 0, (int)Math.Floor(z));
		s.Now = frame;
		if (cam != s.Ring.Center) { rel.Clear(); s.Move(cam, rel); }
		int skipsLeft = 1 << 16;
		for (int n = 0; n < pops && s.TryPop(out var c, ref skipsLeft); n++) if (D(c, cam) > 10) far++;
		s.Drain(Math.Max(1, s.DrainDue));
		if (s.NeedsCompact) s.Compact(4096);
		if (frame == frames / 4) start = (s.Incoming, s.Count, far);
	}
	return (s.Incoming, s.Count, s.Pending, far);
}
var cruise = Cruise(int.MaxValue, false, 2400, out var c0);
Check(cruise.Incoming <= Math.Max(4 * c0.Incoming, 8 * 2000) && cruise.Far - c0.Far >= 2 * c0.Far, $"straight cruise, drain floor only: incoming {c0.Incoming} -> {cruise.Incoming}, far pops keep their rate");
var tight = Cruise(600, true, 2400, out var t0);
Check(tight.Count <= 2 * tight.Pending + ChunkStream.CompactSlack && tight.Incoming <= 8 * 2000, $"turning cruise over the pop budget: entries {tight.Count} for {tight.Pending} queued (compacted), incoming {tight.Incoming}");
Check(tight.Far - t0.Far >= 2 * t0.Far * 9 / 10, "turning cruise over the pop budget: far chunks still fill at the start's rate");

// --- failed chunks (pools full) come back: at once inside the mid sphere, farther after the backoff ---
var fs = new ChunkStream(small, 3);
while (fs.TryPop(out _)) { }
var farC = new ChunkPos(small.Center.X + 8, small.Center.Y, small.Center.Z);
var midC = new ChunkPos(small.Center.X + 2, small.Center.Y, small.Center.Z);
fs.Now = 100;
fs.Fail(farC);
fs.Fail(midC);
Check(fs.State(farC) == SlotState.Failed && fs.Failed == 1 && fs.State(midC) == SlotState.Queued, "a failed far chunk is a hole, a failed mid chunk is queued again");
fs.Now = 100 + ChunkStream.RetryFrames - 1;
fs.Retry();
Check(fs.State(farC) == SlotState.Failed, "no retry before the backoff");
fs.Now = 100 + ChunkStream.RetryFrames;
fs.Retry();
Check(fs.State(farC) == SlotState.Queued && fs.Failed == 0, "after the backoff the failed chunk is queued again");
while (fs.TryPop(out _)) { }
fs.Fail(farC);
fs.Now += ChunkStream.RetryFrames;
fs.Retry();
Check(fs.State(farC) == SlotState.Failed, "a second failure waits twice as long");
fs.Move(new ChunkPos(small.Center.X + 6, small.Center.Y, small.Center.Z), new List<ChunkPos>());
Check(fs.State(farC) == SlotState.Queued && fs.Failed == 0, "a failed chunk entering the mid sphere is queued at once");

// --- memory pressure: the ring's radius shrinks and grows around the same slots (docs/v2.md §3.6) ---
{
	var rs = new ChunkStream(small, 3);
	while (rs.TryPop(out _)) { }
	var rel = new List<ChunkPos>();
	var whole = WantedSet(small);
	rs.Resize(5, rel);
	var shrunk = rs.Ring;
	Check(shrunk.Min == small.Min && shrunk.SizeXZ == small.SizeXZ && shrunk.SizeY == small.SizeY && shrunk.Radius == 5 && shrunk.Reach == 9, "a shrunk ring keeps its slots and Min");
	Check(rel.ToHashSet().SetEquals(whole.Except(WantedSet(shrunk))) && rel.All(c => rs.State(c) == SlotState.None) && rs.Pending == 0, "shrinking releases exactly the shell, its slots are free");
	rs.Move(new ChunkPos(small.Center.X + 2, small.Center.Y, small.Center.Z), rel);
	rs.Drain(int.MaxValue);
	Check(WantedSet(rs.Ring).All(c => rs.Ring.Wanted(c) && Math.Abs(c.X - rs.Ring.Center.X) <= 5) && rs.Ring.Radius == 5, "a shrunk ring moves at its radius");
	while (rs.TryPop(out _)) { }
	rel.Clear();
	rs.Resize(9, rel);
	rs.Drain(int.MaxValue);
	var regrown = WantedSet(rs.Ring);
	Check(rel.Count == 0 && regrown.SetEquals(WantedSet(small.MovedTo(rs.Ring.Center))) && regrown.All(c => rs.State(c) != SlotState.None), "growing back queues the shell again, nothing released");
}

// --- levels of detail (docs/v2.md §5.17) ---
{
	float a4k = 2 * MathF.Tan(40 * MathF.PI / 180) / 2160, a1080 = 2 * a4k;
	var u4k = Lod.Plan(a4k, 2, 9600, 325);
	Check(u4k.Levels == 4 && Math.Abs(u4k.SwitchM[1] - 643.6f) < 1 && Math.Abs(u4k.SwitchM[2] - 1287.2f) < 1 && Math.Abs(u4k.SwitchM[3] - 2574.4f) < 2, "u4k 4K: L1 from ~644 m, then twice as far per level");
	float cm = Coords.ChunkSize * Coords.BlockMeters;
	Check(u4k.Radius[0] == (int)MathF.Ceiling(u4k.SwitchM[1] / cm) + Lod.Margin && u4k.Radius[3] == (int)MathF.Ceiling(9600 / (8 * cm)) + Lod.Margin && Math.Abs(u4k.RadiusM(3) - u4k.Radius[3] * 8 * cm) < 1 && u4k.RadiusM(3) >= 9600, "a level reaches the next one's start, the last the horizon (+ margin)");
	Check(Enumerable.Range(1, 3).All(l => Math.Abs(u4k.Radius[l] - u4k.Radius[0]) <= 1 || l == 3), "about the same chunks per level radius");
	var l1080 = Lod.Plan(a1080, 4, 1200, 325);
	Check(l1080.Levels == 3 && l1080.SwitchM[1] == 325 && l1080.SwitchM[2] == 650 && l1080.RadiusM(2) >= 1200, "l1080 1080p: never inside the mid sphere, levels double, the horizon reached");
	Check(Lod.Plan(a4k, 2, 1175, 325, maxLevels: 1) is { Levels: 1 } one && one.Radius[0] == Math.Min((int)MathF.Ceiling(1175 / cm) + Lod.Margin, Lod.MaxRadius) && Lod.Plan(a4k, 2, 9600, 325, maxLevels: 1).Radius[0] == Lod.MaxRadius, "one level: L0 to the horizon, at most MaxRadius chunks");
	var reference = Lod.Plan(a4k, 2, 9600, 325, refLevel: 1);
	Check(reference.SwitchM[1] == u4k.SwitchM[2] && (reference.RadiusM(0) >= u4k.SwitchM[2] || reference.Radius[0] == Lod.MaxRadius), "lod-ref 1: L1's band is drawn by L0 (as far as MaxRadius chunks reach)");
	Check(Lod.SampleOffset(1, 0) == (0, 0, 0) && Lod.SampleOffset(1, 7) == (1, 1, 1) && Lod.SampleOffset(3, 5) == (6, 2, 6), "sample offsets: L1 every block, L3 at 2 and 6");
	Check(Enumerable.Range(0, 8).All(k => Lod.SampleOffset(2, k) is var o && Lod.SampleOf(2, o.X, o.Y, o.Z) == k) && Lod.SampleOf(2, 0, 1, 1) == -1, "SampleOf inverts SampleOffset, other blocks are no sample");
	Check(Lod.Parent(new ChunkPos(-1, 5, -8), 2) == new ChunkPos(-1, 1, -2), "parent chunk floors negatives");
	var s0 = new ChunkStream(new RingWindow(new ChunkPos(0, 0, 0), 6, 1), 2);
	var s1 = new ChunkStream(new RingWindow(new ChunkPos(0, 0, 0), 6, 1), 0);
	while (s0.NextKey() < 3 * ChunkStream.Tiers && s0.TryPop(out _)) { }
	Check(s0.NextKey() == 3 * ChunkStream.Tiers + 1 && s1.NextKey() <= 0, "NextKey: the bucket's distance in own chunks times Tiers plus its tier (past the mid sphere: in the view cone = tier 1), comparable across levels");
	Check(s0.NextKey(2) == int.MaxValue, "NextKey honours maxDistance");
}

// --- visibility tiers and eviction (docs/v2.md §5.17) ---
{
	var vr = new RingWindow(new ChunkPos(0, 0, 0), 8, 1);
	var vs = new ChunkStream(vr, 2, System.Numerics.Vector3.UnitX, 0.9f);
	var farSeen = new ChunkPos(-7, 0, 0); // behind the camera, outside the cone
	var bits = new uint[(vr.Slots + 31) / 32];
	int fslot = vr.SlotIndex(farSeen);
	bits[fslot >> 5] |= 1u << (fslot & 31);
	vs.MarkSeen(bits);
	ChunkPos first = default;
	while (vs.TryPop(out var c)) { if ((int)D(c, vr.Center) > 2) { first = c; break; } }
	Check(first == farSeen, "past the mid sphere a seen chunk comes before the view cone and the rest");
	while (vs.TryPop(out _)) { }
	var rel = new List<ChunkPos>();
	vs.MarkSeen(new uint[bits.Length]);
	int n = vs.Evict(2, (int)vr.Slots, int.MaxValue, rel);
	Check(n > 0 && rel.All(c => D(c, vr.Center) > 2 && vs.State(c) == SlotState.Failed), "eviction releases resident chunks past minDistance only, they wait parked");
	Check(!rel.Contains(farSeen), "a chunk seen within SeenPeriods stays");
	var back = new uint[bits.Length];
	int ps = vr.SlotIndex(rel[0]);
	back[ps >> 5] |= 1u << (ps & 31);
	vs.MarkSeen(back);
	Check(vs.State(rel[0]) == SlotState.Queued, "a parked chunk that rays want again is queued at once");
}

// --- palette and bricks (mirror of gen.compute FILL) ---
(uint, uint) stone = (5 | 1u << Layout.FlagShift, 0), air = (0, 0), water = (0, 9), fence = (12 | 128u << Layout.FlagShift, 9);
Check(Layout.PackBrick(Enumerable.Repeat(stone, Layout.GroupCells).ToArray()).Class == -1, "one pair is a uniform group, no brick");
int third = Layout.GroupCells / 3;
var cells = Enumerable.Range(0, Layout.GroupCells).Select(v => v < third ? stone : v < 2 * third ? air : v % 2 == 0 ? water : fence).ToArray();
var (cls, words) = Layout.PackBrick(cells);
Check(cls == 0 && words.Length == Layout.BrickWords(0) && Layout.BrickWords(0) == Layout.IndexWords + 8, "4 pairs: Brick4, index words + 4 pairs");
Check(Enumerable.Range(0, Layout.GroupCells).All(v => Layout.Cell(words, v) == cells[v]), "Brick4 cells read back");
Check((words[0] & 0xFF) == 0 && (words[third >> 2] >> (third & 3) * 8 & 0xFF) == 1, "palette in first-use order, a byte index per cell");
Check((words[Layout.BoxWord] & ~0xFFu) == 0 && Layout.BoxWord * 4 >= Layout.GroupCells - 1, "bytes 1-3 of the box word left for the air box");
var many = Enumerable.Range(0, Layout.GroupCells).Select(v => ((uint)(v % 16 + 1), 0u)).ToArray();
Check(Layout.PackBrick(many).Class == 1 && Layout.BrickWords(1) == Layout.IndexWords + 32, "16 pairs: Brick16");
var all = Enumerable.Range(0, Layout.GroupCells).Select(v => ((uint)v + 1, (uint)v % 3)).ToArray();
var (cFull, wFull) = Layout.PackBrick(all);
Check(cFull == 2 && wFull.Length == Layout.IndexWords + 2 * Layout.GroupCells && Enumerable.Range(0, Layout.GroupCells).All(v => Layout.Cell(wFull, v) == all[v]), "a pair per cell: BrickFull, all read back");
Check(Layout.ClassOf(4) == 0 && Layout.ClassOf(5) == 1 && Layout.ClassOf(17) == 2 && Layout.ClassOf(Layout.GroupCells) == 2, "class limits 4 / 16 / GroupCells");
Check(Layout.PoolWords(WorldPool.Records) == Layout.ChunkRecWords && Layout.ChunkRecWords > Layout.StampWord && Layout.StampWord == 2 * Layout.Groups && Layout.PoolWords(WorldPool.Brick16) == Layout.BrickWords(1), "pool record sizes: group entries then the stamp");
Check(Coords.ChunkSize != 25 || Coords.GroupSize != 5 || Layout.ChunkRecWords == 256 && Layout.BrickWords(0) == 40 && Layout.BrickWords(2) == 282, "25^3 chunks of 5^3 groups keep the old sizes (1 KB records, 160 B Brick4, 1128 B BrickFull)");
