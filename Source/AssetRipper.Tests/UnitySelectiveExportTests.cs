using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssetRipper.Tests;

[Category("ExportSelection"), NonParallelizable]
public class UnitySelectiveExportTests
{
	[TestCase("TypeTree")]
	[TestCase("AssemblyFallback")]
	public async Task SceneSelectionKeepsUserGraphAndReportsMissingEngineResource(string schema)
	{
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Selective scene Editor import NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		var fixture = UnitySceneFixture.Require(); using var manager = fixture.CreateManager();
		var (bundle, _) = UnitySceneBundleTests.Load(fixture, manager, schema);
		string output = UnitySceneFixture.EvidenceDirectory(schema, "Selection-Editor"); fixture.CopyInputs(output);
		GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings); handler.Process(data); AddExcluded(data);
		var component = bundle.FetchAssets().OfType<IMonoBehaviour>().First();
		settings.ExportSettings.Selection = new() { ClassIds = [114], PathIds = [component.PathID], Collections = [component.Collection.Name] };
		handler.Export(data, output, LocalFileSystem.Instance); Assert.That(handler.LastExportSucceeded, Is.False, output);
		var selection = handler.LastCollectionReport!.Selection!;
		Assert.That(selection.DependencyCoverage, Is.EqualTo("Incomplete"));
		var missing = selection.Issues.Single();
		Assert.That((missing.Kind, missing.Source!.ClassId, missing.Field, missing.FileId, missing.PathId),
			Is.EqualTo(("UnresolvedReference", 104, "m_SpotCookie", 2, 10001L)), "This source corpus omits the engine resource file; it must not be silently treated as complete.");
		Assert.That(selection.MatchedAssets, Has.Count.EqualTo(1));
		Assert.That(selection.IncludedAssets.All(a => a.Asset.Name != "IgnoreMe"), Is.True);
		string project = Path.Combine(output, "ExportedProject"), assets = Path.Combine(project, "Assets");
		Assert.That(Directory.GetFiles(assets, "*.unity", SearchOption.AllDirectories), Has.Length.EqualTo(1));
		Assert.That(Directory.GetFiles(assets, "IgnoreMe*", SearchOption.AllDirectories), Is.Empty);
		string validators = Path.Combine(assets, "Editor", "AssetRipperValidation"); Directory.CreateDirectory(validators);
		using (var source = typeof(UnitySelectiveExportTests).Assembly.GetManifestResourceStream("UnitySceneValidator.cs")!)
		using (var target = File.Create(Path.Combine(validators, "AssetRipperSceneValidator.cs"))) source.CopyTo(target);
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			SchemaSource = schema, Selection = selection, ExportSucceeded = handler.LastExportSucceeded,
			ValidationScope = "Source-owned scene hierarchy, fields and thirteen user references; omitted engine cookie remains unresolved",
			BeforeImport = UnityEditorRoundTripRunner.Snapshot(project, output),
		}, new JsonSerializerOptions { WriteIndented = true }));
		var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperSceneValidator"); using var report = result.Report;
		Assert.That(result.ExitCode, Is.Zero); Assert.That(report.RootElement.GetProperty("Passed").GetBoolean(), Is.True, report.RootElement.ToString());
		Assert.That(report.RootElement.GetProperty("MatchingComponentCount").GetInt32(), Is.EqualTo(2));
		Assert.That(report.RootElement.GetProperty("Nodes").GetArrayLength(), Is.EqualTo(5));
		Assert.That(report.RootElement.GetProperty("References").GetArrayLength(), Is.EqualTo(13));
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public void TextSelectionExportsOnlyTheExpectedSubset(bool primary, bool stripped)
	{
		var fixture = UnityAssemblyGraphFixture.Require(); using var manager = fixture.CreateManager();
		var bundle = UnityAssemblyGraphTests.Load(fixture, manager, stripped);
		string output = fixture.CopyInputs($"Selection-Text-{primary}-{stripped}");
		FullConfiguration settings = new(); settings.ExportSettings.Selection = new() { ClassIds = [49], Names = ["GraphExternal"] };
		ExportHandler handler = new(settings); GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		handler.Process(data); AddExcluded(data);
		if (primary)
		{
			settings.ExportRootPath = Path.Combine(output, "PrimaryContent");
			var exporter = PrimaryContentExporter.CreateDefault(data, settings); exporter.Export(bundle, settings, LocalFileSystem.Instance);
			Assert.That(exporter.LastExportSucceeded, Is.True, output);
			Assert.That(exporter.LastReport!.Selection!.IncludedAssets.Select(a => a.Asset.Name), Is.EqualTo(new[] { "GraphExternal" }));
		}
		else
		{
			handler.Export(data, output, LocalFileSystem.Instance); Assert.That(handler.LastExportSucceeded, Is.True, output);
			Assert.That(handler.LastCollectionReport!.Selection!.IncludedAssets.Select(a => a.Asset.Name), Is.EqualTo(new[] { "GraphExternal" }));
			using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "AuxiliaryFiles", "path_id_map.json")));
			Assert.That(map.RootElement.ToString(), Does.Contain("GraphExternal").And.Not.Contain("IgnoreMe"));
		}
		string assets = primary ? Path.Combine(output, "PrimaryContent") : Path.Combine(output, "ExportedProject", "Assets");
		Assert.That(Directory.GetFiles(assets, "GraphExternal*", SearchOption.AllDirectories).Any(), Is.True);
		Assert.That(Directory.GetFiles(assets, "IgnoreMe*", SearchOption.AllDirectories), Is.Empty);
		Assert.That(Directory.GetFiles(assets, "*.prefab", SearchOption.AllDirectories), Is.Empty);
		Assert.That(Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories), Is.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void MissingExternalDependencyCannotProduceSuccessfulSelection(bool stripped)
	{
		var fixture = UnityAssemblyGraphFixture.Require(); using var manager = fixture.CreateManager();
		var bundle = UnityAssemblyGraphTests.Load(fixture, manager, stripped, false);
		string output = fixture.CopyInputs($"Selection-MissingExternal-{stripped}");
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings); GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		handler.Process(data); SelectNode(settings, data); handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.False, output);
		var report = handler.LastCollectionReport!;
		Assert.That(report.Completed, Is.True); Assert.That(report.Selection!.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(report.Selection.Issues.Any(i => i.Kind == "UnresolvedReference"), Is.True);
		Assert.That(report.ExportedCollectionCount, Is.GreaterThan(0), "Known collections still reach disk.");
	}

	[TestCase(false, ScriptExportMode.Decompiled)]
	[TestCase(true, ScriptExportMode.Decompiled)]
	[TestCase(false, ScriptExportMode.Hybrid)]
	[TestCase(true, ScriptExportMode.Hybrid)]
	public async Task SelectedComponentPromotesItsPrefabAndDependenciesAndReimports(bool stripped, ScriptExportMode mode)
	{
		var fixture = UnityAssemblyGraphFixture.Require();
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Selective Editor import NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		Assert.That(File.Exists(executable), Is.True);
		using var manager = fixture.CreateManager(); var bundle = UnityAssemblyGraphTests.Load(fixture, manager, stripped);
		string output = fixture.CopyInputs($"Selection-Editor-{stripped}-{mode}");
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = mode;
		ExportHandler handler = new(settings); GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		handler.Process(data); AddExcluded(data); SelectNode(settings, data);
		handler.Export(data, output, LocalFileSystem.Instance); Assert.That(handler.LastExportSucceeded, Is.True, output);
		var selection = handler.LastCollectionReport!.Selection!;
		Assert.That(selection.MatchedAssets, Has.Count.EqualTo(1));
		Assert.That(selection.IncludedAssets.Any(a => a.Asset.Name == "GraphExternal" && a.Reason == "Dependency"), Is.True);
		Assert.That(selection.IncludedAssets.Any(a => a.Asset.ClassId == 114 && a.Reason == "AtomicCollection"), Is.True);
		Assert.That(selection.IncludedAssets.All(a => a.Asset.Name != "IgnoreMe"), Is.True);
		string project = Path.Combine(output, "ExportedProject"), assets = Path.Combine(project, "Assets");
		Assert.That(Directory.GetFiles(assets, "IgnoreMe*", SearchOption.AllDirectories), Is.Empty);
		string validators = Path.Combine(assets, "Editor", "AssetRipperValidation"); Directory.CreateDirectory(validators);
		using (var source = typeof(UnitySelectiveExportTests).Assembly.GetManifestResourceStream("UnityAssemblyGraphValidator.cs")!)
		using (var target = File.Create(Path.Combine(validators, "AssetRipperAssemblyGraphValidator.cs"))) source.CopyTo(target);
		string oraclePath = Path.Combine(output, "oracle.json"); var oracle = JsonNode.Parse(File.ReadAllText(oraclePath))!;
		oracle["ExportMode"] = mode.ToString(); File.WriteAllText(oraclePath, oracle.ToJsonString());
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			SchemaSource = stripped ? "Assembly" : "TypeTree", ExportMode = mode.ToString(), Selection = selection,
			BeforeImport = UnityEditorRoundTripRunner.Snapshot(project, output), ExportSucceeded = handler.LastExportSucceeded,
		}, new JsonSerializerOptions { WriteIndented = true }));
		var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperAssemblyGraphValidator");
		using var report = result.Report;
		Assert.That(result.ExitCode, Is.Zero); Assert.That(report.RootElement.GetProperty("Passed").GetBoolean(), Is.True, report.RootElement.ToString());
		Assert.That(report.RootElement.GetProperty("Bindings").GetArrayLength(), Is.EqualTo(2));
		Assert.That(report.RootElement.GetProperty("NodeResults").EnumerateArray().Select(n => n.GetInt32()), Is.EqualTo(fixture.Oracle.NodeResults));
		Assert.That(report.RootElement.GetProperty("BridgeResults").EnumerateArray().Select(n => n.GetInt32()), Is.EqualTo(fixture.Oracle.BridgeResults));
		Assert.That(report.RootElement.GetProperty("InterfaceResults").EnumerateArray().Select(n => n.GetInt32()), Is.EqualTo(fixture.Oracle.NodeResults));
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}
	private static void SelectNode(FullConfiguration settings, GameData data)
	{
		IMonoBehaviour node = data.GameBundle.FetchAssets().OfType<IMonoBehaviour>().Single(c => c.ScriptP!.ClassName_R == "GraphNode");
		settings.ExportSettings.Selection = new() { ClassIds = [114], PathIds = [node.PathID], Collections = [node.Collection.Name] };
	}
	private static void AddExcluded(GameData data)
	{
		var text = data.GameBundle.AddNewProcessedCollection("SelectionExcluded", data.ProjectVersion).CreateAsset(49, TextAsset.Create);
		text.Name = "IgnoreMe"; text.Script_C49 = "This asset must not be exported.";
	}
}
