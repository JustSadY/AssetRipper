using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// This checker observes the recovered scene and compiled recovered types only.
public static class AssetRipperSceneValidator
{
	[Serializable] public sealed class Node
	{
		public string Path; public int SiblingIndex; public bool ActiveSelf; public float[] Position; public float[] Rotation; public float[] Scale;
	}
	[Serializable] public sealed class Pointer
	{
		public string Field; public bool IsNull; public string Type; public string ScenePath; public string Name; public string Text;
	}
	[Serializable] public sealed class Values
	{
		public string Path; public int Inherited; public int Hidden; public int Score; public string Label; public int NestedCount; public Pointer[] Pointers;
	}
	[Serializable] public sealed class Oracle
	{
		public string UnityVersion; public string SceneName; public string ScriptAssembly; public string ScriptNamespace; public string ScriptType;
		public Node[] Nodes; public Values[] Components;
	}
	[Serializable] public sealed class Failure { public string Code; public string Detail; }
	[Serializable] public sealed class ReferenceObservation
	{
		public string Component; public string Field; public bool IsNull; public string Type; public string ScenePath;
		public string Name; public string Text; public string AssetPath; public string Guid; public long LocalId;
	}
	[Serializable] public sealed class ScriptBinding
	{
		public string Component; public string ScriptPath; public string Guid; public long LocalId;
	}
	[Serializable] public sealed class Report
	{
		public int SchemaVersion = 1; public string UnityVersion; public bool Passed; public int SceneFileCount;
		public string ScenePath; public string SceneName; public string SceneGuid; public int MissingScriptCount; public int MatchingComponentCount;
		public List<Node> Nodes = new List<Node>(); public List<Values> Components = new List<Values>();
		public List<ScriptBinding> Scripts = new List<ScriptBinding>(); public List<ReferenceObservation> References = new List<ReferenceObservation>();
		public List<Failure> Failures = new List<Failure>();
	}

