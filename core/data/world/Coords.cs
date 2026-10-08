using Coord = System.Int32; // ±2^31 blocks = ±1.07 million km; Int64 changes the region format (bump Region.Version)

namespace Renesance.World;

// Integer world coordinates. Divisions are floor-correct for negatives.
public readonly record struct BlockPos(Coord X, Coord Y, Coord Z)
{
	public ChunkPos Chunk => new(Coords.FloorDiv(X, Coords.ChunkSize), Coords.FloorDiv(Y, Coords.ChunkSize), Coords.FloorDiv(Z, Coords.ChunkSize));
	public int LocalIndex => (int)(Coords.FloorMod(X, Coords.ChunkSize) + Coords.ChunkSize * (Coords.FloorMod(Y, Coords.ChunkSize) + Coords.ChunkSize * Coords.FloorMod(Z, Coords.ChunkSize)));
	public override string ToString() => $"({X}, {Y}, {Z})";
}

public readonly record struct ChunkPos(Coord X, Coord Y, Coord Z)
{
	public RegionPos Region => new(Coords.FloorDiv(X, Coords.RegionSize), Coords.FloorDiv(Y, Coords.RegionSize), Coords.FloorDiv(Z, Coords.RegionSize));
	public int LocalIndex => (int)(Coords.FloorMod(X, Coords.RegionSize) + Coords.RegionSize * (Coords.FloorMod(Y, Coords.RegionSize) + Coords.RegionSize * Coords.FloorMod(Z, Coords.RegionSize)));
	public BlockPos Origin => new(X * Coords.ChunkSize, Y * Coords.ChunkSize, Z * Coords.ChunkSize);
	public override string ToString() => $"({X}, {Y}, {Z})";
}

public readonly record struct RegionPos(Coord X, Coord Y, Coord Z)
{
	public ChunkPos ChunkAt(int localIndex) => new(
		X * Coords.RegionSize + localIndex % Coords.RegionSize,
		Y * Coords.RegionSize + localIndex / Coords.RegionSize % Coords.RegionSize,
		Z * Coords.RegionSize + localIndex / (Coords.RegionSize * Coords.RegionSize));
}

public static class Coords
{
	public const int ChunkSize = 25;  // blocks per edge
	public const int RegionSize = 5;  // chunks per edge
	public const float BlockMeters = 0.5f;
	public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;
	public const int RegionVolume = RegionSize * RegionSize * RegionSize;

	public static Coord FloorDiv(Coord a, Coord d) { Coord q = a / d; return (a % d != 0 && (a < 0) != (d < 0)) ? q - 1 : q; }
	public static Coord FloorMod(Coord a, Coord d) { Coord m = a % d; return m < 0 ? m + d : m; }
}
