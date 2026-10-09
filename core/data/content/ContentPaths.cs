using System;
using System.IO;

namespace Renesance.Content;

public static class ContentPaths
{
	public const string Assets = "assets", Textures = "assets/textures", Packs = "assets/textures/packs", VoxSources = "assets/models/vox",
		ModelLibrary = "assets/models/library", Structures = "assets/structures";
	public const string SunTexture = "environment/sun", MoonTexture = "environment/moon_phases";
	public static readonly string[] SkyTextures = { SunTexture, MoonTexture };

	public static string Mod(string mod) => $"mods/{mod}";
	public static string TextureFile(string name) => name + ".png";
	public static string TextureMeta(string name) => TextureFile(name) + ".mcmeta";
	public static string FindRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
