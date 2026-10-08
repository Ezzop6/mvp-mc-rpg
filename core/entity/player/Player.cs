using Godot;

public enum MoveMode { Fly, Walk }

// Mouse look; Fly: WASD, Space / Shift along world up and down, Ctrl fast, wheel speed, through everything; Walk: Player.Walk.cs. Keeps its position inside World's anchor chunk.
public partial class Player : Camera3D
{
	[Export] public World World;
	[Export, Setting(MenuTab.World, "Camera", 1, 1000, 1, Label = "Fly speed (m/s)")] public float Speed = 20f;
	public bool Autopilot; // someone else moves the camera (tests)
	const float MaxPitch = 1.55f;

	float yaw, pitch;

	// Radians: yaw around up, pitch up positive.
	public (float Yaw, float Pitch) Look
	{
		get => (yaw, pitch);
		set { (yaw, pitch) = (value.Yaw, Mathf.Clamp(value.Pitch, -MaxPitch, MaxPitch)); Rotation = new Vector3(pitch, yaw, 0); }
	}

	public override void _Ready() => ProcessPriority = -1; // moves before the HUD and the world's streaming read the position

	// Above the generator's surface at the origin.
	public void Spawn() => World.CameraPosition = new Vector3(0, World.SurfaceHeight(0, 0) + 20, 0);

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			yaw -= mm.Relative.X * 0.0025f;
			pitch = Mathf.Clamp(pitch - mm.Relative.Y * 0.0025f, -MaxPitch, MaxPitch);
		}
		else if (e is InputEventMouseButton { Pressed: true } mb)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp) Speed *= 1.25f;
			else if (mb.ButtonIndex == MouseButton.WheelDown) Speed /= 1.25f;
		}
	}

	public override void _Process(double delta)
	{
		bool typing = GetViewport().GuiGetFocusOwner() is LineEdit;
		int Down(Key k) => !typing && Input.IsKeyPressed(k) ? 1 : 0;
		var input = new Vector2(Down(Key.D) - Down(Key.A), Down(Key.W) - Down(Key.S));
		if (!Autopilot && Mode == MoveMode.Walk) Walk(delta, input, Down(Key.Space) > 0, Down(Key.Ctrl) > 0);
		else if (!Autopilot) Fly(delta, input, Down(Key.Space) - Down(Key.Shift), Down(Key.Ctrl) > 0);
		World.Rebase();
	}

	// input: x right, y forward.
	void Fly(double delta, Vector2 input, float up, bool fast)
	{
		Rotation = new Vector3(pitch, yaw, 0);
		var move = (GlobalBasis * new Vector3(input.X, 0, -input.Y) + Vector3.Up * up).LimitLength(1);
		GlobalPosition += move * Speed * (fast ? 8f : 1f) * (float)delta;
	}
}
