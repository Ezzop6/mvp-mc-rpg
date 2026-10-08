using System.Collections.Generic;
using System.Linq;

namespace Renesance.World;

// Blocks and fluids changed on top of the generator, per chunk BlockPos.LocalIndex -> value; re-applied when a chunk regenerates.
public sealed class WorldEdits
{
	readonly Dictionary<ChunkPos, Dictionary<int, BlockState>> chunks = new();
	readonly Dictionary<ChunkPos, Dictionary<int, FluidState>> fluids = new();

	public int Count { get; private set; }
	public int FluidCount { get; private set; }
	public IEnumerable<ChunkPos> Chunks => chunks.Keys.Union(fluids.Keys);
	public bool Has(ChunkPos c) => chunks.ContainsKey(c) || fluids.ContainsKey(c);

	public void Set(BlockPos p, BlockState s) => Set(p.Chunk, p.LocalIndex, s);
	public void Set(ChunkPos c, int localIndex, BlockState s) => Count += Put(chunks, c, localIndex, s);
	public void SetFluid(BlockPos p, FluidState f) => SetFluid(p.Chunk, p.LocalIndex, f);
	public void SetFluid(ChunkPos c, int localIndex, FluidState f) => FluidCount += Put(fluids, c, localIndex, f);

	static int Put<T>(Dictionary<ChunkPos, Dictionary<int, T>> store, ChunkPos c, int localIndex, T v)
	{
		if (!store.TryGetValue(c, out var cells)) store[c] = cells = new();
		bool added = !cells.ContainsKey(localIndex);
		cells[localIndex] = v;
		return added ? 1 : 0;
	}

	public bool TryGet(BlockPos p, out BlockState s)
	{
		s = default;
		return chunks.TryGetValue(p.Chunk, out var blocks) && blocks.TryGetValue(p.LocalIndex, out s);
	}

	public bool TryGetFluid(BlockPos p, out FluidState f)
	{
		f = default;
		return fluids.TryGetValue(p.Chunk, out var cells) && cells.TryGetValue(p.LocalIndex, out f);
	}

	public IEnumerable<(BlockPos Pos, BlockState State)> InChunk(ChunkPos c) => Cells(chunks, c);
	public IEnumerable<(BlockPos Pos, FluidState Fluid)> FluidsInChunk(ChunkPos c) => Cells(fluids, c);

	static IEnumerable<(BlockPos, T)> Cells<T>(Dictionary<ChunkPos, Dictionary<int, T>> store, ChunkPos c)
	{
		if (!store.TryGetValue(c, out var cells)) yield break;
		var o = c.Origin;
		foreach (var (i, v) in cells)
			yield return (new BlockPos(o.X + i % Coords.ChunkSize, o.Y + i / Coords.ChunkSize % Coords.ChunkSize, o.Z + i / (Coords.ChunkSize * Coords.ChunkSize)), v);
	}

	public void Clear()
	{
		chunks.Clear();
		fluids.Clear();
		Count = FluidCount = 0;
	}
}
