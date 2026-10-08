using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Content.Format;
using Renesance.World;

namespace Renesance.Content;

public enum Family { None, Fence, Pane, Wall, Wire, Stairs }

public static class BlockRules
{
	public const string North = "north", East = "east", South = "south", West = "west", Up = "up", Shape = "shape", Facing = "facing", Half = "half",
		True = "true", False = "false", Low = "low", Tall = "tall", Side = "side", None = "none", Straight = "straight",
		InnerLeft = "inner_left", InnerRight = "inner_right", OuterLeft = "outer_left", OuterRight = "outer_right";
	public const string HalfMultiblock = "half", PartMultiblock = "part";
	static readonly string[] sides = { North, East, South, West };
	static readonly (int X, int Z)[] steps = { (0, -1), (1, 0), (0, 1), (-1, 0) };

	static BlockParam P(BlockDef d, string name) => d.Params.FirstOrDefault(p => p.Name == name);
	static BlockPos Add(BlockPos p, int x, int y, int z) => new(p.X + x, p.Y + y, p.Z + z);
	static int SideOf(string direction) => Array.IndexOf(sides, direction);

	public static BlockState DefaultState(BlockDef d) =>
		d.Entry?.Kind == EntryKind.Wall && P(d, Up) is { } up ? up.With(new BlockState(d.Id, d.DefaultData), True) : new BlockState(d.Id, d.DefaultData);

	public static Family FamilyOf(BlockDef d) => d.Entry?.Kind switch
	{
		EntryKind.Fence => Family.Fence,
		EntryKind.Pane => Family.Pane,
		EntryKind.Custom when sides.All(n => P(d, n)?.IndexOf(True) >= 0) && d.Name.EndsWith("_bars", StringComparison.Ordinal) => Family.Pane,
		EntryKind.Wall => Family.Wall,
		EntryKind.RedstoneDust => Family.Wire,
		EntryKind.Stairs => Family.Stairs,
		_ => Family.None,
	};

	public static bool Solid(BlockDef d) => d.Entry is { Kind: EntryKind.Cube, Surface: Surface.Opaque };

	static bool Wood(BlockDef d) => d.Entry.Tags?.Any(t => t.StartsWith("wood:", StringComparison.Ordinal)) == true;

	static bool Joins(BlockDef self, Family f, BlockDef other) => Solid(other) || (FamilyOf(other), f) switch
	{
		(Family.Fence, Family.Fence) => Wood(self) == Wood(other),
		(_, Family.Fence) => other.Entry?.Kind == EntryKind.FenceGate,
		(Family.Pane or Family.Wall, Family.Pane) => true,
		(Family.Wall or Family.Pane, Family.Wall) => true,
		(_, Family.Wall) => other.Entry?.Kind == EntryKind.FenceGate,
		(Family.Wire, Family.Wire) => true,
		_ => false,
	};

	public static (BlockPos Pos, BlockState State)? Partner(BlockRegistry reg, BlockPos p, BlockState s)
	{
		var d = reg.Get(s);
		if (d.Entry?.Multiblock is not { } kind || P(d, kind) is not { } part) return null;
		int i = part.Get(s), dir = i == 0 ? 1 : -1;
		BlockPos q;
		if (kind == HalfMultiblock) q = Add(p, 0, dir, 0);
		else if (kind == PartMultiblock && P(d, Facing) is { } f && SideOf(f.Value(s)) is var k and >= 0) q = Add(p, steps[k].X * dir, 0, steps[k].Z * dir);
		else return null;
		return (q, part.With(s, 1 - i));
	}

	static bool SamePart(BlockRegistry reg, BlockState a, BlockState b) =>
		a.Type == b.Type && reg.Get(a).Entry?.Multiblock is { } kind && P(reg.Get(a), kind) is { } part && part.Get(a) == part.Get(b);

	public static BlockState Connect(BlockRegistry reg, Func<BlockPos, BlockState> at, BlockPos p, BlockState s)
	{
		var d = reg.Get(s);
		var f = FamilyOf(d);
		if (f == Family.None) return s;
		if (f == Family.Stairs) return StairShape(reg, at, p, s, d);
		var near = new bool[4];
		for (int k = 0; k < 4; k++) near[k] = Joins(d, f, reg.Get(at(Add(p, steps[k].X, 0, steps[k].Z))));
		if (f == Family.Wire) return Wire(reg, at, p, s, d);
		bool tall = Solid(reg.Get(at(Add(p, 0, 1, 0)))) || FamilyOf(reg.Get(at(Add(p, 0, 1, 0)))) == Family.Wall;
		for (int k = 0; k < 4; k++)
			if (P(d, sides[k]) is { } side) s = side.With(s, f != Family.Wall ? (near[k] ? True : False) : !near[k] ? None : tall ? Tall : Low);
		if (f == Family.Wall && P(d, Up) is { } up)
			s = up.With(s, near[0] && near[2] && !near[1] && !near[3] || near[1] && near[3] && !near[0] && !near[2] ? False : True);
		return s;
	}

