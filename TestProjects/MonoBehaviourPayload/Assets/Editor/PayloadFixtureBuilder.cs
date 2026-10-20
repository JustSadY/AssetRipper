using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AssetRipper.EditorPayloadFixture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class PayloadFixtureBuilder
{
	private const string PrefabPath = "Assets/Payload.prefab";
	private const string ScenePath = "Assets/PayloadScene.unity";
	private const string ExternalPath = "Assets/ExternalTarget.txt";

	[Serializable] public sealed class PointerValue
	{
		public string Field;
		public bool IsNull;
		public string Name;
		public string Type;
		public string SourceGuid;
		public long SourceLocalId;
		public string Text;
	}
	[Serializable] public sealed class Values
	{
		public int Inherited;
		public int Hidden;
		public bool Flag;
		public string Label;
		public int[] Numbers;
		public int NestedCount;
		public PointerValue[] Pointers;
	}
	[Serializable] public sealed class Oracle
	{
		public int SchemaVersion = 1;
		public string UnityVersion;
		public string BuildTarget;
		public string ScriptAssembly = "Assembly-CSharp";
		public string ScriptNamespace = "AssetRipper.EditorPayloadFixture";
		public string ScriptType = "EditorPayload";
		public bool EditorSceneReadbackVerified;
		public Values Expected;
	}
	[Serializable] public sealed class FileHash { public string Path; public string Sha256; }
	[Serializable] public sealed class Manifest
	{
		public int SchemaVersion = 1;
		public string UnityVersion;
		public string BuildTarget;
		public FileHash[] Files;
	}

	public static void Build()
	{
		if (Application.unityVersion != "6000.4.0f1") throw new InvalidOperationException("The fixture requires Unity 6000.4.0f1.");
		string output = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_OUTPUT");
		if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("Set an absolute ASSETRIPPER_UNITY_FIXTURE_OUTPUT.");
		if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Fixture output must be empty.");
		Directory.CreateDirectory(output);
		EditorSettings.serializationMode = SerializationMode.ForceText;
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		File.WriteAllText(ExternalPath, "Editor-owned external reference: snowman ☃");
		AssetDatabase.ImportAsset(ExternalPath, ImportAssetOptions.ForceSynchronousImport);
		TextAsset external = AssetDatabase.LoadAssetAtPath<TextAsset>(ExternalPath);
		GameObject root = new GameObject("PayloadRoot");
		GameObject local = new GameObject("LocalTarget"); local.transform.SetParent(root.transform, false);
		EditorPayload payload = root.AddComponent<EditorPayload>();
		payload.inherited = -17; payload.flag = true; payload.label = "Merhaba ☃";
		payload.values = new[] { 7, -9, 42 }; payload.direct = local;
		payload.links = new UnityEngine.Object[] { local, external, null };
		payload.nested = new NestedPayload { count = 91, target = external };
		SerializedObject serialized = new SerializedObject(payload);
		serialized.FindProperty("hidden").intValue = -1234;
		serialized.ApplyModifiedPropertiesWithoutUndo();
		PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
		if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath)) throw new IOException("Scene save failed.");
		AssetDatabase.SaveAssets();
		EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
		EditorPayload scenePayload = UnityEngine.Object.FindFirstObjectByType<EditorPayload>();
		Values sceneValues = Observe(scenePayload);
		GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
		Values expected = Observe(prefab.GetComponent<EditorPayload>());
		if (sceneValues.Inherited != expected.Inherited || sceneValues.Hidden != expected.Hidden || sceneValues.Flag != expected.Flag || sceneValues.Label != expected.Label
			|| !sceneValues.Numbers.SequenceEqual(expected.Numbers) || sceneValues.NestedCount != expected.NestedCount
			|| !sceneValues.Pointers.Select(p => p.IsNull + ":" + p.Name + ":" + p.Type + ":" + p.Text)
				.SequenceEqual(expected.Pointers.Select(p => p.IsNull + ":" + p.Name + ":" + p.Type + ":" + p.Text)))
			throw new InvalidOperationException("Scene/prefab readback differs.");
		Oracle oracle = new Oracle { UnityVersion = Application.unityVersion, BuildTarget = "StandaloneLinux64", EditorSceneReadbackVerified = true, Expected = expected };
		File.WriteAllText(Path.Combine(output, "oracle.json"), JsonUtility.ToJson(oracle, true));
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
		Manifest manifest = new Manifest { UnityVersion = Application.unityVersion, BuildTarget = "StandaloneLinux64",
			Files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
				.Select(p => new FileHash { Path = p.Substring(output.Length + 1).Replace('\\', '/'), Sha256 = Hash(File.ReadAllBytes(p)) }).ToArray() };
		File.WriteAllText(Path.Combine(output, "fixture-manifest.json"), JsonUtility.ToJson(manifest, true));
		Debug.Log("AssetRipper Editor payload fixture completed: " + output);
	}

	private static Values Observe(EditorPayload payload)
	{
		if (payload == null) throw new InvalidOperationException("Missing EditorPayload component.");
		SerializedObject serialized = new SerializedObject(payload); serialized.Update();
		string[] paths = { "direct", "links.Array.data[0]", "links.Array.data[1]", "links.Array.data[2]", "nested.target" };
		return new Values { Inherited = serialized.FindProperty("inherited").intValue, Hidden = serialized.FindProperty("hidden").intValue,
			Flag = serialized.FindProperty("flag").boolValue, Label = serialized.FindProperty("label").stringValue,
			Numbers = Enumerable.Range(0, serialized.FindProperty("values").arraySize).Select(i => serialized.FindProperty("values").GetArrayElementAtIndex(i).intValue).ToArray(),
			NestedCount = serialized.FindProperty("nested.count").intValue,
			Pointers = paths.Select(path => {
				UnityEngine.Object target = serialized.FindProperty(path).objectReferenceValue;
				PointerValue result = new PointerValue { Field = path.Replace(".Array.data", ""), IsNull = target == null };
				if (target != null) { result.Name = target.name; result.Type = target.GetType().FullName;
					AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out result.SourceGuid, out result.SourceLocalId);
					if (target is TextAsset text) result.Text = text.text;
				}
				return result;
			}).ToArray() };
	}
	private static string Hash(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
