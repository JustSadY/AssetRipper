using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetRipper.EditorPayloadFixture;
using AssetRipper.EditorSceneFixture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SceneFixtureBuilder
{
	private const string ScenePath = "Assets/SceneFixture.unity";
	private const string ExternalPath = "Assets/SceneExternal.txt";
	[Serializable] public sealed class Node
	{
		public string Path; public int SiblingIndex; public bool ActiveSelf; public float[] Position; public float[] Rotation; public float[] Scale;
	}
	[Serializable] public sealed class Pointer
	{
		public string Field; public bool IsNull; public string Type; public string ScenePath; public string Name; public string Text;
		public string SourceGuid; public long SourceLocalId;
	}
	[Serializable] public sealed class Component
	{
		public string Path; public int Inherited; public int Hidden; public int Score; public string Label; public int NestedCount; public Pointer[] Pointers;
	}
	[Serializable] public sealed class Oracle
	{
		public int SchemaVersion = 1; public string UnityVersion; public string BuildTarget = "StandaloneLinux64";
		public string SceneName; public string ScriptAssembly = "Assembly-CSharp"; public string ScriptNamespace = "AssetRipper.EditorSceneFixture";
		public string ScriptType = "ScenePayload"; public bool EditorSceneReadbackVerified; public Node[] Nodes; public Component[] Components;
	}

	public static void Build()
	{
		if (Application.unityVersion != "6000.4.0f1") throw new InvalidOperationException("Requires Unity 6000.4.0f1.");
		string output = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_OUTPUT");
		if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("Set an absolute fixture output path.");
		if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Fixture output must be empty.");
		Directory.CreateDirectory(output);
		EditorSettings.serializationMode = SerializationMode.ForceText;
		Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		File.WriteAllText(ExternalPath, "Scene external reference: ☃");
		AssetDatabase.ImportAsset(ExternalPath, ImportAssetOptions.ForceSynchronousImport);
		TextAsset external = AssetDatabase.LoadAssetAtPath<TextAsset>(ExternalPath);
		GameObject root = new GameObject("SceneRoot"); root.transform.localPosition = new Vector3(1.5f, -2.25f, 3);
		root.transform.localScale = new Vector3(2, 1, 0.5f); root.transform.localRotation = Quaternion.Euler(0, 30, 0);
		GameObject child = new GameObject("ChildTarget"); child.transform.SetParent(root.transform, false); child.transform.localPosition = new Vector3(-1, 0.5f, 2);
		GameObject grandchild = new GameObject("Grandchild"); grandchild.transform.SetParent(child.transform, false); grandchild.transform.localPosition = new Vector3(0, 2, 0);
		GameObject inactive = new GameObject("InactiveChild"); inactive.transform.SetParent(root.transform, false); inactive.SetActive(false);
		GameObject other = new GameObject("OtherRoot"); other.transform.localPosition = new Vector3(8, 0, -4);
		ScenePayload first = root.AddComponent<ScenePayload>(); ScenePayload second = other.AddComponent<ScenePayload>();
		first.inherited = -17; first.score = 17; first.label = "Scene ☃"; first.direct = child; first.peer = second;
		first.links = new UnityEngine.Object[] { other, external, null, second }; first.nested = new NestedPayload { count = 33, target = other.transform };
		second.inherited = 91; second.score = 24; second.label = "Other"; second.direct = root; second.peer = first;
		second.links = new UnityEngine.Object[] { external, first, null }; second.nested = new NestedPayload { count = 44, target = grandchild };
		SetHidden(first, -1234); SetHidden(second, 7);
		Oracle before = Observe(scene);
		if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Scene save failed.");
		AssetDatabase.SaveAssets(); scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
		Oracle oracle = Observe(scene);
		// GUID/local IDs are observations after save, not the semantic comparison.
		if (!before.Nodes.Select(n => JsonUtility.ToJson(n)).SequenceEqual(oracle.Nodes.Select(n => JsonUtility.ToJson(n)))) throw new InvalidOperationException("Scene hierarchy changed after save/reload.");
		VerifyConstants(scene, oracle);
		oracle.EditorSceneReadbackVerified = true;
		File.WriteAllText(Path.Combine(output, "oracle.json"), JsonUtility.ToJson(oracle, true));
		AssetBundleBuild[] builds = {
			new AssetBundleBuild { assetBundleName = "payload", assetNames = new[] { ScenePath } },
			new AssetBundleBuild { assetBundleName = "external", assetNames = new[] { ExternalPath } }
		};
		foreach (bool strip in new[] { false, true })
		{
			string directory = Path.Combine(output, strip ? "WithoutTypeTree" : "WithTypeTree"); Directory.CreateDirectory(directory);
			BuildAssetBundleOptions options = BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle;
			if (strip) options |= BuildAssetBundleOptions.DisableWriteTypeTree;
			if (BuildPipeline.BuildAssetBundles(directory, builds, options, BuildTarget.StandaloneLinux64) == null) throw new InvalidOperationException("Scene bundle build failed.");
		}
		File.Copy("Library/ScriptAssemblies/Assembly-CSharp.dll", Path.Combine(output, "Assembly-CSharp.dll"));
		string sources = Path.Combine(output, "Sources"); Directory.CreateDirectory(sources);
		foreach (string source in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories).Concat(new[] { ScenePath })) File.Copy(source, Path.Combine(sources, Path.GetFileName(source)));
		PayloadFixtureBuilder.Manifest manifest = new PayloadFixtureBuilder.Manifest { UnityVersion = Application.unityVersion, BuildTarget = "StandaloneLinux64",
			Files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
				.Select(p => new PayloadFixtureBuilder.FileHash { Path = p.Substring(output.Length + 1).Replace('\\', '/'), Sha256 = Hash(File.ReadAllBytes(p)) }).ToArray() };
		File.WriteAllText(Path.Combine(output, "fixture-manifest.json"), JsonUtility.ToJson(manifest, true));
	}

	private static void SetHidden(ScenePayload component, int value)
	{
		SerializedObject serialized = new SerializedObject(component); serialized.FindProperty("hidden").intValue = value; serialized.ApplyModifiedPropertiesWithoutUndo();
	}
	private static Oracle Observe(Scene scene)
	{
		Transform[] transforms = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).OrderBy(t => PathOf(t), StringComparer.Ordinal).ToArray();
		return new Oracle { UnityVersion = Application.unityVersion, SceneName = scene.name,
			Nodes = transforms.Select(t => new Node { Path = PathOf(t), SiblingIndex = t.GetSiblingIndex(), ActiveSelf = t.gameObject.activeSelf,
				Position = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z }, Rotation = new[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w },
				Scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z } }).ToArray(),
			Components = transforms.Select(t => t.GetComponent<ScenePayload>()).Where(c => c != null).Select(c => {
				SerializedObject serialized = new SerializedObject(c); serialized.Update();
				string[] fields = new[] { "direct", "peer", "nested.target" }.Concat(Enumerable.Range(0, serialized.FindProperty("links").arraySize).Select(i => "links.Array.data[" + i + "]")).ToArray();
				return new Component { Path = PathOf(c.transform), Inherited = serialized.FindProperty("inherited").intValue, Hidden = serialized.FindProperty("hidden").intValue,
					Score = serialized.FindProperty("score").intValue, Label = serialized.FindProperty("label").stringValue, NestedCount = serialized.FindProperty("nested.count").intValue,
					Pointers = fields.Select(f => ObservePointer(f, serialized.FindProperty(f).objectReferenceValue)).ToArray() };
			}).ToArray() };
	}
	private static Pointer ObservePointer(string field, UnityEngine.Object target)
	{
		Pointer result = new Pointer { Field = field.Replace(".Array.data", ""), IsNull = target == null };
		if (target != null)
		{
			result.Type = target.GetType().FullName; result.Name = target.name;
			result.ScenePath = target is GameObject go ? PathOf(go.transform) : target is UnityEngine.Component component ? PathOf(component.transform) : "";
			result.Text = target is TextAsset text ? text.text : "";
			AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out result.SourceGuid, out result.SourceLocalId);
		}
		return result;
	}
	private static void VerifyConstants(Scene scene, Oracle oracle)
	{
		string[] paths = { "OtherRoot", "SceneRoot", "SceneRoot/ChildTarget", "SceneRoot/ChildTarget/Grandchild", "SceneRoot/InactiveChild" };
		if (!oracle.Nodes.Select(n => n.Path).SequenceEqual(paths) || oracle.Nodes.Last().ActiveSelf || oracle.Components.Length != 2) throw new InvalidOperationException("Unexpected source hierarchy.");
		ScenePayload first = scene.GetRootGameObjects().Single(g => g.name == "SceneRoot").GetComponent<ScenePayload>();
		ScenePayload second = scene.GetRootGameObjects().Single(g => g.name == "OtherRoot").GetComponent<ScenePayload>();
		if (first.inherited != -17 || first.score != 17 || first.label != "Scene ☃" || first.nested.count != 33
			|| second.inherited != 91 || second.score != 24 || second.label != "Other" || second.nested.count != 44
			|| oracle.Components.Single(c => c.Path == "SceneRoot").Hidden != -1234 || oracle.Components.Single(c => c.Path == "OtherRoot").Hidden != 7
			|| first.peer != second || second.peer != first || first.direct != first.transform.Find("ChildTarget").gameObject || second.direct != first.gameObject
			|| first.links.Length != 4 || first.links[0] != second.gameObject || first.links[3] != second || first.links[2] != null
			|| second.links.Length != 3 || second.links[1] != first || second.links[2] != null || first.links[1] != second.links[0]
			|| !(first.links[1] is TextAsset external) || external.text != "Scene external reference: ☃"
			|| first.nested.target != second.transform || second.nested.target != first.transform.Find("ChildTarget/Grandchild").gameObject)
			throw new InvalidOperationException("Source scene fields or actual object identities differ from independent constants.");
	}
	private static string PathOf(Transform t) { return t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name; }
	private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
