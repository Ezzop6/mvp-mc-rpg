using System;
using System.Collections.Generic;
using System.Numerics;
using Renesance.World;

// The L0 ring of chunks around Center (docs/v2.md §3.1): slot = pmod(chunk, size), wanted = chunk centre inside the ellipsoid; Godot-free.
// Radius can shrink below Reach (memory pressure, §3.6): the slots and Min stay those of Reach.
public sealed class RingWindow
{
	public readonly ChunkPos Center, Min;
	public readonly int Radius, RadiusY, SizeXZ, SizeY, Reach, ReachY;

	readonly float flatten;
	readonly int[] halfWidths; // HalfWidth per row (dy, dz), shared by moved copies

	public RingWindow(ChunkPos center, int radius, float flatten) : this(center, radius, RadiusYOf(radius, flatten), null, radius, RadiusYOf(radius, flatten), flatten) { }

	static int RadiusYOf(int radius, float flatten) => Math.Max(1, (int)MathF.Round(radius * flatten));

	RingWindow(ChunkPos center, int radius, int radiusY, int[] halfWidths, int reach, int reachY, float flatten)
	{
		(Center, Radius, RadiusY, Reach, ReachY, this.flatten) = (center, radius, radiusY, reach, reachY, flatten);
		(SizeXZ, SizeY) = (RoundUp4(2 * reach + 1), RoundUp4(2 * reachY + 1));
		Min = new ChunkPos(center.X - reach, center.Y - reachY, center.Z - reach);
		if (halfWidths == null)
		{
			halfWidths = new int[(2 * radiusY + 1) * (2 * radius + 1)];
			for (int dy = -radiusY; dy <= radiusY; dy++)
				for (int dz = -radius; dz <= radius; dz++) halfWidths[(dy + radiusY) * (2 * radius + 1) + dz + radius] = HalfWidth(dy, dz);
		}
		this.halfWidths = halfWidths;
	}

	// Same sizes and slots, another centre (docs/v2.md §3.1, shift).
	public RingWindow MovedTo(ChunkPos center) => new(center, Radius, RadiusY, halfWidths, Reach, ReachY, flatten);

	// Same centre and slots, another wanted radius (at most Reach).
	public RingWindow Resized(int radius)
	{
		radius = Math.Clamp(radius, 1, Reach);
		return radius == Radius ? this : new(Center, radius, Math.Min(ReachY, RadiusYOf(radius, flatten)), null, Reach, ReachY, flatten);
	}

	static int RoundUp4(int n) => (n + 3) & ~3; // the occupancy mip is 4^3 slots
	public static int Pmod(int a, int m) => Coords.FloorMod(a, m);

	public long Slots => (long)SizeXZ * SizeY * SizeXZ;
	public int Columns => SizeXZ * SizeXZ;
	public ChunkPos Offset => new(Pmod(Min.X, SizeXZ), Pmod(Min.Y, SizeY), Pmod(Min.Z, SizeXZ));

	public bool Contains(ChunkPos c) =>
		c.X >= Min.X && c.X < Min.X + SizeXZ && c.Y >= Min.Y && c.Y < Min.Y + SizeY && c.Z >= Min.Z && c.Z < Min.Z + SizeXZ;

	public bool Wanted(ChunkPos c)
	{
		int dx = Math.Abs(c.X - Center.X), dy = c.Y - Center.Y, dz = c.Z - Center.Z;
		return dy >= -RadiusY && dy <= RadiusY && dz >= -Radius && dz <= Radius && dx <= halfWidths[(dy + RadiusY) * (2 * Radius + 1) + dz + Radius];
	}

	bool WantedAt(double dx, double dy, double dz) => (dx * dx + dz * dz) / ((double)Radius * Radius) + dy * dy / ((double)RadiusY * RadiusY) <= 1;

