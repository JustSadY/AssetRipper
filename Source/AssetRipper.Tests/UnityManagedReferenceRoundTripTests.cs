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

[Category("UnityManagedReference"), NonParallelizable]
public class UnityManagedReferenceRoundTripTests
{
	[TestCase(false, "None")]
	[TestCase(true, "None")]
	[TestCase(false, "SharedIdentity")]
	[TestCase(false, "Cycle")]
	[TestCase(false, "NodeData")]
	public async Task ExportedGraphReimportsWithIdentityCycleAndConcreteData(bool stripped, string damage)
	{
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Managed graph Editor reimport NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		Assert.That(File.Exists(executable), Is.True, "Configured Unity executable is missing.");
		string? fixturePath = Environment.GetEnvironmentVariable(UnityManagedReferenceFixture.EnvironmentVariable);
		// Older configured payload jobs do not implicitly opt in to this new corpus.
		if (string.IsNullOrWhiteSpace(fixturePath)) Assert.Ignore($"Managed graph Editor reimport NotRun: set {UnityManagedReferenceFixture.EnvironmentVariable}.");
		UnityManagedReferenceFixture fixture = UnityManagedReferenceFixture.Load(fixturePath!, LocalFileSystem.Instance);
		using var manager = fixture.CreateManager();
		string input = Path.Combine(fixture.DirectoryPath, stripped ? "WithoutTypeTree" : "WithTypeTree");
		GameBundle bundle = GameBundle.FromPaths([Path.Combine(input, "payload"), Path.Combine(input, "external")], new GameAssetFactory(manager), LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		string output = UnityManagedReferenceTests.EvidenceDirectory(stripped, "Editor-" + damage);
		UnityManagedReferenceTests.CopyInputs(fixture, output);
		GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings); handler.Process(data); handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.True, "Export failed; inspect " + output);
		string project = Path.Combine(output, "ExportedProject");
		Assert.That(Directory.GetFiles(Path.Combine(project, "Assets"), "Assembly-CSharp.dll", SearchOption.AllDirectories), Is.Empty);
		string editorDirectory = Path.Combine(project, "Assets", "Editor", "AssetRipperValidation"); Directory.CreateDirectory(editorDirectory);
		using (Stream source = typeof(UnityManagedReferenceRoundTripTests).Assembly.GetManifestResourceStream("UnityGraphValidator.cs")!)
		using (Stream target = File.Create(Path.Combine(editorDirectory, "AssetRipperGraphValidator.cs"))) source.CopyTo(target);
		object? mutation = InjectDamage(project, damage);
		object[] hashes = UnityEditorRoundTripRunner.Snapshot(project, output);
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, SchemaSource = stripped ? "Assembly" : "TypeTree", Damage = damage,
			ReferenceProfile = PinnedUnityReferences.ReadManifest(),
			Mutation = mutation, ExportSucceeded = handler.LastExportSucceeded, BeforeImport = hashes,
		}, new JsonSerializerOptions { WriteIndented = true }));
		var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperGraphValidator");
		using JsonDocument report = result.Report;
		Assert.That(report.RootElement.GetProperty("Passed").GetBoolean(), Is.EqualTo(damage == "None"), report.RootElement.ToString());
		Assert.That(report.RootElement.GetProperty("UnityVersion").GetString(), Is.EqualTo(fixture.Oracle.UnityVersion));
		Assert.That(result.ExitCode, Is.EqualTo(damage == "None" ? 0 : 1));
		if (damage != "None")
		{
			string[] codes = report.RootElement.GetProperty("Failures").EnumerateArray().Select(f => f.GetProperty("Code").GetString()!).ToArray();
			Assert.That(codes, Does.Contain(damage).And.Not.Contain("ValidationException").And.Not.Contain("MissingScript"), report.RootElement.ToString());
		}
		else
		{
			Assert.That(report.RootElement.GetProperty("SharedIdentityVerified").GetBoolean(), Is.True);
			Assert.That(report.RootElement.GetProperty("CycleVerified").GetBoolean(), Is.True);
			Assert.That(report.RootElement.GetProperty("Edges").GetArrayLength(), Is.EqualTo(7));
			Assert.That(report.RootElement.GetProperty("Nodes").GetArrayLength(), Is.EqualTo(2));
		}
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}

	private static object? InjectDamage(string project, string damage)
	{
		if (damage == "None") return null;
		string file = Directory.GetFiles(Path.Combine(project, "Assets"), "*.prefab", SearchOption.AllDirectories).Single();
		string original = File.ReadAllText(file);
		// Match only the named field or node, never a global replacement of an RID.
		string changed = damage switch
		{
			"SharedIdentity" => Regex.Replace(original, "(?m)^(  alias:\\s*\\r?\\n    rid: )101\\b", "${1}4294967419"),
			"Cycle" => Regex.Replace(original, "(?m)^(        next:\\s*\\r?\\n          rid: )101\\b", "${1}-2"),
			"NodeData" => original.Replace("number: -37", "number: -38", StringComparison.Ordinal),
			_ => throw new ArgumentOutOfRangeException(nameof(damage)),
		};
		Assert.That(changed, Is.Not.EqualTo(original), original);
		string originalHash = FixtureCompiler.Hash(File.ReadAllBytes(file)); File.WriteAllText(file, changed);
		return new { Path = Path.GetRelativePath(project, file), OriginalSha256 = originalHash, ModifiedSha256 = FixtureCompiler.Hash(File.ReadAllBytes(file)) };
	}
}
