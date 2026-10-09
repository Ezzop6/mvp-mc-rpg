using Godot;
using System;
using System.Linq;
using Renesance.Content;
using Renesance.World;

public partial class CreativeEditor : CanvasLayer
{
	const int Slots = 9;
	const double HoldDelay = 0.5;

	[Export] public NodePath WorldPath = "../World";

	EditorWorld World;
	GhostBlocks ghost;

	readonly BlockDef[] hotbar = new BlockDef[Slots];
	readonly Panel[] slots = new Panel[Slots];
	int selected;
	bool filled;
	Control hud, menu;
	Label itemName;
	Inventory inventory;
	StyleBoxFlat slotStyle, selectedStyle;
	MouseButton? held;
	double heldSince;
	bool freshPick;

	bool UiOpen => inventory.Visible || menu.Visible || dialog.Visible || World.Main?.MenuOpen == true;

	public override void _Ready()
	{
		World = GetNode<EditorWorld>(WorldPath);
		ghost = World.Ghost;
		hud = GetNode<Control>("Hud");
		itemName = GetNode<Label>("Hud/ItemName");
		inventory = GetNode<Inventory>("Inventory");
		menu = GetNode<Control>("Menu");
		hud.Draw += DrawHud;
		World.Picked += OnPicked;
		inventory.Chosen += Choose;
		GetNode<Button>("Menu/Center/Box/Resume").Pressed += CloseUi;
		GetNode<Button>("Menu/Center/Box/NewFlat").Pressed += () => NewWorld("flat");
		GetNode<Button>("Menu/Center/Box/NewShowcase").Pressed += () => NewWorld("showcase");
		GetNode<Button>("Menu/Center/Box/NewRandom").Pressed += () => NewWorld("random");
		slotStyle = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f), BorderColor = new Color(0.5f, 0.5f, 0.5f) };
		slotStyle.SetBorderWidthAll(2);
		selectedStyle = (StyleBoxFlat)slotStyle.Duplicate();
		selectedStyle.BorderColor = Colors.White;
		selectedStyle.SetBorderWidthAll(4);
		BuildHotbar(GetNode<HBoxContainer>("Hud/Hotbar"));
		BuildDialog();
	}

	public override void _ExitTree()
	{
		World.Picked -= OnPicked;
	}

	void OnPicked(BlockPick? _) => freshPick = true;

	void BuildHotbar(HBoxContainer bar)
	{
		for (int i = 0; i < Slots; i++)
		{
			int slot = i;
			var panel = new Panel { CustomMinimumSize = new Vector2(80, 80), MouseFilter = Control.MouseFilterEnum.Stop };
			panel.AddChild(new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(10, 10), Size = new Vector2(60, 60) });
			panel.AddChild(new Label { Text = (i + 1).ToString(), Position = new Vector2(6, 2), MouseFilter = Control.MouseFilterEnum.Ignore });
			panel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Select(slot); };
			panel.SetDragForwarding(new Callable(),
				Callable.From((Vector2 _, Godot.Variant data) => data.VariantType == Godot.Variant.Type.Int),
				Callable.From((Vector2 _, Godot.Variant data) => { SetSlot(slot, World.Registry[(ushort)data.AsInt32()]); Select(slot); }));
			bar.AddChild(panel);
			slots[i] = panel;
		}
		Select(0);
	}

	void Choose(BlockDef d)
	{
		if (d.Item != null) World.DropItem(World.Camera.GlobalPosition - World.Camera.GlobalBasis.Z * 2f, d.Item.Model);
		else SetSlot(selected, d);
	}

	void SetSlot(int slot, BlockDef d)
	{
		hotbar[slot] = d;
		var icon = slots[slot].GetChild<TextureRect>(0);
		icon.Texture = EditorItems.Icon(World.Registry, d);
		slots[slot].TooltipText = EditorItems.Tooltip(World.Registry, d);
		if (slot == selected) Select(slot);
	}

	void Select(int slot)
	{
		selected = slot;
		for (int i = 0; i < Slots; i++) slots[i].AddThemeStyleboxOverride("panel", i == slot ? selectedStyle : slotStyle);
		itemName.Text = hotbar[slot] is { } d ? EditorItems.DisplayName(d) : "";
	}

	public override void _Process(double delta)
	{
		if (!filled && World.Registry != null)
		{
			filled = true;
			foreach (var (d, i) in EditorItems.All(World.Registry).Where(EditorItems.IsItem).Take(Slots).Select((d, i) => (d, i))) SetSlot(i, d);
		}
		RepeatHeld();
		ApplySets();
		hud.QueueRedraw();
	}

	public override void _Input(InputEvent e)
	{
		bool handled = e switch
		{
			InputEventKey { Pressed: true, Echo: false } k => KeyInput(k),
			InputEventMouseButton { Pressed: true } mb when !UiOpen && Input.MouseMode == Input.MouseModeEnum.Captured && World.Registry != null => MouseInput(mb.ButtonIndex),
			_ => false,
		};
		if (handled) GetViewport().SetInputAsHandled();
	}

	bool KeyInput(InputEventKey k)
	{
		var key = k.Keycode;
		if (World.Main?.MenuOpen == true) return false;
		if (!UiOpen && GetViewport().GuiGetFocusOwner() is not LineEdit && World.Registry != null)
		{
			if (key == Key.Alt) { SetMode(!selectMode); return true; }
			if (SelectionKey(k)) return true;
		}
		if (key == Key.Escape)
		{
			if (UiOpen) CloseUi();
			else if (selection.A != null || selection.B != null || clip != null) ClearCornersAndClip();
			else OpenUi(menu);
		}
		else if (GetViewport().GuiGetFocusOwner() is LineEdit) return false;
		else if (key == Key.E && !menu.Visible && World.Registry != null)
		{
			if (inventory.Visible) CloseUi();
			else OpenUi(inventory);
		}
		else if (key is >= Key.Key1 and <= Key.Key9 && !UiOpen) Select((int)(key - Key.Key1));
		else if (key == Key.K && !UiOpen && World.Registry != null) QuickBookmark();
		else return false;
		return true;
	}

	bool MouseInput(MouseButton button)
	{
		if (ReachWheel(button)) return true;
		switch (button)
		{
			case MouseButton.Left or MouseButton.Right when selectMode:
				SetCorner(button);
				break;
			case MouseButton.Left or MouseButton.Right:
				(held, heldSince, holdStep) = (button, Now, null);
				Act(button);
				break;
			case MouseButton.Middle when World.LastPick is { ObjectPart: false } p: PickItem(World.Registry.Get(p.State)); break;
			case MouseButton.WheelUp: Select((selected + Slots - 1) % Slots); break;
			case MouseButton.WheelDown: Select((selected + 1) % Slots); break;
			default: return button is MouseButton.Middle;
		}
		return true;
	}

	static double Now => Time.GetTicksMsec() / 1000.0;

	void Act(MouseButton button)
	{
		var pick = World.LastPick;
		if (button == MouseButton.Left && pick is { } p) Change(p.Block, Vector3I.One, _ => BlockState.Air, force: true, hold: true);
		else if (button == MouseButton.Right && pick is { } q && q.Normal != Vector3I.Zero && hotbar[selected] is { } d) Change(q.Place, Vector3I.One, _ => PlaceState(d, q, -World.Camera.GlobalBasis.Z), force: true, hold: true);
	}

	static BlockState PlaceState(BlockDef d, BlockPick pick, Vector3 look) =>
		BlockFacing.Place(d, (pick.Normal.X, pick.Normal.Y, pick.Normal.Z), (look.X, look.Y, look.Z));

	void RepeatHeld()
	{
		if (held is { } b && (selectMode || UiOpen || Input.MouseMode != Input.MouseModeEnum.Captured || !Input.IsMouseButtonPressed(b))) held = null;
		if (held is { } h && freshPick && Now - heldSince >= HoldDelay) Act(h);
		freshPick = false;
	}

	void PickItem(BlockDef d)
	{
		if (!EditorItems.IsItem(d)) return;
		int slot = Array.IndexOf(hotbar, d);
		if (slot >= 0) Select(slot);
		else SetSlot(selected, d);
	}

	void OpenUi(Control ui)
	{
		if (ui == inventory) inventory.Open(World.Registry);
		else ui.Show();
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	void CloseUi()
	{
		inventory.Hide();
		menu.Hide();
		dialog.Hide();
		GetViewport().GuiReleaseFocus();
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	void NewWorld(string generator)
	{
		ClearSelection();
		World.NewWorld(generator);
		CloseUi();
	}

	void DrawHud()
	{
		if (UiOpen) { ghost.Clear(); return; }
		var c = hud.Size / 2;
		hud.DrawLine(c - new Vector2(12, 0), c + new Vector2(12, 0), Colors.White, 2);
		hud.DrawLine(c - new Vector2(0, 12), c + new Vector2(0, 12), Colors.White, 2);
		if (Near is { } p) DrawBox(p.Block, Vector3I.One, new Color(0, 0, 0, 0.8f), 2);
		if (selectMode) DrawSelection();
		else ghost.Clear();
		hud.DrawString(ThemeDB.FallbackFont, c + new Vector2(16, 16), selectMode ? "S" : "B", fontSize: 20);
	}
}