	// Largest |dx| of a wanted chunk on the row (dy, dz) from the centre, -1 when the row has none.
	public int HalfWidth(int dy, int dz)
	{
		double v = (1 - (double)dy * dy / ((double)RadiusY * RadiusY)) * Radius * Radius - (double)dz * dz;
		if (v < 0) return WantedAt(0, dy, dz) ? 0 : -1;
		int k = (int)Math.Sqrt(v);
		while (k >= 0 && !WantedAt(k, dy, dz)) k--;
		while (WantedAt(k + 1, dy, dz)) k++;
		return k;
	}

	// Wanted chunks of the row at world y, z as an x range; empty when lo > hi.
	(int Lo, int Hi) Row(int y, int z)
	{
		int dy = y - Center.Y, dz = z - Center.Z, k = Math.Abs(dy) > RadiusY || Math.Abs(dz) > Radius ? -1 : halfWidths[(dy + RadiusY) * (2 * Radius + 1) + dz + Radius];
		return (Center.X - k, Center.X + k);
	}

	// Chunks wanted by `from` and not by `to` go to left, the other way round to entered; work per row is O(1) plus the output.
	public static void Diff(RingWindow from, RingWindow to, List<ChunkPos> left, List<ChunkPos> entered)
	{
		var s = new Shift(from, to);
		int rows = int.MaxValue;
		s.Rows(ref rows, left, entered);
	}

	// A Diff that can stop after any row and go on later (a crossing spread over frames).
	public struct Shift
	{
		readonly RingWindow from, to;
		readonly int y0, y1, z1;
		int y, z;

		public Shift(RingWindow from, RingWindow to)
		{
			(this.from, this.to) = (from, to);
			(y0, y1) = (Math.Min(from.Center.Y - from.RadiusY, to.Center.Y - to.RadiusY), Math.Max(from.Center.Y + from.RadiusY, to.Center.Y + to.RadiusY));
			(z, z1) = (Math.Min(from.Center.Z - from.Radius, to.Center.Z - to.Radius), Math.Max(from.Center.Z + from.Radius, to.Center.Z + to.Radius));
			y = y0;
		}

		// Up to `rows` rows (counted down); true when the whole region is done.
		public bool Rows(ref int rows, List<ChunkPos> left, List<ChunkPos> entered)
		{
			for (; z <= z1; z++, y = y0)
				for (; y <= y1; y++)
				{
					if (rows-- <= 0) return false;
					var (a, b) = (from.Row(y, z), to.Row(y, z));
					Outside(a, b, y, z, left);
					Outside(b, a, y, z, entered);
				}
			return true;
		}
	}

	static void Outside((int Lo, int Hi) a, (int Lo, int Hi) b, int y, int z, List<ChunkPos> into)
	{
		if (b.Lo > b.Hi) (b.Lo, b.Hi) = (a.Hi + 1, a.Hi);
		for (int x = a.Lo; x <= Math.Min(a.Hi, b.Lo - 1); x++) into.Add(new ChunkPos(x, y, z));
		for (int x = Math.Max(a.Lo, b.Hi + 1); x <= a.Hi; x++) into.Add(new ChunkPos(x, y, z));
	}

	// Every wanted chunk, row by row.
	public void EachWanted(Action<ChunkPos> f)
	{
		for (int z = Center.Z - Radius; z <= Center.Z + Radius; z++)
			for (int y = Center.Y - RadiusY; y <= Center.Y + RadiusY; y++)
			{
				var (lo, hi) = Row(y, z);
				for (int x = lo; x <= hi; x++) f(new ChunkPos(x, y, z));
			}
	}

	public int SlotIndex(ChunkPos c)
	{
		int x = c.X % SizeXZ, y = c.Y % SizeY, z = c.Z % SizeXZ;
		return (x < 0 ? x + SizeXZ : x) + SizeXZ * ((y < 0 ? y + SizeY : y) + SizeY * (z < 0 ? z + SizeXZ : z));
	}
	public int ColumnSlot(int x, int z) => Pmod(x, SizeXZ) + SizeXZ * Pmod(z, SizeXZ);
}

