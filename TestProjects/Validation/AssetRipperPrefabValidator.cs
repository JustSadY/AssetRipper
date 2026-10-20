using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Effective runtime data and original authoring relations have separate outcomes.
public static class AssetRipperPrefabValidator
{
	[Serializable] public sealed class Node { public string Path; public int SiblingIndex; public bool ActiveSelf; public float[] Position; public float[] Rotation; public float[] Scale; }
	[Serializable] public sealed class Pointer { public string Field; public bool IsNull; public string Type; public string ScenePath; public string Name; public string Text; }
	[Serializable] public sealed class Values { public string Path; public int Inherited; public int Hidden; public int Score; public string Label; public int NestedCount; public Pointer[] Pointers; }
	[Serializable] public sealed class Link { public string Path; public string SourceRoot; public string[] PropertyPaths; }
	[Serializable] public sealed class Prefab { public string Root; public string AssetType; public Link[] Links; public Node[] Nodes; public Values[] Components; }
	[Serializable] public sealed class Oracle { public string UnityVersion; public string ScriptAssembly; public string ScriptNamespace; public string ScriptType; public Prefab[] Prefabs; }
	[Serializable] public sealed class Failure { public string Code; public string Detail; }
	[Serializable] public sealed class AssetObservation { public string Root; public string AssetPath; public string Guid; public string AssetType; public Link[] Links; }
	[Serializable] public sealed class ScriptBinding { public string Component; public string Path; public string Guid; public long LocalId; }
	[Serializable] public sealed class ReferenceObservation
	{
		public string Component; public string Field; public bool IsNull; public string Type; public string HierarchyPath;
		public string AssetPath; public string Guid; public long LocalId; public string Text;
	}
	[Serializable] public sealed class Report
	{
		public int SchemaVersion = 1; public string UnityVersion; public bool EffectiveDataPassed; public bool AuthoringRelationsMatched; public string FullRoundTripStatus;
		public int MissingScriptCount; public int MatchingComponentCount;
		public List<AssetObservation> Prefabs = new List<AssetObservation>(); public List<Node> Nodes = new List<Node>();
		public List<Values> Components = new List<Values>(); public List<ScriptBinding> Scripts = new List<ScriptBinding>();
		public List<ReferenceObservation> References = new List<ReferenceObservation>();
		public List<Failure> Failures = new List<Failure>(); public List<Failure> AuthoringDifferences = new List<Failure>();
	}
	public static void Validate()
	{
		Report report = new Report { UnityVersion = Application.unityVersion };
		try
		{
			Oracle oracle = JsonUtility.FromJson<Oracle>(File.ReadAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_ORACLE")));
			Check(report, oracle.UnityVersion == Application.unityVersion, "EditorVersion", Application.unityVersion);
			string[] files = Directory.GetFiles("Assets", "*.prefab", SearchOption.AllDirectories);
			Check(report, files.Length == oracle.Prefabs.Length, "PrefabCount", "Prefab files: " + files.Length);
			GameObject[] assets = files.Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p.Replace('\\', '/'))).Where(p => p != null).ToArray();
			Transform[] transforms = assets.SelectMany(a => a.GetComponentsInChildren<Transform>(true)).ToArray();
			foreach (Prefab expected in oracle.Prefabs)
			{
				GameObject asset = assets.SingleOrDefault(a => a.name == expected.Root);
				Check(report, asset != null, "PrefabBinding", expected.Root); if (asset == null) continue;
				Transform[] nodes = asset.GetComponentsInChildren<Transform>(true).OrderBy(t => PathOf(t), StringComparer.Ordinal).ToArray();
				AssetObservation observed = new AssetObservation { Root = asset.name, AssetPath = AssetDatabase.GetAssetPath(asset), AssetType = PrefabUtility.GetPrefabAssetType(asset).ToString(),
					Links = nodes.Where(t => PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) != null).Select(t => {
						GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
						return new Link { Path = PathOf(t), SourceRoot = source.transform.root.name,
							PropertyPaths = (PrefabUtility.GetPropertyModifications(t.gameObject) ?? new PropertyModification[0]).Select(p => p.propertyPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray() };
					}).ToArray() };
				observed.Guid = AssetDatabase.AssetPathToGUID(observed.AssetPath); report.Prefabs.Add(observed);
				if (observed.AssetType != expected.AssetType) report.AuthoringDifferences.Add(new Failure { Code = "PrefabAssetType", Detail = expected.Root + ": " + expected.AssetType + " -> " + observed.AssetType });
				if (!expected.Links.Select(l => l.Path + ":" + l.SourceRoot + ":" + string.Join(",", l.PropertyPaths))
					.SequenceEqual(observed.Links.Select(l => l.Path + ":" + l.SourceRoot + ":" + string.Join(",", l.PropertyPaths))))
					report.AuthoringDifferences.Add(new Failure { Code = "PrefabSourceLinks", Detail = expected.Root });
				Check(report, nodes.Select(t => PathOf(t)).SequenceEqual(expected.Nodes.Select(n => n.Path)), "HierarchyMismatch", expected.Root);
				foreach (Transform t in nodes)
				{
					Node node = new Node { Path = PathOf(t), SiblingIndex = t.GetSiblingIndex(), ActiveSelf = t.gameObject.activeSelf,
						Position = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z }, Rotation = new[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w }, Scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z } };
					report.Nodes.Add(node); report.MissingScriptCount += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
					Node e = expected.Nodes.SingleOrDefault(n => n.Path == node.Path);
					if (e != null) Check(report, node.ActiveSelf == e.ActiveSelf && (t.parent == null || node.SiblingIndex == e.SiblingIndex)
						&& Close(node.Position, e.Position) && Close(node.Rotation, e.Rotation) && Close(node.Scale, e.Scale), "HierarchyMismatch", node.Path);
				}
				foreach (Values values in expected.Components)
				{
					Transform t = nodes.SingleOrDefault(n => PathOf(n) == values.Path);
					MonoBehaviour component = t == null ? null : t.GetComponents<MonoBehaviour>().SingleOrDefault(c => c != null && c.GetType().FullName == oracle.ScriptNamespace + "." + oracle.ScriptType);
					if (component == null) continue;
					report.MatchingComponentCount++; Check(report, component.GetType().Assembly.GetName().Name == oracle.ScriptAssembly, "ScriptBinding", values.Path);
					ObserveComponent(report, values, component, transforms);
				}
			}
			Check(report, report.MissingScriptCount == 0, "MissingScript", "Missing slots: " + report.MissingScriptCount);
			Check(report, report.MatchingComponentCount == oracle.Prefabs.Sum(p => p.Components.Length), "ComponentCount", "Components: " + report.MatchingComponentCount);
		}
		catch (Exception ex) { Check(report, false, "ValidationException", ex.ToString()); }
		report.EffectiveDataPassed = report.Failures.Count == 0; report.AuthoringRelationsMatched = report.AuthoringDifferences.Count == 0 && report.EffectiveDataPassed;
		report.FullRoundTripStatus = !report.EffectiveDataPassed ? "Failed" : report.AuthoringRelationsMatched ? "Passed" : "Partial";
		File.WriteAllText(Environment.GetEnvironmentVariable("ASSETRIPPER_ROUNDTRIP_REPORT"), JsonUtility.ToJson(report, true));
		EditorApplication.Exit(report.EffectiveDataPassed ? 0 : 1);
	}
	private static void ObserveComponent(Report report, Values expected, MonoBehaviour component, Transform[] transforms)
	{
		SerializedObject so = new SerializedObject(component); so.Update();
		Values actual = new Values { Path = expected.Path, Inherited = so.FindProperty("inherited").intValue, Hidden = so.FindProperty("hidden").intValue,
			Score = so.FindProperty("score").intValue, Label = so.FindProperty("label").stringValue, NestedCount = so.FindProperty("nested.count").intValue };
		report.Components.Add(actual);
		Check(report, actual.Inherited == expected.Inherited && actual.Hidden == expected.Hidden && actual.Score == expected.Score
			&& actual.Label == expected.Label && actual.NestedCount == expected.NestedCount, "ScalarMismatch", JsonUtility.ToJson(actual));
		MonoScript script = MonoScript.FromMonoBehaviour(component); ScriptBinding binding = new ScriptBinding { Component = expected.Path, Path = AssetDatabase.GetAssetPath(script) };
		AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out binding.Guid, out binding.LocalId); report.Scripts.Add(binding);
		Check(report, so.FindProperty("m_Script").objectReferenceValue == script && binding.LocalId == 11500000 && binding.Path.EndsWith("/ScenePayload.cs", StringComparison.Ordinal), "ScriptBinding", expected.Path);
		foreach (Pointer pointer in expected.Pointers)
		{
			UnityEngine.Object target = so.FindProperty(pointer.Field.Replace("[", ".Array.data[")).objectReferenceValue;
			var observation = new ReferenceObservation { Component = expected.Path, Field = pointer.Field, IsNull = target == null };
			if (target != null)
			{
				observation.Type = target.GetType().FullName; observation.AssetPath = AssetDatabase.GetAssetPath(target); observation.Text = target is TextAsset text ? text.text : "";
				observation.HierarchyPath = target is GameObject go ? PathOf(go.transform) : target is UnityEngine.Component c ? PathOf(c.transform) : "";
				AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out observation.Guid, out observation.LocalId);
			}
			report.References.Add(observation); bool matches = observation.IsNull == pointer.IsNull;
			if (!pointer.IsNull)
			{
				matches &= target != null && observation.Type == pointer.Type;
				if (pointer.Type == "UnityEngine.TextAsset") matches &= observation.Text == pointer.Text && !string.IsNullOrEmpty(observation.Guid) && !string.IsNullOrEmpty(observation.AssetPath);
				else
				{
					Transform t = transforms.SingleOrDefault(n => PathOf(n) == pointer.ScenePath);
					UnityEngine.Object identity = t == null ? null : pointer.Type == "UnityEngine.GameObject" ? (UnityEngine.Object)t.gameObject : pointer.Type == "UnityEngine.Transform" ? t : t.GetComponent(component.GetType());
					matches &= identity != null && target == identity && observation.HierarchyPath == pointer.ScenePath;
				}
			}
			Check(report, matches, "ReferenceMismatch", JsonUtility.ToJson(observation));
		}
	}
	private static bool Close(float[] a, float[] b) { return a.Length == b.Length && a.Zip(b, (x, y) => Mathf.Abs(x - y) <= 0.00001f).All(equal => equal); }
	private static string PathOf(Transform t) { return t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name; }
	private static void Check(Report report, bool passed, string code, string detail) { if (!passed) report.Failures.Add(new Failure { Code = code, Detail = detail }); }
}
