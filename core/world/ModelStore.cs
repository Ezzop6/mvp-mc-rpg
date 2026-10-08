using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Renesance.Content;
using Renesance.World;
using Format = Renesance.Content.Format;

public enum ModelHeader : uint { Models, Placeable, PlaceableList, Records, Materials, Cells, Pools }
public enum PoolSection : uint { Bricks, CellRefs, CellTails }

// The model library (models.glsli) on the CPU behind one GPU buffer: loading, packing, block looks, composed models written into the slack.
public sealed class ModelStore
{
	public const int HeaderWords = 16, ModelRecWords = 4, MaterialWords = 4;
	public static readonly uint PoolStride = (uint)Enum.GetValues<PoolSection>().Length;
	public const float ModelSlack = 0.5f;
	readonly object gate = new();
	ModelLibrary library;
	int gpuModels;
	bool composeFull;
	readonly Dictionary<string, int> blockModels = new(), composed = new();
	readonly uint[] sections = new uint[HeaderWords], caps = new uint[HeaderWords];
	readonly List<(uint Word, uint[] Data)> uploads = new();
	public int CatalogModels { get; private set; }
	public int[] Placeable { get; private set; } = Array.Empty<int>();

	// The CPU library behind the GPU buffer, for the editor's mesh builder.
	public ModelLibrary Library { get { lock (gate) { if (library == null) Load(); return library; } } }

	static IEnumerable<uint> Words(List<byte> bytes, int from = 0)
	{
		var b = bytes.Skip(from).Concat(new byte[(4 - (bytes.Count - from) % 4) % 4]).ToArray();
		for (int i = 0; i < b.Length; i += 4) yield return BitConverter.ToUInt32(b, i);
	}

	static uint Half2(float a, float b) => BitConverter.HalfToUInt16Bits((Half)a) | (uint)BitConverter.HalfToUInt16Bits((Half)b) << 16;

	static uint[] Material(VoxelMaterial m)
	{
		float E(int shift) => Mathf.Pow((m.EmissionRgb >> shift & 255) / 255f, 2.2f) * m.EmissionStrength; // linear
		return new uint[MaterialWords] { m.Rgba, Half2(E(0), E(8)), Half2(E(16), m.Roughness), Half2(m.Metallic, m.Tint ? 1 : 0) };
	}

	static uint[] Record(ModelInfo m) => new uint[ModelRecWords]
	{
		(uint)(m.CellsX | m.CellsY << 8 | m.CellsZ << 16 | m.Width << 24), (uint)m.PaletteStart, (uint)m.CellTableStart, (uint)(m.SizeX | m.SizeZ << 16),
	};

	static uint PoolSlot(int pool, PoolSection section) => (uint)ModelHeader.Pools + PoolStride * (uint)pool + (uint)section;

	// models.glsli: header (MH_*), placeable ids, records, materials, cell table, then per voxel width bricks, refs, tails.
	static byte[] Pack(ModelLibrary lib, int[] placeable, int catalogModels, uint[] sectionBase, uint[] sectionCap)
	{
		var w = new List<uint>(new uint[HeaderWords]);
		void Section(uint slot, IEnumerable<uint> words)
		{
			w[(int)slot] = sectionBase[slot] = (uint)w.Count;
			w.AddRange(words);
			int n = w.Count - (int)sectionBase[slot];
			w.AddRange(new uint[(int)(n * ModelSlack)]);
			sectionCap[slot] = (uint)w.Count;
		}
		w[(int)ModelHeader.Models] = (uint)catalogModels;
		w[(int)ModelHeader.Placeable] = (uint)placeable.Length;
		Section((uint)ModelHeader.PlaceableList, placeable.Select(i => (uint)i));
		Section((uint)ModelHeader.Records, lib.Models.SelectMany(Record));
		Section((uint)ModelHeader.Materials, lib.Materials.SelectMany(Material));
		Section((uint)ModelHeader.Cells, lib.CellTable);
		for (int p = 0; p < 2; p++)
		{
			Section(PoolSlot(p, PoolSection.Bricks), Words(lib.Pools[p].Bricks));
			Section(PoolSlot(p, PoolSection.CellRefs), lib.Pools[p].CellRefs);
			Section(PoolSlot(p, PoolSection.CellTails), Words(lib.Pools[p].CellTails));
		}
		var bytes = new byte[w.Count * 4];
		Buffer.BlockCopy(w.ToArray(), 0, bytes, 0, bytes.Length);
		return bytes;
	}

	// The whole library for a new buffer (loaded on first use); pending uploads are part of it.
	public byte[] Pack()
	{
		lock (gate)
		{
			if (library == null) Load();
			var data = Pack(library, Placeable, CatalogModels, sections, caps);
			uploads.Clear();
			(gpuModels, composeFull) = (library.Models.Count, false);
			return data;
		}
	}

