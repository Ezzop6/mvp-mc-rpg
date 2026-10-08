using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Tools;
using Renesance.World;

// Selection box, clipboard and undo of the editor (controls: docs/testing.md, Editor).
public partial class CreativeEditor
{
	const int UndoSteps = 8, MaxReach = 10, SetsPerFrame = 8192;

	sealed record UndoStep(List<(BlockPos, BlockState)> Prior);

	readonly Selection selection = new();
	Clipboard clip;
	UndoStep holdStep;
	bool pasteAir = true;
	bool selectMode;
	readonly LinkedList<UndoStep> undo = new();
	readonly Queue<(BlockPos, BlockState)> sets = new();
	int reach = MaxReach;
	string status = "";
	double statusUntil;

	BlockPick? Near => World.LastPick is { } p && p.Distance <= MaxReach * Voxel.BlockSize ? p : null;
	BlockPos Target => Near is { } p ? p.Block : FreeCursor;
	BlockPos PasteAt => Near is { } p && p.Normal != Vector3I.Zero ? p.Place : FreeCursor;
	// Corner A of the clipboard goes to the cursor, so R turns the box around A (four turns return to the start).
	BlockPos PasteMin => clip == null ? PasteAt : new BlockPos(PasteAt.X - clip.Anchor.X, PasteAt.Y - clip.Anchor.Y, PasteAt.Z - clip.Anchor.Z);

	BlockPos FreeCursor
	{
		get
		{
			var cam = World.Camera;
			var v = (Vector3I)((cam.GlobalPosition - cam.GlobalBasis.Z * reach * Voxel.BlockSize) / Voxel.BlockSize).Floor() + World.OriginBlock;
			return new BlockPos(v.X, v.Y, v.Z);
		}
	}

	bool ReachWheel(MouseButton b)
	{
		if (!Input.IsKeyPressed(Key.Ctrl) || b is not (MouseButton.WheelUp or MouseButton.WheelDown)) return false;
		reach = Math.Clamp(reach + (b == MouseButton.WheelUp ? 1 : -1), 1, MaxReach);
		Status($"Free cursor {reach} blocks ahead");
		return true;
	}

	static Vector3I Vec((int X, int Y, int Z) s) => new(s.X, s.Y, s.Z);

	void Status(string text)
	{
		status = text;
		statusUntil = Now + 3;
	}

	void SetMode(bool select)
	{
		selectMode = select;
		held = null;
		Status(select ? "Select: LMB/RMB corners, Ctrl+C/V, R, Esc clears" : "Build: LMB breaks, RMB places");
	}

	bool SelectionKey(InputEventKey k)
	{
		if (k.Keycode == Key.Z && k.CtrlPressed) { Undo(); return true; }
		if (!selectMode) return false;
		switch (k.Keycode)
		{
			case Key.C when k.CtrlPressed: Copy(); break;
			case Key.V when k.CtrlPressed: Paste(); break;
			case Key.R when clip != null: clip = clip.Rotate(); Status($"Clipboard rotated: {SizeText(clip.Size)}"); break;
			case Key.F when clip != null: clip = clip.Flip(); Status($"Clipboard flipped: {SizeText(clip.Size)}"); break;
			case Key.T: pasteAir = !pasteAir; Status(pasteAir ? "Paste overwrites with air" : "Paste skips air"); break;
			default: return false;
		}
		return true;
	}

	void SetCorner(MouseButton button) => selection.Set(button == MouseButton.Left, Target);

	void ClearCornersAndClip()
	{
		selection.Clear();
		clip = null;
		Status("Selection and clipboard cleared");
	}

	static string SizeText(Vector3I s) => $"{s.X} x {s.Y} x {s.Z} ({(long)s.X * s.Y * s.Z} blocks)";

	void Copy()
	{
		if (!selection.Complete) { Status("Select two corners with LMB and RMB first"); return; }
		var min = selection.Min;
		var size = Vec(selection.Size);
		if (selection.Volume > EditorWorld.MaxReadBlocks) { Status($"Selection too large: {SizeText(size)}, max {EditorWorld.MaxReadBlocks} blocks"); return; }
		var a = selection.A.Value;
		World.ReadBox(min, size, states =>
		{
			if (states == null) { Status("World not ready"); return; }
			clip = new Clipboard(size, states, new Vector3I(a.X - min.X, a.Y - min.Y, a.Z - min.Z));
			Status($"Copied {SizeText(size)}");
		});
	}

