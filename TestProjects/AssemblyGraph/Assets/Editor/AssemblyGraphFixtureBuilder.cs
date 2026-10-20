using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetRipper.AssemblyGraph;
using AssetRipper.AssemblyGraph.Components;
using AssetRipper.AssemblyGraph.Model;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AssemblyGraphFixtureBuilder
{
	private const string PrefabPath = "Assets/AssemblyGraphRoot.prefab";
	private const string ScenePath = "Assets/AssemblyGraphScene.unity";
	private const string ExternalPath = "Assets/GraphExternal.txt";
	[Serializable] public sealed class Oracle
	{
		public int SchemaVersion = 1;
		public string UnityVersion;
		public string BuildTarget = "StandaloneLinux64";
		public bool EditorSceneReadbackVerified;
		public string[] Assemblies = { "Assembly-CSharp", "AssetRipper.GraphComponents", "AssetRipper.GraphModel" };
		public int[] Inputs = { -9, 0, 42 };
		public int[] NodeResults = { -1169, -1160, -1118 };
		public int[] BridgeResults = { -856, -847, -805 };
		public int Inherited = -17;
		public int Hidden = -1234;
		public int NodeCount = 91;
		public string NodeLabel = "node ☃";
		public int BridgeCount = -8;
		public string BridgeLabel = "bridge ☃";
		public int Stamp = 321;
		public string ExternalText = "Assembly graph external: Merhaba ☃";
	}
	[Serializable] public sealed class FileHash { public string Path; public string Sha256; }
	[Serializable] public sealed class Manifest
	{
		public int SchemaVersion = 1; public string UnityVersion; public string BuildTarget = "StandaloneLinux64"; public FileHash[] Files;
	}
	public static void Build()
	{
		if (Application.unityVersion != "6000.4.0f1") throw new InvalidOperationException("Requires Unity 6000.4.0f1.");
		string output = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_OUTPUT");
		if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("An absolute output path is required.");
		if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Output must be empty.");
		Directory.CreateDirectory(output); EditorSettings.serializationMode = SerializationMode.ForceText;
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		Oracle oracle = new Oracle { UnityVersion = Application.unityVersion };
		File.WriteAllText(ExternalPath, oracle.ExternalText); AssetDatabase.ImportAsset(ExternalPath, ImportAssetOptions.ForceSynchronousImport);
		TextAsset external = AssetDatabase.LoadAssetAtPath<TextAsset>(ExternalPath);
		GameObject root = new GameObject("AssemblyGraphRoot");
		GameObject local = new GameObject("GraphLocal"); local.transform.SetParent(root.transform, false);
		GraphNode node = root.AddComponent<GraphNode>(); AssemblyBridge bridge = root.AddComponent<AssemblyBridge>();
		node.inherited = oracle.Inherited; node.payload = new SharedPayload { count = oracle.NodeCount, label = oracle.NodeLabel, target = external };
		node.local = local; node.links = new UnityEngine.Object[] { local, external, null, bridge };
		SerializedObject fields = new SerializedObject(node); fields.FindProperty("hidden").intValue = oracle.Hidden; fields.ApplyModifiedPropertiesWithoutUndo();
		bridge.node = node; bridge.shared = new SharedPayload { count = oracle.BridgeCount, label = oracle.BridgeLabel, target = external };
		bridge.links = new UnityEngine.Object[] { node, local, external, null }; bridge.stamp = oracle.Stamp;
		PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
		if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath)) throw new IOException("Scene save failed.");
		AssetDatabase.SaveAssets(); EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
		Verify(GameObject.Find("AssemblyGraphRoot"), external, oracle);
		Verify(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), external, oracle);
		oracle.EditorSceneReadbackVerified = true;
		File.WriteAllText(Path.Combine(output, "oracle.json"), JsonUtility.ToJson(oracle, true));
		AssetBundleBuild[] builds = { new AssetBundleBuild { assetBundleName = "payload", assetNames = new[] { PrefabPath } },
			new AssetBundleBuild { assetBundleName = "external", assetNames = new[] { ExternalPath } } };
		foreach (bool stripped in new[] { false, true })
		{
			string directory = Path.Combine(output, stripped ? "WithoutTypeTree" : "WithTypeTree"); Directory.CreateDirectory(directory);
			var options = BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.ForceRebuildAssetBundle;
			if (stripped) options |= BuildAssetBundleOptions.DisableWriteTypeTree;
			if (BuildPipeline.BuildAssetBundles(directory, builds, options, BuildTarget.StandaloneLinux64) == null) throw new InvalidOperationException("Bundle build failed.");
		}
		foreach (string assembly in oracle.Assemblies) File.Copy("Library/ScriptAssemblies/" + assembly + ".dll", Path.Combine(output, assembly + ".dll"));
		foreach (string source in Directory.GetFiles("Assets", "*", SearchOption.AllDirectories).Where(p =>
			p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".asmdef", StringComparison.Ordinal) || p == PrefabPath || p == ScenePath || p.EndsWith(".meta", StringComparison.Ordinal)))
		{
			string target = Path.Combine(output, "Sources", source.Substring("Assets/".Length)); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target);
		}
		Manifest manifest = new Manifest { UnityVersion = Application.unityVersion,
			Files = Directory.GetFiles(output, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Select(p =>
				new FileHash { Path = p.Substring(output.Length + 1).Replace('\\', '/'), Sha256 = Hash(File.ReadAllBytes(p)) }).ToArray() };
		File.WriteAllText(Path.Combine(output, "fixture-manifest.json"), JsonUtility.ToJson(manifest, true));
		Debug.Log("Assembly graph corpus completed: " + output);
	}
	private static void Verify(GameObject root, TextAsset external, Oracle expected)
	{
		GraphNode node = root.GetComponent<GraphNode>(); AssemblyBridge bridge = root.GetComponent<AssemblyBridge>();
		if (node == null || bridge == null || node.GetType().Assembly.GetName().Name != "AssetRipper.GraphComponents"
			|| typeof(SharedPayload).Assembly.GetName().Name != "AssetRipper.GraphModel" || bridge.GetType().Assembly.GetName().Name != "Assembly-CSharp"
			|| node.inherited != -17 || new SerializedObject(node).FindProperty("hidden").intValue != -1234
			|| node.payload.count != 91 || node.payload.label != "node ☃" || node.payload.target != external
			|| node.local != root.transform.Find("GraphLocal").gameObject || !SameLinks(node.links, new UnityEngine.Object[] { node.local, external, null, bridge })
			|| bridge.node != node || bridge.shared.count != -8 || bridge.shared.label != "bridge ☃" || bridge.shared.target != external || bridge.stamp != 321
			|| !SameLinks(bridge.links, new UnityEngine.Object[] { node, node.local, external, null })
			|| !expected.Inputs.Select(node.Evaluate).SequenceEqual(expected.NodeResults) || !expected.Inputs.Select(bridge.Evaluate).SequenceEqual(expected.BridgeResults)
			|| ((IGraphNode)node).Evaluate(42) != -1118)
			throw new InvalidOperationException("Independent source scene/prefab field, identity or behavior oracle failed: "
				+ root.name + " assemblies=" + node.GetType().Assembly.GetName().Name + "," + typeof(SharedPayload).Assembly.GetName().Name + "," + bridge.GetType().Assembly.GetName().Name
				+ " inherited=" + node.inherited + " hidden=" + new SerializedObject(node).FindProperty("hidden").intValue
				+ " node=" + node.payload.count + ":" + node.payload.label + " nodeTarget=" + (node.payload.target == external)
				+ " local=" + (node.local == root.transform.Find("GraphLocal").gameObject) + " nodeLinks=" + string.Join(",", node.links.Select(x => x == null ? "null" : x.name))
				+ " bridge=" + bridge.shared.count + ":" + bridge.shared.label + ":" + bridge.stamp + " bridgeTarget=" + (bridge.shared.target == external)
				+ " bridgeNode=" + (bridge.node == node) + " bridgeLinks=" + string.Join(",", bridge.links.Select(x => x == null ? "null" : x.name))
				+ " nodeResults=" + string.Join(",", expected.Inputs.Select(node.Evaluate)) + " bridgeResults=" + string.Join(",", expected.Inputs.Select(bridge.Evaluate)));
	}
	private static bool SameLinks(UnityEngine.Object[] actual, UnityEngine.Object[] expected)
		=> actual.Length == expected.Length && actual.Zip(expected, (a, b) => a == b).All(equal => equal);
	private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
