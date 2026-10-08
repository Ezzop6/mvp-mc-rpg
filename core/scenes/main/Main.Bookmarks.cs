using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Renesance.Save;

// Snapshot bookmarks (TestCore.cs): the whole state into tests/bookmarks/<name> and back, for the editor, --world, --save-bookmark and test lists (docs/testing.md, Záložky).
public partial class Main
{
	public sealed record PassState(bool Enabled, bool Active, string Variant);

	// Active and Variant are what the passes ran with; the preset and Tune decide them again on load.
	public sealed class RenderState
	{
		public Dictionary<PassId, PassState> Passes { get; set; } = new();
		public bool EffectsOff { get; set; }
		public DebugView DebugView { get; set; }
		public float RenderScale { get; set; }
		public float EffectiveScale { get; set; } = 1;
		public ViewPreset View { get; set; } = ViewPreset.Ultra;
		public bool Lod { get; set; } // levels of detail (docs/v2.md §5.17), off = L0 only to the preset's radius
		public float HorizonM { get; set; } // 0 = the preset's
		public float LodPixels { get; set; }
		public int LodLevels { get; set; } = global::Lod.MaxLevels;
		public int LodRef { get; set; }
		public bool FixedBudget { get; set; }
		public float GenTargetMs { get; set; } = 2;
		public float StreamCpuMs { get; set; } = 3;
		public int EditChunksPerFrame { get; set; } = 64;
		public string Preset { get; set; } // TuneSet.Edit, null = blended by the sun
		public TuneSet.Changes Tune { get; set; }
	}

	RenderState startup; // for a bookmark without Render
	public string BookmarkName { get; private set; } // last loaded or saved

	RenderState CaptureRender() => new()
	{
		Passes = Renderer.Passes.ToDictionary(p => p.Id, p => new PassState(p.Enabled, p.Active, p.Selected)),
		EffectsOff = Pass.EffectsOff, DebugView = Renderer.View, RenderScale = Renderer.RenderScale, EffectiveScale = Renderer.EffectiveScale,
		View = World.View, HorizonM = World.HorizonM, LodPixels = World.LodPixels, Lod = World.LodOn, LodLevels = World.MaxLevels, LodRef = World.LodRef, FixedBudget = World.FixedBudget, GenTargetMs = World.GenTargetMs, StreamCpuMs = World.StreamCpuMs, EditChunksPerFrame = World.EditChunksPerFrame,
		Preset = TuneSet.Edit, Tune = TuneSet.Overrides(),
	};

	// --on / --off win over the bookmark's passes; tests take the effective scale of an auto-scaled bookmark.
	void RestoreRender(RenderState r)
	{
		(World.View, World.FixedBudget, World.GenTargetMs, World.StreamCpuMs, World.EditChunksPerFrame) = (r.View, r.FixedBudget, r.GenTargetMs, r.StreamCpuMs, r.EditChunksPerFrame);
		(World.LodOn, World.HorizonM, World.LodPixels, World.MaxLevels, World.LodRef) = (r.Lod, r.HorizonM, r.LodPixels, r.LodLevels, r.LodRef);
		foreach (var p in Renderer.Passes)
			p.Enabled = p.Info.Required || (argPasses.TryGetValue(p.Id, out var on) ? on : r.Passes.TryGetValue(p.Id, out var s) ? s.Enabled : TuneSet.DefaultEnabled(p));
		Pass.EffectsOff = r.EffectsOff;
		TuneSet.Restore(r.Tune);
		TuneSet.Edit = r.Preset != null && TuneSet.Presets.ContainsKey(r.Preset) ? r.Preset : null;
		foreach (var v in argVariants) SelectVariant(v);
		if (presetPicker != null) FillPresets();
		SetView(r.DebugView);
		Renderer.RenderScale = r.RenderScale > 0 || !testing ? r.RenderScale : r.EffectiveScale;
	}

	public Pose CameraPose
	{
		get
		{
			var (a, l, (yaw, pitch)) = (World.Anchor, World.Camera.GlobalPosition, Player.Look);
			return new Pose(new Vec3I(a.X, a.Y, a.Z), new Vec3(l.X, l.Y, l.Z), yaw, pitch);
		}
	}

