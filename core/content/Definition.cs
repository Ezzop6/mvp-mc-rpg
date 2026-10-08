using Godot;

namespace Renesance.Content;

[GlobalClass]
public abstract partial class Definition : Resource
{
	[Export] public string DisplayName { get; set; } = "";
	[Export] public Category Category { get; set; }
	[Export] public string[] Tags { get; set; } = [];
	[Export(PropertyHint.Range, "1,999")] public int MaxStack { get; set; } = 64;

	public string Id => ResourcePath.GetFile().GetBaseName();
	public virtual Texture2D Icon => null;
}
