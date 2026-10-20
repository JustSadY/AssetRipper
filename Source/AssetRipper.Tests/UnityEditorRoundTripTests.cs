using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetRipper.Tests;

[Category("UnityEditorRoundTrip"), NonParallelizable]
public class UnityEditorRoundTripTests
{
	[TestCase(false, "None", TestName = "EditorRoundTrip_TypeTree")]
	[TestCase(true, "None", TestName = "EditorRoundTrip_AssemblySchema")]
	[TestCase(false, "MissingScript", TestName = "EditorRoundTrip_MissingScriptDetected")]
	[TestCase(false, "ScalarMismatch", TestName = "EditorRoundTrip_ChangedValueDetected")]
	[TestCase(false, "ReferenceMismatch", TestName = "EditorRoundTrip_BrokenReferenceDetected")]
	public async Task ExportedProjectReimportsWithKnownFieldsAndReferences(bool stripped, string damage)
	{
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Exported project Editor import NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		Assert.That(File.Exists(executable), Is.True, "Configured Unity executable is missing.");
		string? fixturePath = Environment.GetEnvironmentVariable(UnityEditorPayloadFixture.EnvironmentVariable);
		Assert.That(fixturePath, Is.Not.Null.And.Not.Empty, "Editor import requires a configured Editor payload fixture.");
		UnityEditorPayloadFixture fixture = UnityEditorPayloadFixture.Load(fixturePath!, LocalFileSystem.Instance);
		using var manager = fixture.CreateManager();
		string input = Path.Combine(fixture.DirectoryPath, stripped ? "WithoutTypeTree" : "WithTypeTree");
		GameBundle bundle = GameBundle.FromPaths([Path.Combine(input, "payload"), Path.Combine(input, "external")], new GameAssetFactory(manager), LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-editor-roundtrip", $"{(stripped ? "Assembly" : "TypeTree")}-{damage}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(output);
		File.Copy(Path.Combine(fixture.DirectoryPath, "oracle.json"), Path.Combine(output, "oracle.json"));
		File.Copy(Path.Combine(fixture.DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings);
		handler.Process(data);
		handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.True, "AssetRipper export failed; inspect reports in " + output);
		string project = Path.Combine(output, "ExportedProject");
		Assert.That(Directory.GetFiles(Path.Combine(project, "Assets"), "Assembly-CSharp.dll", SearchOption.AllDirectories), Is.Empty,
			"Editor must compile the decompiled source, not load the original fixture DLL.");
		string editorDirectory = Path.Combine(project, "Assets", "Editor", "AssetRipperValidation"); Directory.CreateDirectory(editorDirectory);
		using (Stream source = typeof(UnityEditorRoundTripTests).Assembly.GetManifestResourceStream("UnityRoundTripValidator.cs")!)
		using (Stream target = File.Create(Path.Combine(editorDirectory, "AssetRipperRoundTripValidator.cs"))) source.CopyTo(target);
		object? mutation = InjectDamage(project, damage);
		string evidence = Path.Combine(output, "Evidence");
		object[] hashes = UnityEditorRoundTripRunner.Snapshot(project, output);
		foreach (var file in fixture.Manifest.Files)
		{
			string target = Path.Combine(evidence, "Input", file.Path);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(fixture.DirectoryPath, file.Path), target);
		}
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, SchemaSource = stripped ? "Assembly" : "TypeTree", Damage = damage,
			Mutation = mutation, ExportSucceeded = handler.LastExportSucceeded, BeforeImport = hashes,
		}, new JsonSerializerOptions { WriteIndented = true }));
		string reportPath = Path.Combine(output, "editor-import-report.json");
		var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperRoundTripValidator");
		using JsonDocument report = result.Report;
		bool expectedSuccess = damage == "None";
		Assert.That(report.RootElement.GetProperty("Passed").GetBoolean(), Is.EqualTo(expectedSuccess), File.ReadAllText(reportPath));
		Assert.That(report.RootElement.GetProperty("UnityVersion").GetString(), Is.EqualTo(fixture.Oracle.UnityVersion));
		Assert.That(result.ExitCode, Is.EqualTo(expectedSuccess ? 0 : 1));
		if (!expectedSuccess) Assert.That(report.RootElement.GetProperty("Failures").EnumerateArray().Any(f => f.GetProperty("Code").GetString() == damage), Is.True, File.ReadAllText(reportPath));
		// Keep the recovered project and curated evidence, not hundreds of MB of
		// disposable Editor import caches for every successful/expected-failure case.
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}

	private static object? InjectDamage(string project, string damage)
	{
		if (damage == "None") return null;
		string assets = Path.Combine(project, "Assets");
		string file;
		string original;
		string changed;
		if (damage == "ScalarMismatch")
		{
			file = Directory.GetFiles(assets, "*.prefab", SearchOption.AllDirectories).Single(p => File.ReadAllText(p).Contains("inherited: -17", StringComparison.Ordinal));
			original = File.ReadAllText(file); changed = original.Replace("inherited: -17", "inherited: -18", StringComparison.Ordinal);
		}
		else
		{
			file = damage == "MissingScript"
				? Directory.GetFiles(assets, "EditorPayload.cs.meta", SearchOption.AllDirectories).Single()
				: Directory.GetFiles(assets, "*.meta", SearchOption.AllDirectories).Single(p => Path.GetFileName(p).StartsWith("ExternalTarget.", StringComparison.Ordinal));
			original = File.ReadAllText(file);
			changed = Regex.Replace(original, "(?m)^guid: [0-9a-fA-F]{32}", "guid: " + Guid.NewGuid().ToString("N"));
		}
		Assert.That(changed, Is.Not.EqualTo(original));
		string originalHash = FixtureCompiler.Hash(File.ReadAllBytes(file));
		File.WriteAllText(file, changed);
		return new { Path = Path.GetRelativePath(project, file), OriginalSha256 = originalHash, ModifiedSha256 = FixtureCompiler.Hash(File.ReadAllBytes(file)) };
	}
}
