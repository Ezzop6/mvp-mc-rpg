using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Renesance.Content;

// Items by the content's category tree (categories.json): a category shows its own items and its children's.
public partial class Inventory : Control
{
	const string AllCategories = "";

	public event Action<BlockDef> Chosen;

	LineEdit search;
	OptionButton sort;
	Tree categories;
	GridContainer grid;
	BlockRegistry registry;
	List<BlockDef> items;
	Dictionary<string, int> order;      // category id -> position in the tree
	Dictionary<string, string> parent;  // category id -> parent id
	string category = AllCategories;

	public override void _Ready()
	{
		search = GetNode<LineEdit>("Window/Box/Top/Search");
		sort = GetNode<OptionButton>("Window/Box/Top/Sort");
		categories = GetNode<Tree>("Window/Box/Split/Categories");
		grid = GetNode<GridContainer>("Window/Box/Split/Scroll/Grid");
		sort.AddItem("Sort by name");
		sort.AddItem("Sort by category");
		search.TextChanged += _ => Refresh();
		sort.ItemSelected += _ => Refresh();
		categories.ItemSelected += () => { category = categories.GetSelected().GetMetadata(0).AsString(); Refresh(); };
	}

	public void Open(BlockRegistry reg)
	{
		if (items == null)
		{
			registry = reg;
			items = EditorItems.All(reg).ToList();
			var defs = reg.Content.Categories;
			parent = defs.ToDictionary(c => c.Id, c => c.Parent);
			order = defs.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
			var used = items.Select(d => d.Category).Where(c => c != null).ToHashSet();
			var root = categories.CreateItem();
			AddCategory(root, "All", AllCategories).Select(0);
			foreach (var top in defs.Where(c => c.Parent == null && (used.Contains(c.Id) || defs.Any(k => k.Parent == c.Id && used.Contains(k.Id)))))
			{
				var node = AddCategory(root, top.Name, top.Id);
				foreach (var child in defs.Where(k => k.Parent == top.Id && used.Contains(k.Id))) AddCategory(node, child.Name, child.Id);
			}
		}
		Show();
		search.GrabFocus();
		search.SelectAll();
	}

	TreeItem AddCategory(TreeItem root, string text, string id)
	{
		var item = categories.CreateItem(root);
		item.SetText(0, text);
		item.SetMetadata(0, id);
		return item;
	}

	bool InCategory(BlockDef d) => category == AllCategories || d.Category == category || (d.Category != null && parent.GetValueOrDefault(d.Category) == category);

	int Order(BlockDef d) => d.Category != null && order.TryGetValue(d.Category, out var i) ? i : int.MaxValue;

	void Refresh()
	{
		foreach (var child in grid.GetChildren()) child.QueueFree();
		var shown = items.Where(d => InCategory(d) && EditorItems.Matches(registry, d, search.Text));
		shown = sort.Selected == 1 ? shown.OrderBy(Order).ThenBy(EditorItems.DisplayName) : shown.OrderBy(EditorItems.DisplayName);
		foreach (var d in shown) grid.AddChild(ItemButton(d));
	}

	Button ItemButton(BlockDef d)
	{
		var b = new Button
		{
			Icon = EditorItems.Icon(d), ExpandIcon = true, IconAlignment = HorizontalAlignment.Center,
			CustomMinimumSize = new Vector2(72, 72), TooltipText = EditorItems.Tooltip(registry, d), FocusMode = FocusModeEnum.None,
		};
		b.Pressed += () => Chosen?.Invoke(d);
		if (EditorItems.IsItem(d)) b.SetDragForwarding(Callable.From((Vector2 _) =>
		{
			b.SetDragPreview(new TextureRect { Texture = b.Icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, Size = new Vector2(56, 56) });
			return Godot.Variant.From((int)d.Id);
		}), new Callable(), new Callable());
		return b;
	}
}
