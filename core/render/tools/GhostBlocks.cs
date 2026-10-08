using Godot;
using System;
using System.Collections.Generic;
using Renesance.Content;
using Renesance.Tools;
using Renesance.World;
using Format = Renesance.Content.Format;

// Depth-tested translucent ghost of the clipboard: a MeshInstance3D under World, not a pass (docs/v2.md §5.13).
public partial class GhostBlocks : MeshInstance3D
{
	public delegate ArrayMesh GhostMeshBuilder(Clipboard clipboard);
	public GhostMeshBuilder Builder;

	const int MaxVoxels = 4_000_000;      // ponytail: past this many model voxels the rest of the models show as boxes
	const int MaxVerts = 1_000_000;       // ponytail: stop adding geometry past this, keep the editor responsive
	const float Alpha = 1f; // opaque while tuning the filtering (the user's request)
	const uint Tint = 0xFFFFBF66;         // (0.4, 0.75, 1) packed RGBA (VoxelMaterial order), alpha here stays 255 (the material holds it)

	World world;
	Clipboard built;
	ShaderMaterial atlasMaterial;

	public override void _Ready()
	{
		CastShadow = ShadowCastingSetting.Off;
		Visible = false;
		world = GetParent() as World;
		Builder = Build;
	}

	// Show the clipboard with corner A on cornerA; originBlock rebases it as the world moves with the camera.
	public void Display(Clipboard clipboard, BlockPos cornerA, Vector3I originBlock)
	{
		if (clipboard == null) { Clear(); return; }
		if (!ReferenceEquals(clipboard, built)) { Mesh = Builder(clipboard); built = clipboard; }
		var min = new BlockPos(cornerA.X - clipboard.Anchor.X, cornerA.Y - clipboard.Anchor.Y, cornerA.Z - clipboard.Anchor.Z);
		Position = (new Vector3(min.X, min.Y, min.Z) - new Vector3(originBlock.X, originBlock.Y, originBlock.Z)) * Voxel.BlockSize;
		MaterialOverride ??= Material();
		var sun = Renderer.SunLight;
		atlasMaterial.SetShaderParameter("light", new Vector3(sun.R, sun.G, sun.B));
		Visible = true;
	}

	// Nothing shown: no mesh, no geometry, no work.
	public void Clear()
	{
		if (!Visible && built == null) return;
		Visible = false;
		Mesh = null;
		built = null;
	}

	// Unshaded, alpha blended, depth-tested against the world; every face carries its colour in the vertex colours (like the model voxels).
	ShaderMaterial Material()
	{
		if (atlasMaterial != null) return atlasMaterial;
		atlasMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://core/render/tools/ghost.gdshader") };
		atlasMaterial.SetShaderParameter("alpha", Alpha);
		return atlasMaterial;
	}

	// Cubes as their textured faces, models as their translucent mip-0 voxel faces; past the model budget a model cell falls back to a box.
	ArrayMesh Build(Clipboard c)
	{
		var verts = new List<MeshVertex>();
		if (world?.Registry != null) Detailed(verts, c, world.Registry);
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		foreach (var v in verts)
		{
			st.SetColor(Color.Color8((byte)v.Rgba, (byte)(v.Rgba >> 8), (byte)(v.Rgba >> 16), (byte)(v.Rgba >> 24)));
			st.SetUV(new Vector2(v.U, v.V));
			st.SetUV2(new Vector2(v.Layer, v.Overlay));
			st.AddVertex(new Vector3(v.X, v.Y, v.Z));
		}
		return st.Commit();
	}

	// Air is skipped: cubes show their real faces, models within budget their voxel faces, models past the budget a tinted box.
	void Detailed(List<MeshVertex> verts, Clipboard c, BlockRegistry reg)
	{
		var lib = world.Models.Library;
		var tex = world?.Textures;
		long voxels = 0;
		bool models = true;
		for (int z = 0; z < c.Size.Z; z++)
			for (int y = 0; y < c.Size.Y; y++)
				for (int x = 0; x < c.Size.X; x++)
				{
					var state = c.At(x, y, z);
					if (state == BlockState.Air) continue;
					var def = reg.Get(state);
					var look = BlockLooks.Resolve(def, state);
					if (look.Empty) continue;
					int faces = Faces(c, x, y, z);
					if (look.Cube != null)
					{
						ModelMesher.TexelCube(verts, x, y, z, faces, CubeFaces(look.Cube, BlockFacing.Map(def, state), def, tex), tex.Texels, GpuBlockTable.TexelsPerLayer * 4, GpuBlockTable.TexSize, 16, MaxVerts);
						if (verts.Count > MaxVerts) return;
						continue;
					}
					if (models)
					{
						var words = world.Models.Looks(look);
						if (words.Length > 0)
						{
							uint word = words[0];
							int model = (int)(word & ((1 << GpuBlockTable.LookModelBits) - 1));
							var info = lib.Models[model];
							voxels += (long)info.SizeX * info.SizeY * info.SizeZ;
							var rot = new Rotation24((byte)(word >> GpuBlockTable.LookRotShift & 31));
							if (voxels <= MaxVoxels && ModelMesher.Append(verts, lib, model, rot, x, y, z, 1f, MaxVerts)) continue;
							models = false;
						}
					}
					ModelMesher.Box(verts, x, y, z, 1, 1, 1, faces, Tint);
					if (verts.Count > MaxVerts) return;
				}
	}

	// Per Sides[d]: the world face's looked-up layer/overlay, its UV quarter turns, and its tint colour where the face asks for one.
	static CubeFace[] CubeFaces(Format.Faces cube, (Format.FaceDir Face, int Turns)[] map, BlockDef def, GpuBlockTable.Textures tex)
	{
		uint tint = TintRgba(def.Entry?.Tint);
		var faces = new CubeFace[6];
		for (int d = 0; d < 6; d++)
		{
			var (dir, turns) = map[ModelMesher.FaceCode[d]];
			var f = cube.Get(dir);
			int layer = f?.Texture != null && tex != null && tex.Index.TryGetValue(f.Texture, out var li) ? li : -1;
			int overlay = layer >= 0 && f.Overlay != null && tex.Index.TryGetValue(f.Overlay, out var oi) ? oi : -1;
			faces[d] = new CubeFace(layer, overlay, ((f?.Rotation ?? 0) / 90 + turns) & 3, f?.Tint == true && def.Entry?.Tint != null ? tint : 0xFFFFFFFFu);
		}
		return faces;
	}

	// The entry tint ("#rrggbb") as packed RGBA (VoxelMaterial order, sRGB bytes like the model voxel colours); white when there is no colour.
	static uint TintRgba(Format.Tint tint)
	{
		if (tint?.Color is not { } hex) return 0xFFFFFFFFu;
		var c = BlockRegistry.Rgb(hex);
		return (uint)(byte)(c[0] * 255) | (uint)(byte)(c[1] * 255) << 8 | (uint)(byte)(c[2] * 255) << 16 | 0xFF000000u;
	}

	// The outer faces of the cell: a face is drawn when its neighbour is air.
	static int Faces(Clipboard c, int x, int y, int z)
	{
		int faces = 0;
		for (int d = 0; d < 6; d++)
		{
			var s = ModelMesher.Sides[d];
			if (c.At(x + s.X, y + s.Y, z + s.Z) == BlockState.Air) faces |= 1 << d;
		}
		return faces;
	}
}