public enum SlotState : byte { None, Queued, Sent, Failed }

// CPU streaming state (docs/v2.md §4.1, §5.17): the ring, a state byte per slot, the queue (chunks entering the mid sphere first, then FIFO buckets of whole chunks of distance, past the mid sphere in three tiers: seen or wanted by rays, in the view cone, the rest), which column each column slot holds; Godot-free.
public sealed class ChunkStream
{
	public RingWindow Ring => ring;
	public RingWindow Mid { get; private set; }
	public int Count { get; private set; }   // bucket entries, stale ones included until popped
	public int Pending { get; private set; } // slots in Queued
	public int Incoming => incoming.Count + staged.Count; // entered chunks not queued yet (Drain; staged until their crossing's releases are done)
	public int Shifts => shifts.Count + (inShift ? 1 : 0); // crossings whose diff is not done (Advance)
	public int MidEntries => mid.Count;
	public int Failed { get; private set; } // slots in Failed: wanted, not in memory, pools were full; Retry queues them again
	public int Retrying => retry.Count;
	public const int RetryFrames = 30, MaxBackoff = 4, DrainFrames = 8;
	public int DrainDue => (Incoming + DrainFrames - 1) / DrainFrames; // entered chunks to queue this frame at least, so Incoming never falls behind for good
	public int Now; // the caller's frame, stamped on queued slots for Age
	RingWindow ring;
	readonly SlotState[] state;
	readonly int[] queuedAt;
	readonly byte[] fails; // per slot, since the chunk entered: Fail backoff
	readonly PriorityQueue<ChunkPos, int> retry = new(); // by frame due
	readonly long[] columnOf;
	readonly Queue<ChunkPos>[] buckets; // Tiers x distance
	readonly ushort[] seenAt; // per slot: the visibility period rays last touched it (MarkSeen)
	ushort period = 1000;
	public const int Tiers = 3, SeenPeriods = 4; // seen within this many periods counts
	readonly Queue<ChunkPos> mid = new();
	readonly Queue<ChunkPos> incoming = new();
	readonly List<ChunkPos> staged = new(), midEntered = new(), midLeft = new();
	readonly Queue<RingWindow.Shift> shifts = new();
	RingWindow.Shift shift; // the crossing Advance is in
	bool inShift;
	int lowest, sweep, sweepLeft; // sweep: the bucket Compact walks, sweepLeft its entries not visited yet
	Vector3 forward;
	float viewCos = -1;

	public ChunkStream(RingWindow ring, int midRadius, Vector3 forward = default, float viewCos = -1)
	{
		this.ring = ring;
		Look(forward, viewCos);
		Mid = new RingWindow(ring.Center, Math.Clamp(midRadius, 1, ring.RadiusY), 1);
		state = new SlotState[ring.Slots];
		queuedAt = new int[ring.Slots];
		fails = new byte[ring.Slots];
		columnOf = new long[ring.Columns];
		Array.Fill(columnOf, long.MinValue);
		buckets = new Queue<ChunkPos>[Tiers * (Math.Max(ring.Radius, ring.RadiusY) + 1)];
		seenAt = new ushort[ring.Slots];
		parked = new bool[ring.Slots];
		for (int i = 0; i < buckets.Length; i++) buckets[i] = new();
		ring.EachWanted(c => Enqueue(c, ring.SlotIndex(c)));
	}

	public SlotState State(ChunkPos c) => ring.Contains(c) ? state[ring.SlotIndex(c)] : SlotState.None;
	public int Age(ChunkPos c) => Now - queuedAt[ring.SlotIndex(c)]; // frames since it was last queued
	public int Entries(int lo, int hi) { int n = 0; for (int b = lo; b < Math.Min(hi, buckets.Length); b++) n += buckets[b].Count; return n; } // bucket entries of distances [lo, hi) (the view cone; past Ring radius those outside it), stale ones included

	void Enqueue(ChunkPos c, int slot)
	{
		if (state[slot] != SlotState.Queued) { Pending++; queuedAt[slot] = Now; }
		state[slot] = SlotState.Queued;
		Put(c);
	}

