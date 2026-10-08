using Godot;
using System;
using System.Linq;
using Renesance.Content;
using Renesance.Content.Format;
using Renesance.World;
using Matter = Renesance.Content.Format.Matter;

// Walk mode (docs/testing.md, Ovládání): a 0.6 x 1.8 m box (eye 1.62 m) with gravity, jumping, a one-block auto-step and swimming,
// against whole cells of a block box read back from the GPU around the player (ReadBox, a few frames old) with World.Edits over it.
// Cells outside the box or not in memory are solid, so the player hovers until the world is in.
public partial class Player
{
	public const float HalfWidth = 0.3f, Height = 1.8f, EyeHeight = 1.62f, StepUp = Voxel.BlockSize;
	const float Gravity = 32, JumpSpeed = 8.95f, MaxFall = 40, WalkSpeed = 4.317f, SprintSpeed = 5.612f; // jump ≈ 1.25 m
	const float SwimUp = 2.5f, Sink = 1, WaterDrag = 30, WaterSlow = 0.5f, MaxStep = 0.05f, Eps = 1e-3f;
	const int ReadRetryFrames = 30;
	static readonly BlockState Unknown = new(uint.MaxValue);
	static readonly Vector3I Reach = new(8, 16, 8); // blocks around the eye: a few frames of falling at MaxFall

	public Action<BlockPos, Vector3I, Action<BlockState[]>, BlockState> ReadBox; // EditorWorld.ReadBox, set by Main

	MoveMode mode;
	public MoveMode Mode { get => mode; set => (mode, velocity) = (value, Vector3.Zero); }

	Vector3 velocity; // m/s
	bool grounded;
	BlockState[] box;
	Vector3I boxMin, boxSize;
	ulong asked;
	bool reading;
	BlockRegistry kindsOf;
	bool[] solid, liquid;

	// One step: move.x right, move.y forward (both -1..1), jump held (swims up in a liquid).
	public void Walk(double delta, Vector2 move, bool jump, bool sprint)
	{
		Rotation = new Vector3(pitch, yaw, 0);
		Request();
		if (World.Registry == null || World.Loading || box == null) return;
		Kinds();
		const float b = Voxel.BlockSize;
		float dt = Math.Min((float)delta, MaxStep), hw = HalfWidth / b;
		var o = World.Anchor * Layout.Chunk;
		var feet = GlobalPosition / b - new Vector3(0, EyeHeight / b, 0);
		var lo = feet - new Vector3(hw, 0, hw);
		var hi = feet + new Vector3(hw, Height / b, hw);
		bool water = InLiquid(lo, hi, o);

		var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
		var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
		var wish = (right * move.X + forward * move.Y).LimitLength(1) * (sprint ? SprintSpeed : WalkSpeed) * (water ? WaterSlow : 1);
		(velocity.X, velocity.Z) = (wish.X, wish.Z);
		if (water) velocity.Y = jump ? SwimUp : Mathf.MoveToward(velocity.Y, -Sink, WaterDrag * dt);
		else velocity.Y = Math.Max((grounded && jump ? JumpSpeed : velocity.Y) - Gravity * dt, -MaxFall);

		var d = velocity * dt / b;
		float dy = Clip(1, d.Y, lo, hi, o);
		(lo.Y, hi.Y) = (lo.Y + dy, hi.Y + dy);
		grounded = d.Y < 0 && dy > d.Y;
		if (dy != d.Y) velocity.Y = 0;
		var (flo, fhi) = Slide(lo, hi, d, o);
		if (grounded && Flat(fhi - hi) < Flat(d) - Eps * Eps)
		{
			float up = Clip(1, StepUp / b, lo, hi, o);
			var (slo, shi) = Slide(lo + Vector3.Up * up, hi + Vector3.Up * up, d, o);
			float down = Clip(1, -up, slo, shi, o);
			(slo.Y, shi.Y) = (slo.Y + down, shi.Y + down);
			if (Flat(shi - hi) > Flat(fhi - hi) + Eps) (flo, fhi) = (slo, shi);
		}
		GlobalPosition = new Vector3(flo.X + hw, flo.Y, flo.Z + hw) * b + new Vector3(0, EyeHeight, 0);
		World.Rebase();
	}

	static float Flat(Vector3 v) => v.X * v.X + v.Z * v.Z;

