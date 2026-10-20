using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Metadata;
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

[Category("UnityEditorPayload")]
public class UnityEditorPayloadTests
{
	[TestCase(false, true)]
	[TestCase(true, true)]
	[TestCase(false, false)]
	[TestCase(true, false)]
	public void EditorGeneratedBundlesMatchSerializedFieldAndReferenceOracle(bool stripped, bool includeExternal)
	{
		UnityEditorPayloadFixture fixture = UnityEditorPayloadFixture.Require();
		using var manager = fixture.CreateManager();
		string mode = stripped ? "WithoutTypeTree" : "WithTypeTree";
		string directory = Path.Combine(fixture.DirectoryPath, mode);
		List<string> inputs = [Path.Combine(directory, "payload")];
		if (includeExternal) inputs.Add(Path.Combine(directory, "external"));
		GameBundle bundle = GameBundle.FromPaths(inputs, new GameAssetFactory(manager), LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		IMonoBehaviour asset = bundle.FetchAssets().OfType<IMonoBehaviour>().Single(m => m.ScriptP?.ClassName_R == fixture.Oracle.ScriptType
			&& m.ScriptP.Namespace == fixture.Oracle.ScriptNamespace);
		Assert.That(asset.ScriptP!.GetAssemblyNameFixed(), Is.EqualTo(fixture.Oracle.ScriptAssembly));
		Assert.That(asset.Collection.Version.ToString(), Is.EqualTo(fixture.Oracle.UnityVersion));
		Assert.That(asset.Structure, stripped ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		SerializableStructure structure = asset.LoadStructure()!;
		Assert.That(structure, Is.Not.Null, (asset.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
		EditorValueOracle expected = fixture.Oracle.Expected;
		Assert.That(expected.Inherited, Is.EqualTo(-17)); Assert.That(expected.Hidden, Is.EqualTo(-1234));
		Assert.That(expected.Label, Is.EqualTo("Merhaba ☃")); Assert.That(expected.Numbers, Is.EqualTo(new[] { 7, -9, 42 }));
		Assert.That(expected.Flag, Is.True); Assert.That(expected.NestedCount, Is.EqualTo(91));
		Assert.That(structure.Type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "inherited", "hidden", "flag", "label", "values", "direct", "links", "nested" }));
		var observedValues = new EditorValueOracle(structure["inherited"].AsInt32, structure["hidden"].AsInt16, structure["flag"].AsBoolean,
			structure["label"].AsString, structure["values"].AsInt32Array, structure["nested"].AsStructure["count"].AsInt32, []);
		Assert.That(observedValues.Inherited, Is.EqualTo(expected.Inherited)); Assert.That(observedValues.Hidden, Is.EqualTo(expected.Hidden));
		Assert.That(observedValues.Flag, Is.EqualTo(expected.Flag)); Assert.That(observedValues.Label, Is.EqualTo(expected.Label));
		Assert.That(observedValues.Numbers, Is.EqualTo(expected.Numbers)); Assert.That(observedValues.NestedCount, Is.EqualTo(expected.NestedCount));
		var dependencies = structure.FetchDependencies().ToArray();
		Assert.That(dependencies.Select(p => p.Item1), Is.EqualTo(expected.Pointers.Select(p => p.Field)));
		if (!includeExternal)
		{
			GameBundle complete = GameBundle.FromPaths([Path.Combine(directory, "payload"), Path.Combine(directory, "external")], new GameAssetFactory(manager), LocalFileSystem.Instance);
			IMonoBehaviour completeAsset = complete.FetchAssets().OfType<IMonoBehaviour>().Single(m => m.ScriptP?.ClassName_R == fixture.Oracle.ScriptType);
			Assert.That(dependencies.Select(p => p.Item2), Is.EqualTo(completeAsset.LoadStructure()!.FetchDependencies().Select(p => p.Item2)),
				"Missing external files must not change the serialized pointer IDs.");
		}
		List<object> observedPointers = [];
		for (int i = 0; i < expected.Pointers.Length; i++)
		{
			EditorPointerOracle pointer = expected.Pointers[i]; PPtr raw = dependencies[i].Item2;
			IUnityObjectBase? target = asset.Collection.TryGetAsset(raw);
			Assert.That(raw.IsNull, Is.EqualTo(pointer.IsNull));
			bool missingExternal = !includeExternal && pointer.Type == "UnityEngine.TextAsset";
			if (pointer.IsNull || missingExternal) Assert.That(target, Is.Null);
			else
			{
				Assert.That(target, Is.Not.Null);
				Assert.That(((INamed)target!).Name.String, Is.EqualTo(pointer.Name));
				Assert.That(target.ClassID, Is.EqualTo(pointer.Type == "UnityEngine.GameObject" ? 1 : 49));
				Assert.That(ReferenceEquals(target.Collection, asset.Collection), Is.EqualTo(pointer.Type == "UnityEngine.GameObject"));
				if (target is ITextAsset text) Assert.That(text.Script_C49.String, Is.EqualTo(pointer.Text));
			}
			observedPointers.Add(new { pointer.Field, raw.FileID, raw.PathID, IsNull = raw.IsNull, Resolved = target is not null,
				TargetName = (target as INamed)?.Name.String, TargetCollection = target?.Collection.Name, MissingExternal = missingExternal });
		}
		WriteEvidence(fixture, stripped, includeExternal, asset, observedValues, observedPointers);
	}

	[Test]
	public void ManifestLoaderAcceptsOnlyExplicitHashedInputs()
	{
		var fs = MakeFixtureStorage();
		Assert.That(UnityEditorPayloadFixture.Load("/Fixture", fs).Oracle.EditorSceneReadbackVerified, Is.True);
	}

	[TestCase("changed")]
	[TestCase("missing")]
	[TestCase("path")]
	[TestCase("oracle")]
	public void InvalidFixtureInputsAreRejectedBeforeBundleParsing(string kind)
	{
		VirtualFileSystem fs = MakeFixtureStorage();
		if (kind == "changed") fs.File.WriteAllBytes("/Fixture/WithTypeTree/payload", [99]);
		if (kind == "missing") fs.File.Delete("/Fixture/WithTypeTree/payload");
		if (kind is "path" or "oracle")
		{
			var manifest = JsonSerializer.Deserialize<EditorFixtureManifest>(fs.File.ReadAllText("/Fixture/fixture-manifest.json"))!;
			if (kind == "path") manifest.Files[0] = new("../outside", new string('0', 64));
			else
			{
				var oracle = JsonSerializer.Deserialize<EditorPayloadOracle>(fs.File.ReadAllText("/Fixture/oracle.json"))! with { EditorSceneReadbackVerified = false };
				byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(oracle); fs.File.WriteAllBytes("/Fixture/oracle.json", bytes);
				int index = Array.FindIndex(manifest.Files, f => f.Path == "oracle.json");
				manifest.Files[index] = new("oracle.json", FixtureCompiler.Hash(bytes));
			}
			fs.File.WriteAllText("/Fixture/fixture-manifest.json", JsonSerializer.Serialize(manifest));
		}
		if (kind == "missing") Assert.Throws<FileNotFoundException>(() => UnityEditorPayloadFixture.Load("/Fixture", fs));
		else Assert.Throws<InvalidDataException>(() => UnityEditorPayloadFixture.Load("/Fixture", fs));
	}

	private static VirtualFileSystem MakeFixtureStorage()
	{
		VirtualFileSystem fs = new(); fs.Directory.Create("/Fixture");
		string[] paths = ["Assembly-CSharp.dll", "oracle.json", "WithTypeTree/payload", "WithTypeTree/external", "WithoutTypeTree/payload",
			"WithoutTypeTree/external", "Sources/EditorPayload.cs", "Sources/PayloadFixtureBuilder.cs", "Sources/PayloadScene.unity", "Sources/Payload.prefab"];
		EditorPayloadOracle oracle = new(1, "6000.4.0f1", "StandaloneLinux64", "Assembly-CSharp", "AssetRipper.EditorPayloadFixture", "EditorPayload", true,
			new(-17, -1234, true, "Merhaba ☃", [7, -9, 42], 91, []));
		List<EditorFixtureFile> files = [];
		foreach (string path in paths)
		{
			fs.Directory.Create(fs.Path.GetDirectoryName("/Fixture/" + path)!);
			byte[] bytes = path == "oracle.json" ? JsonSerializer.SerializeToUtf8Bytes(oracle) : [1, 2, 3];
			fs.File.WriteAllBytes("/Fixture/" + path, bytes); files.Add(new(path, FixtureCompiler.Hash(bytes)));
		}
		fs.File.WriteAllText("/Fixture/fixture-manifest.json", JsonSerializer.Serialize(new EditorFixtureManifest(1, "6000.4.0f1", "StandaloneLinux64", files.ToArray())));
		return fs;
	}

	private static void WriteEvidence(UnityEditorPayloadFixture fixture, bool stripped, bool includeExternal, IMonoBehaviour asset,
		EditorValueOracle values, List<object> pointers)
	{
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-editor-payload", $"{(stripped ? "Assembly" : "TypeTree")}-{includeExternal}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(output);
		File.Copy(Path.Combine(fixture.DirectoryPath, "oracle.json"), Path.Combine(output, "oracle.json"));
		File.Copy(Path.Combine(fixture.DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		foreach (var input in fixture.Manifest.Files)
		{
			string target = Path.Combine(output, "Input", input.Path);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(Path.Combine(fixture.DirectoryPath, input.Path), target);
		}
		string path = Path.Combine(output, "editor-payload-verification.json");
		File.WriteAllText(path, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, fixture.Manifest.BuildTarget, Source = "UnityEditorGeneratedAssetBundle",
			ReferenceProfile = PinnedUnityReferences.ReadManifest(),
			SchemaSource = stripped ? "Assembly" : "TypeTree", IncludeExternal = includeExternal, Values = values, Pointers = pointers,
			FieldAndReferenceOracle = "Passed", fixture.Oracle.EditorSceneReadbackVerified,
			ExportedProjectEditorImport = "NotRun", SourceCollection = asset.Collection.Name,
		}, new JsonSerializerOptions { WriteIndented = true }));
		TestContext.AddTestAttachment(path, "Actual Editor-generated AssetBundle fields and PPtr target verification");
	}
}