	void Put(ChunkPos c)
	{
		int b = Bucket(c);
		buckets[b].Enqueue(c);
		Count++;
		if (b < lowest) lowest = b;
	}

	// View cone (unit forward, cosine of the half angle; -1 = everything in view): past the mid sphere, chunks in it go to the second tier of the buckets, the rest to the third (seen ones to the first). Entries keep their bucket until popped or compacted.
	public void Look(Vector3 forward, float viewCos) => (this.forward, this.viewCos) = (forward, viewCos);

	int Bucket(ChunkPos c)
	{
		long dx = c.X - ring.Center.X, dy = c.Y - ring.Center.Y, dz = c.Z - ring.Center.Z;
		double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		int third = buckets.Length / Tiers, b = Math.Min(third - 1, (int)d);
		return b <= Mid.Radius || Seen(ring.SlotIndex(c)) ? b : dx * forward.X + dy * forward.Y + dz * forward.Z >= viewCos * d ? third + b : 2 * third + b;
	}

	// Stale bucket entries over this many per queued slot start Compact.
	public bool NeedsCompact => Count > 2 * Pending + CompactSlack;
	public const int CompactSlack = 1 << 16;

	// Visits up to max bucket entries, bucket after bucket: stale ones are dropped, live ones move to the bucket of their distance now.
	public void Compact(int max)
	{
		for (int i = 0; i < max && Count > 0; i++)
		{
			while ((sweepLeft = Math.Min(sweepLeft, buckets[sweep].Count)) == 0) { sweep = (sweep + 1) % buckets.Length; sweepLeft = buckets[sweep].Count; }
			sweepLeft--;
			var c = buckets[sweep].Dequeue();
			Count--;
			if (state[ring.SlotIndex(c)] == SlotState.Queued && ring.Wanted(c)) Put(c);
		}
	}

	// The camera is in another chunk: the crossing's diff runs within maxRows rows now, the rest in Advance. Chunks that left go to released (their slots become None), the ones that entered wait in Incoming for Drain once the crossing's releases are all done (a slot is never queued while its old chunk holds it); those entering the mid sphere are queued now and go first (their slots were outside the old ring; the previous crossing is finished first, so one is in progress at most).
	public void Move(ChunkPos center, List<ChunkPos> released, int maxRows = int.MaxValue)
	{
		Advance(released, int.MaxValue);
		var next = ring.MovedTo(center);
		shifts.Enqueue(new RingWindow.Shift(ring, next));
		ring = next;
		var nextMid = Mid.MovedTo(center);
		midEntered.Clear();
		midLeft.Clear();
		RingWindow.Diff(Mid, nextMid, midLeft, midEntered);
		Mid = nextMid;
		foreach (var c in midEntered)
		{
			int slot = ring.SlotIndex(c);
			if (state[slot] == SlotState.Failed) { Failed--; (state[slot], parked[slot]) = (SlotState.None, false); }
			if (state[slot] == SlotState.None) Enqueue(c, slot);
			if (state[slot] == SlotState.Queued) mid.Enqueue(c);
		}
		Advance(released, maxRows);
	}

	// Another wanted radius around the same centre (memory pressure): the shell that left goes to released like a crossing's, one that entered to Incoming.
	public void Resize(int radius, List<ChunkPos> released, int maxRows = int.MaxValue)
	{
		Advance(released, int.MaxValue);
		var next = ring.Resized(radius);
		if (next == ring) return;
		shifts.Enqueue(new RingWindow.Shift(ring, next));
		ring = next;
		Advance(released, maxRows);
	}

