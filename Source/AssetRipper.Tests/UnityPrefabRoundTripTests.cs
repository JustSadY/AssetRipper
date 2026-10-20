using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetRipper.Tests;

[Category("UnityPrefab"), NonParallelizable]
public class UnityPrefabRoundTripTests
{
	[TestCase(false, "None")]
	[TestCase(true, "None")]
	[TestCase(false, "MissingScript")]
	[TestCase(false, "ScalarMismatch")]
	[TestCase(false, "ReferenceMismatch")]
	[TestCase(false, "HierarchyMismatch")]
	public async Task EffectivePrefabDataReimportsAndAuthoringLossIsExplicit(bool stripped, string damage)
	{
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Prefab graph Editor reimport NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		Assert.That(File.Exists(executable), Is.True, "Configured Editor executable is missing.");
		var fixture = UnityPrefabFixture.Require(); using var manager = fixture.CreateManager(); var bundle = UnityPrefabGraphTests.Load(fixture, manager, stripped);
		int sourceObjects = bundle.FetchAssets().OfType<IPrefabInstance>().Count(); Assert.That(sourceObjects, Is.Zero);
		string output = UnityPrefabFixture.EvidenceDirectory(stripped, "Editor-" + damage); fixture.CopyInputs(output);
		var data = new GameData(bundle, bundle.GetMaxUnityVersion(), manager, null);
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		var handler = new ExportHandler(settings); handler.Process(data); handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.True); AssertOrigins(handler.LastCollectionReport!);
		string project = Path.Combine(output, "ExportedProject"); Assert.That(Directory.GetFiles(Path.Combine(project, "Assets"), "Assembly-CSharp.dll", SearchOption.AllDirectories), Is.Empty);
		string editor = Path.Combine(project, "Assets", "Editor", "AssetRipperValidation"); Directory.CreateDirectory(editor);
		using (var source = typeof(UnityPrefabRoundTripTests).Assembly.GetManifestResourceStream("UnityPrefabValidator.cs")!)
		using (var target = File.Create(Path.Combine(editor, "AssetRipperPrefabValidator.cs"))) source.CopyTo(target);
		object? mutation = InjectDamage(project, output, damage); object[] snapshot = UnityEditorRoundTripRunner.Snapshot(project, output);
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, SchemaSource = stripped ? "Assembly" : "TypeTree", Damage = damage,
			SourcePrefabInstanceObjectCount = sourceObjects, Mutation = mutation, BeforeImport = snapshot,
		}, new JsonSerializerOptions { WriteIndented = true }));
		var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperPrefabValidator"); using var report = result.Report;
		bool effective = damage == "None";
		Assert.That(result.ExitCode, Is.EqualTo(effective ? 0 : 1));
		Assert.That(report.RootElement.GetProperty("EffectiveDataPassed").GetBoolean(), Is.EqualTo(effective), report.RootElement.ToString());
		Assert.That(report.RootElement.GetProperty("FullRoundTripStatus").GetString(), Is.EqualTo(effective ? "Partial" : "Failed"));
		Assert.That(report.RootElement.GetProperty("AuthoringRelationsMatched").GetBoolean(), Is.False);
		var failures = report.RootElement.GetProperty("Failures").EnumerateArray().Select(f => f.GetProperty("Code").GetString()).ToArray();
		Assert.That(failures, Does.Not.Contain("ValidationException"), report.RootElement.ToString());
		if (!effective) Assert.That(failures, Does.Contain(damage), report.RootElement.ToString());
		else
		{
			Assert.That(report.RootElement.GetProperty("Nodes").GetArrayLength(), Is.EqualTo(9));
			Assert.That(report.RootElement.GetProperty("MatchingComponentCount").GetInt32(), Is.EqualTo(4));
			Assert.That(report.RootElement.GetProperty("References").GetArrayLength(), Is.EqualTo(25));
			Assert.That(report.RootElement.GetProperty("Prefabs").EnumerateArray().Select(p => p.GetProperty("AssetType").GetString()), Is.All.EqualTo("Regular"));
			Assert.That(report.RootElement.GetProperty("AuthoringDifferences").EnumerateArray().Select(f => f.GetProperty("Code").GetString()), Does.Contain("PrefabSourceLinks").And.Contain("PrefabAssetType"));
		}
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}
	[TestCase(false)]
	[TestCase(true)]
	public void PrimaryExportReportsGeneratedPrefabOrigin(bool stripped)
	{
		var fixture = UnityPrefabFixture.Require(); using var manager = fixture.CreateManager(); var bundle = UnityPrefabGraphTests.Load(fixture, manager, stripped);
		var data = new GameData(bundle, bundle.GetMaxUnityVersion(), manager, null);
		string output = UnityPrefabFixture.EvidenceDirectory(stripped, "Primary"); fixture.CopyInputs(output);
		FullConfiguration settings = new() { ExportRootPath = output }; settings.SetProjectSettings(data.ProjectVersion);
		var handler = new ExportHandler(settings); handler.Process(data);
		var exporter = PrimaryContentExporter.CreateDefault(data, settings); exporter.Export(bundle, settings, LocalFileSystem.Instance);
		Assert.That(exporter.LastExportSucceeded, Is.True); AssertOrigins(exporter.LastReport!);
		UnityPrefabGraphTests.Verify(bundle, fixture, true);
	}
	private static void AssertOrigins(CollectionExportReport report)
	{
		Assert.That(report.PrefabReconstructions.Select(p => p.RootName), Is.EquivalentTo(new[] { "BaseRig", "VariantRig", "NestedRig" }));
		Assert.That(report.PrefabReconstructions.Select(p => p.PrefabOrigin), Is.All.EqualTo("GeneratedFromHierarchy"));
		Assert.That(report.PrefabReconstructions.Select(p => p.AuthoringRelationsValidation), Is.All.EqualTo("NotRun"));
	}
	private static object? InjectDamage(string project, string output, string damage)
	{
		if (damage == "None") return null;
		string assets = Path.Combine(project, "Assets");
		string FindPrefab(string root) => Directory.GetFiles(assets, "*.prefab", SearchOption.AllDirectories).Single(p => File.ReadAllText(p).Contains("m_Name: " + root + "\n", StringComparison.Ordinal));
		string file = damage switch
		{
			"MissingScript" => Directory.GetFiles(assets, "ScenePayload.cs.meta", SearchOption.AllDirectories).Single(),
			"ReferenceMismatch" => FindPrefab("BaseRig") + ".meta",
			"ScalarMismatch" => FindPrefab("VariantRig"),
			_ => FindPrefab("NestedRig"),
		};
		byte[] bytes = File.ReadAllBytes(file); string original = File.ReadAllText(file);
		string changed = damage switch
		{
			"ScalarMismatch" => original.Replace("score: 29", "score: 30", StringComparison.Ordinal),
			"HierarchyMismatch" => original.Replace("m_Name: VariantOnly", "m_Name: RenamedChild", StringComparison.Ordinal),
			_ => Regex.Replace(original, "(?m)^guid: [0-9a-fA-F]{32}", "guid: " + Guid.NewGuid().ToString("N")),
		};
		Assert.That(changed, Is.Not.EqualTo(original)); string evidence = Path.Combine(output, "Evidence", "Mutation"); Directory.CreateDirectory(evidence);
		File.WriteAllBytes(Path.Combine(evidence, "original-file.bin"), bytes); File.WriteAllText(file, changed);
		return new { Path = Path.GetRelativePath(project, file), OriginalSha256 = FixtureCompiler.Hash(bytes), ModifiedSha256 = FixtureCompiler.Hash(File.ReadAllBytes(file)) };
	}
}