	void Load()
	{
		string dir = ProjectSettings.GlobalizePath("res://" + ContentPaths.ModelLibrary);
		library = new ModelLibrary();
		var catalog = new List<CatalogEntry<ModelCategory>>();
		if (File.Exists(Path.Combine(dir, Catalog.ModelLibraryFile)))
		{
			using (var f = File.OpenRead(Path.Combine(dir, Catalog.ModelLibraryFile))) library = ModelLibrary.Load(f);
			catalog = Catalog.Load<ModelCategory>(ProjectSettings.GlobalizePath($"res://{ContentPaths.Mod(BlockId.BaseMod)}/{Catalog.ModelsFile}"));
			if (catalog.Count != library.Models.Count) throw new InvalidDataException($"{Catalog.ModelsFile} doesn't match {Catalog.ModelLibraryFile}");
		}
		else GD.PushWarning("[World] no models, run tools/ContentImport");
		CatalogModels = library.Models.Count;
		foreach (var file in new[] { Catalog.BlockModelLibraryFile, Catalog.ItemModelLibraryFile })
			if (File.Exists(Path.Combine(dir, file)))
			{
				using var f = File.OpenRead(Path.Combine(dir, file));
				var lib = ModelLibrary.Load(f);
				int first = library.Append(lib);
				for (int i = 0; i < lib.Models.Count; i++) blockModels.TryAdd(lib.Models[i].Name, first + i);
			}
			else GD.PushWarning($"[World] no {file}, run tools/PackImport");
		Placeable = catalog.Select((e, i) => (e, i)).Where(x => x.e.Category == ModelCategory.Prop).Select(x => x.i).ToArray();
	}

	public uint[] Looks(VariantLook look)
	{
		lock (gate)
		{
			uint Word((int Model, Rotation24 Rot) m, int weight) => GpuBlockTable.LookWord(m.Model, m.Rot.Value, weight);
			(int, Rotation24)? Model(Format.Look l) => blockModels.TryGetValue(l.Model, out int m) ? (m, Rotation24.FromMinecraft(l.X, l.Y)) : null;
			if (look.Parts.Length == 1)
			{
				int sum = 0;
				return look.Parts[0].Select(l => (Model: Model(l), Weight: l.Weight ?? 1)).Where(x => x.Model != null).Take(BlockFacing.Faces)
					.Select(x => Word(x.Model.Value, sum = Math.Min(sum + x.Weight, GpuBlockTable.LookWeightMax))).ToArray();
			}
			var parts = look.Parts.Select(p => Model(p[0])).Where(m => m != null).Select(m => m.Value).ToArray();
			if (parts.Length <= 1) return parts.Select(m => Word(m, 1)).ToArray();
			string key = string.Join(";", parts.Select(m => $"{m.Item1}@{m.Item2.Value}"));
			if (!composed.TryGetValue(key, out int model) && !composeFull) composed[key] = model = Compose(key, parts);
			return new[] { Word(model < gpuModels && composed.ContainsKey(key) ? (model, new Rotation24(0)) : parts[0], 1) };
		}
	}

	int Compose(string key, (int, Rotation24)[] parts)
	{
		var lib = library;
		int models = lib.Models.Count, mats = lib.Materials.Count, cells = lib.CellTable.Count;
		var pools = lib.Pools.Select(p => (Bricks: p.Bricks.Count, Refs: p.CellRefs.Count, Tails: p.CellTails.Count)).ToArray();
		int m = lib.Compose(key, parts);
		var added = new List<(uint, uint[])>
		{
			(sections[(int)ModelHeader.Records] + (uint)(models * ModelRecWords), Record(lib.Models[m])),
			(sections[(int)ModelHeader.Materials] + (uint)(mats * MaterialWords), lib.Materials.Skip(mats).SelectMany(Material).ToArray()),
			(sections[(int)ModelHeader.Cells] + (uint)cells, lib.CellTable.Skip(cells).ToArray()),
		};
		for (int p = 0; p < 2; p++)
		{
			var pool = lib.Pools[p];
			added.Add((sections[PoolSlot(p, PoolSection.Bricks)] + (uint)(pools[p].Bricks / 4), Words(pool.Bricks, pools[p].Bricks).ToArray()));
			added.Add((sections[PoolSlot(p, PoolSection.CellRefs)] + (uint)pools[p].Refs, pool.CellRefs.Skip(pools[p].Refs).ToArray()));
			int tail = pools[p].Tails / 4 * 4;
			added.Add((sections[PoolSlot(p, PoolSection.CellTails)] + (uint)(tail / 4), Words(pool.CellTails, tail).ToArray()));
		}
		var slots = new[] { (uint)ModelHeader.Records, (uint)ModelHeader.Materials, (uint)ModelHeader.Cells }
			.Concat(Enumerable.Range(0, 2).SelectMany(p => Enum.GetValues<PoolSection>().Select(s => PoolSlot(p, s)))).ToArray();
		if (added.Select((u, i) => u.Item1 + (uint)u.Item2.Length > caps[slots[i]]).Any(x => x))
		{
			composeFull = true;
			GD.PushWarning($"[World] model library full after {composed.Count} composed models, the rest show their first part until a rebuild (ModelStore.ModelSlack)");
			return m;
		}
		uploads.AddRange(added);
		gpuModels = lib.Models.Count;
		return m;
	}

	// Render thread: composed models into the buffer, before the block rows that use them.
	public void Upload(RenderingDevice rd, Rid buffer)
	{
		lock (gate)
		{
			foreach (var (word, data) in uploads)
				if (data.Length > 0) rd.BufferUpdate(buffer, word * 4, (uint)data.Length * 4, MemoryMarshal.AsBytes(data.AsSpan()).ToArray());
			uploads.Clear();
		}
	}
}
