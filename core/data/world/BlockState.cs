namespace Renesance.World;

// 32-bit block state: type (16) | state data (16, meaning defined by the type's params).
public readonly record struct BlockState(uint Raw)
{
	public static readonly BlockState Air = default;
	public BlockState(ushort type, ushort data) : this((uint)type << 16 | data) { }
	public ushort Type => (ushort)(Raw >> 16);
	public ushort Data => (ushort)Raw;
}

// Fluid in a cell beside its block: liquid block type (registry id, 0 = none) and level (0 = full, 1–7 as the liquid's level state).
public readonly record struct FluidState(ushort Type, byte Level)
{
	public static readonly FluidState None = default;
}
