using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Collections;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

// These files retain their real 6000.4 payloads/version signatures. Only metadata is rewritten;
// none of these checks is evidence for Unity's actual 6000.5 wire format.
[Category("SerializedFile23")]
public class SerializedFile23CorpusTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void RewrittenV22ScalarPayloadPreservesValuesAndResolvedPointers(bool externalTrees)
	{
		var fixture = UnityEditorPayloadFixture.Require(); using var manager = fixture.CreateManager();
		var converted = Rewrite(fixture.DirectoryPath, externalTrees);
		GameBundle bundle = GameBundle.FromPaths(converted.Paths, new GameAssetFactory(manager), converted.FileSystem);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		IMonoBehaviour asset = bundle.FetchAssets().OfType<IMonoBehaviour>().Single(m => m.ScriptP?.ClassName_R == fixture.Oracle.ScriptType);
		Assert.That(asset.Structure, externalTrees ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		var fields = asset.LoadStructure(); Assert.That(fields, Is.Not.Null, (asset.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
		var expected = fixture.Oracle.Expected;
		Assert.That(fields!["inherited"].AsInt32, Is.EqualTo(expected.Inherited));
		Assert.That(fields["hidden"].AsInt16, Is.EqualTo(expected.Hidden)); Assert.That(fields["flag"].AsBoolean, Is.EqualTo(expected.Flag));
		Assert.That(fields["label"].AsString, Is.EqualTo(expected.Label)); Assert.That(fields["values"].AsInt32Array, Is.EqualTo(expected.Numbers));
		Assert.That(fields["nested"]["count"].AsInt32, Is.EqualTo(expected.NestedCount));
		var dependencies = fields.FetchDependencies().ToArray();
		Assert.That(dependencies.Select(p => p.Item1), Is.EqualTo(expected.Pointers.Select(p => p.Field)));
		for (int i = 0; i < dependencies.Length; i++)
		{
			var target = asset.Collection.TryGetAsset(dependencies[i].Item2); var pointer = expected.Pointers[i];
			Assert.That(dependencies[i].Item2.IsNull, Is.EqualTo(pointer.IsNull));
			if (pointer.IsNull) Assert.That(target, Is.Null);
			else
			{
				Assert.That((target as INamed)?.Name.String, Is.EqualTo(pointer.Name));
				if (target is ITextAsset text) Assert.That(text.Script_C49.String, Is.EqualTo(pointer.Text));
				Assert.That(target!.ClassID, Is.EqualTo(pointer.Type == "UnityEngine.GameObject" ? 1 : 49));
			}
		}
		GameBundle original = GameBundle.FromPaths([Path.Combine(fixture.DirectoryPath, "WithTypeTree", "payload"),
			Path.Combine(fixture.DirectoryPath, "WithTypeTree", "external")], new GameAssetFactory(manager), LocalFileSystem.Instance);
		foreach (bool primary in new[] { false, true })
		{
			var sourceOutput = ExportSnapshot(original, manager, primary, converted.Output, "V22");
			var rewrittenOutput = ExportSnapshot(bundle, manager, primary, converted.Output, "SyntheticV23");
			Assert.That(rewrittenOutput, Is.EqualTo(sourceOutput), "Script/text export must preserve the original v22 result.");
		}
		VerifyCounts(bundle, externalTrees); converted.Complete("ScalarFieldsPointersAndBothExportsPassed");
	}
	private static string[] ExportSnapshot(GameBundle bundle, UnityEditorPayloadFixture.EditorPayloadManager manager, bool primary, string evidence, string kind)
	{
		VirtualFileSystem output = new();
		var report = SerializedFile23ImportTests.Export(bundle, manager, output, primary);
		Assert.That(report.Succeeded, Is.True);
		File.WriteAllText(Path.Combine(evidence, $"{kind}-{primary}-collection-export-report.json"), output.File.ReadAllText("/Output/collection-export-report.json"));
		string[] snapshot = output.Directory.EnumerateFiles("/Output", "*", SearchOption.AllDirectories)
			.Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".txt", StringComparison.Ordinal) || p.EndsWith(".bytes", StringComparison.Ordinal))
			.Select(p => output.Path.GetRelativePath("/Output", p) + "=" + FixtureCompiler.Hash(output.File.ReadAllBytes(p))).Order().ToArray();
		Assert.That(snapshot, Is.Not.Empty); Assert.That(snapshot, Has.Some.Contains(".cs="));
		Assert.That(snapshot.Any(p => p.Contains(".txt=", StringComparison.Ordinal) || p.Contains(".bytes=", StringComparison.Ordinal)), Is.True);
		File.WriteAllText(Path.Combine(evidence, $"{kind}-{primary}-export-hashes.json"), JsonSerializer.Serialize(snapshot));
		return snapshot;
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RewrittenV22ReferenceTypesPreservePolymorphismAliasesCycleAndExternalTargets(bool externalTrees)
	{
		var fixture = UnityManagedReferenceFixture.Require(); using var manager = fixture.CreateManager();
		var converted = Rewrite(fixture.DirectoryPath, externalTrees);
		GameBundle bundle = GameBundle.FromPaths(converted.Paths, new GameAssetFactory(manager), converted.FileSystem);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		IMonoBehaviour asset = bundle.FetchAssets().OfType<IMonoBehaviour>().Single(m => m.ScriptP?.ClassName_R == fixture.Oracle.ScriptType);
		Assert.That(asset.Structure, externalTrees ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		var structure = asset.LoadStructure(); Assert.That(structure, Is.Not.Null, (asset.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
		UnityManagedReferenceTests.AssertGraph(structure!, fixture.Oracle, asset, true);
		VerifyCounts(bundle, externalTrees); converted.Complete("ManagedReferenceGraphPassed");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RewrittenV22PrefabPayloadPreservesHierarchyAndTwentyFivePointerEdges(bool externalTrees)
	{
		var fixture = UnityPrefabFixture.Require(); using var manager = fixture.CreateManager();
		var converted = Rewrite(fixture.DirectoryPath, externalTrees);
		GameBundle bundle = GameBundle.FromPaths(converted.Paths, new GameAssetFactory(manager), converted.FileSystem);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		UnityPrefabGraphTests.Verify(bundle, fixture, true);
		VerifyCounts(bundle, externalTrees); converted.Complete("PrefabGraphPassed");
	}

	private static void VerifyCounts(GameBundle bundle, bool externalTrees)
	{
		CollectionExportReport report = CollectionExportReport.Create("Verification", bundle); report.Completed = true;
		Assert.That(report.Succeeded, Is.True);
		if (externalTrees) Assert.That(report.ExtractedTypeTrees.Sum(c => c.ExtractedTypeTrees), Is.GreaterThan(0));
		else Assert.That(report.ExtractedTypeTrees, Is.Empty);
	}
	private static RewrittenFixture Rewrite(string corpus, bool externalTrees)
	{
		VirtualFileSystem fs = new(); fs.Directory.Create("/Synthetic"); List<string> paths = []; List<object> evidence = [];
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "serialized-file23", $"Corpus-{externalTrees}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(output);
		File.Copy(Path.Combine(corpus, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		File.Copy(Path.Combine(corpus, "oracle.json"), Path.Combine(output, "oracle.json"));
		foreach (string name in new[] { "payload", "external" })
		{
			string input = Path.Combine(corpus, "WithTypeTree", name);
			using var container = (FileContainer)SchemeReader.LoadFile(input, LocalFileSystem.Instance);
			container.ReadContentsRecursively();
			foreach (SerializedFile source in container.FetchSerializedFiles())
			{
				Assert.That(source.Generation, Is.EqualTo(FormatVersion.LargeFilesSupport));
				Assert.That(source.HasTypeTree, Is.True);
				SerializedFileBuilder builder = new()
				{
					Generation = FormatVersion.ExtractedTypeTreeSupport, Version = source.Version, Platform = source.Platform,
					EndianType = source.EndianType, HasTypeTree = source.HasTypeTree, UserInformation = source.UserInformation,
				};
				builder.Types.AddRange(source.Types.ToArray()); builder.RefTypes.AddRange(source.RefTypes.ToArray());
				builder.Objects.AddRange(source.Objects.ToArray()); builder.Dependencies.AddRange(source.Dependencies.ToArray());
				builder.ScriptTypes.AddRange(source.ScriptTypes.ToArray());
				if (externalTrees)
				{
					foreach (var type in builder.Types.Where(t => t.TypeID == 114)) type.IsTypeTreeExtracted = true;
					foreach (var type in builder.RefTypes) type.IsTypeTreeExtracted = true;
				}
				using var file = builder.Build(); byte[] bytes = file.ToByteArray(); string path = "/Synthetic/" + source.Name;
				using (Stream stream = fs.File.Create(path)) { stream.Write(bytes); stream.Position = 0; }
				paths.Add(path); File.WriteAllBytes(Path.Combine(output, source.Name + ".bin"), bytes);
				using var read = (SerializedFile)SchemeReader.ReadFile(bytes, path, source.Name);
				Assert.That(read.Version, Is.EqualTo(source.Version)); Assert.That(read.Objects.Length, Is.EqualTo(source.Objects.Length));
				Assert.That(read.Types.Length, Is.EqualTo(source.Types.Length)); Assert.That(read.RefTypes.Length, Is.EqualTo(source.RefTypes.Length));
				Assert.That(read.Dependencies.ToArray(), Is.EqualTo(source.Dependencies.ToArray()));
				for (int i = 0; i < read.Objects.Length; i++)
				{
					Assert.That(read.Objects[i].FileID, Is.EqualTo(source.Objects[i].FileID));
					Assert.That(read.Objects[i].ObjectData, Is.EqualTo(source.Objects[i].ObjectData));
				}
				evidence.Add(new { source.Name, SourceFormat = 22, SyntheticFormat = 23, Sha256 = FixtureCompiler.Hash(bytes),
					Objects = read.Objects.Length, Types = read.Types.Length, ReferenceTypes = read.RefTypes.Length, read.ExtractedTypeTreeCount,
					ObjectHashes = read.Objects.ToArray().Select(o => new { o.FileID, Sha256 = FixtureCompiler.Hash(o.ObjectData) }).ToArray() });
			}
		}
		Assert.That(paths, Is.Not.Empty);
		return new(fs, paths, output, evidence);
	}
	private sealed record RewrittenFixture(VirtualFileSystem FileSystem, List<string> Paths, string Output, List<object> Files)
	{
		public void Complete(string result)
		{
			File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
			{
				Source = "SyntheticV23MetadataFromUnity6000.4V22", RealUnity6000_5Validation = "NotRun", Files, Result = result,
			}, new JsonSerializerOptions { WriteIndented = true }));
		}
	}
}
