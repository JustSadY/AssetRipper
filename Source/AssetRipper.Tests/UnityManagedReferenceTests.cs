using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.SerializationLogic;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("UnityManagedReference")]
public class UnityManagedReferenceTests
{
	[TestCase(false, true)]
	[TestCase(true, true)]
	[TestCase(false, false)]
	[TestCase(true, false)]
	public void RealEditorRegistryPreservesSharedIdsCyclePolymorphismAndDependencies(bool stripped, bool includeExternal)
	{
		UnityManagedReferenceFixture fixture = UnityManagedReferenceFixture.Require();
		using var manager = fixture.CreateManager();
		IMonoBehaviour asset = Load(fixture, manager, stripped, includeExternal);
		SerializableStructure structure = asset.LoadStructure()!;
		Assert.That(structure, Is.Not.Null, (asset.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
		AssertGraph(structure, fixture.Oracle, asset, includeExternal);
		if (!includeExternal)
		{
			var complete = Load(fixture, manager, stripped, true).LoadStructure()!;
			Assert.That(structure.FetchDependencies().Select(p => p.Item2), Is.EqualTo(complete.FetchDependencies().Select(p => p.Item2)));
		}
		string output = EvidenceDirectory(stripped, "Read-" + includeExternal);
		CopyInputs(fixture, output);
		File.WriteAllText(Path.Combine(output, "graph-verification.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Oracle.UnityVersion, SchemaSource = stripped ? "Assembly" : "TypeTree", IncludeExternal = includeExternal,
			ReferenceProfile = PinnedUnityReferences.ReadManifest(),
			Edges = ReadEdges(structure), Nodes = ReadEntries(structure).Select(e => new { Rid = e["rid"].AsInt64, Class = e["type"]["class"].AsString,
				Fields = e["data"].AsStructure.Type.Fields.Select(f => f.Name).ToArray() }).ToArray(),
			Dependencies = structure.FetchDependencies().Select(p => new { Field = p.Item1, p.Item2.FileID, p.Item2.PathID, Resolved = asset.Collection.TryGetAsset(p.Item2) is not null }).ToArray(),
			FieldAndGraphOracle = "Passed", ExportedProjectEditorImport = "NotRun",
		}, new JsonSerializerOptions { WriteIndented = true }));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void DeepClonePreservesConcreteRegistryDataAndDependencies(bool stripped)
	{
		UnityManagedReferenceFixture fixture = UnityManagedReferenceFixture.Require();
		using var manager = fixture.CreateManager();
		IMonoBehaviour asset = Load(fixture, manager, stripped, true);
		SerializableStructure original = asset.LoadStructure()!;
		SerializableStructure clone = original.DeepClone(new PPtrConverter(asset.Collection, asset.Collection));
		AssertGraph(clone, fixture.Oracle, asset, true);
		var sourceData = ReadEntries(original).Where(e => e["rid"].AsInt64 >= 0).ToArray();
		var cloneData = ReadEntries(clone).Where(e => e["rid"].AsInt64 >= 0).ToArray();
		Assert.That(cloneData.Length, Is.EqualTo(sourceData.Length));
		for (int i = 0; i < sourceData.Length; i++)
		{
			Assert.That(cloneData[i]["data"].AsStructure, Is.Not.SameAs(sourceData[i]["data"].AsStructure));
			cloneData[i]["data"]["label"].AsString = "clone-only";
			Assert.That(sourceData[i]["data"]["label"].AsString, Is.Not.EqualTo("clone-only"));
		}
	}

	internal static IMonoBehaviour Load(UnityManagedReferenceFixture fixture, UnityEditorPayloadFixture.EditorPayloadManager manager, bool stripped, bool includeExternal)
	{
		string directory = Path.Combine(fixture.DirectoryPath, stripped ? "WithoutTypeTree" : "WithTypeTree");
		List<string> inputs = [Path.Combine(directory, "payload")];
		if (includeExternal) inputs.Add(Path.Combine(directory, "external"));
		GameBundle bundle = GameBundle.FromPaths(inputs, new GameAssetFactory(manager), LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		IMonoBehaviour asset = bundle.FetchAssets().OfType<IMonoBehaviour>().Single(m => m.ScriptP?.ClassName_R == fixture.Oracle.ScriptType);
		Assert.That(asset.Structure, stripped ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		return asset;
	}

	internal static void AssertGraph(SerializableStructure structure, EditorGraphOracle oracle, IMonoBehaviour asset, bool includeExternal)
	{
		Assert.That(structure.Type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "root", "alias", "missing", "items", "inline", "references" }));
		Assert.That(ReadEdges(structure), Is.EqualTo(oracle.Edges));
		Assert.That(oracle.Edges.Select(e => e.Rid), Is.EqualTo(new long[] { 101, 101, -2, 4294967419, -2, 101, 4294967419 }));
		Assert.That(structure["references"]["version"].AsInt32, Is.EqualTo(2));
		SerializableStructure[] entries = ReadEntries(structure);
		Assert.That(entries.Select(e => e["rid"].AsInt64), Is.EqualTo(new long[] { -2, 101, 4294967419 }));
		Assert.That(entries[0]["data"].AsStructure.Fields, Is.Empty);
		Dictionary<long, SerializableStructure> nodes = entries.Skip(1).ToDictionary(e => e["rid"].AsInt64);
		foreach (EditorGraphNode expected in oracle.Nodes)
		{
			SerializableStructure node = nodes[expected.Rid];
			Assert.That(node["type"]["class"].AsString, Is.EqualTo(expected.Class));
			Assert.That(node["type"]["ns"].AsString, Is.EqualTo(oracle.ScriptNamespace));
			Assert.That(node["type"]["asm"].AsString, Is.EqualTo(oracle.ScriptAssembly));
			SerializableStructure data = node["data"].AsStructure;
			Assert.That(data.Type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "label", "next", "target", expected.Class == "NumberNode" ? "number" : "text" }));
			Assert.That(data["label"].AsString, Is.EqualTo(expected.Label));
			Assert.That(data["next"]["rid"].AsInt64, Is.EqualTo(expected.Next));
			Assert.That(nodes.ContainsKey(expected.Next), Is.True, "A cycle edge must point to a concrete registry object.");
			if (expected.Class == "NumberNode") Assert.That(data["number"].AsInt32, Is.EqualTo(-37).And.EqualTo(expected.Number));
			else Assert.That(data["text"].AsString, Is.EqualTo("polymorphic ☃").And.EqualTo(expected.Text));
			PPtr pointer = new(data["target"].AsPPtr);
			IUnityObjectBase? target = asset.Collection.TryGetAsset(pointer);
			bool missing = !includeExternal && expected.TargetType == "UnityEngine.TextAsset";
			Assert.That(pointer.IsNull, Is.False);
			if (missing) Assert.That(target, Is.Null);
			else
			{
				Assert.That(target, Is.Not.Null);
				Assert.That((target as INamed)?.Name.String, Is.EqualTo(expected.TargetName));
				if (target is ITextAsset text) Assert.That(text.Script_C49.String, Is.EqualTo(expected.TargetText));
				else Assert.That(target!.ClassID, Is.EqualTo(1));
			}
		}
		Assert.That(structure.FetchDependencies().Select(p => p.Item1), Is.EqualTo(new[] { "references.RefIds[1].data.target", "references.RefIds[2].data.target" }));
	}