	static BlockState Wire(BlockRegistry reg, Func<BlockPos, BlockState> at, BlockPos p, BlockState s, BlockDef d)
	{
		bool Is(BlockPos q) => FamilyOf(reg.Get(at(q))) == Family.Wire;
		bool roof = Solid(reg.Get(at(Add(p, 0, 1, 0))));
		var v = new string[4];
		for (int k = 0; k < 4; k++)
		{
			var n = Add(p, steps[k].X, 0, steps[k].Z);
			bool solid = Solid(reg.Get(at(n)));
			v[k] = Is(n) || !solid && Is(Add(n, 0, -1, 0)) ? Side : solid && !roof && Is(Add(n, 0, 1, 0)) ? Up : None;
		}
		if (v.Count(x => x != None) == 1)
			for (int k = 0; k < 4; k++) if (v[k] != None) { if (v[(k + 2) % 4] == None) v[(k + 2) % 4] = Side; break; }
		for (int k = 0; k < 4; k++) if (P(d, sides[k]) is { } side) s = side.With(s, v[k]);
		return s;
	}

	static BlockState StairShape(BlockRegistry reg, Func<BlockPos, BlockState> at, BlockPos p, BlockState s, BlockDef d)
	{
		if (P(d, Shape) is not { } shape || P(d, Facing) is not { } facing || P(d, Half) is not { } half || SideOf(facing.Value(s)) is not (var f and >= 0)) return s;
		(int Facing, string Half)? Stair(BlockPos q)
		{
			var o = at(q);
			var od = reg.Get(o);
			return FamilyOf(od) == Family.Stairs && P(od, Facing) is { } of && P(od, Half) is { } oh && SideOf(of.Value(o)) is var k and >= 0 ? (k, oh.Value(o)) : null;
		}
		BlockPos Step(int k, int sign = 1) => Add(p, steps[k].X * sign, 0, steps[k].Z * sign);
		string h = half.Value(s);
		bool CanTake(int k) => Stair(Step(k)) is not { } o || o.Facing != f || o.Half != h;
		int ccw = (f + 3) % 4;
		string v = Straight;
		if (Stair(Step(f)) is { } front && front.Half == h && front.Facing % 2 != f % 2 && CanTake((front.Facing + 2) % 4)) v = front.Facing == ccw ? OuterLeft : OuterRight;
		else if (Stair(Step(f, -1)) is { } back && back.Half == h && back.Facing % 2 != f % 2 && CanTake(back.Facing)) v = back.Facing == ccw ? InnerLeft : InnerRight;
		return shape.IndexOf(v) >= 0 ? shape.With(s, v) : s;
	}

	public static List<(BlockPos Pos, BlockState State)> Apply(BlockRegistry reg, Func<BlockPos, BlockState> world, IEnumerable<(BlockPos Pos, BlockState State)> requested)
	{
		var req = requested.ToList();
		var set = new Dictionary<BlockPos, BlockState>();
		BlockState At(BlockPos q) => set.TryGetValue(q, out var v) ? v : world(q);
		foreach (var (p, s) in req) set[p] = s;
		var asked = set.Keys.ToHashSet();
		foreach (var (p, s) in req)
		{
			var prior = world(p);
			var part = Partner(reg, p, s);
			if (part is { } q && At(q.Pos) != q.State && At(q.Pos).Type != BlockRegistry.AirId && At(q.Pos).Type != s.Type) { set[p] = prior; continue; }
			if (prior != s && Partner(reg, p, prior) is { } old && !asked.Contains(old.Pos) && SamePart(reg, At(old.Pos), old.State)) set[old.Pos] = BlockState.Air;
			if (part is { } n) set[n.Pos] = n.State;
		}
		var dirty = new HashSet<BlockPos>();
		foreach (var p in set.Keys)
			for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
				if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) <= 2) dirty.Add(Add(p, dx, dy, dz));
		foreach (var p in dirty)
		{
			var s = At(p);
			var c = Connect(reg, At, p, s);
			if (c != s) set[p] = c;
		}
		return set.Where(kv => asked.Contains(kv.Key) || kv.Value != world(kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();
	}
}
