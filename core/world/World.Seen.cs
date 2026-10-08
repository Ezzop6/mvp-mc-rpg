using Godot;
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Renesance.World;

// Measurement of visibility-driven streaming (docs/v2-mereni.md, Viditelnost chunků): the seen bits of Primary/dda-seen against the resident ring, by distance and kind, by count and pool MB.
public partial class World
{
	enum SeenKind { Missing, Air, Solid, Mixed }
	static readonly string[] SeenClasses = { "near", "mid", "far" };

	// Render thread, synchronous readbacks of the directory and the used records (a measurement run only).
	public void SeenReport(byte[] seenBytes, string label)
	{
		var ring = gpuRings?[0];
		if (ring == null) return;
		var rd = Gpu.Rd;
		var seen = MemoryMarshal.Cast<byte, uint>(seenBytes).ToArray();
		var dirs = MemoryMarshal.Cast<byte, uint>(rd.BufferGetData(dir, 0, (uint)(ring.Slots * 8))).ToArray();
		long records = Math.Min(PoolHigh[0], PoolCap[0]);
		var rec = records == 0 ? Array.Empty<uint>() : MemoryMarshal.Cast<byte, uint>(rd.BufferGetData(this.records, 0, (uint)(records * Layout.ChunkRecWords * 4))).ToArray();
		var n = new long[3, 4, 2];   // class, kind, seen: chunks
		var mb = new double[3, 4, 2]; // pool MB
		float near = NearRadius / Voxel.ChunkMeters, mid = MidRadius / Voxel.ChunkMeters + MidMargin; // far = what visibility may defer (the mid sphere is the simulation distance)
		var c0 = ring.Center;
		ring.EachWanted(c =>
		{
			int slot = ring.SlotIndex(c);
			uint x = dirs[2 * slot], y = dirs[2 * slot + 1];
			var kind = x == Layout.None ? SeenKind.Missing : (x & Layout.Uni) == 0 ? SeenKind.Mixed : (x & ~Layout.Uni) == 0 && y == 0 ? SeenKind.Air : SeenKind.Solid;
			long dx = c.X - c0.X, dy = c.Y - c0.Y, dz = c.Z - c0.Z;
			float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
			int cls = d <= near ? 0 : d <= mid ? 1 : 2, s = (int)(seen[slot >> 5] >> (slot & 31)) & 1;
			n[cls, (int)kind, s]++;
			if (kind != SeenKind.Mixed || x >= records) return;
			long bytes = Layout.ChunkRecWords * 4;
			for (int g = 0; g < Layout.Groups; g++)
			{
				uint e = rec[x * Layout.ChunkRecWords + 2 * g];
				if ((e & Layout.Uni) == 0 && e >> Layout.ClassShift != Layout.ClassAir) bytes += Layout.BrickWords((int)(e >> Layout.ClassShift)) * 4;
			}
			mb[cls, (int)kind, s] += bytes / 1048576.0;
		});
		var sb = new StringBuilder();
		var ic = CultureInfo.InvariantCulture;
		for (int cls = 0; cls < 3; cls++)
			foreach (var k in Enum.GetValues<SeenKind>())
				sb.Append(ic, $"[seen] {label} {SeenClasses[cls]} {k}: {n[cls, (int)k, 0] + n[cls, (int)k, 1]} chunks, seen {n[cls, (int)k, 1]}; {mb[cls, (int)k, 0] + mb[cls, (int)k, 1]:0.0} MB, seen {mb[cls, (int)k, 1]:0.0} MB\n");
		GD.Print(sb.ToString().TrimEnd());
	}
}
