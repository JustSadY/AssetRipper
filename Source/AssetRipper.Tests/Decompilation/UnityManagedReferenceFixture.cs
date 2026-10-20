using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record EditorGraphEdge(string Field, long Rid);
internal sealed record EditorGraphNode(long Rid, string Class, string Label, long Next, int Number, string? Text,
	string TargetName, string TargetType, string? TargetText);
internal sealed record EditorGraphOracle(int SchemaVersion, string UnityVersion, string BuildTarget, string ScriptAssembly,
	string ScriptNamespace, string ScriptType, bool EditorSceneReadbackVerified, bool SharedIdentityVerified, bool CycleVerified,
	EditorGraphEdge[] Edges, EditorGraphNode[] Nodes);

internal sealed record UnityManagedReferenceFixture(string DirectoryPath, EditorFixtureManifest Manifest, EditorGraphOracle Oracle)
{
	public const string EnvironmentVariable = "ASSETRIPPER_UNITY_MANAGED_REFERENCE_FIXTURE";
	public static UnityManagedReferenceFixture Require()
	{
		string? directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Managed reference Editor corpus NotRun: set {EnvironmentVariable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}

	public static UnityManagedReferenceFixture Load(string directory, FileSystem fs)
	{
		EditorFixtureManifest manifest = UnityEditorPayloadFixture.VerifyManifest(directory, fs,
			["Sources/GraphPayload.cs", "Sources/GraphFixtureBuilder.cs", "Sources/GraphScene.unity", "Sources/Graph.prefab"]);
		EditorGraphOracle oracle = JsonSerializer.Deserialize<EditorGraphOracle>(fs.File.ReadAllText(fs.Path.Join(directory, "oracle.json")))!;
		if (oracle.SchemaVersion != 1 || oracle.UnityVersion != manifest.UnityVersion || oracle.BuildTarget != manifest.BuildTarget
			|| !oracle.EditorSceneReadbackVerified || !oracle.SharedIdentityVerified || !oracle.CycleVerified
			|| oracle.ScriptAssembly != "Assembly-CSharp" || oracle.ScriptNamespace != "AssetRipper.EditorGraphFixture" || oracle.ScriptType != "GraphPayload"
			|| oracle.Nodes.Length != 2 || oracle.Nodes.Select(n => n.Rid).Distinct().Count() != 2 || oracle.Edges.Length != 7)
			throw new InvalidDataException("Managed reference oracle is missing its verified source graph.");
		return new(directory, manifest, oracle);
	}

	public UnityEditorPayloadFixture.EditorPayloadManager CreateManager() => UnityEditorPayloadFixture.CreateManager(DirectoryPath);
}
