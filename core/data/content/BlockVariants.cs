using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Renesance.World;

namespace Renesance.Content;

// Global block variant ids (type + RenderMask states), allocated on first use, 0 = air; thread-safe (main and render thread).
public sealed class BlockVariants
{
	readonly List<BlockState> states = new();
	readonly Dictionary<BlockState, int> ids = new();
	readonly int capacity;

	public BlockRegistry Registry { get; }
	public int MaxCount { get; }

	public BlockVariants(BlockRegistry registry, int capacity)
	{
		Registry = registry;
		this.capacity = capacity;
		MaxCount = (int)Math.Min(capacity, registry.All.Sum(d => 1L << BitOperations.PopCount(d.RenderMask)));
		Id(BlockState.Air);
	}

	// Ids of the default states in registry order, before any other use: shader constants (Gpu prelude B_*) then don't depend on the worlds loaded before. Model blocks stay on first use (their rows compose models).
	public void ReserveDefaults()
	{
		foreach (var d in Registry.All)
			if (d.Entry?.Kind is null or Format.EntryKind.Cube or Format.EntryKind.Liquid or Format.EntryKind.Volume) Id(d.Default);
	}

	public int Count { get { lock (states) return states.Count; } }

	public BlockState Key(BlockState s) => new(s.Type, (ushort)(s.Data & Registry[s.Type].RenderMask));

	public int Id(BlockState s)
	{
		var k = Key(s);
		lock (states)
		{
			if (ids.TryGetValue(k, out var id)) return id;
			if (states.Count >= capacity)
				throw new InvalidOperationException($"{capacity} block variant ids, {Registry.Get(s).Name} doesn't fit");
			ids[k] = states.Count;
			states.Add(k);
			return states.Count - 1;
		}
	}

	// Ids past Count read as air.
	public BlockState State(int id) { lock (states) return id < states.Count ? states[id] : BlockState.Air; }

	public BlockState[] From(int first) { lock (states) return states.Skip(first).ToArray(); }
}
