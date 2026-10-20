using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetRipper.Tests;

[Category("UnityScene"), NonParallelizable]
public class UnitySceneRoundTripTests
{
	[TestCase("TypeTree", "None")]
	[TestCase("DisableWriteTypeTree", "None")]
	[TestCase("AssemblyFallback", "None")]
	[TestCase("TypeTree", "MissingScript")]
	[TestCase("TypeTree", "ScalarMismatch")]
	[TestCase("TypeTree", "ReferenceMismatch")]
	[TestCase("TypeTree", "HierarchyMismatch")]
	public async Task RecoveredSceneOpensWithHierarchyScriptAndReferenceIdentity(string mode, string damage)
	{
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Recovered scene Editor import NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		Assert.That(File.Exists(executable), Is.True, "Configured Unity executable is missing.");
		UnitySceneFixture fixture = UnitySceneFixture.Require(); using var manager = fixture.CreateManager();
		var (bundle, schemas) = UnitySceneBundleTests.Load(fixture, manager, mode);
		string output = UnitySceneFixture.EvidenceDirectory(mode, "Editor-" + damage); fixture.CopyInputs(output);
		GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings); handler.Process(data);
		SceneDefinition[] scenes = bundle.FetchAssetCollections().Where(c => c.Scene != null).Select(c => c.Scene!).Distinct().ToArray();
		Assert.That(scenes.Length, Is.EqualTo(1)); Assert.That(scenes[0].Name, Is.EqualTo(fixture.Oracle.SceneName));
		handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.True, "Export failed; inspect " + output);
		string project = Path.Combine(output, "ExportedProject");
		Assert.That(Directory.GetFiles(Path.Combine(project, "Assets"), "Assembly-CSharp.dll", SearchOption.AllDirectories), Is.Empty);
		Assert.That(Directory.GetFiles(Path.Combine(project, "Assets"), "*.unity", SearchOption.AllDirectories), Has.Length.EqualTo(1));
		string editorDirectory = Path.Combine(project, "Assets", "Editor", "AssetRipperValidation"); Directory.CreateDirectory(editorDirectory);
		using (Stream source = typeof(UnitySceneRoundTripTests).Assembly.GetManifestResourceStream("UnitySceneValidator.cs")!)
		using (Stream target = File.Create(Path.Combine(editorDirectory, "AssetRipperSceneValidator.cs"))) source.CopyTo(target);
		object? mutation = InjectDamage(project, output, damage); object[] hashes = UnityEditorRoundTripRunner.Snapshot(project, output);
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, SchemaSource = mode == "AssemblyFallback" ? "Assembly" : "TypeTree", BuildMode = mode == "TypeTree" ? "Default" : "DisableWriteTypeTree",
			ControlledTypeTreeSuppression = mode == "AssemblyFallback", Schemas = schemas, Damage = damage,
			Mutation = mutation, ExportSucceeded = handler.LastExportSucceeded, BeforeImport = hashes, ReferenceProfile = PinnedUnityReferences.ReadManifest(),
		}, new JsonSerializerOptions { WriteIndented = true }));
		var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperSceneValidator"); using var report = result.Report;
		Assert.That(report.RootElement.GetProperty("Passed").GetBoolean(), Is.EqualTo(damage == "None"), report.RootElement.ToString());
		Assert.That(result.ExitCode, Is.EqualTo(damage == "None" ? 0 : 1));
		Assert.That(report.RootElement.GetProperty("UnityVersion").GetString(), Is.EqualTo(fixture.Oracle.UnityVersion));
		if (damage != "None")
		{
			var codes = report.RootElement.GetProperty("Failures").EnumerateArray().Select(f => f.GetProperty("Code").GetString()).ToArray();
			Assert.That(codes, Does.Contain(damage).And.Not.Contain("ValidationException"), report.RootElement.ToString());
		}
		else
		{
			Assert.That(report.RootElement.GetProperty("MatchingComponentCount").GetInt32(), Is.EqualTo(2));
			Assert.That(report.RootElement.GetProperty("Nodes").GetArrayLength(), Is.EqualTo(5));
			Assert.That(report.RootElement.GetProperty("References").GetArrayLength(), Is.EqualTo(13));
		}
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}
	private static object? InjectDamage(string project, string output, string damage)
	{
		if (damage == "None") return null;
		string assets = Path.Combine(project, "Assets");
		string file = damage == "MissingScript" ? Directory.GetFiles(assets, "ScenePayload.cs.meta", SearchOption.AllDirectories).Single()
			: damage == "ReferenceMismatch" ? Directory.GetFiles(assets, "SceneExternal.*.meta", SearchOption.AllDirectories).Single()
			: Directory.GetFiles(assets, "*.unity", SearchOption.AllDirectories).Single();
		string original = File.ReadAllText(file);
		string changed = damage switch
		{
			"ScalarMismatch" => original.Replace("score: 17", "score: 18", StringComparison.Ordinal),
			"HierarchyMismatch" => original.Replace("m_Name: InactiveChild", "m_Name: RenamedInactive", StringComparison.Ordinal),
			_ => Regex.Replace(original, "(?m)^guid: [0-9a-fA-F]{32}", "guid: " + Guid.NewGuid().ToString("N")),
		};
		Assert.That(changed, Is.Not.EqualTo(original));
		byte[] bytes = File.ReadAllBytes(file); string hash = FixtureCompiler.Hash(bytes);
		string evidence = Path.Combine(output, "Evidence", "Mutation"); Directory.CreateDirectory(evidence);
		File.WriteAllBytes(Path.Combine(evidence, "original-file.bin"), bytes); File.WriteAllText(file, changed);
		return new { Path = Path.GetRelativePath(project, file), OriginalSha256 = hash, ModifiedSha256 = FixtureCompiler.Hash(File.ReadAllBytes(file)) };
	}
}
