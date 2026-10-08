using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Renesance.Save;

public sealed class DimensionInfo
{
	public string Generator { get; set; } = "base";
	public int GeneratorVersion { get; set; }
	public JsonElement? Params { get; set; }
}

// Metres; look in degrees (yaw around up, pitch up positive).
public record struct View(string Dimension, Vec3 Position, float Yaw, float Pitch);

public record struct Vec3(float X, float Y, float Z);

public record struct Vec3I(int X, int Y, int Z);

// A camera view that reproduces a look: Preset = tuning preset name or AutoPreset; Off / On = render options against their defaults; Test = the test runner visits it.
public sealed class Bookmark
{
	public const string AutoPreset = "auto", DefaultResolution = "4k";
	public static readonly IReadOnlyDictionary<string, (int Width, int Height)> Resolutions = new Dictionary<string, (int, int)>
	{
		[DefaultResolution] = (3840, 2160), ["1440p"] = (2560, 1440), ["1080p"] = (1920, 1080), ["720p"] = (1280, 720),
	};
	public string Name { get; set; }
	public View View { get; set; }
	public float TimeOfDay { get; set; }
	public int Day { get; set; }
	public string Preset { get; set; } = AutoPreset;
	public string[] Off { get; set; }
	public string[] On { get; set; }
	public string DebugView { get; set; } // DebugView name, null = none
	public bool Test { get; set; }
	public string Resolution { get; set; } = DefaultResolution;
	public string Note { get; set; }
}

// world.json: everything needed to regenerate untouched terrain and to remap saved ids, plus player state and bookmarks.
public sealed class WorldHeader
{
	public const string FileName = "world.json", DefaultDimension = "overworld";
	public const int CurrentFormat = 1;
	public string Name { get; set; }
	public int Format { get; set; } = CurrentFormat;
	public int? ChunkSize { get; set; }  // blocks per chunk edge the regions were written with, null = 25 (saved before the field)
	public int? RegionSize { get; set; } // chunks per region edge, null = 5
	public Dictionary<string, DimensionInfo> Dimensions { get; set; } = new() { [DefaultDimension] = new() };
	public Dictionary<string, ushort> Blocks { get; set; } = new();   // block name -> id used in region files
	public Dictionary<string, uint> Models { get; set; } = new();     // model name -> id used in region files
	public View Player { get; set; } = new(DefaultDimension, default, 0, 0);
	public float TimeOfDay { get; set; }
	public int Day { get; set; }
	public List<Bookmark> Bookmarks { get; set; } = new();

	public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

	public void Save(string path)
	{
		File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
		File.Move(path + ".tmp", path, overwrite: true);
	}

	public static WorldHeader Load(string path)
	{
		var j = JsonNode.Parse(File.ReadAllText(path)).AsObject();
		if (!j.ContainsKey(nameof(Dimensions)))
		{
			j.Remove(nameof(DimensionInfo.GeneratorVersion), out var version);
			j.Remove("GeneratorParams", out var parameters);
			j[nameof(Dimensions)] = new JsonObject { [DefaultDimension] = new JsonObject { [nameof(DimensionInfo.GeneratorVersion)] = version, [nameof(DimensionInfo.Params)] = parameters } };
		}
		return j.Deserialize<WorldHeader>(Json);
	}
}
