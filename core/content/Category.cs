using Godot;

namespace Renesance.Content;

[GlobalClass]
public partial class Category : Resource
{
	[Export] public string DisplayName { get; set; } = "";
	[Export] public Texture2D Icon { get; set; }
	[Export] public int Order { get; set; }
	[Export] public Category Parent { get; set; }

	public string Id => ResourcePath.GetFile().GetBaseName();
}
