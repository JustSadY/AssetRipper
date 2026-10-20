using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Collections;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;
using AssetRipper.SourceGenerated.Extensions;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("UnityPrefab")]
public class PrefabReconstructionReportTests
{
	[TestCase("GeneratedFromHierarchy")]
	[TestCase("SourceSerializedObject")]
	[TestCase("Unknown")]
	public void PrefabProvenanceDoesNotClaimAuthoringValidation(string expectedOrigin)
	{
		UnityVersion version = UnityVersion.Parse("6000.4.0f1"); GameBundle bundle = new();
		var collection = bundle.AddNewProcessedCollection("Generated", version);
		var root = collection.CreateAsset(1, GameObject.Create); root.Name = "Root";
		var transform = collection.CreateTransform(); transform.InitializeDefault(); transform.GameObject_C4P = root; root.AddComponent(ClassIDType.Transform, transform);
		IPrefabInstance prefab;
		if (expectedOrigin == "SourceSerializedObject")
		{
			SerializedType type = new() { TypeID = 1001, ScriptTypeIndex = -1 };
			SerializedFileBuilder builder = new() { Generation = FormatVersion.LargeFilesSupport, Version = version };
			builder.Types.Add(type); builder.Objects.Add(new ObjectInfo(type) { FileID = 123, SerializedTypeIndex = 0, ObjectData = [] });
			var file = builder.Build(); file.Name = "source.assets";
			prefab = bundle.AddCollectionFromSerializedFile(file, new PrefabFactory()).OfType<IPrefabInstance>().Single();
		}
		else prefab = root.CreatePrefabForRoot(collection);
		PrefabHierarchyObject.Create(collection, root, prefab, prefabWasGenerated: expectedOrigin == "GeneratedFromHierarchy");
		var report = CollectionExportReport.Create("UnityProject", bundle); report.Completed = true;
		Assert.That(report.Succeeded, Is.True, "Collection-loop success remains distinct from authoring validation.");
		Assert.That(report.PrefabReconstructions, Has.Count.EqualTo(1));
		var entry = report.PrefabReconstructions.Single();
		Assert.That(entry.PrefabOrigin, Is.EqualTo(expectedOrigin));
		Assert.That(entry.AuthoringRelationsValidation, Is.EqualTo("NotRun"));
		Assert.That(entry.RootPathId, Is.EqualTo(root.PathID)); Assert.That(entry.PrefabPathId, Is.EqualTo(prefab.PathID));
		var fs = new VirtualFileSystem(); report.Write("/Output", fs);
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(json.RootElement.GetProperty("PrefabReconstructions")[0].GetProperty("PrefabOrigin").GetString(), Is.EqualTo(entry.PrefabOrigin));
		Assert.That(json.RootElement.GetProperty("PrefabReconstructions")[0].GetProperty("AuthoringRelationsValidation").GetString(), Is.EqualTo("NotRun"));
	}
	[Test]
	public void EmptyBundleReportsNoPrefabReconstructions()
	{
		Assert.That(CollectionExportReport.Create("PrimaryContent", new GameBundle()).PrefabReconstructions, Is.Empty);
	}
	private sealed class PrefabFactory : AssetFactoryBase
	{
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
			=> PrefabInstance.Create(info);
	}
}
