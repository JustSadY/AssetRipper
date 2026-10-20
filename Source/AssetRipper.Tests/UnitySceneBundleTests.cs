using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_3;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("UnityScene")]
public class UnitySceneBundleTests
{
	[TestCase("TypeTree", true)]
	[TestCase("DisableWriteTypeTree", true)]
	[TestCase("AssemblyFallback", true)]
	[TestCase("TypeTree", false)]
	[TestCase("DisableWriteTypeTree", false)]
	[TestCase("AssemblyFallback", false)]
	public void RealSceneBundlePreservesHierarchyFieldsAndCrossRootPointers(string mode, bool includeExternal)
	{
		UnitySceneFixture fixture = UnitySceneFixture.Require(); using var manager = fixture.CreateManager();
		var (bundle, schemas) = Load(fixture, manager, mode, includeExternal);
		Assert.That(bundle.FetchAssets().OfType<ILevelGameManager>().Any(), Is.True, "Scene collections must contain native scene managers.");
		IGameObject[] objects = bundle.FetchAssets().OfType<IGameObject>().OrderBy(g => g.GetTransform().GetRootPath(), StringComparer.Ordinal).ToArray();
		Assert.That(objects.Select(g => g.GetTransform().GetRootPath()), Is.EqualTo(fixture.Oracle.Nodes.Select(n => n.Path)));
		for (int i = 0; i < objects.Length; i++)
		{
			var node = fixture.Oracle.Nodes[i]; var transform = objects[i].GetTransform();
			Assert.That(objects[i].GetIsActive(), Is.EqualTo(node.ActiveSelf));
			Assert.That(new[] { transform.LocalPosition_C4.X, transform.LocalPosition_C4.Y, transform.LocalPosition_C4.Z }, Is.EqualTo(node.Position).Within(0.00001f));
			Assert.That(new[] { transform.LocalScale_C4.X, transform.LocalScale_C4.Y, transform.LocalScale_C4.Z }, Is.EqualTo(node.Scale).Within(0.00001f));
			Assert.That(new[] { transform.LocalRotation_C4.X, transform.LocalRotation_C4.Y, transform.LocalRotation_C4.Z, transform.LocalRotation_C4.W }, Is.EqualTo(node.Rotation).Within(0.00001f));
			if (transform.Father_C4P != null) Assert.That(transform.CalculateRootOrder(), Is.EqualTo(node.SiblingIndex));
		}
		IMonoBehaviour[] components = bundle.FetchAssets().OfType<IMonoBehaviour>().OrderBy(c => c.GameObjectP!.GetTransform().GetRootPath(), StringComparer.Ordinal).ToArray();
		Assert.That(components.Length, Is.EqualTo(2)); List<object> pointers = [];
		foreach (var component in components) Assert.That(component.Structure, mode == "AssemblyFallback" ? Is.TypeOf<UnloadedStructure>() : Is.TypeOf<SerializableStructure>());
		foreach (var component in components)
		{
			string path = component.GameObjectP!.GetTransform().GetRootPath(); var expected = fixture.Oracle.Components.Single(c => c.Path == path);
			Assert.That(component.ScriptP?.ClassName_R.String, Is.EqualTo(fixture.Oracle.ScriptType));
			SerializableStructure fields = component.LoadStructure()!;
			Assert.That(fields, Is.Not.Null, (component.Structure as UnreadableStructure)?.ReadResult.Diagnostic);
			Assert.That(fields["inherited"].AsInt32, Is.EqualTo(expected.Inherited)); Assert.That(fields["hidden"].AsInt16, Is.EqualTo(expected.Hidden));
			Assert.That(fields["score"].AsInt32, Is.EqualTo(expected.Score)); Assert.That(fields["label"].AsString, Is.EqualTo(expected.Label));
			Assert.That(fields["nested"]["count"].AsInt32, Is.EqualTo(expected.NestedCount));
			var dependencies = fields.FetchDependencies().ToDictionary(p => p.Item1, p => p.Item2);
			Assert.That(dependencies.Keys, Is.EquivalentTo(expected.Pointers.Select(p => p.Field)));
			foreach (var pointer in expected.Pointers)
			{
				PPtr raw = dependencies[pointer.Field]; IUnityObjectBase? target = component.Collection.TryGetAsset(raw);
				bool missing = !includeExternal && pointer.Type == "UnityEngine.TextAsset";
				Assert.That(raw.IsNull, Is.EqualTo(pointer.IsNull));
				if (pointer.IsNull || missing) Assert.That(target, Is.Null);
				else
				{
					Assert.That(target, Is.Not.Null);
					if (target is ITextAsset text) Assert.That(text.Script_C49.String, Is.EqualTo(pointer.Text));
					else
					{
						IGameObject go = target is IGameObject gameObject ? gameObject : target is IMonoBehaviour behaviour ? behaviour.GameObjectP! : ((IComponent)target!).GameObject_C2P!;
						Assert.That(go.GetTransform().GetRootPath(), Is.EqualTo(pointer.ScenePath));
						Assert.That(target!.ClassID, Is.EqualTo(pointer.Type == "UnityEngine.GameObject" ? 1 : pointer.Type == "UnityEngine.Transform" ? 4 : 114));
					}
				}
				pointers.Add(new { Component = path, pointer.Field, raw.FileID, raw.PathID, Resolved = target is not null, MissingExternal = missing });
			}
		}
		if (!includeExternal)
		{
			var complete = Load(fixture, manager, mode, true).Bundle.FetchAssets().OfType<IMonoBehaviour>().OrderBy(c => c.GameObjectP!.GetTransform().GetRootPath(), StringComparer.Ordinal);
			IEnumerable<PPtr> observed = components.SelectMany(c => c.LoadStructure()!.FetchDependencies().Select(p => p.Item2));
			IEnumerable<PPtr> full = complete.SelectMany(c => c.LoadStructure()!.FetchDependencies().Select(p => p.Item2));
			Assert.That(observed, Is.EqualTo(full));
		}
		string output = UnitySceneFixture.EvidenceDirectory(mode, "Read-" + includeExternal); fixture.CopyInputs(output);
		File.WriteAllText(Path.Combine(output, "scene-bundle-verification.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, SchemaSource = mode == "AssemblyFallback" ? "Assembly" : "TypeTree", BuildMode = mode == "TypeTree" ? "Default" : "DisableWriteTypeTree",
			ControlledTypeTreeSuppression = mode == "AssemblyFallback", Schemas = schemas, IncludeExternal = includeExternal,
			HierarchyAndFields = "Passed", Pointers = pointers, ExportedSceneEditorImport = "NotRun", ReferenceProfile = PinnedUnityReferences.ReadManifest(),
		}, new JsonSerializerOptions { WriteIndented = true }));
	}
	internal static (GameBundle Bundle, List<SceneSchemaObservation> Schemas) Load(UnitySceneFixture fixture, UnityEditorPayloadFixture.EditorPayloadManager manager, string mode, bool includeExternal = true)
	{
		Assert.That(mode, Is.AnyOf("TypeTree", "DisableWriteTypeTree", "AssemblyFallback"));
		string input = Path.Combine(fixture.DirectoryPath, mode == "TypeTree" ? "WithTypeTree" : "WithoutTypeTree");
		List<string> paths = [Path.Combine(input, "payload")]; if (includeExternal) paths.Add(Path.Combine(input, "external"));
		ObservingFactory factory = new(manager, mode == "AssemblyFallback");
		GameBundle bundle = GameBundle.FromPaths(paths, factory, LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		Assert.That(factory.Schemas, Has.Count.EqualTo(2));
		foreach (var schema in factory.Schemas)
		{
			// This pinned scene corpus retains trees even with DisableWriteTypeTree.
			// The separate controlled case withholds only MonoBehaviour schema metadata.
			Assert.That(schema.SourceTypeTreeNodeCount, Is.GreaterThan(0));
			Assert.That(schema.InitialStructure, Is.EqualTo(mode == "AssemblyFallback" ? nameof(UnloadedStructure) : nameof(SerializableStructure)));
		}
		return (bundle, factory.Schemas);
	}
	internal sealed record SceneSchemaObservation(string SourceName, long PathID, int SourceTypeTreeNodeCount, bool TypeTreeSuppressed, string? InitialStructure, string ObjectSha256);
	private sealed class ObservingFactory(UnityEditorPayloadFixture.EditorPayloadManager manager, bool suppressTypeTree) : AssetFactoryBase
	{
		private readonly GameAssetFactory factory = new(manager);
		public List<SceneSchemaObservation> Schemas { get; } = [];
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		{
			bool suppress = suppressTypeTree && info.ClassID == 114;
			var asset = factory.ReadAsset(info, bytes, suppress ? null : type, references);
			if (asset is IMonoBehaviour behaviour) Schemas.Add(new(info.Collection.OriginalName, info.PathID, type?.OldType.Nodes.Count ?? 0,
				suppress, behaviour.Structure?.GetType().Name, FixtureCompiler.Hash(bytes.ToArray())));
			return asset;
		}
	}
}
