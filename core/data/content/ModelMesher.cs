using System.Collections.Generic;
using Renesance.World;

namespace Renesance.Content;

// A CPU mesh vertex: position in world meters, packed RGBA (r | g<<8 | b<<16 | a<<24, like VoxelMaterial.Rgba),
// and the atlas face (U, V, layer, overlay; layer < 0 = no texture, the colour carries the look).
public readonly record struct MeshVertex(float X, float Y, float Z, uint Rgba, float U = 0, float V = 0, int Layer = -1, int Overlay = -1);

// One full-cube face: atlas layer and overlay (-1 = none), UV quarter turns, and its packed RGBA tint.
public readonly record struct CubeFace(int Layer, int Overlay, int Turns, uint Rgba);

// Outer faces of a micro-voxel model at mip 0, Godot-free; placement mirrors models.glsli (docs/world-format.md).
public static class ModelMesher
{
	const float Block = ModelLibrary.Cell;

	// Face corners of a unit voxel, in the order of Sides: +Z, -Z, +X, -X, +Y, -Y.
	public static readonly int[][][] Quads =
	[
		[[0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]],
		[[1, 0, 0], [0, 0, 0], [0, 1, 0], [1, 1, 0]],
		[[1, 0, 1], [1, 0, 0], [1, 1, 0], [1, 1, 1]],
		[[0, 0, 0], [0, 0, 1], [0, 1, 1], [0, 1, 0]],
		[[0, 1, 1], [1, 1, 1], [1, 1, 0], [0, 1, 0]],
		[[0, 0, 0], [1, 0, 0], [1, 0, 1], [0, 0, 1]],
	];
	public static readonly (int X, int Y, int Z)[] Sides = [new(0, 0, 1), new(0, 0, -1), new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0)];
	public static readonly int[] FaceCode = [5, 4, 1, 0, 3, 2]; // faces.glsli code (axis * 2 + positive) of Sides[d]
	static readonly int[] Triangle = [0, 1, 2, 0, 2, 3];

	// A block-aligned cuboid at (x, y, z) blocks + size, emitting the faces in `faces` (bit d = Sides[d]).
	public static void Box(List<MeshVertex> verts, float x, float y, float z, float sx, float sy, float sz, int faces, uint rgba)
	{
		for (int d = 0; d < 6; d++)
		{
			if ((faces & 1 << d) == 0) continue;
			foreach (int c in Triangle)
			{
				var q = Quads[d][c];
				verts.Add(new MeshVertex((x + q[0] * sx) * Coords.BlockMeters, (y + q[1] * sy) * Coords.BlockMeters, (z + q[2] * sz) * Coords.BlockMeters, rgba));
			}
		}
	}

	// A one-block cube cell with per-face textures, emitting the faces in `faces` (bit d = Sides[d]); UVs match blocks.glsli face_uv.
	public static void Cube(List<MeshVertex> verts, float x, float y, float z, int faces, IReadOnlyList<CubeFace> faceTex)
	{
		for (int d = 0; d < 6; d++)
		{
			if ((faces & 1 << d) == 0) continue;
			var f = faceTex[d];
			int code = FaceCode[d];
			foreach (int c in Triangle)
			{
				var q = Quads[d][c];
				var (u, v) = FaceUV(code, q[0], q[1], q[2], f.Turns);
				verts.Add(new MeshVertex((x + q[0]) * Coords.BlockMeters, (y + q[1]) * Coords.BlockMeters, (z + q[2]) * Coords.BlockMeters, f.Rgba, u, v, f.Layer, f.Overlay));
			}
		}
	}

	// blocks.glsli face_uv on a unit face: corner (x, y, z) in 0..1, row 0 at the top, turned by the face's quarter turns.
	static (float U, float V) FaceUV(int code, float x, float y, float z, int turns)
	{
		bool pos = (code & 1) != 0;
		int axis = code >> 1;
		float u, v;
		if (axis == 1) { u = x; v = pos ? z : 1 - z; }
		else if (axis == 0) { u = pos ? 1 - z : z; v = 1 - y; }
		else { u = pos ? x : 1 - x; v = 1 - y; }
		return turns switch { 1 => (v, 1 - u), 2 => (1 - u, 1 - v), 3 => (1 - v, u), _ => (u, v) };
	}

	// Appends `model`'s outer faces (a face shared with a solid model voxel is skipped) at `rot`, cell corner (cx, cy, cz) blocks;
	// `alpha` 0..1 scales each voxel's colour alpha; false when the vertex cap would be passed.
	public static bool Append(List<MeshVertex> verts, ModelLibrary lib, int model, Rotation24 rot, int cx, int cy, int cz, float alpha, int maxVerts)
	{
		var m = lib.Models[model];
		int sx = m.SizeX, sy = m.SizeY, sz = m.SizeZ;
		var solid = new ushort[sx * sy * sz];
		for (int z = 0; z < sz; z++)
			for (int y = 0; y < sy; y++)
				for (int x = 0; x < sx; x++)
					solid[x + sx * (y + sy * z)] = lib.GetVoxel(model, x, y, z);
		var (p, s) = (rot.Perm, rot.Signs);
		int[] axis = [p.X, p.Y, p.Z], sign = [s.X, s.Y, s.Z], cells = [m.CellsX, m.CellsY, m.CellsZ];
		int[] shift = [sign[0] < 0 ? cells[0] : 0, sign[1] < 0 ? cells[1] : 0, sign[2] < 0 ? cells[2] : 0];
		float[] o = new float[3], w = new float[3];
		for (int z = 0; z < sz; z++)
			for (int y = 0; y < sy; y++)
				for (int x = 0; x < sx; x++)
				{
					ushort v = solid[x + sx * (y + sy * z)];
					if (v == 0) continue;
					uint rgba = WithAlpha(lib.GetMaterial(model, v).Rgba, alpha);
					for (int d = 0; d < 6; d++)
					{
						int nx = x + Sides[d].X, ny = y + Sides[d].Y, nz = z + Sides[d].Z;
						if ((uint)nx < (uint)sx && (uint)ny < (uint)sy && (uint)nz < (uint)sz && solid[nx + sx * (ny + sy * nz)] != 0) continue;
						if (verts.Count + 6 > maxVerts) return false;
						foreach (int c in Triangle)
						{
							var q = Quads[d][c];
							o[0] = (x + q[0]) / Block; o[1] = (y + q[1]) / Block; o[2] = (z + q[2]) / Block;
							for (int i = 0; i < 3; i++) w[axis[i]] = sign[i] * (o[i] - shift[i]);
							verts.Add(new MeshVertex((cx + w[0]) * Coords.BlockMeters, (cy + w[1]) * Coords.BlockMeters, (cz + w[2]) * Coords.BlockMeters, rgba));
						}
					}
				}
		return true;
	}

	// Packed RGBA with its alpha byte scaled by `alpha` 0..1.
	static uint WithAlpha(uint rgba, float alpha) => (rgba & 0x00FFFFFFu) | (uint)(byte)((rgba >> 24) * alpha) << 24;

	// A cube face as a Res x Res grid of vertex-coloured texels from the atlas (no texture sampling in the shader), mirroring albedo_at:
	// the overlay's tinted texel where it is opaque, else the base (untinted when the face has an overlay); false past the vertex cap.
	public static bool TexelCube(List<MeshVertex> verts, int x, int y, int z, int faces, IReadOnlyList<CubeFace> faceTex, byte[] texels, int stride, int texSize, int res, int maxVerts)
	{
		for (int d = 0; d < 6; d++)
		{
			if ((faces & 1 << d) == 0) continue;
			var f = faceTex[d];
			if (f.Layer < 0) continue;
			int code = FaceCode[d];
			var q0 = Quads[d][0]; var q1 = Quads[d][1]; var q3 = Quads[d][3];
			var (u0, v0) = FaceUV(code, q0[0], q0[1], q0[2], f.Turns);
			var (u1, v1) = FaceUV(code, q1[0], q1[1], q1[2], f.Turns);
			var (u3, v3) = FaceUV(code, q3[0], q3[1], q3[2], f.Turns);
			float ox = q0[0], oy = q0[1], oz = q0[2];
			float ux = q1[0] - q0[0], uy = q1[1] - q0[1], uz = q1[2] - q0[2];
			float vx = q3[0] - q0[0], vy = q3[1] - q0[1], vz = q3[2] - q0[2];
			float shade = d == 4 ? 1f : d == 5 ? 0.5f : 0.8f;
			for (int i = 0; i < res; i++)
				for (int j = 0; j < res; j++)
				{
					if (verts.Count + 6 > maxVerts) return false;
					float ca = (i + 0.5f) / res, cb = (j + 0.5f) / res;
					float tu = u0 + (u1 - u0) * ca + (u3 - u0) * cb, tv = v0 + (v1 - v0) * ca + (v3 - v0) * cb;
					int tx = Clamp((int)(tu * texSize), texSize), ty = Clamp((int)(tv * texSize), texSize);
					uint rgba = Shade(AtlasTexel(texels, stride, f.Layer, f.Overlay, tx, ty, texSize, f.Rgba), shade);
					Quad(verts, x, y, z, ox, oy, oz, ux, uy, uz, vx, vy, vz, (float)i / res, (float)(i + 1) / res, (float)j / res, (float)(j + 1) / res, rgba);
				}
		}
		return true;
	}

	static int Clamp(int v, int size) => v < 0 ? 0 : v >= size ? size - 1 : v;

	// Minecraft's face shading: the top stays, the bottom darkens most; makes a face read as a block, not a flat side.
	static uint Shade(uint rgba, float s) => (uint)((rgba & 0xFF) * s) | (uint)(((rgba >> 8) & 0xFF) * s) << 8 | (uint)(((rgba >> 16) & 0xFF) * s) << 16 | (rgba & 0xFF000000u);

	// One texel of a face: the overlay (tinted) where it is opaque, else the base (untinted when the face has an overlay, else tinted).
	static uint AtlasTexel(byte[] t, int stride, int layer, int overlay, int tx, int ty, int size, uint tint)
	{
		byte tr = (byte)tint, tg = (byte)(tint >> 8), tb = (byte)(tint >> 16);
		int o = layer * stride + (ty * size + tx) * 4;
		if (overlay >= 0)
		{
			int oo = overlay * stride + (ty * size + tx) * 4;
			if (t[oo + 3] >= 128) return (uint)(t[oo] * tr / 255) | (uint)(t[oo + 1] * tg / 255) << 8 | (uint)(t[oo + 2] * tb / 255) << 16 | 0xFF000000u;
			return (uint)t[o] | (uint)t[o + 1] << 8 | (uint)t[o + 2] << 16 | 0xFF000000u;
		}
		return (uint)(t[o] * tr / 255) | (uint)(t[o + 1] * tg / 255) << 8 | (uint)(t[o + 2] * tb / 255) << 16 | 0xFF000000u;
	}

	// A quad on a face: corner (a, b) maps to origin + U*a + V*b in the cell, times the block size.
	static void Quad(List<MeshVertex> verts, int x, int y, int z, float ox, float oy, float oz, float ux, float uy, float uz, float vx, float vy, float vz, float aL, float aR, float bD, float bU, uint rgba)
	{
		MeshVertex P(float a, float b) => new MeshVertex((x + ox + ux * a + vx * b) * Coords.BlockMeters, (y + oy + uy * a + vy * b) * Coords.BlockMeters, (z + oz + uz * a + vz * b) * Coords.BlockMeters, rgba);
		var p0 = P(aL, bD); var p1 = P(aR, bD); var p2 = P(aR, bU); var p3 = P(aL, bU);
		verts.Add(p0); verts.Add(p1); verts.Add(p2);
		verts.Add(p0); verts.Add(p2); verts.Add(p3);
	}
}
