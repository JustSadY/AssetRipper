using System.Text.Json.Serialization;

namespace AssetRipper.Export.UnityProjects.Scripts.AssemblyDefinitions;

public sealed class AssemblyDefinitionAsset
{
	[JsonPropertyName("name")]
	public string Name { get; set; }
	[JsonPropertyName("references")]
	public List<string> References { get; set; }
	[JsonPropertyName("allowUnsafeCode")]
	public bool AllowUnsafeCode { get; set; }
	[JsonPropertyName("overrideReferences")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool OverrideReferences { get; set; }
	[JsonPropertyName("precompiledReferences")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<string>? PrecompiledReferences { get; set; }

	public AssemblyDefinitionAsset(string name)
	{
		Name = name;
		AllowUnsafeCode = true;
		References = new();
	}
}
