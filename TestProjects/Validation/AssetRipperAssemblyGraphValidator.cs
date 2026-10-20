using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Uses reflection/SerializedObject, never the original fixture DLL or runtime source.
public static class AssetRipperAssemblyGraphValidator
{
	[Serializable] public sealed class Oracle
	{
		public string UnityVersion; public string ExportMode; public int[] Inputs; public int[] NodeResults; public int[] BridgeResults;
		public int Inherited; public int Hidden; public int NodeCount; public string NodeLabel;
		public int BridgeCount; public string BridgeLabel; public int Stamp; public string ExternalText;
	}
	[Serializable] public sealed class Failure { public string Code; public string Detail; }
	[Serializable] public sealed class Binding
	{
		public string Type; public string Assembly; public string ScriptPath; public string Guid; public long LocalId;
	}
	[Serializable] public sealed class Plugin
	{
		public string Path; public bool AnyPlatform; public bool Editor; public bool ExcludedEditor;
	}
	[Serializable] public sealed class Report
	{
		public int SchemaVersion = 1; public string UnityVersion; public bool Passed; public int MatchingPrefabCount; public int MissingScriptCount;
		public int[] NodeResults; public int[] BridgeResults; public int[] InterfaceResults;
		public List<Binding> Bindings = new List<Binding>(); public List<Plugin> Plugins = new List<Plugin>();
		public List<Failure> Failures = new List<Failure>();
	}
	public static void Validate()
	{
		Report report = new Report { UnityVersion = Application.unityVersion };
		try
		{
			Oracle oracle = JsonUtility.FromJson<Oracle>(File.ReadAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_ORACLE")));
			Check(report, oracle.UnityVersion == report.UnityVersion, "EditorVersion", report.UnityVersion);
			foreach (string file in Directory.GetFiles("Assets", "*.prefab", SearchOption.AllDirectories))
			{
				GameObject root = PrefabUtility.LoadPrefabContents(file.Replace('\\', '/'));
				try
				{
					if (root.name != "AssemblyGraphRoot") continue;
					report.MatchingPrefabCount++;
					foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
						report.MissingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
					MonoBehaviour[] components = root.GetComponents<MonoBehaviour>().Where(c => c != null).ToArray();
					MonoBehaviour node = components.SingleOrDefault(c => c.GetType().FullName == "AssetRipper.AssemblyGraph.Components.GraphNode");
					MonoBehaviour bridge = components.SingleOrDefault(c => c.GetType().FullName == "AssetRipper.AssemblyGraph.AssemblyBridge");
					Check(report, node != null && bridge != null && components.Length == 2, "ScriptBinding", "Expected two components from two assemblies.");
					if (node == null || bridge == null) continue;
					Bind(report, node, "AssetRipper.GraphComponents", oracle.ExportMode);
					Bind(report, bridge, "Assembly-CSharp", oracle.ExportMode);
					Check(report, node.GetType().BaseType.Assembly.GetName().Name == "AssetRipper.GraphModel"
						&& node.GetType().GetField("payload").FieldType.Assembly.GetName().Name == "AssetRipper.GraphModel"
						&& bridge.GetType().GetField("shared").FieldType.Assembly.GetName().Name == "AssetRipper.GraphModel", "AssemblyIdentity", "Base/field type assembly mismatch.");
					Observe(report, root, node, bridge, oracle);
				}
				finally { PrefabUtility.UnloadPrefabContents(root); }
			}
			Check(report, report.MatchingPrefabCount == 1, "PrefabCount", report.MatchingPrefabCount.ToString());
			Check(report, report.MissingScriptCount == 0, "MissingScript", report.MissingScriptCount.ToString());
			foreach (string file in Directory.GetFiles("Assets", "*.dll", SearchOption.AllDirectories))
			{
				PluginImporter importer = AssetImporter.GetAtPath(file.Replace('\\', '/')) as PluginImporter;
				if (importer == null) continue;
				report.Plugins.Add(new Plugin { Path = file, AnyPlatform = importer.GetCompatibleWithAnyPlatform(),
					Editor = importer.GetCompatibleWithEditor(), ExcludedEditor = importer.GetExcludeEditorFromAnyPlatform() });
			}
		}
		catch (Exception exception) { Check(report, false, "ValidationException", exception.ToString()); }
		report.Passed = report.Failures.Count == 0;
		File.WriteAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_REPORT"), JsonUtility.ToJson(report, true));
		EditorApplication.Exit(report.Passed ? 0 : 1);
	}
	private static void Bind(Report report, MonoBehaviour component, string assembly, string mode)
	{
		MonoScript script = MonoScript.FromMonoBehaviour(component); string guid; long localId;
		AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out guid, out localId);
		string path = AssetDatabase.GetAssetPath(script);
		bool compiled = mode == "Hybrid" && assembly != "Assembly-CSharp";
		report.Bindings.Add(new Binding { Type = component.GetType().FullName, Assembly = component.GetType().Assembly.GetName().Name,
			ScriptPath = path, Guid = guid, LocalId = localId });
		Check(report, component.GetType().Assembly.GetName().Name == assembly && script.GetClass() == component.GetType()
			&& new SerializedObject(component).FindProperty("m_Script").objectReferenceValue == script && !string.IsNullOrEmpty(guid)
			&& (compiled ? path.EndsWith("/" + assembly + ".dll", StringComparison.Ordinal) && localId != 0 && localId != 11500000
				: path.EndsWith("/" + component.GetType().Name + ".cs", StringComparison.Ordinal) && localId == 11500000), "ScriptBinding", path + ":" + localId);
	}
	private static void Observe(Report report, GameObject root, MonoBehaviour node, MonoBehaviour bridge, Oracle oracle)
	{
		SerializedObject n = new SerializedObject(node), b = new SerializedObject(bridge); n.Update(); b.Update();
		Check(report, n.FindProperty("inherited").intValue == oracle.Inherited && n.FindProperty("hidden").intValue == oracle.Hidden
			&& n.FindProperty("payload.count").intValue == oracle.NodeCount && n.FindProperty("payload.label").stringValue == oracle.NodeLabel
			&& b.FindProperty("shared.count").intValue == oracle.BridgeCount && b.FindProperty("shared.label").stringValue == oracle.BridgeLabel
			&& b.FindProperty("stamp").intValue == oracle.Stamp, "ScalarMismatch", "Inherited/private/shared fields differ.");
		GameObject local = root.transform.Find("GraphLocal").gameObject;
		TextAsset external = n.FindProperty("payload.target").objectReferenceValue as TextAsset;
		Check(report, external != null && external.name == "GraphExternal" && external.text == oracle.ExternalText
			&& !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(external))
			&& b.FindProperty("shared.target").objectReferenceValue == external && b.FindProperty("node").objectReferenceValue == node
			&& n.FindProperty("local").objectReferenceValue == local
			&& SameLinks(ReadLinks(n), new UnityEngine.Object[] { local, external, null, bridge })
			&& SameLinks(ReadLinks(b), new UnityEngine.Object[] { node, local, external, null }), "ReferenceMismatch", "Cross-assembly/local/external/null links differ.");
		MethodInfo nodeMethod = node.GetType().GetMethod("Evaluate"), bridgeMethod = bridge.GetType().GetMethod("Evaluate");
		report.NodeResults = oracle.Inputs.Select(v => (int)nodeMethod.Invoke(node, new object[] { v })).ToArray();
		report.BridgeResults = oracle.Inputs.Select(v => (int)bridgeMethod.Invoke(bridge, new object[] { v })).ToArray();
		Type contract = node.GetType().GetInterface("AssetRipper.AssemblyGraph.Model.IGraphNode");
		report.InterfaceResults = oracle.Inputs.Select(v => (int)contract.GetMethod("Evaluate").Invoke(node, new object[] { v })).ToArray();
		Check(report, report.NodeResults.SequenceEqual(oracle.NodeResults) && report.BridgeResults.SequenceEqual(oracle.BridgeResults)
			&& report.InterfaceResults.SequenceEqual(oracle.NodeResults), "BehaviorMismatch", "Source-owned arithmetic/interface oracle differs.");
	}
	private static UnityEngine.Object[] ReadLinks(SerializedObject fields)
	{
		SerializedProperty array = fields.FindProperty("links");
		return Enumerable.Range(0, array.arraySize).Select(i => array.GetArrayElementAtIndex(i).objectReferenceValue).ToArray();
	}
	private static bool SameLinks(UnityEngine.Object[] actual, UnityEngine.Object[] expected)
		=> actual.Length == expected.Length && actual.Zip(expected, (a, b) => a == b).All(equal => equal);
	private static void Check(Report report, bool passed, string code, string detail)
	{
		if (!passed) report.Failures.Add(new Failure { Code = code, Detail = detail });
	}
}
