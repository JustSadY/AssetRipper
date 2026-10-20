using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Compiles only inside the recovered project's Editor assembly. It does not
// reference the original fixture assembly or copy any fixture runtime source.
public static class AssetRipperRoundTripValidator
{
	[Serializable] public sealed class Pointer
	{
		public string Field; public bool IsNull; public string Name; public string Type; public string Text;
	}
	[Serializable] public sealed class Values
	{
		public int Inherited; public int Hidden; public bool Flag; public string Label; public int[] Numbers; public int NestedCount; public Pointer[] Pointers;
	}
	[Serializable] public sealed class Oracle
	{
		public string UnityVersion; public string ScriptAssembly; public string ScriptNamespace; public string ScriptType; public Values Expected;
	}
	[Serializable] public sealed class Failure { public string Code; public string Detail; }
	[Serializable] public sealed class ReferenceObservation
	{
		public string Field; public bool IsNull; public string Name; public string Type; public string AssetPath; public string Guid; public long LocalId; public bool IsLocal; public string Text;
	}
	[Serializable] public sealed class Report
	{
		public int SchemaVersion = 1; public string UnityVersion; public bool Passed;
		public int PrefabCount; public int MatchingComponentCount; public int MissingScriptCount;
		public string ScriptPath; public string ScriptGuid; public long ScriptLocalId;
		public Values Observed;
		public List<ReferenceObservation> References = new List<ReferenceObservation>();
		public List<Failure> Failures = new List<Failure>();
	}

	public static void Validate()
	{
		Report report = new Report { UnityVersion = Application.unityVersion };
		string reportPath = Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_REPORT");
		try
		{
			Oracle oracle = JsonUtility.FromJson<Oracle>(File.ReadAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_ORACLE")));
			Check(report, Application.unityVersion == oracle.UnityVersion, "EditorVersion", Application.unityVersion);
			string[] prefabs = Directory.GetFiles("Assets", "*.prefab", SearchOption.AllDirectories);
			report.PrefabCount = prefabs.Length;
			Check(report, prefabs.Length > 0, "PrefabCount", "No exported prefab was found.");
			foreach (string file in prefabs)
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
						Observe(report, oracle.Expected, root, component);
					}
				}
				finally { PrefabUtility.UnloadPrefabContents(root); }
			}
			Check(report, report.MissingScriptCount == 0, "MissingScript", "Missing component slots: " + report.MissingScriptCount);
			Check(report, report.MatchingComponentCount == 1, "ComponentCount", "Matching components: " + report.MatchingComponentCount);
		}
		catch (Exception exception) { Check(report, false, "ValidationException", exception.ToString()); }
		report.Passed = report.Failures.Count == 0;
		File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
		EditorApplication.Exit(report.Passed ? 0 : 1);
	}

	private static void Observe(Report report, Values expected, GameObject root, MonoBehaviour component)
	{
		SerializedObject serialized = new SerializedObject(component); serialized.Update();
		Values actual = new Values {
			Inherited = serialized.FindProperty("inherited").intValue, Hidden = serialized.FindProperty("hidden").intValue,
			Flag = serialized.FindProperty("flag").boolValue, Label = serialized.FindProperty("label").stringValue,
			Numbers = Enumerable.Range(0, serialized.FindProperty("values").arraySize).Select(i => serialized.FindProperty("values").GetArrayElementAtIndex(i).intValue).ToArray(),
			NestedCount = serialized.FindProperty("nested.count").intValue
		};
		report.Observed = actual;
		Check(report, actual.Inherited == expected.Inherited && actual.Hidden == expected.Hidden && actual.Flag == expected.Flag
			&& actual.Label == expected.Label && actual.Numbers.SequenceEqual(expected.Numbers) && actual.NestedCount == expected.NestedCount,
			"ScalarMismatch", JsonUtility.ToJson(actual));
		MonoScript script = MonoScript.FromMonoBehaviour(component);
		report.ScriptPath = AssetDatabase.GetAssetPath(script);
		AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out report.ScriptGuid, out report.ScriptLocalId);
		Check(report, serialized.FindProperty("m_Script").objectReferenceValue == script && report.ScriptLocalId == 11500000
			&& report.ScriptPath.EndsWith("/EditorPayload.cs", StringComparison.Ordinal), "ScriptBinding", report.ScriptPath);
		foreach (Pointer pointer in expected.Pointers)
		{
			SerializedProperty property = serialized.FindProperty(pointer.Field.Replace("[", ".Array.data["));
			UnityEngine.Object target = property.objectReferenceValue;
			ReferenceObservation observed = new ReferenceObservation { Field = pointer.Field, IsNull = target == null };
			if (target != null)
			{
				observed.Name = target.name; observed.Type = target.GetType().FullName; observed.AssetPath = AssetDatabase.GetAssetPath(target);
				AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out observed.Guid, out observed.LocalId);
				if (target is TextAsset textAsset) observed.Text = textAsset.text;
				if (target is GameObject gameObject) observed.IsLocal = gameObject.transform.root == root.transform;
			}
			report.References.Add(observed);
			bool matches = observed.IsNull == pointer.IsNull;
			if (!pointer.IsNull)
			{
				matches &= target != null && observed.Name == pointer.Name && observed.Type == pointer.Type;
				if (pointer.Type == "UnityEngine.GameObject")
					matches &= observed.IsLocal && root.transform.Find(pointer.Name) != null && target == root.transform.Find(pointer.Name).gameObject;
				if (pointer.Type == "UnityEngine.TextAsset")
					matches &= target is TextAsset text && text.text == pointer.Text && !string.IsNullOrEmpty(observed.AssetPath) && !string.IsNullOrEmpty(observed.Guid);
			}
			Check(report, matches, "ReferenceMismatch", pointer.Field + ": " + JsonUtility.ToJson(observed));
		}
	}
	private static void Check(Report report, bool condition, string code, string detail)
	{
		if (!condition) report.Failures.Add(new Failure { Code = code, Detail = detail });
	}
}