	void Paste()
	{
		if (clip == null) { Status("Clipboard is empty"); return; }
		var at = PasteMin;
		var c = clip;
		Change(at, c.Size, i => pasteAir || c.States[i] != BlockState.Air ? c.States[i] : null, $"Pasted {SizeText(c.Size)}");
	}

	// The box through the block rules (EditorWorld.Resolve), then sets every changing block (`force`: every asked one) with undo.
	void Change(BlockPos min, Vector3I size, Func<int, BlockState?> state, string done = null, bool force = false, bool hold = false) =>
		World.Resolve(min, size, state, force, changes =>
		{
			if (changes == null) { Status("World not ready"); return; }
			var step = new List<(BlockPos, BlockState)>();
			foreach (var (p, s, prior) in changes)
			{
				sets.Enqueue((p, s));
				step.Add((p, prior));
			}
			if (step.Count == 0) return;
			if (hold && holdStep != null && undo.Last?.Value == holdStep) holdStep.Prior.AddRange(step);
			else
			{
				undo.AddLast(new UndoStep(step));
				if (undo.Count > UndoSteps) undo.RemoveFirst();
				if (hold) holdStep = undo.Last.Value;
			}
			if (done != null) Status($"{done}, {step.Count} changed");
		});

	void Undo()
	{
		if (undo.Last is not { } last) { Status("Nothing to undo"); return; }
		undo.RemoveLast();
		foreach (var e in Enumerable.Reverse(last.Value.Prior)) sets.Enqueue(e);
		Status($"Undone {last.Value.Prior.Count} blocks, {undo.Count} steps left");
	}

	void ApplySets()
	{
		for (int n = 0; n < SetsPerFrame && sets.TryDequeue(out var e); n++) World.SetBlock(e.Item1, e.Item2);
	}

	void ClearSelection()
	{
		selection.Clear();
		sets.Clear();
		undo.Clear();
	}

	void DrawSelection()
	{
		if (Near == null && ((selection.A != null && selection.B == null) || clip != null || Input.IsKeyPressed(Key.Ctrl))) DrawBox(FreeCursor, Vector3I.One, new Color(1, 1, 1, 0.7f), 1.5f);
		if (selection.A is { } a)
		{
			var (min, size) = Selection.Normalized(a, selection.B ?? Target);
			DrawBox(min, Vec(size), new Color(1f, 0.85f, 0.2f), 3);
		}
		if (clip != null) ghost.Display(clip, PasteAt, World.OriginBlock);
		else ghost.Clear();
	}

	// Edges of the box [min, min + size) in blocks, clipped at the near plane.
	void DrawBox(BlockPos min, Vector3I size, Color color, float width)
	{
		var cam = World.Camera;
		const float s = Voxel.BlockSize, pad = 0.004f;
		var o = World.OriginBlock;
		var lo = new Vector3(min.X - o.X, min.Y - o.Y, min.Z - o.Z) * s - Vector3.One * pad;
		var ext = new Vector3(size.X, size.Y, size.Z) * s + Vector3.One * 2 * pad;
		var corners = Enumerable.Range(0, 8).Select(i => lo + new Vector3(i & 1, i >> 1 & 1, i >> 2 & 1) * ext).ToArray();
		var view = cam.GlobalTransform.AffineInverse();
		float near = -cam.Near * 1.01f;
		for (int i = 0; i < 8; i++)
			foreach (int axis in new[] { 1, 2, 4 })
			{
				if ((i & axis) != 0) continue;
				Vector3 p = corners[i], q = corners[i | axis];
				float zp = (view * p).Z, zq = (view * q).Z;
				if (zp > near && zq > near) continue;
				if (zp > near) p = p.Lerp(q, (zp - near) / (zp - zq));
				else if (zq > near) q = q.Lerp(p, (zq - near) / (zq - zp));
				hud.DrawLine(cam.UnprojectPosition(p), cam.UnprojectPosition(q), color, width);
			}
	}
}
