using Renesance.Import;
using Renesance.Content;
using static Renesance.Content.TemplateCategory;

static class BaseBlocks
{
	public const string Grass = "base:grass_block", SnowBlock = "base:snow_block", Log = "base:oak_log", BirchLog = "base:birch_log",
		Leaves = "base:oak_leaves", AutumnLeaves = "base:cherry_leaves", CoalOre = "base:coal_ore", IronOre = "base:iron_ore", GoldOre = "base:gold_ore",
		DiamondOre = "base:diamond_ore", Water = "base:water", Lamp = "base:glowstone", Glass = "base:glass", GlassCyan = "base:cyan_stained_glass";
}

static class BaseMapping
{
	// decorative or two-tone textures: speckled/striped walls when picked by average colour
	public static readonly HashSet<string> TwoTone = new()
	{
		BaseBlocks.DiamondOre, BaseBlocks.CoalOre, BaseBlocks.IronOre, BaseBlocks.GoldOre, BaseBlocks.Grass,
		BaseBlocks.Log, BaseBlocks.BirchLog, // bark only via the tree rules
	};

	// Colour -> block for a category: special materials by hue rules, everything else by nearest texture colour.
	public static Func<uint, string> Mapper(BlockColors colors, TemplateCategory category) => rgba =>
	{
		var (h, s, v) = BlockColors.Hsv(rgba);
		bool vegetation = category is Tree or Bush;
		bool windows = category is House or Prop or Light or Vehicle; // big stone blues in monuments/scenes stay stone
		if (vegetation)
		{
			if (h >= 65 && h <= 170 && s > 0.2f && v > 0.15f) return BaseBlocks.Leaves;
			if (h < 60 && s > 0.3f && v < 0.6f) return BaseBlocks.Log;
			if (h < 60 && s > 0.45f && v >= 0.6f) return BaseBlocks.AutumnLeaves;
			if (s < 0.12f && v > 0.85f) return BaseBlocks.SnowBlock;
		}
		if (category is Scene or Tile or Monument && h >= 185 && h <= 235 && s > 0.35f && v > 0.35f) return BaseBlocks.Water;
		if (category is Light && v > 0.85f && (s < 0.25f || h >= 25 && h <= 65)) return BaseBlocks.Lamp; // bright white/warm parts glow
		if (windows && h >= 170 && h <= 230 && v > 0.7f && s > 0.12f && s < 0.6f) return s > 0.3f ? BaseBlocks.GlassCyan : BaseBlocks.Glass;
		return colors.Nearest(rgba);
	};
}
