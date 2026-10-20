using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

// This Editor checker uses reflection on the recovered runtime types. It never
// references or supplies the source fixture DLL to the exported project.
public static class AssetRipperGraphValidator
{
	[Serializable] public sealed class Edge { public string Field; public long Rid; }
	[Serializable] public sealed class Node
	{
		public long Rid; public string Class; public string Label; public long Next; public int Number; public string Text;
		public string TargetName; public string TargetType; public string TargetText;
	}
	[Serializable] public sealed class Oracle
	{
		public string UnityVersion; public string ScriptAssembly; public string ScriptNamespace; public string ScriptType;
		public Edge[] Edges; public Node[] Nodes;
	}
	[Serializable] public sealed class Failure { public string Code; public string Detail; }
	[Serializable] public sealed class Report
	{
		public int SchemaVersion = 1; public string UnityVersion; public bool Passed;
		public int MatchingComponentCount; public int MissingScriptCount; public bool SharedIdentityVerified; public bool CycleVerified;
		public string ScriptGuid; public string ScriptPath; public long ScriptLocalId;
		public List<Edge> Edges = new List<Edge>(); public List<Node> Nodes = new List<Node>();
		public List<Failure> Failures = new List<Failure>();
	}

	public static void Validate()
	{
		Report report = new Report { UnityVersion = Application.unityVersion };
		try
		{
			Oracle oracle = JsonUtility.FromJson<Oracle>(File.ReadAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_ORACLE")));
			Check(report, oracle.UnityVersion == Application.unityVersion, "EditorVersion", Application.unityVersion);
			foreach (string file in Directory.GetFiles("Assets", "*.prefab", SearchOption.AllDirectories))
			{
				GameObject root = PrefabUtility.LoadPrefabContents(file.Replace('\\', '/'));
				try
				{
					foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
						report.MissingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
					foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
					{
						if (component == null || component.GetType().FullName != oracle.ScriptNamespace + "." + oracle.ScriptType) continue;
						report.MatchingComponentCount++;
						Check(report, component.GetType().Assembly.GetName().Name == oracle.ScriptAssembly, "ScriptBinding", component.GetType().Assembly.FullName);
						Observe(report, oracle, root, component);
					}
				}
				finally { PrefabUtility.UnloadPrefabContents(root); }
			}
			Check(report, report.MissingScriptCount == 0, "MissingScript", "Missing slots: " + report.MissingScriptCount);
			Check(report, report.MatchingComponentCount == 1, "ComponentCount", "Matching components: " + report.MatchingComponentCount);
		}
		catch (Exception exception) { Check(report, false, "ValidationException", exception.ToString()); }
		report.Passed = report.Failures.Count == 0;
		File.WriteAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_REPORT"), JsonUtility.ToJson(report, true));
		EditorApplication.Exit(report.Passed ? 0 : 1);
	}

	private static void Observe(Report report, Oracle oracle, GameObject root, MonoBehaviour component)
	{
		SerializedObject serialized = new SerializedObject(component); serialized.Update();
		MonoScript script = MonoScript.FromMonoBehaviour(component);
		report.ScriptPath = AssetDatabase.GetAssetPath(script);
		AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out report.ScriptGuid, out report.ScriptLocalId);
		Check(report, serialized.FindProperty("m_Script").objectReferenceValue == script && report.ScriptLocalId == 11500000
			&& report.ScriptPath.EndsWith("/GraphPayload.cs", StringComparison.Ordinal), "ScriptBinding", report.ScriptPath);
		foreach (Edge expected in oracle.Edges)
		{
			SerializedProperty property = serialized.FindProperty(expected.Field.Replace("[", ".Array.data["));
			Edge observed = new Edge { Field = expected.Field, Rid = property.managedReferenceId }; report.Edges.Add(observed);
			Check(report, observed.Rid == expected.Rid, "ReferenceId", JsonUtility.ToJson(observed));
		}
		object first = serialized.FindProperty("root").managedReferenceValue;
		object second = serialized.FindProperty("items").GetArrayElementAtIndex(0).managedReferenceValue;
		report.SharedIdentityVerified = first != null && second != null
			&& ReferenceEquals(first, serialized.FindProperty("alias").managedReferenceValue)
			&& ReferenceEquals(first, serialized.FindProperty("items").GetArrayElementAtIndex(2).managedReferenceValue)
			&& ReferenceEquals(second, serialized.FindProperty("inline.child").managedReferenceValue)
			&& serialized.FindProperty("missing").managedReferenceValue == null
			&& serialized.FindProperty("items").GetArrayElementAtIndex(1).managedReferenceValue == null;
		report.CycleVerified = first != null && second != null && ReferenceEquals(Get(first, "next"), second) && ReferenceEquals(Get(second, "next"), first);
		Check(report, report.SharedIdentityVerified, "SharedIdentity", "Shared/null managed object identity differs.");
		Check(report, report.CycleVerified, "Cycle", "The two-node back edge was not restored.");
		foreach (Node expected in oracle.Nodes)
		{
			object node = ManagedReferenceUtility.GetManagedReference(component, expected.Rid);
			if (node == null) { Check(report, false, "NodeData", "Missing node: " + expected.Rid); continue; }
			object next = Get(node, "next"); UnityEngine.Object target = Get(node, "target") as UnityEngine.Object;
			Node observed = new Node { Rid = ManagedReferenceUtility.GetManagedReferenceIdForObject(component, node), Class = node.GetType().Name,
				Label = (string)Get(node, "label"), Next = next == null ? -2 : ManagedReferenceUtility.GetManagedReferenceIdForObject(component, next),
				Number = node.GetType().GetField("number") == null ? 0 : (int)Get(node, "number"),
				Text = node.GetType().GetField("text") == null ? "" : (string)Get(node, "text"),
				TargetName = target == null ? "" : target.name, TargetType = target == null ? "" : target.GetType().FullName,
				TargetText = target is TextAsset text ? text.text : "" };
			report.Nodes.Add(observed);
			Check(report, observed.Rid == expected.Rid && node.GetType().FullName == oracle.ScriptNamespace + "." + expected.Class
				&& observed.Label == expected.Label && observed.Next == expected.Next && observed.Number == expected.Number
				&& observed.Text == (expected.Text ?? ""), "NodeData", JsonUtility.ToJson(observed));
			bool targetMatches = observed.TargetName == expected.TargetName && observed.TargetType == expected.TargetType;
			if (expected.TargetType == "UnityEngine.GameObject") targetMatches &= root.transform.Find(expected.TargetName) != null && target == root.transform.Find(expected.TargetName).gameObject;
			if (expected.TargetType == "UnityEngine.TextAsset") targetMatches &= observed.TargetText == expected.TargetText && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(target));
			Check(report, targetMatches, "UnityReference", JsonUtility.ToJson(observed));
		}
	}
	private static object Get(object node, string field) { return node.GetType().GetField(field).GetValue(node); }
	private static void Check(Report report, bool condition, string code, string detail)
	{
		if (!condition) report.Failures.Add(new Failure { Code = code, Detail = detail });
	}
}