	private static SerializableStructure[] ReadEntries(SerializableStructure structure) => structure[ManagedReferenceTypes.RegistryFieldName]["RefIds"].AsAssetArray.Cast<SerializableStructure>().ToArray();
	private static EditorGraphEdge[] ReadEdges(SerializableStructure s) => [new("root", s["root"]["rid"].AsInt64), new("alias", s["alias"]["rid"].AsInt64),
		new("missing", s["missing"]["rid"].AsInt64), .. s["items"].AsAssetArray.Cast<SerializableStructure>().Select((item, i) => new EditorGraphEdge($"items[{i}]", item["rid"].AsInt64)),
		new("inline.child", s["inline"]["child"]["rid"].AsInt64)];

	internal static string EvidenceDirectory(bool stripped, string kind)
	{
		string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-managed-reference", $"{(stripped ? "Assembly" : "TypeTree")}-{kind}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path); return path;
	}
	internal static void CopyInputs(UnityManagedReferenceFixture fixture, string output)
	{
		File.Copy(Path.Combine(fixture.DirectoryPath, "oracle.json"), Path.Combine(output, "oracle.json"));
		File.Copy(Path.Combine(fixture.DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		foreach (var file in fixture.Manifest.Files)
		{
			string target = Path.Combine(output, "Evidence", "Input", file.Path);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(fixture.DirectoryPath, file.Path), target);
		}
	}
}