	// Up to maxRows rows of the crossings in progress, oldest first.
	public void Advance(List<ChunkPos> released, int maxRows)
	{
		while (maxRows > 0)
		{
			if (!inShift && !(inShift = shifts.TryDequeue(out shift))) return;
			int first = released.Count;
			bool done = shift.Rows(ref maxRows, released, staged);
			for (int i = first; i < released.Count; i++)
			{
				int slot = ring.SlotIndex(released[i]);
				if (state[slot] == SlotState.Queued) Pending--;
				if (state[slot] == SlotState.Failed) Failed--;
				(state[slot], fails[slot], parked[slot], seenAt[slot]) = (SlotState.None, 0, false, (ushort)(period - SeenPeriods - 1));
			}
			if (!done) return;
			inShift = false;
			foreach (var c in staged) incoming.Enqueue(c);
			staged.Clear();
		}
	}

	// Queues up to max entered chunks (oldest first) that are still wanted and not queued; the rest of a crossing waits for the next frames.
	public void Drain(int max)
	{
		for (int n = 0; n < max && incoming.TryDequeue(out var c); n++)
		{
			int slot = ring.SlotIndex(c);
			if (state[slot] == SlotState.None && ring.Wanted(c)) Enqueue(c, slot);
		}
	}

	bool Take(ChunkPos c, int slot)
	{
		if (state[slot] != SlotState.Queued || !ring.Wanted(c)) return false;
		state[slot] = SlotState.Sent;
		Pending--;
		return true;
	}

	public bool TryPop(out ChunkPos c)
	{
		int skips = int.MaxValue;
		return TryPop(out c, ref skips);
	}

	public bool TryPop(out ChunkPos c, ref int skips) => TryPop(out c, ref skips, int.MaxValue);

	// Next chunk to generate, now Sent: mid sphere entries first, then the nearest bucket, oldest first; an entry whose distance grew moves to its bucket. Gives up after `skips` stale entries, or at a bucket farther than maxDistance (memory pressure).
	public bool TryPop(out ChunkPos c, ref int skips, int maxDistance)
	{
		while (mid.TryDequeue(out c)) if (Take(c, ring.SlotIndex(c))) return true;
		while (Count > 0 && skips > 0)
		{
			while (buckets[lowest].Count == 0) lowest++;
			if (lowest % (buckets.Length / Tiers) > maxDistance) break;
			c = buckets[lowest].Dequeue();
			Count--;
			int slot = ring.SlotIndex(c);
			if (state[slot] != SlotState.Queued || !ring.Wanted(c)) { skips--; continue; }
			if (Bucket(c) > lowest) { Put(c); skips--; continue; }
			state[slot] = SlotState.Sent;
			Pending--;
			return true;
		}
		c = default;
		return false;
	}

	// Key of the next TryPop, comparable across rings of other levels (docs/v2.md §5.17): -1 for mid sphere entries, else the bucket's distance in own chunks (pixel footprint) times Tiers plus its tier (tier-major keys measured worse: lod-cruise-150 fallback 7.9 % against 5.7 %); int.MaxValue when nothing is queued within maxDistance. Stale entries count until popped.
	public int NextKey(int maxDistance = int.MaxValue)
	{
		if (mid.Count > 0) return -1;
		if (Count == 0) return int.MaxValue;
		while (buckets[lowest].Count == 0) lowest++;
		int third = buckets.Length / Tiers, b = lowest % third;
		return b > maxDistance ? int.MaxValue : b * Tiers + lowest / third;
	}

	public bool Seen(int slot) => (ushort)(period - seenAt[slot]) <= SeenPeriods;

	// A visibility readback (docs/v2.md §5.17): bit per slot of this ring, set where primary rays hit or wanted a chunk. A queued chunk moves to the first tier, a parked one (Evict) is queued again.
	public void MarkSeen(ReadOnlySpan<uint> bits)
	{
		period++;
		for (int w = 0; w < bits.Length; w++)
			for (uint m = bits[w]; m != 0; m &= m - 1)
			{
				int slot = 32 * w + System.Numerics.BitOperations.TrailingZeroCount(m);
				if (slot >= seenAt.Length) return;
				seenAt[slot] = period;
				if (state[slot] == SlotState.Queued) Put(ChunkAt(slot));
				else if (state[slot] == SlotState.Failed && parked[slot]) { Failed--; parked[slot] = false; state[slot] = SlotState.None; Enqueue(ChunkAt(slot), slot); }
			}
	}

