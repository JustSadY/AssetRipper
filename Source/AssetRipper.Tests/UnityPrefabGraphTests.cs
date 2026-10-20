using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Configuration;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("UnityPrefab")]
public class UnityPrefabGraphTests
{
	[TestCase(false, true)]
	[TestCase(true, true)]
	[TestCase(false, false)]
	[TestCase(true, false)]
	public void EffectiveVariantAndNestedValuesSurviveReadingAndProcessing(bool stripped, bool includeExternal)
	{
		var fixture = UnityPrefabFixture.Require(); using var manager = fixture.CreateManager();
		GameBundle bundle = Load(fixture, manager, stripped, includeExternal);
		var components = bundle.FetchAssets().OfType<IMonoBehaviour>().ToArray(); Assert.That(components, Has.Length.EqualTo(4));
		foreach (var component in components) Assert.That(component.Structure, stripped ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		int sourcePrefabObjects = bundle.FetchAssets().OfType<IPrefabInstance>().Count();
		object[] before = Verify(bundle, fixture, includeExternal);
		if (!includeExternal)
		{
			var complete = Load(fixture, manager, stripped, true);
			Assert.That(PointerIds(bundle), Is.EqualTo(PointerIds(complete)), "Unresolved external targets must keep their native IDs.");
		}
		var data = new GameData(bundle, bundle.GetMaxUnityVersion(), manager, null); new ExportHandler(new FullConfiguration()).Process(data);
		object[] after = Verify(bundle, fixture, includeExternal);
		Assert.That(bundle.FetchAssets().OfType<PrefabHierarchyObject>().Select(p => p.Root.Name.String), Is.EquivalentTo(fixture.Oracle.Prefabs.Select(p => p.Root)));
		string output = UnityPrefabFixture.EvidenceDirectory(stripped, "Read-" + includeExternal); fixture.CopyInputs(output);
		File.WriteAllText(Path.Combine(output, "prefab-graph-verification.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, SchemaSource = stripped ? "Assembly" : "TypeTree", IncludeExternal = includeExternal,
			SourcePrefabInstanceObjectCount = sourcePrefabObjects, EffectiveValues = "Passed", BeforeProcessing = before, AfterProcessing = after,
			AuthoringRelations = "NotValidatedByEffectiveValues", EditorReimport = "NotRun",
		}, new JsonSerializerOptions { WriteIndented = true }));
	}
	internal static GameBundle Load(UnityPrefabFixture fixture, UnityEditorPayloadFixture.EditorPayloadManager manager, bool stripped, bool includeExternal = true)
	{
		string path = Path.Combine(fixture.DirectoryPath, stripped ? "WithoutTypeTree" : "WithTypeTree");
		List<string> paths = [Path.Combine(path, "payload")]; if (includeExternal) paths.Add(Path.Combine(path, "external"));
		GameBundle bundle = GameBundle.FromPaths(paths, new GameAssetFactory(manager), LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty); return bundle;
	}
	private static object[] PointerIds(GameBundle bundle) => bundle.FetchAssets().OfType<IMonoBehaviour>()
		.OrderBy(c => c.GameObjectP!.GetTransform().GetRootPath(), StringComparer.Ordinal)
		.SelectMany(c => c.LoadStructure()!.FetchDependencies().Select(p => (object)new { Component = c.GameObjectP!.GetTransform().GetRootPath(), Field = p.Item1, p.Item2.FileID, p.Item2.PathID })).ToArray();
	internal static object[] Verify(GameBundle bundle, UnityPrefabFixture fixture, bool includeExternal)
	{
		var objects = bundle.FetchAssets().OfType<IGameObject>().ToDictionary(g => g.GetTransform().GetRootPath());
		Assert.That(objects.Keys, Is.EquivalentTo(fixture.Oracle.Prefabs.SelectMany(p => p.Nodes).Select(n => n.Path)));
		foreach (var node in fixture.Oracle.Prefabs.SelectMany(p => p.Nodes))
		{
			var go = objects[node.Path]; var t = go.GetTransform(); Assert.That(go.GetIsActive(), Is.EqualTo(node.ActiveSelf));
			Assert.That(new[] { t.LocalPosition_C4.X, t.LocalPosition_C4.Y, t.LocalPosition_C4.Z }, Is.EqualTo(node.Position).Within(0.00001f));
			Assert.That(new[] { t.LocalRotation_C4.X, t.LocalRotation_C4.Y, t.LocalRotation_C4.Z, t.LocalRotation_C4.W }, Is.EqualTo(node.Rotation).Within(0.00001f));
			Assert.That(new[] { t.LocalScale_C4.X, t.LocalScale_C4.Y, t.LocalScale_C4.Z }, Is.EqualTo(node.Scale).Within(0.00001f));
			if (t.Father_C4P != null) Assert.That(t.CalculateRootOrder(), Is.EqualTo(node.SiblingIndex));
		}
		List<object> observations = [];
		foreach (var expected in fixture.Oracle.Prefabs.SelectMany(p => p.Components))
		{
			var component = bundle.FetchAssets().OfType<IMonoBehaviour>().Single(c => c.GameObjectP!.GetTransform().GetRootPath() == expected.Path);
			var fields = component.LoadStructure(); Assert.That(fields, Is.Not.Null, (component.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
			Assert.That(fields!["inherited"].AsInt32, Is.EqualTo(expected.Inherited)); Assert.That(fields["hidden"].AsInt16, Is.EqualTo(expected.Hidden));
			Assert.That(fields["score"].AsInt32, Is.EqualTo(expected.Score)); Assert.That(fields["label"].AsString, Is.EqualTo(expected.Label));
			Assert.That(fields["nested"]["count"].AsInt32, Is.EqualTo(expected.NestedCount));
			var pointers = fields.FetchDependencies().ToDictionary(p => p.Item1, p => p.Item2);
			Assert.That(pointers.Keys, Is.EquivalentTo(expected.Pointers.Select(p => p.Field)));
			foreach (var pointer in expected.Pointers)
			{
				PPtr raw = pointers[pointer.Field]; var target = component.Collection.TryGetAsset(raw);
				bool missing = !includeExternal && pointer.Type == "UnityEngine.TextAsset";
				Assert.That(raw.IsNull, Is.EqualTo(pointer.IsNull));
				if (missing || pointer.IsNull) Assert.That(target, Is.Null);
				else if (target is ITextAsset text) Assert.That(text.Script_C49.String, Is.EqualTo(pointer.Text));
				else
				{
					Assert.That(target, Is.Not.Null);
					var go = target is IGameObject g ? g : target is IMonoBehaviour m ? m.GameObjectP! : ((IComponent)target!).GameObject_C2P!;
					Assert.That(go.GetTransform().GetRootPath(), Is.EqualTo(pointer.ScenePath));
					Assert.That(target!.ClassID, Is.EqualTo(pointer.Type == "UnityEngine.GameObject" ? 1 : pointer.Type == "UnityEngine.Transform" ? 4 : 114));
				}
				observations.Add(new { Component = expected.Path, pointer.Field, raw.FileID, raw.PathID, Resolved = target != null, MissingExternal = missing });
			}
		}
		return observations.ToArray();
	}
}
