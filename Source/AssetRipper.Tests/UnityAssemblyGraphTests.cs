using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("UnityAssemblyGraph")]
public class UnityAssemblyGraphTests
{
	[TestCase(false, true)]
	[TestCase(true, true)]
	[TestCase(false, false)]
	[TestCase(true, false)]
	public void CrossAssemblyFieldsInheritanceAndTwelvePointersMatchTheEditorOracle(bool stripped, bool includeExternal)
	{
		var fixture = UnityAssemblyGraphFixture.Require(); using var manager = fixture.CreateManager();
		var bundle = Load(fixture, manager, stripped, includeExternal);
		var components = bundle.FetchAssets().OfType<IMonoBehaviour>().ToArray(); Assert.That(components, Has.Length.EqualTo(2));
		foreach (var component in components) Assert.That(component.Structure, stripped ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		IMonoBehaviour node = components.Single(c => c.ScriptP!.ClassName_R == "GraphNode"), bridge = components.Single(c => c.ScriptP!.ClassName_R == "AssemblyBridge");
		Assert.That(node.ScriptP!.GetAssemblyNameFixed(), Is.EqualTo("AssetRipper.GraphComponents"));
		Assert.That(bridge.ScriptP!.GetAssemblyNameFixed(), Is.EqualTo("Assembly-CSharp"));
		var n = node.LoadStructure(); var b = bridge.LoadStructure();
		Assert.That(n, Is.Not.Null, (node.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
		Assert.That(b, Is.Not.Null, (bridge.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
		Assert.That(n!["inherited"].AsInt32, Is.EqualTo(fixture.Oracle.Inherited)); Assert.That(n["hidden"].AsInt16, Is.EqualTo(fixture.Oracle.Hidden));
		Assert.That(n["payload"]["count"].AsInt32, Is.EqualTo(fixture.Oracle.NodeCount)); Assert.That(n["payload"]["label"].AsString, Is.EqualTo(fixture.Oracle.NodeLabel));
		Assert.That(b!["shared"]["count"].AsInt32, Is.EqualTo(fixture.Oracle.BridgeCount)); Assert.That(b["shared"]["label"].AsString, Is.EqualTo(fixture.Oracle.BridgeLabel));
		Assert.That(b["stamp"].AsInt32, Is.EqualTo(fixture.Oracle.Stamp));
		CheckPointers(node, n, ["payload.target", "local", "links[0]", "links[1]", "links[2]", "links[3]"],
			["GraphExternal", "GraphLocal", "GraphLocal", "GraphExternal", null, "AssemblyBridge"], fixture.Oracle, includeExternal);
		CheckPointers(bridge, b, ["node", "shared.target", "links[0]", "links[1]", "links[2]", "links[3]"],
			["GraphNode", "GraphExternal", "GraphNode", "GraphLocal", "GraphExternal", null], fixture.Oracle, includeExternal);
		if (!includeExternal)
		{
			var full = Load(fixture, manager, stripped, true);
			Assert.That(Pointers(bundle), Is.EqualTo(Pointers(full)), "Missing external inputs must preserve every serialized pointer ID.");
		}
		string output = fixture.CopyInputs($"Read-{stripped}-{includeExternal}");
		File.WriteAllText(Path.Combine(output, "field-and-reference-verification.json"), JsonSerializer.Serialize(new
		{
			SchemaSource = stripped ? "Assembly" : "TypeTree", IncludeExternal = includeExternal, FieldAndReferenceOracle = "Passed",
			Pointers = Pointers(bundle), Assemblies = manager.GetAssemblies().Select(a => a.Name!.ToString()).Order().ToArray(), EditorImport = "NotRun",
		}, new JsonSerializerOptions { WriteIndented = true }));
	}
	internal static GameBundle Load(UnityAssemblyGraphFixture fixture, UnityEditorPayloadFixture.EditorPayloadManager manager, bool stripped, bool includeExternal = true)
	{
		string directory = Path.Combine(fixture.DirectoryPath, stripped ? "WithoutTypeTree" : "WithTypeTree");
		List<string> paths = [Path.Combine(directory, "payload")]; if (includeExternal) paths.Add(Path.Combine(directory, "external"));
		var bundle = GameBundle.FromPaths(paths, new GameAssetFactory(manager), LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty); return bundle;
	}
	private static void CheckPointers(IMonoBehaviour asset, SerializableStructure structure, string[] paths, string?[] targets, AssemblyGraphOracle oracle, bool includeExternal)
	{
		var dependencies = structure.FetchDependencies().ToArray(); Assert.That(dependencies.Select(d => d.Item1), Is.EqualTo(paths));
		for (int i = 0; i < dependencies.Length; i++)
		{
			var pointer = dependencies[i].Item2; var target = asset.Collection.TryGetAsset(pointer); string? expected = targets[i];
			Assert.That(pointer.IsNull, Is.EqualTo(expected is null));
			if (expected is null || expected == "GraphExternal" && !includeExternal) { Assert.That(target, Is.Null); continue; }
			Assert.That(target, Is.Not.Null);
			string? name = target is IMonoBehaviour component ? component.ScriptP?.ClassName_R.String : (target as INamed)?.Name.String;
			Assert.That(name, Is.EqualTo(expected));
			if (target is ITextAsset text) Assert.That(text.Script_C49.String, Is.EqualTo(oracle.ExternalText));
		}
	}
	private static object[] Pointers(GameBundle bundle) => bundle.FetchAssets().OfType<IMonoBehaviour>().OrderBy(c => c.ScriptP!.ClassName_R.String, StringComparer.Ordinal)
		.SelectMany(c => c.LoadStructure()!.FetchDependencies().Select(p => (object)new { Component = c.ScriptP!.ClassName_R.String, Field = p.Item1, p.Item2.FileID, p.Item2.PathID })).ToArray();

	[Test]
	public void FixtureManifestAcceptsTheThreeExplicitHashedAssemblies() => Assert.That(UnityAssemblyGraphFixture.Load("/Fixture", Storage()).Oracle.EditorSceneReadbackVerified, Is.True);
	[TestCase("changed")]
	[TestCase("missing")]
	[TestCase("traversal")]
	[TestCase("oracle")]
	[TestCase("unlisted")]
	public void FixtureManifestRejectsInvalidAssemblyInputs(string kind)
	{
		var fs = Storage();
		if (kind == "changed") fs.File.WriteAllBytes("/Fixture/AssetRipper.GraphModel.dll", [9]);
		if (kind == "missing") fs.File.Delete("/Fixture/AssetRipper.GraphComponents.dll");
		var manifest = JsonSerializer.Deserialize<EditorFixtureManifest>(fs.File.ReadAllText("/Fixture/fixture-manifest.json"))!;
		if (kind == "traversal") manifest.Files[0] = new("../outside", new string('0', 64));
		if (kind == "oracle")
		{
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Oracle() with { Hidden = 7 }); fs.File.WriteAllBytes("/Fixture/oracle.json", bytes);
			int index = Array.FindIndex(manifest.Files, f => f.Path == "oracle.json"); manifest.Files[index] = new("oracle.json", FixtureCompiler.Hash(bytes));
		}
		if (kind == "unlisted") manifest = manifest with { Files = manifest.Files.Where(f => f.Path != "AssetRipper.GraphModel.dll").ToArray() };
		fs.File.WriteAllText("/Fixture/fixture-manifest.json", JsonSerializer.Serialize(manifest));
		if (kind == "missing") Assert.Throws<FileNotFoundException>(() => UnityAssemblyGraphFixture.Load("/Fixture", fs));
		else Assert.Throws<InvalidDataException>(() => UnityAssemblyGraphFixture.Load("/Fixture", fs));
	}
	private static AssemblyGraphOracle Oracle() => new(1, "6000.4.0f1", "StandaloneLinux64", true, UnityAssemblyGraphFixture.AssemblyNames,
		[-9, 0, 42], [-1169, -1160, -1118], [-856, -847, -805], -17, -1234, 91, "node ☃", -8, "bridge ☃", 321, "Assembly graph external: Merhaba ☃");
	private static VirtualFileSystem Storage()
	{
		VirtualFileSystem fs = new(); List<EditorFixtureFile> files = [];
		string[] paths = ["Assembly-CSharp.dll", "AssetRipper.GraphComponents.dll", "AssetRipper.GraphModel.dll", "oracle.json", "WithTypeTree/payload", "WithTypeTree/external",
			"WithoutTypeTree/payload", "WithoutTypeTree/external", "Sources/AssemblyBridge.cs", "Sources/Components/GraphNode.cs", "Sources/Model/SharedPayload.cs",
			"Sources/Components/AssetRipper.GraphComponents.asmdef", "Sources/Model/AssetRipper.GraphModel.asmdef", "Sources/Editor/AssemblyGraphFixtureBuilder.cs",
			"Sources/AssemblyGraphRoot.prefab", "Sources/AssemblyGraphScene.unity"];
		foreach (string path in paths)
		{
			fs.Directory.Create(fs.Path.GetDirectoryName("/Fixture/" + path));
			byte[] bytes = path == "oracle.json" ? JsonSerializer.SerializeToUtf8Bytes(Oracle()) : [1, 2, 3];
			fs.File.WriteAllBytes("/Fixture/" + path, bytes); files.Add(new(path, FixtureCompiler.Hash(bytes)));
		}
		fs.File.WriteAllText("/Fixture/fixture-manifest.json", JsonSerializer.Serialize(new EditorFixtureManifest(1, "6000.4.0f1", "StandaloneLinux64", files.ToArray())));
		return fs;
	}
}