	// The chunk of the ring that holds a slot.
	ChunkPos ChunkAt(int slot)
	{
		int sx = ring.SizeXZ, sy = ring.SizeY, x = slot % sx, y = slot / sx % sy, z = slot / (sx * sy);
		var o = ring.Offset;
		return new(ring.Min.X + RingWindow.Pmod(x - o.X, sx), ring.Min.Y + RingWindow.Pmod(y - o.Y, sy), ring.Min.Z + RingWindow.Pmod(z - o.Z, sx));
	}

	readonly bool[] parked;
	int evictCursor;

	// Memory pressure: resident chunks past minDistance that rays have not touched for SeenPeriods go to released and wait parked (Failed, back after a long backoff or when seen); walks up to maxSlots slots, evicts up to max chunks.
	public int Evict(int minDistance, int maxSlots, int max, List<ChunkPos> released)
	{
		int n = 0;
		for (int i = 0; i < maxSlots && n < max; i++, evictCursor = (evictCursor + 1) % seenAt.Length)
		{
			if (state[evictCursor] != SlotState.Sent || Seen(evictCursor)) continue;
			var c = ChunkAt(evictCursor);
			long dx = c.X - ring.Center.X, dy = c.Y - ring.Center.Y, dz = c.Z - ring.Center.Z;
			if (!ring.Wanted(c) || dx * dx + dy * dy + dz * dz <= (long)minDistance * minDistance) continue;
			released.Add(c);
			(state[evictCursor], parked[evictCursor]) = (SlotState.Failed, true);
			Failed++;
			retry.Enqueue(c, Now + (RetryFrames << MaxBackoff));
			n++;
		}
		return n;
	}

	// Sent now, out of queue order (the near sphere first); false unless it is wanted and queued.
	public bool Claim(ChunkPos c) => Take(c, ring.SlotIndex(c));

	// The GPU could not store it (pools full): inside the mid sphere it is queued again at once, farther after RetryFrames << failures (or when it enters the mid sphere).
	public void Fail(ChunkPos c)
	{
		int slot = ring.SlotIndex(c);
		if (state[slot] != SlotState.Sent || !ring.Wanted(c)) return;
		if (Mid.Wanted(c)) { Enqueue(c, slot); mid.Enqueue(c); return; }
		state[slot] = SlotState.Failed;
		Failed++;
		retry.Enqueue(c, Now + (RetryFrames << Math.Min((int)fails[slot], MaxBackoff)));
		if (fails[slot] < byte.MaxValue) fails[slot]++;
	}

	// Failed chunks whose backoff is over go back into the queue.
	public void Retry()
	{
		while (retry.TryPeek(out var c, out int due) && due <= Now)
		{
			retry.Dequeue();
			int slot = ring.SlotIndex(c);
			if (state[slot] != SlotState.Failed || !ring.Wanted(c)) continue;
			Failed--;
			parked[slot] = false;
			state[slot] = SlotState.None;
			Enqueue(c, slot);
		}
	}

	// Back into the queue (deferred by the GPU, or over this frame's budget) unless it left meanwhile; inside the mid sphere it goes first.
	public void Requeue(ChunkPos c)
	{
		int slot = ring.SlotIndex(c);
		if (state[slot] != SlotState.Sent || !ring.Wanted(c)) return;
		Enqueue(c, slot);
		if (Mid.Wanted(c)) mid.Enqueue(c);
	}

	public bool HasColumn(ChunkPos c) => columnOf[ring.ColumnSlot(c.X, c.Z)] == ColumnKey(c);
	public void SetColumn(ChunkPos c) => columnOf[ring.ColumnSlot(c.X, c.Z)] = ColumnKey(c);
	static long ColumnKey(ChunkPos c) => (long)c.X << 32 | (uint)c.Z;
}