	// Into tests/bookmarks/<name>: a new auto-named one for null, else overwritten keeping its note, status and redirect.
	public Snapshot SaveBookmark(string name, string note = null)
	{
		var old = name != null && Snapshot.Exists(name) ? Snapshot.Load(name) : null;
		name ??= Snapshot.FreeName(World.Generator);
		string dir = Path.Combine(Snapshot.Root, name);
		WriteWorld(WorldSave.Exists(dir) ? WorldSave.Open(dir) : WorldSave.Create(Snapshot.Root, name));
		var size = GetWindow().Size;
		var s = new Snapshot
		{
			Name = name, Note = old?.Note ?? note, Created = old?.Created ?? DateTime.Now.ToString("s"), Commit = TestCore.Commit(), Status = old?.Status ?? BookmarkStatus.Open,
			Redirect = old?.Redirect, Camera = CameraPose, ChunkSize = Renesance.World.Coords.ChunkSize, Move = Player.Mode, TimeOfDay = World.TimeOfDay, Day = World.Day, TimePaused = World.TimePaused, Clock = World.Clock, Width = size.X, Height = size.Y,
			Render = JsonSerializer.SerializeToNode(CaptureRender(), TestCore.Json).AsObject(),
		};
		s.Save();
		BookmarkName = name;
		return s;
	}

	// A full load (world, camera, time, window, render state); the editor's own save stays out of it.
	public void LoadBookmark(Snapshot s)
	{
		RestoreRender(s.Render?.Deserialize<RenderState>(TestCore.Json) ?? startup);
		World.Load(WorldSave.Open(s.Dir));
		var c = s.CameraNow;
		World.Anchor = new Vector3I(c.Anchor.X, c.Anchor.Y, c.Anchor.Z);
		World.Camera.GlobalPosition = new Vector3(c.Local.X, c.Local.Y, c.Local.Z);
		Player.Look = (c.Yaw, c.Pitch);
		Player.Mode = s.Move;
		(World.TimeOfDay, World.Day, World.TimePaused, World.Clock) = (s.TimeOfDay, s.Day, s.TimePaused, s.Clock);
		var win = GetWindow();
		if (!testing && win.Mode == Window.ModeEnum.Windowed) win.Size = new Vector2I(s.Width, s.Height);
		if (EditorWorld is { } w) (w.SaveFile, w.Unsaved) = (null, 0);
		BookmarkName = s.Name;
	}

	// The generator with its whole settings (seed included), the player's view and every edit.
	public void WriteWorld(WorldSave save)
	{
		if (World.Registry == null) throw new InvalidOperationException("the world isn't ready");
		var h = save.Header;
		var settings = World.GeneratorInstance.Settings;
		var p = settings != null ? JsonSerializer.SerializeToElement(settings, settings.GetType(), WorldHeader.Json) : World.GeneratorParams;
		h.Dimensions[WorldHeader.DefaultDimension] = new DimensionInfo { Generator = World.Generator, GeneratorVersion = World.GeneratorInstance.Version, Params = p };
		(h.Player, h.TimeOfDay, h.Day) = (CurrentView(), World.TimeOfDay, World.Day);
		save.Save(World.Registry, World.Edits);
	}

	public View CurrentView()
	{
		var p = World.CameraPosition;
		var (yaw, pitch) = Player.Look;
		return new View(WorldHeader.DefaultDimension, new Vec3(p.X, p.Y, p.Z), Mathf.RadToDeg(yaw), Mathf.RadToDeg(pitch));
	}

	public bool NearReady => World.GpuReady && !World.Loading;

	// Flights: the levels of detail of the start (command line), at their preset or the start's.
	public void StartLod(ViewPreset? view, bool? lod) =>
		(World.View, World.LodOn, World.HorizonM, World.LodPixels, World.MaxLevels, World.LodRef) = (view ?? startup.View, lod ?? startup.Lod, startup.HorizonM, startup.LodPixels, startup.LodLevels, startup.LodRef);
	// The world's pools in use, then everything the RenderingDevice holds (VRAM of buffers and textures).
	public Dictionary<string, double> PoolMB
	{
		get
		{
			var mb = Enum.GetValues<WorldPool>().ToDictionary(p => p.ToString(), p => Math.Round(World.PoolBytes(p) / 1048576.0, 1));
			var rd = RenderingServer.GetRenderingDevice();
			mb["DeviceBuffers"] = Math.Round(rd.GetMemoryUsage(RenderingDevice.MemoryType.Buffers) / 1048576.0, 1);
			mb["DeviceTextures"] = Math.Round(rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures) / 1048576.0, 1);
			return mb;
		}
	}

	public bool SetDebugView(string name)
	{
		if (!Enum.TryParse<DebugView>(name, true, out var v)) return false;
		SetView(v);
		return true;
	}
}
