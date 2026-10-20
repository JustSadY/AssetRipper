using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetRipper.EditorPayloadFixture;
using AssetRipper.EditorSceneFixture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class PrefabGraphFixtureBuilder
{
	private const string BasePath = "Assets/BaseRig.prefab", VariantPath = "Assets/VariantRig.prefab", NestedPath = "Assets/NestedRig.prefab";
	[Serializable] public sealed class Link { public string Path; public string SourceAssetPath; public string SourceRoot; public string[] PropertyPaths; }
	[Serializable] public sealed class Prefab
	{
		public string Root; public string AssetType; public Link[] Links;
		public SceneFixtureBuilder.Node[] Nodes; public SceneFixtureBuilder.Component[] Components;
	}
	[Serializable] public sealed class Oracle
	{
		public int SchemaVersion = 1; public string UnityVersion; public string BuildTarget = "StandaloneLinux64";
		public string ScriptAssembly = "Assembly-CSharp", ScriptNamespace = "AssetRipper.EditorSceneFixture", ScriptType = "ScenePayload";
		public bool EditorPrefabReadbackVerified; public Prefab[] Prefabs;
	}

	public static void Build()
	{
		if (Application.unityVersion != "6000.4.0f1") throw new InvalidOperationException("Requires Unity 6000.4.0f1.");
		string output = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_OUTPUT");
		if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("Set an absolute fixture output path.");
		if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Fixture output must be empty.");
		Directory.CreateDirectory(output); EditorSettings.serializationMode = SerializationMode.ForceText;
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		const string externalPath = "Assets/PrefabExternal.txt";
		File.WriteAllText(externalPath, "Prefab graph external: ☃"); AssetDatabase.ImportAsset(externalPath, ImportAssetOptions.ForceSynchronousImport);
		TextAsset external = AssetDatabase.LoadAssetAtPath<TextAsset>(externalPath);
		GameObject root = new GameObject("BaseRig"); GameObject socket = new GameObject("Socket");
		socket.transform.SetParent(root.transform, false); socket.transform.localPosition = new Vector3(1, 2, 3);
		ScenePayload payload = root.AddComponent<ScenePayload>(); payload.inherited = -17; SetHidden(payload, -1234);
		payload.score = 11; payload.label = "Base"; payload.direct = socket; payload.links = new UnityEngine.Object[] { external, null, socket.transform };
		payload.nested = new NestedPayload { count = 33, target = socket };
		GameObject baseAsset = Save(root, BasePath); UnityEngine.Object.DestroyImmediate(root);
		GameObject variant = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset); variant.name = "VariantRig";
		payload = variant.GetComponent<ScenePayload>(); payload.score = 29; payload.label = "Variant ☃"; SetHidden(payload, -7);
		GameObject added = new GameObject("VariantOnly"); added.transform.SetParent(variant.transform, false); added.SetActive(false); payload.direct = added;
		variant.transform.Find("Socket").localPosition = new Vector3(2, 3, 4);
		PrefabUtility.RecordPrefabInstancePropertyModifications(payload); PrefabUtility.RecordPrefabInstancePropertyModifications(variant);
		PrefabUtility.RecordPrefabInstancePropertyModifications(variant.transform.Find("Socket"));
		GameObject variantAsset = Save(variant, VariantPath); UnityEngine.Object.DestroyImmediate(variant);
		GameObject nested = new GameObject("NestedRig"); GameObject embedded = (GameObject)PrefabUtility.InstantiatePrefab(variantAsset);
		embedded.transform.SetParent(nested.transform, false); embedded.name = "EmbeddedVariant";
		ScenePayload child = embedded.GetComponent<ScenePayload>(); child.score = 41; child.label = "Nested override"; child.direct = embedded.transform.Find("Socket").gameObject;
		ScenePayload container = nested.AddComponent<ScenePayload>(); container.inherited = 91; SetHidden(container, 7);
		container.score = 55; container.label = "Container"; container.direct = child.direct; container.peer = child; child.peer = container;
		container.links = new UnityEngine.Object[] { baseAsset, variantAsset, external, null };
		container.nested = new NestedPayload { count = 77, target = embedded.transform };
		PrefabUtility.RecordPrefabInstancePropertyModifications(child); PrefabUtility.RecordPrefabInstancePropertyModifications(embedded);
		Save(nested, NestedPath); UnityEngine.Object.DestroyImmediate(nested); AssetDatabase.SaveAssets();
		foreach (string path in new[] { BasePath, VariantPath, NestedPath }) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
		GameObject[] assets = new[] { BasePath, NestedPath, VariantPath }.Select(AssetDatabase.LoadAssetAtPath<GameObject>).ToArray();
		VerifyConstants(assets);
		Oracle oracle = new Oracle { UnityVersion = Application.unityVersion, EditorPrefabReadbackVerified = true, Prefabs = assets.Select(Observe).ToArray() };
		if (oracle.Prefabs.Single(p => p.Root == "VariantRig").AssetType != "Variant"
			|| !oracle.Prefabs.Single(p => p.Root == "VariantRig").Links.Any(l => l.SourceRoot == "BaseRig" && l.PropertyPaths.Contains("score"))
			|| !oracle.Prefabs.Single(p => p.Root == "NestedRig").Links.Any(l => l.SourceRoot == "VariantRig" && l.PropertyPaths.Contains("peer")))
			throw new InvalidOperationException("Source variant/nested authoring links were not retained.");
		File.WriteAllText(Path.Combine(output, "oracle.json"), JsonUtility.ToJson(oracle, true));
		AssetBundleBuild[] builds = {
			new AssetBundleBuild { assetBundleName = "payload", assetNames = new[] { BasePath, VariantPath, NestedPath } },
			new AssetBundleBuild { assetBundleName = "external", assetNames = new[] { externalPath } }
		};
		foreach (bool strip in new[] { false, true })
		{
			string directory = Path.Combine(output, strip ? "WithoutTypeTree" : "WithTypeTree"); Directory.CreateDirectory(directory);
			BuildAssetBundleOptions options = BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle;
			if (strip) options |= BuildAssetBundleOptions.DisableWriteTypeTree;
			if (BuildPipeline.BuildAssetBundles(directory, builds, options, BuildTarget.StandaloneLinux64) == null) throw new InvalidOperationException("Prefab graph bundle build failed.");
		}
		File.Copy("Library/ScriptAssemblies/Assembly-CSharp.dll", Path.Combine(output, "Assembly-CSharp.dll"));
		string sources = Path.Combine(output, "Sources"); Directory.CreateDirectory(sources);
		foreach (string source in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories)
			.Concat(new[] { BasePath, VariantPath, NestedPath, BasePath + ".meta", VariantPath + ".meta", NestedPath + ".meta" }))
			File.Copy(source, Path.Combine(sources, Path.GetFileName(source)));
		PayloadFixtureBuilder.Manifest manifest = new PayloadFixtureBuilder.Manifest { UnityVersion = Application.unityVersion, BuildTarget = "StandaloneLinux64",
			Files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
				.Select(p => new PayloadFixtureBuilder.FileHash { Path = p.Substring(output.Length + 1).Replace('\\', '/'), Sha256 = Hash(File.ReadAllBytes(p)) }).ToArray() };
		File.WriteAllText(Path.Combine(output, "fixture-manifest.json"), JsonUtility.ToJson(manifest, true));
	}
	private static GameObject Save(GameObject root, string path)
	{
		GameObject result = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
		if (!success || result == null) throw new IOException("Prefab save failed: " + path); return result;
	}
	private static void SetHidden(ScenePayload payload, int value)
	{
		SerializedObject so = new SerializedObject(payload); so.FindProperty("hidden").intValue = value; so.ApplyModifiedPropertiesWithoutUndo();
	}
	private static Prefab Observe(GameObject asset)
	{
		Transform[] transforms = asset.GetComponentsInChildren<Transform>(true).OrderBy(t => PathOf(t), StringComparer.Ordinal).ToArray();
		return new Prefab { Root = asset.name, AssetType = PrefabUtility.GetPrefabAssetType(asset).ToString(),
			Links = transforms.Where(t => PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) != null).Select(t => {
				GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
				return new Link { Path = PathOf(t), SourceAssetPath = AssetDatabase.GetAssetPath(source), SourceRoot = source.transform.root.name,
					PropertyPaths = (PrefabUtility.GetPropertyModifications(t.gameObject) ?? new PropertyModification[0]).Select(p => p.propertyPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray() };
			}).ToArray(),
			Nodes = transforms.Select(t => new SceneFixtureBuilder.Node { Path = PathOf(t), SiblingIndex = t.GetSiblingIndex(), ActiveSelf = t.gameObject.activeSelf,
				Position = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z }, Rotation = new[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w },
				Scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z } }).ToArray(),
			Components = transforms.Select(t => t.GetComponent<ScenePayload>()).Where(c => c != null).Select(c => {
				SerializedObject so = new SerializedObject(c); so.Update();
				string[] fields = new[] { "direct", "peer", "nested.target" }.Concat(Enumerable.Range(0, so.FindProperty("links").arraySize).Select(i => "links.Array.data[" + i + "]")).ToArray();
				return new SceneFixtureBuilder.Component { Path = PathOf(c.transform), Inherited = so.FindProperty("inherited").intValue, Hidden = so.FindProperty("hidden").intValue,
					Score = so.FindProperty("score").intValue, Label = so.FindProperty("label").stringValue, NestedCount = so.FindProperty("nested.count").intValue,
					Pointers = fields.Select(f => Pointer(f, so.FindProperty(f).objectReferenceValue)).ToArray() };
			}).ToArray() };
	}
	private static SceneFixtureBuilder.Pointer Pointer(string field, UnityEngine.Object target)
	{
		var p = new SceneFixtureBuilder.Pointer { Field = field.Replace(".Array.data", ""), IsNull = target == null };
		if (target != null)
		{
			p.Type = target.GetType().FullName; p.Name = target.name; p.Text = target is TextAsset text ? text.text : "";
			p.ScenePath = target is GameObject go ? PathOf(go.transform) : target is UnityEngine.Component c ? PathOf(c.transform) : "";
			AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out p.SourceGuid, out p.SourceLocalId);
		}
		return p;
	}
	private static void VerifyConstants(GameObject[] assets)
	{
		GameObject b = assets.Single(a => a.name == "BaseRig"), v = assets.Single(a => a.name == "VariantRig"), n = assets.Single(a => a.name == "NestedRig");
		ScenePayload bp = b.GetComponent<ScenePayload>(), vp = v.GetComponent<ScenePayload>(), np = n.GetComponent<ScenePayload>();
		Transform e = n.transform.Find("EmbeddedVariant"); ScenePayload ep = e.GetComponent<ScenePayload>();
		if (assets.Sum(a => a.GetComponentsInChildren<Transform>(true).Length) != 9 || bp.score != 11 || bp.label != "Base" || bp.direct != b.transform.Find("Socket").gameObject
			|| vp.score != 29 || vp.label != "Variant ☃" || vp.direct != v.transform.Find("VariantOnly").gameObject || v.transform.Find("VariantOnly").gameObject.activeSelf
			|| v.transform.Find("Socket").localPosition != new Vector3(2, 3, 4) || ep.score != 41 || ep.label != "Nested override" || ep.direct != e.Find("Socket").gameObject
			|| np.score != 55 || np.peer != ep || ep.peer != np || np.links[0] != b || np.links[1] != v || np.links[3] != null || np.direct != ep.direct
			|| bp.links[0] != vp.links[0] || bp.links[0] != np.links[2] || !(bp.links[0] is TextAsset text) || text.text != "Prefab graph external: ☃")
			throw new InvalidOperationException("Source prefab effective values or identities differ from independent constants.");
	}
	private static string PathOf(Transform t) { return t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name; }
	private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
