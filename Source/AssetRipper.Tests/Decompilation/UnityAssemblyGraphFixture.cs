using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record AssemblyGraphOracle(int SchemaVersion, string UnityVersion, string BuildTarget, bool EditorSceneReadbackVerified,
	string[] Assemblies, int[] Inputs, int[] NodeResults, int[] BridgeResults, int Inherited, int Hidden, int NodeCount,
	string NodeLabel, int BridgeCount, string BridgeLabel, int Stamp, string ExternalText);

internal sealed record UnityAssemblyGraphFixture(string DirectoryPath, EditorFixtureManifest Manifest, AssemblyGraphOracle Oracle)
{
	public const string EnvironmentVariable = "ASSETRIPPER_UNITY_ASSEMBLY_GRAPH_FIXTURE";
	internal static readonly string[] AssemblyNames = ["Assembly-CSharp", "AssetRipper.GraphComponents", "AssetRipper.GraphModel"];
	public static UnityAssemblyGraphFixture Require()
	{
		string? directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Assembly graph corpus NotRun: set {EnvironmentVariable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}
	internal static UnityAssemblyGraphFixture Load(string directory, FileSystem fs)
	{
		var manifest = UnityEditorPayloadFixture.VerifyManifest(directory, fs,
			["AssetRipper.GraphComponents.dll", "AssetRipper.GraphModel.dll", "Sources/AssemblyBridge.cs", "Sources/Components/GraphNode.cs",
			"Sources/Model/SharedPayload.cs", "Sources/Components/AssetRipper.GraphComponents.asmdef", "Sources/Model/AssetRipper.GraphModel.asmdef",
			"Sources/Editor/AssemblyGraphFixtureBuilder.cs", "Sources/AssemblyGraphRoot.prefab", "Sources/AssemblyGraphScene.unity"]);
		var oracle = JsonSerializer.Deserialize<AssemblyGraphOracle>(fs.File.ReadAllText(fs.Path.Join(directory, "oracle.json")))!;
		if (oracle.SchemaVersion != 1 || oracle.UnityVersion != manifest.UnityVersion || oracle.BuildTarget != manifest.BuildTarget || !oracle.EditorSceneReadbackVerified
			|| !oracle.Assemblies.SequenceEqual(AssemblyNames) || !oracle.Inputs.SequenceEqual([-9, 0, 42])
			|| !oracle.NodeResults.SequenceEqual([-1169, -1160, -1118]) || !oracle.BridgeResults.SequenceEqual([-856, -847, -805])
			|| oracle.Inherited != -17 || oracle.Hidden != -1234 || oracle.NodeCount != 91 || oracle.BridgeCount != -8 || oracle.Stamp != 321
			|| oracle.NodeLabel != "node ☃" || oracle.BridgeLabel != "bridge ☃" || oracle.ExternalText != "Assembly graph external: Merhaba ☃")
			throw new InvalidDataException("Assembly graph oracle differs from the source-owned three-assembly contract.");
		return new(directory, manifest, oracle);
	}
	public UnityEditorPayloadFixture.EditorPayloadManager CreateManager() => UnityEditorPayloadFixture.CreateManager(DirectoryPath, AssemblyNames);
	public string CopyInputs(string kind)
	{
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-assembly-graph", kind + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(output);
		File.Copy(Path.Combine(DirectoryPath, "oracle.json"), Path.Combine(output, "oracle.json"));
		File.Copy(Path.Combine(DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		foreach (var file in Manifest.Files)
		{
			string target = Path.Combine(output, "Evidence", "Input", file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(Path.Combine(DirectoryPath, file.Path), target);
		}
		return output;
	}
}
