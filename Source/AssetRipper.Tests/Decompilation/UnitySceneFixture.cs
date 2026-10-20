using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record EditorSceneNode(string Path, int SiblingIndex, bool ActiveSelf, float[] Position, float[] Rotation, float[] Scale);
internal sealed record EditorScenePointer(string Field, bool IsNull, string? Type, string? ScenePath, string? Name, string? Text, string? SourceGuid, long SourceLocalId);
internal sealed record EditorSceneComponent(string Path, int Inherited, int Hidden, int Score, string Label, int NestedCount, EditorScenePointer[] Pointers);
internal sealed record EditorSceneOracle(int SchemaVersion, string UnityVersion, string BuildTarget, string SceneName, string ScriptAssembly,
	string ScriptNamespace, string ScriptType, bool EditorSceneReadbackVerified, EditorSceneNode[] Nodes, EditorSceneComponent[] Components);

internal sealed record UnitySceneFixture(string DirectoryPath, EditorFixtureManifest Manifest, EditorSceneOracle Oracle)
{
	public const string EnvironmentVariable = "ASSETRIPPER_UNITY_SCENE_FIXTURE";
	public static UnitySceneFixture Require()
	{
		string? directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Scene bundle corpus NotRun: set {EnvironmentVariable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}
	public static UnitySceneFixture Load(string directory, FileSystem fs)
	{
		EditorFixtureManifest manifest = UnityEditorPayloadFixture.VerifyManifest(directory, fs,
			["Sources/ScenePayload.cs", "Sources/EditorPayload.cs", "Sources/SceneFixtureBuilder.cs", "Sources/SceneFixture.unity"]);
		EditorSceneOracle oracle = JsonSerializer.Deserialize<EditorSceneOracle>(fs.File.ReadAllText(fs.Path.Join(directory, "oracle.json")))!;
		if (oracle.SchemaVersion != 1 || oracle.UnityVersion != manifest.UnityVersion || oracle.BuildTarget != manifest.BuildTarget
			|| !oracle.EditorSceneReadbackVerified || oracle.SceneName != "SceneFixture" || oracle.ScriptAssembly != "Assembly-CSharp"
			|| oracle.ScriptNamespace != "AssetRipper.EditorSceneFixture" || oracle.ScriptType != "ScenePayload"
			|| oracle.Nodes.Length != 5 || oracle.Nodes.Select(n => n.Path).Distinct().Count() != 5
			|| oracle.Components.Length != 2 || oracle.Components.Select(c => c.Path).Distinct().Count() != 2
			|| oracle.Components.Sum(c => c.Pointers.Length) != 13)
			throw new InvalidDataException("The scene oracle is not a verified source scene with five objects and two components.");
		return new(directory, manifest, oracle);
	}
	public UnityEditorPayloadFixture.EditorPayloadManager CreateManager() => UnityEditorPayloadFixture.CreateManager(DirectoryPath);
	public void CopyInputs(string output)
	{
		File.Copy(Path.Combine(DirectoryPath, "oracle.json"), Path.Combine(output, "oracle.json"));
		File.Copy(Path.Combine(DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		foreach (var file in Manifest.Files)
		{
			string target = Path.Combine(output, "Evidence", "Input", file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(Path.Combine(DirectoryPath, file.Path), target);
		}
	}
	public static string EvidenceDirectory(string mode, string kind)
	{
		string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-scene", $"{mode}-{kind}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path); return path;
	}
}
