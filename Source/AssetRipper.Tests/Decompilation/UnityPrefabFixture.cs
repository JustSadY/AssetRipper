using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record EditorPrefabLink(string Path, string SourceAssetPath, string SourceRoot, string[] PropertyPaths);
internal sealed record EditorPrefabOracle(string Root, string AssetType, EditorPrefabLink[] Links, EditorSceneNode[] Nodes, EditorSceneComponent[] Components);
internal sealed record EditorPrefabGraphOracle(int SchemaVersion, string UnityVersion, string BuildTarget, string ScriptAssembly,
	string ScriptNamespace, string ScriptType, bool EditorPrefabReadbackVerified, EditorPrefabOracle[] Prefabs);

internal sealed record UnityPrefabFixture(string DirectoryPath, EditorFixtureManifest Manifest, EditorPrefabGraphOracle Oracle)
{
	public const string EnvironmentVariable = "ASSETRIPPER_UNITY_PREFAB_FIXTURE";
	public static UnityPrefabFixture Require()
	{
		string? directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Prefab graph corpus NotRun: set {EnvironmentVariable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}
	internal static UnityPrefabFixture Load(string directory, FileSystem fs)
	{
		var manifest = UnityEditorPayloadFixture.VerifyManifest(directory, fs,
			["Sources/ScenePayload.cs", "Sources/EditorPayload.cs", "Sources/PrefabGraphFixtureBuilder.cs", "Sources/BaseRig.prefab", "Sources/VariantRig.prefab", "Sources/NestedRig.prefab",
			"Sources/BaseRig.prefab.meta", "Sources/VariantRig.prefab.meta", "Sources/NestedRig.prefab.meta"]);
		var oracle = JsonSerializer.Deserialize<EditorPrefabGraphOracle>(fs.File.ReadAllText(fs.Path.Join(directory, "oracle.json")))!;
		if (oracle.SchemaVersion != 1 || oracle.UnityVersion != manifest.UnityVersion || oracle.BuildTarget != manifest.BuildTarget
			|| !oracle.EditorPrefabReadbackVerified || oracle.ScriptAssembly != "Assembly-CSharp" || oracle.ScriptNamespace != "AssetRipper.EditorSceneFixture" || oracle.ScriptType != "ScenePayload"
			|| !oracle.Prefabs.Select(p => p.Root).SequenceEqual(["BaseRig", "NestedRig", "VariantRig"])
			|| oracle.Prefabs.Sum(p => p.Nodes.Length) != 9 || oracle.Prefabs.Sum(p => p.Components.Length) != 4
			|| oracle.Prefabs.SelectMany(p => p.Components).Sum(c => c.Pointers.Length) != 25
			|| oracle.Prefabs[2].AssetType != "Variant" || !oracle.Prefabs[2].Links.Any(l => l.Path == "VariantRig" && l.SourceRoot == "BaseRig" && l.PropertyPaths.Contains("score"))
			|| !oracle.Prefabs[1].Links.Any(l => l.Path == "NestedRig/EmbeddedVariant" && l.SourceRoot == "VariantRig" && l.PropertyPaths.Contains("peer")))
			throw new InvalidDataException("Prefab oracle must contain a verified base/variant/nested graph and its effective fields and authoring links.");
		return new(directory, manifest, oracle);
	}
	public UnityEditorPayloadFixture.EditorPayloadManager CreateManager() => UnityEditorPayloadFixture.CreateManager(DirectoryPath);
	public void CopyInputs(string output)
	{
		File.Copy(Path.Combine(DirectoryPath, "oracle.json"), Path.Combine(output, "oracle.json"));
		File.Copy(Path.Combine(DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		foreach (var f in Manifest.Files)
		{
			string path = Path.Combine(output, "Evidence", "Input", f.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.Copy(Path.Combine(DirectoryPath, f.Path), path);
		}
	}
	public static string EvidenceDirectory(bool stripped, string kind)
	{
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-prefab", $"{(stripped ? "Assembly" : "TypeTree")}-{kind}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(output); return output;
	}
}