	public static void Validate()
	{
		Report report = new Report { UnityVersion = Application.unityVersion };
		try
		{
			Oracle oracle = JsonUtility.FromJson<Oracle>(File.ReadAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_ORACLE")));
			Check(report, oracle.UnityVersion == Application.unityVersion, "EditorVersion", Application.unityVersion);
			string[] files = Directory.GetFiles("Assets", "*.unity", SearchOption.AllDirectories); report.SceneFileCount = files.Length;
			Check(report, files.Length == 1, "SceneCount", "Scene files: " + files.Length);
			if (files.Length == 1)
			{
				report.ScenePath = files[0].Replace('\\', '/'); report.SceneGuid = AssetDatabase.AssetPathToGUID(report.ScenePath);
				Scene scene = EditorSceneManager.OpenScene(report.ScenePath, OpenSceneMode.Single); report.SceneName = scene.name;
				Check(report, scene.name == oracle.SceneName && !string.IsNullOrEmpty(report.SceneGuid), "SceneBinding", scene.name);
				Transform[] transforms = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).OrderBy(t => PathOf(t), StringComparer.Ordinal).ToArray();
				foreach (Transform t in transforms)
				{
					report.Nodes.Add(new Node { Path = PathOf(t), SiblingIndex = t.GetSiblingIndex(), ActiveSelf = t.gameObject.activeSelf,
						Position = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z }, Rotation = new[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w },
						Scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z } });
					report.MissingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
				}
				Check(report, report.Nodes.Select(n => n.Path).SequenceEqual(oracle.Nodes.Select(n => n.Path)), "HierarchyMismatch", "Scene object paths differ.");
				foreach (Node expected in oracle.Nodes)
				{
					Node actual = report.Nodes.SingleOrDefault(n => n.Path == expected.Path);
					if (actual == null) continue;
					Check(report, actual.SiblingIndex == expected.SiblingIndex && actual.ActiveSelf == expected.ActiveSelf
						&& Close(actual.Position, expected.Position) && Close(actual.Scale, expected.Scale) && Close(actual.Rotation, expected.Rotation),
						"HierarchyMismatch", JsonUtility.ToJson(actual));
				}
				foreach (Values expected in oracle.Components)
				{
					Transform transform = transforms.SingleOrDefault(t => PathOf(t) == expected.Path);
					MonoBehaviour component = transform == null ? null : transform.GetComponents<MonoBehaviour>().SingleOrDefault(c => c != null && c.GetType().FullName == oracle.ScriptNamespace + "." + oracle.ScriptType);
					if (component == null) continue;
					report.MatchingComponentCount++;
					Check(report, component.GetType().Assembly.GetName().Name == oracle.ScriptAssembly, "ScriptBinding", component.GetType().Assembly.FullName);
					ObserveComponent(report, expected, component, transforms);
				}
				Check(report, report.MissingScriptCount == 0, "MissingScript", "Missing script slots: " + report.MissingScriptCount);
				Check(report, report.MatchingComponentCount == oracle.Components.Length, "ComponentCount", "Components: " + report.MatchingComponentCount);
			}
		}
		catch (Exception exception) { Check(report, false, "ValidationException", exception.ToString()); }
		report.Passed = report.Failures.Count == 0;
		File.WriteAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_REPORT"), JsonUtility.ToJson(report, true));
		EditorApplication.Exit(report.Passed ? 0 : 1);
	}
	private static void ObserveComponent(Report report, Values expected, MonoBehaviour component, Transform[] transforms)
	{
		SerializedObject serialized = new SerializedObject(component); serialized.Update();
		Values actual = new Values { Path = expected.Path, Inherited = serialized.FindProperty("inherited").intValue, Hidden = serialized.FindProperty("hidden").intValue,
			Score = serialized.FindProperty("score").intValue, Label = serialized.FindProperty("label").stringValue, NestedCount = serialized.FindProperty("nested.count").intValue };
		report.Components.Add(actual);
		Check(report, actual.Inherited == expected.Inherited && actual.Hidden == expected.Hidden && actual.Score == expected.Score
			&& actual.Label == expected.Label && actual.NestedCount == expected.NestedCount, "ScalarMismatch", JsonUtility.ToJson(actual));
		MonoScript script = MonoScript.FromMonoBehaviour(component);
		ScriptBinding binding = new ScriptBinding { Component = expected.Path, ScriptPath = AssetDatabase.GetAssetPath(script) };
		AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out binding.Guid, out binding.LocalId); report.Scripts.Add(binding);
		Check(report, serialized.FindProperty("m_Script").objectReferenceValue == script && binding.LocalId == 11500000
			&& binding.ScriptPath.EndsWith("/ScenePayload.cs", StringComparison.Ordinal), "ScriptBinding", JsonUtility.ToJson(binding));
		foreach (Pointer pointer in expected.Pointers)
		{
			UnityEngine.Object target = serialized.FindProperty(pointer.Field.Replace("[", ".Array.data[")).objectReferenceValue;
			ReferenceObservation observed = new ReferenceObservation { Component = expected.Path, Field = pointer.Field, IsNull = target == null };
			if (target != null)
			{
				observed.Type = target.GetType().FullName; observed.Name = target.name; observed.AssetPath = AssetDatabase.GetAssetPath(target);
				observed.ScenePath = target is GameObject go ? PathOf(go.transform) : target is UnityEngine.Component c ? PathOf(c.transform) : "";
				observed.Text = target is TextAsset text ? text.text : "";
				AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out observed.Guid, out observed.LocalId);
			}
			report.References.Add(observed);
			bool matches = observed.IsNull == pointer.IsNull;
			if (!pointer.IsNull)
			{
				matches &= target != null && observed.Type == pointer.Type && observed.Name == pointer.Name;
				if (pointer.Type == "UnityEngine.TextAsset") matches &= observed.Text == pointer.Text && !string.IsNullOrEmpty(observed.AssetPath) && !string.IsNullOrEmpty(observed.Guid);
				else
				{
					Transform expectedTransform = transforms.SingleOrDefault(t => PathOf(t) == pointer.ScenePath);
					UnityEngine.Object expectedTarget = expectedTransform == null ? null : pointer.Type == "UnityEngine.GameObject" ? (UnityEngine.Object)expectedTransform.gameObject
						: pointer.Type == "UnityEngine.Transform" ? expectedTransform : expectedTransform.GetComponent(component.GetType());
					matches &= expectedTarget != null && target == expectedTarget && observed.ScenePath == pointer.ScenePath;
				}
			}
			Check(report, matches, "ReferenceMismatch", JsonUtility.ToJson(observed));
		}
	}
	private static bool Close(float[] first, float[] second) { return first.Length == second.Length && first.Zip(second, (a, b) => Mathf.Abs(a - b) <= 0.00001f).All(equal => equal); }
	private static string PathOf(Transform t) { return t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name; }
	private static void Check(Report report, bool condition, string code, string detail) { if (!condition) report.Failures.Add(new Failure { Code = code, Detail = detail }); }
}
