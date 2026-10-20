using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetRipper.EditorGraphFixture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Serialization;

public static class GraphFixtureBuilder
{
	private const string PrefabPath = "Assets/Graph.prefab";
	private const string ScenePath = "Assets/GraphScene.unity";
	private const string ExternalPath = "Assets/GraphExternal.txt";
	private const long FirstId = 101;
	private const long SecondId = 4294967419;
	[Serializable] public sealed class Edge { public string Field; public long Rid; }
	[Serializable] public sealed class Node
	{
		public long Rid; public string Class; public string Label; public long Next;
		public int Number; public string Text; public string TargetName; public string TargetType; public string TargetText;
	}
	[Serializable] public sealed class Oracle
	{
		public int SchemaVersion = 1; public string UnityVersion; public string BuildTarget = "StandaloneLinux64";
		public string ScriptAssembly = "Assembly-CSharp"; public string ScriptNamespace = "AssetRipper.EditorGraphFixture";
		public string ScriptType = "GraphPayload"; public bool EditorSceneReadbackVerified;
		public bool SharedIdentityVerified; public bool CycleVerified; public Edge[] Edges; public Node[] Nodes;
	}

	public static void Build()
	{
		if (Application.unityVersion != "6000.4.0f1") throw new InvalidOperationException("Requires Unity 6000.4.0f1.");
		string output = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_OUTPUT");
		if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("Set an absolute fixture output path.");
		if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Fixture output must be empty.");
		Directory.CreateDirectory(output);
		EditorSettings.serializationMode = SerializationMode.ForceText;
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		File.WriteAllText(ExternalPath, "Managed graph external: ☃");
		AssetDatabase.ImportAsset(ExternalPath, ImportAssetOptions.ForceSynchronousImport);
		GameObject root = new GameObject("GraphRoot");
		GameObject local = new GameObject("GraphLocal"); local.transform.SetParent(root.transform, false);
		GraphPayload payload = root.AddComponent<GraphPayload>();
		NumberNode first = new NumberNode { label = "first ☃", number = -37, target = local };
		TextNode second = new TextNode { label = "second", text = "polymorphic ☃", target = AssetDatabase.LoadAssetAtPath<TextAsset>(ExternalPath) };
		first.next = second; second.next = first;
		payload.root = first; payload.alias = first; payload.missing = null;
		payload.items = new System.Collections.Generic.List<GraphNode> { second, null, first };
		payload.inline = new InlineGraph { child = second };
		if (!ManagedReferenceUtility.SetManagedReferenceIdForObject(payload, first, FirstId)
			|| !ManagedReferenceUtility.SetManagedReferenceIdForObject(payload, second, SecondId)) throw new InvalidOperationException("Assigning stable IDs failed.");
		PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
		if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath)) throw new IOException("Scene save failed.");
		AssetDatabase.SaveAssets();
		EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
		Oracle scene = Observe(UnityEngine.Object.FindFirstObjectByType<GraphPayload>());
		Oracle prefab = Observe(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<GraphPayload>());
		if (JsonUtility.ToJson(scene) != JsonUtility.ToJson(prefab)) throw new InvalidOperationException("Scene and prefab graph readback differ.");
		prefab.EditorSceneReadbackVerified = true;
		File.WriteAllText(Path.Combine(output, "oracle.json"), JsonUtility.ToJson(prefab, true));
		AssetBundleBuild[] builds = {
			new AssetBundleBuild { assetBundleName = "payload", assetNames = new[] { PrefabPath } },
			new AssetBundleBuild { assetBundleName = "external", assetNames = new[] { ExternalPath } }
		};
		foreach (bool strip in new[] { false, true })
		{
			string directory = Path.Combine(output, strip ? "WithoutTypeTree" : "WithTypeTree"); Directory.CreateDirectory(directory);
			BuildAssetBundleOptions options = BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle;
			if (strip) options |= BuildAssetBundleOptions.DisableWriteTypeTree;
			if (BuildPipeline.BuildAssetBundles(directory, builds, options, BuildTarget.StandaloneLinux64) == null) throw new InvalidOperationException("AssetBundle build failed.");
		}
		File.Copy("Library/ScriptAssemblies/Assembly-CSharp.dll", Path.Combine(output, "Assembly-CSharp.dll"));
		string sourceDirectory = Path.Combine(output, "Sources"); Directory.CreateDirectory(sourceDirectory);
		foreach (string source in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories).Concat(new[] { PrefabPath, ScenePath }))
			File.Copy(source, Path.Combine(sourceDirectory, Path.GetFileName(source)));
		PayloadFixtureBuilder.Manifest manifest = new PayloadFixtureBuilder.Manifest { UnityVersion = Application.unityVersion, BuildTarget = "StandaloneLinux64",
			Files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
				.Select(p => new PayloadFixtureBuilder.FileHash { Path = p.Substring(output.Length + 1).Replace('\\', '/'), Sha256 = Hash(File.ReadAllBytes(p)) }).ToArray() };
		File.WriteAllText(Path.Combine(output, "fixture-manifest.json"), JsonUtility.ToJson(manifest, true));
	}

	private static Oracle Observe(GraphPayload payload)
	{
		if (payload == null || !(payload.root is NumberNode first) || !(first.next is TextNode second)) throw new InvalidOperationException("Missing polymorphic graph.");
		bool shared = ReferenceEquals(payload.root, payload.alias) && ReferenceEquals(payload.items[0], second)
			&& ReferenceEquals(payload.items[2], first) && ReferenceEquals(payload.inline.child, second);
		bool cycle = ReferenceEquals(second.next, first) && ReferenceEquals(first.next, second);
		if (!shared || !cycle || payload.missing != null || payload.items[1] != null || first.number != -37
			|| first.label != "first ☃" || second.label != "second" || second.text != "polymorphic ☃"
			|| first.target != payload.transform.Find("GraphLocal").gameObject || !(second.target is TextAsset external) || external.text != "Managed graph external: ☃")
			throw new InvalidOperationException("The source Editor graph does not match the independent constants/identity checks.");
		SerializedObject serialized = new SerializedObject(payload); serialized.Update();
		string[] paths = { "root", "alias", "missing", "items.Array.data[0]", "items.Array.data[1]", "items.Array.data[2]", "inline.child" };
		long[] expectedIds = { FirstId, FirstId, -2, SecondId, -2, FirstId, SecondId };
		Edge[] edges = paths.Select(p => new Edge { Field = p.Replace(".Array.data", ""), Rid = serialized.FindProperty(p).managedReferenceId }).ToArray();
		if (!edges.Select(e => e.Rid).SequenceEqual(expectedIds)) throw new InvalidOperationException("Stable IDs differ after Editor readback.");
		return new Oracle { UnityVersion = Application.unityVersion, SharedIdentityVerified = shared, CycleVerified = cycle, Edges = edges,
			Nodes = new GraphNode[] { first, second }.Select(n => new Node { Rid = ManagedReferenceUtility.GetManagedReferenceIdForObject(payload, n),
				Class = n.GetType().Name, Label = n.label, Next = ManagedReferenceUtility.GetManagedReferenceIdForObject(payload, n.next),
				Number = n is NumberNode number ? number.number : 0, Text = n is TextNode text ? text.text : null,
				TargetName = n.target.name, TargetType = n.target.GetType().FullName, TargetText = n.target is TextAsset asset ? asset.text : null }).ToArray() };
	}
	private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