	(Vector3 Lo, Vector3 Hi) Slide(Vector3 lo, Vector3 hi, Vector3 d, Vector3I o)
	{
		float dx = Clip(0, d.X, lo, hi, o);
		(lo.X, hi.X) = (lo.X + dx, hi.X + dx);
		float dz = Clip(2, d.Z, lo, hi, o);
		(lo.Z, hi.Z) = (lo.Z + dz, hi.Z + dz);
		return (lo, hi);
	}

	// Of d (blocks) along axis a, what the box [lo, hi] (local blocks) moves before a solid cell; cells it already overlaps don't stop it.
	float Clip(int a, float d, Vector3 lo, Vector3 hi, Vector3I o)
	{
		if (d == 0) return 0;
		int b = (a + 1) % 3, c = (a + 2) % 3;
		int b0 = Mathf.FloorToInt(lo[b] + Eps), b1 = Mathf.CeilToInt(hi[b] - Eps) - 1, c0 = Mathf.FloorToInt(lo[c] + Eps), c1 = Mathf.CeilToInt(hi[c] - Eps) - 1;
		bool Blocked(int k)
		{
			var cell = Vector3I.Zero;
			cell[a] = k;
			for (int i = b0; i <= b1; i++)
				for (int j = c0; j <= c1; j++)
				{
					(cell[b], cell[c]) = (i, j);
					if (Solid(cell + o)) return true;
				}
			return false;
		}
		if (d > 0)
		{
			for (int k = Mathf.CeilToInt(hi[a] - Eps); k < hi[a] + d; k++)
				if (Blocked(k)) return Math.Max(k - hi[a], 0);
		}
		else
			for (int k = Mathf.FloorToInt(lo[a] + Eps) - 1; k + 1 > lo[a] + d; k--)
				if (Blocked(k)) return Math.Min(k + 1 - lo[a], 0);
		return d;
	}

	// The lower half of the box in a liquid.
	bool InLiquid(Vector3 lo, Vector3 hi, Vector3I o)
	{
		for (int y = Mathf.FloorToInt(lo.Y + Eps); y <= Mathf.FloorToInt((lo.Y + hi.Y) / 2); y++)
			for (int x = Mathf.FloorToInt(lo.X + Eps); x < hi.X - Eps; x++)
				for (int z = Mathf.FloorToInt(lo.Z + Eps); z < hi.Z - Eps; z++)
					if (At(new Vector3I(x, y, z) + o) is var s && s != Unknown && s.Type < liquid.Length && liquid[s.Type]) return true;
		return false;
	}

	bool Solid(Vector3I c) => At(c) is var s && (s == Unknown || s.Type < solid.Length && solid[s.Type]);

	BlockState At(Vector3I c)
	{
		if (World.Edits.TryGet(new BlockPos(c.X, c.Y, c.Z), out var e)) return e;
		var l = c - boxMin;
		if (l.X < 0 || l.Y < 0 || l.Z < 0 || l.X >= boxSize.X || l.Y >= boxSize.Y || l.Z >= boxSize.Z) return Unknown;
		return box[l.X + boxSize.X * (l.Y + boxSize.Y * l.Z)];
	}

	// Whole cells: full blocks and shaped solids (slabs, stairs, walls, fences, panes, beds, ...); plants, torches, rails, carpets, doors, liquids and gases are passable.
	void Kinds()
	{
		if (kindsOf == World.Registry) return;
		kindsOf = World.Registry;
		solid = kindsOf.All.Select(d => d.Item == null && d.Entry is { Matter: Matter.Solid or Matter.Granular } e
			&& e.Kind is EntryKind.Cube or EntryKind.Slab or EntryKind.Stairs or EntryKind.Wall or EntryKind.Fence or EntryKind.Pane or EntryKind.Piston or EntryKind.ThinBlock or EntryKind.Custom).ToArray();
		liquid = kindsOf.All.Select(d => d.Liquid).ToArray();
	}

	// The box around the eye, one read at a time (again after ReadRetryFrames if a world reset dropped it).
	void Request()
	{
		ulong frame = Engine.GetProcessFrames();
		if (ReadBox == null || !World.GpuReady || reading && frame - asked < ReadRetryFrames) return;
		(reading, asked) = (true, frame);
		var min = (Vector3I)(GlobalPosition / Voxel.BlockSize).Floor() + World.Anchor * Layout.Chunk - Reach;
		var size = Reach * 2 + Vector3I.One;
		ReadBox(new BlockPos(min.X, min.Y, min.Z), size, s =>
		{
			reading = false;
			if (s != null) (box, boxMin, boxSize) = (s, min, size);
		}, Unknown);
	}
}
