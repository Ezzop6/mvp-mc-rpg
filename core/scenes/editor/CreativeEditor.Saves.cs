using Godot;
using System;
using System.IO;
using System.Linq;
using Renesance.Save;

// Save, Load and Bookmarks pages of the editor menu, built in code (docs/world-format.md, Uložené světy; docs/testing.md, Záložky).
public partial class CreativeEditor
{
	Control dialog;
	VBoxContainer page;
	Label pageStatus;

	void BuildDialog()
	{
		dialog = new Control { Visible = false };
		dialog.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f) };
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		var center = new CenterContainer();
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		var panel = new PanelContainer();
		page = new VBoxContainer { CustomMinimumSize = new Vector2(720, 0) };
		page.AddThemeConstantOverride("separation", 8);
		panel.AddChild(page);
		center.AddChild(panel);
		dialog.AddChild(dim);
		dialog.AddChild(center);
		AddChild(dialog);
		GetNode<Button>("Menu/Center/Box/Save").Pressed += () => ShowPage(SavePage);
		GetNode<Button>("Menu/Center/Box/Load").Pressed += () => ShowPage(LoadPage);
		GetNode<Button>("Menu/Center/Box/Bookmarks").Pressed += () => ShowPage(BookmarksPage);
		var quit = new Button { Text = "Quit" };
		quit.Pressed += Quit;
		GetNode("Menu/Center/Box").AddChild(quit);
	}

	// As the window's close button: Main saves the edits and drains the GPU first.
	void Quit() => GetTree().Root.PropagateNotification((int)NotificationWMCloseRequest);

	void ShowPage(Action build)
	{
		foreach (var c in page.GetChildren()) { page.RemoveChild(c); c.QueueFree(); }
		build();
		pageStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		page.AddChild(pageStatus);
		menu.Hide();
		dialog.Show();
	}

	T Add<T>(T control) where T : Control { page.AddChild(control); return control; }

	HBoxContainer Row(params (string text, Action pressed)[] buttons)
	{
		var row = Add(new HBoxContainer());
		foreach (var (text, pressed) in buttons)
		{
			var b = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			b.Pressed += () => Try(pressed);
			row.AddChild(b);
		}
		return row;
	}

	void Try(Action a)
	{
		try { a(); }
		catch (Exception e) { pageStatus.Text = e.Message; }
	}

	void Back() { dialog.Hide(); menu.Show(); }

	// Overwriting another existing world asks twice.
	void SavePage()
	{
		Add(new Label { Text = "Save world" });
		var name = Add(new LineEdit { Text = World.SaveFile?.Name ?? "", PlaceholderText = "World name" });
		string confirm = null;
		Row(("Save", () =>
		{
			if (!WorldSave.ValidName(name.Text)) throw new ArgumentException($"'{name.Text}' is not a valid folder name");
			string dir = Path.Combine(EditorWorld.SavesRoot, name.Text);
			if (WorldSave.Exists(dir) && dir != World.SaveFile?.Dir && confirm != dir)
			{
				confirm = dir;
				pageStatus.Text = $"{dir} exists: press Save again to overwrite it";
				return;
			}
			World.Save(WorldSave.Exists(dir) ? WorldSave.Open(dir) : WorldSave.Create(EditorWorld.SavesRoot, name.Text));
			pageStatus.Text = $"Saved {World.Edits.Count} edited blocks to {dir}";
		}), ("Back", Back));
	}

	void LoadPage()
	{
		Add(new Label { Text = "Load world" });
		var list = Add(new ItemList { CustomMinimumSize = new Vector2(0, 420) });
		var name = Add(new LineEdit { PlaceholderText = "New name (Rename)" });
		string confirm = null;
		void Fill()
		{
			list.Clear();
			foreach (var w in WorldSave.List(EditorWorld.SavesRoot)) list.SetItemMetadata(list.AddItem(w), Path.Combine(EditorWorld.SavesRoot, w));
		}
		string Selected() => list.GetSelectedItems() is [var i] ? list.GetItemMetadata(i).AsString() : throw new InvalidOperationException("Select a world first");
		void Load()
		{
			World.Load(WorldSave.Open(Selected()));
			ClearSelection();
			CloseUi();
		}
		list.ItemSelected += i => { name.Text = Path.GetFileName(list.GetItemMetadata((int)i).AsString()); confirm = null; };
		list.ItemActivated += _ => Try(Load);
		Row(("Load", Load), ("Rename", () =>
		{
			string dir = Selected();
			var renamed = WorldSave.Rename(Path.GetDirectoryName(dir), Path.GetFileName(dir), name.Text);
			if (World.SaveFile?.Dir == dir) World.SaveFile = renamed;
			Fill();
		}), ("Delete", () =>
		{
			string dir = Selected();
			if (confirm != dir) { confirm = dir; pageStatus.Text = $"Press Delete again to delete {dir}"; return; }
			WorldSave.Delete(Path.GetDirectoryName(dir), Path.GetFileName(dir));
			if (World.SaveFile?.Dir == dir) World.SaveFile = null;
			pageStatus.Text = $"Deleted {dir}";
			Fill();
		}), ("Back", Back));
		Fill();
	}

	// Snapshot bookmarks of tests/bookmarks: a load replaces the whole world; overwrite and delete ask twice.
	void BookmarksPage()
	{
		Add(new Label { Text = $"Bookmarks in {Snapshot.Root} (K saves one at once)" });
		var list = Add(new ItemList { CustomMinimumSize = new Vector2(0, 360) });
		var name = Add(new LineEdit { PlaceholderText = "Name (Rename)" });
		var note = Add(new LineEdit { PlaceholderText = "Note" });
		var fields = Add(new HBoxContainer());
		var resolved = new CheckBox { Text = "Resolved" };
		var redirect = new LineEdit { PlaceholderText = "Redirect to (another bookmark, loaded instead)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		fields.AddChild(resolved); fields.AddChild(redirect);
		string confirm = null;
		void Fill()
		{
			list.Clear();
			foreach (var s in Snapshot.List().Select(Snapshot.Load))
				list.SetItemMetadata(list.AddItem($"{s.Name}{(s.Status == BookmarkStatus.Resolved ? "  [resolved]" : "")}{(s.Redirect != null ? $"  -> {s.Redirect}" : "")}{(s.Note != null ? "  " + s.Note : "")}"), s.Name);
		}
		string Selected() => list.GetSelectedItems() is [var i] ? list.GetItemMetadata(i).AsString() : throw new InvalidOperationException("Select a bookmark first");
		bool Twice(string what) { if (confirm == what) return true; confirm = what; pageStatus.Text = $"Press again to {what}"; return false; }
		void Load()
		{
			var s = Snapshot.Resolve(Selected());
			World.LoadBookmark(s);
			ClearSelection();
			CloseUi();
			Status($"Bookmark '{s.Name}' loaded");
		}
		list.ItemSelected += _ =>
		{
			var s = Snapshot.Load(Selected());
			(name.Text, note.Text, resolved.ButtonPressed, redirect.Text, confirm) = (s.Name, s.Note ?? "", s.Status == BookmarkStatus.Resolved, s.Redirect ?? "", null);
		};
		list.ItemActivated += _ => Try(Load);
		Row(("Load", Load), ("New from the current state", () => { pageStatus.Text = $"Saved {World.SaveBookmark(null, Text(note)).Name}"; Fill(); }),
			("Save the current state over it", () => { if (Twice($"overwrite {Selected()}")) pageStatus.Text = $"Overwrote {World.SaveBookmark(Selected()).Name}"; }));
		Row(("Apply name and fields", () =>
		{
			string n = Selected();
			if (Text(redirect) is { } r && !Snapshot.Exists(r)) throw new ArgumentException($"No bookmark '{r}' to redirect to");
			if (name.Text != n) Snapshot.Rename(n, n = name.Text);
			var s = Snapshot.Load(n);
			(s.Note, s.Status, s.Redirect) = (Text(note), resolved.ButtonPressed ? BookmarkStatus.Resolved : BookmarkStatus.Open, Text(redirect) is { } to && to != n ? to : null);
			s.Save();
			Fill();
		}), ("Delete", () =>
		{
			string n = Selected();
			if (!Twice($"delete {n}")) return;
			Snapshot.Delete(n);
			pageStatus.Text = $"Deleted {n}";
			Fill();
		}), ("Back", Back));
		Fill();
	}

	static string Text(LineEdit e) => string.IsNullOrWhiteSpace(e.Text) ? null : e.Text.Trim();

	// K: the whole state into a new bookmark at once, then an optional note.
	void QuickBookmark()
	{
		var s = World.SaveBookmark(null);
		ShowPage(() =>
		{
			Add(new Label { Text = $"Bookmark '{s.Name}' saved. Note (optional, Enter):" });
			var note = Add(new LineEdit { PlaceholderText = "What to look at" });
			void Done()
			{
				if (Text(note) is { } n) { s.Note = n; s.Save(); }
				CloseUi();
				Status($"Bookmark '{s.Name}' saved");
			}
			note.TextSubmitted += _ => Try(Done);
			Row(("Save note", Done), ("Skip", CloseUi));
			Callable.From(note.GrabFocus).CallDeferred();
		});
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}
}
