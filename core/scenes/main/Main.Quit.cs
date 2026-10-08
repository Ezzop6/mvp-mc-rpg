using Godot;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renesance.Save;

// Quit, the session log and the crash save (docs/testing.md, Ukončení a záznam sezení).
public partial class Main
{
	const string AutosaveName = "autosave";
	static string CrashSaveRoot => ProjectSettings.GlobalizePath("user://crash-save");
	static string SessionLogPath => Path.Combine(Path.GetDirectoryName(ProjectSettings.GlobalizePath((string)ProjectSettings.GetSetting("debug/file_logging/log_path"))), "session.log");
	bool quitting, crashSaved;
	CrashLogger crashLogger;

	EditorWorld EditorWorld => GetNodeOrNull<EditorWorld>("EditorWorld");

	void InitQuit()
	{
		GetTree().AutoAcceptQuit = false;
		OS.AddLogger(crashLogger = new CrashLogger { Main = this });
		AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
		TaskScheduler.UnobservedTaskException += OnUnobserved;
	}

	void OnUnhandled(object _, UnhandledExceptionEventArgs e) => OnThreadException(e.ExceptionObject);
	void OnUnobserved(object _, UnobservedTaskExceptionEventArgs e)
	{
		GD.PrintErr(e.Exception);
		OnThreadException(e.Exception);
	}

	// A .NET thread outside Godot's callbacks (Godot prints an unhandled one itself): the main thread logs and crash-saves while this one waits, the process ends when an unhandled one's handler returns.
	void OnThreadException(object exception)
	{
		var done = new ManualResetEventSlim();
		Callable.From(() => { OnException(exception.ToString()); done.Set(); }).CallDeferred();
		done.Wait(5000);
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest) QuitByUser("window closed");
	}

	// Menu Quit, Ctrl+Q, window close: unsaved editor edits go to the loaded world (a bookmark or a new world: user://saves/autosave) first.
	void QuitByUser(string reason)
	{
		if (EditorWorld is { Unsaved: > 0 } w)
			try
			{
				bool own = w.SaveFile != null && Path.GetDirectoryName(w.SaveFile.Dir) != Snapshot.Root;
				string dir = Path.Combine(EditorWorld.SavesRoot, AutosaveName);
				w.Save(own ? w.SaveFile : WorldSave.Exists(dir) ? WorldSave.Open(dir) : WorldSave.Create(EditorWorld.SavesRoot, AutosaveName));
				reason += $", edits saved to {w.SaveFile.Dir}";
			}
			catch (Exception e) { reason += $", saving the edits failed: {e.Message}"; }
		Quit(0, reason);
	}

	// The RenderingDevice calls pending readbacks (C# callables) on its last frames, after .NET is gone: stop issuing them and let every frame in flight finish first.
	public void Quit(int code, string reason)
	{
		if (quitting) return;
		quitting = true;
		WriteSessionLog($"{reason} (exit {code})");
		Renderer.SetProcess(false);
		EditorWorld?.SetProcess(false);
		int inFlight = (int)ProjectSettings.GetSetting("rendering/rendering_device/vsync/frame_queue_size") + 1;
		for (int i = 0; i < inFlight; i++) RenderingServer.ForceDraw();
		OS.RemoveLogger(crashLogger);
		AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
		TaskScheduler.UnobservedTaskException -= OnUnobserved;
		GetTree().Quit(code);
	}

	void WriteSessionLog(string reason)
	{
		try
		{
			var p = World.GeneratorParams is { } g ? g.GetRawText() : "default";
			string scene = EditorWorld?.SaveFile?.Name ?? (BookmarkName != null ? $"bookmark {BookmarkName}" : "new world");
			string frame = frameMs.Any(m => m > 0) ? string.Create(CultureInfo.InvariantCulture, $"frame ms last {frameMs[(frameSample + FrameSamples - 1) % FrameSamples]:0.0}, P99 {frameMs.Order().ElementAt(FrameSamples * 99 / 100):0.0}") : "no frame";
			string text = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {reason}\nscene {scene}, generator {World.Generator} {p}\n{frame}\n" +
				string.Join("\n", (hud?.Text ?? "").Split('\n').SkipLast(1)) + "\n";
			if (File.Exists(SessionLogPath) && new FileInfo(SessionLogPath).Length > 1 << 20) File.Delete(SessionLogPath);
			File.AppendAllText(SessionLogPath, text);
		}
		catch (Exception e) { GD.Print($"session log: {e.Message}"); }
	}

	// Main thread, once per session: the world's edits into user://crash-save/<time>, the editor keeps its own save file.
	internal void OnException(string message)
	{
		if (crashSaved || quitting) return;
		crashSaved = true;
		string saved = "no edits";
		if (EditorWorld is { } w && World.Edits.Count > 0)
			try
			{
				var (file, unsaved) = (w.SaveFile, w.Unsaved);
				var save = WorldSave.Create(CrashSaveRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
				w.Save(save);
				(w.SaveFile, w.Unsaved) = (file, unsaved);
				saved = $"edits saved to {save.Dir}";
			}
			catch (Exception e) { saved = $"crash save failed: {e.Message}"; }
		WriteSessionLog($"exception, {saved}: {message.Split('\n').FirstOrDefault(CrashLogger.ExceptionName.IsMatch) ?? message.Split('\n')[0]}");
	}
}

// Any thread: a C# exception reported through Godot's error stream hands its first line to Main on the main thread.
public partial class CrashLogger : Logger
{
	internal static readonly Regex ExceptionName = new(@"\b(System|Godot)\.[\w.]*Exception\b");
	public Main Main;

	public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
	{
		string text = $"{code}\n{rationale}";
		if (ExceptionName.IsMatch(text)) Callable.From(() => Main.OnException(text.Trim())).CallDeferred();
	}
}
