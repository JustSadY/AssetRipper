using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("ManagedReferenceValidation")]
public class UnityManagedReferenceValidationTests
{
	[TestCase(false, "duplicate")]
	[TestCase(true, "duplicate")]
	[TestCase(false, "root")]
	[TestCase(true, "root")]
	[TestCase(false, "node")]
	[TestCase(true, "node")]
	[TestCase(false, "missing-type")]
	[TestCase(true, "missing-type")]
	public void MutatedRealObjectsRetainEvidenceAndCannotPassAsParsed(bool stripped, string damage)
	{
		var fixture = UnityManagedReferenceFixture.Require(); using var manager = fixture.CreateManager();
		ManagedReferenceMutationFactory factory = new(manager, damage);
		GameBundle bundle = Load(fixture, factory, stripped);
		IMonoBehaviour asset = bundle.FetchAssets().OfType<IMonoBehaviour>().Single();
		Assert.That(asset.LoadStructure(), Is.Null, "Corrupt graph must not be accepted after consuming every byte.");
		UnreadableStructure failed = (UnreadableStructure)asset.Structure!;
		Assert.That(failed.SchemaSource, Is.EqualTo(stripped ? "Assembly" : "TypeTree"));
		Assert.That(failed.Data.ToArray(), Is.EqualTo(factory.Modified[factory.StructureStart..]));
		Assert.That(failed.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(failed.ReadResult.Diagnostic, Does.Contain(Diagnostic(damage)));
		Assert.That(bundle.FetchAssets().OfType<ITextAsset>().Single(t => t.Name.String == "GraphExternal").Script_C49.String, Is.EqualTo("Managed graph external: ☃"));
		CollectionExportReport report = CollectionExportReport.Create("ValidationOnly", bundle);
		VirtualFileSystem fs = new(); report.CaptureMonoBehaviourFailures(bundle, "/Output", fs); report.Completed = true; report.Write("/Output", fs);
		AssertReport(report, factory, fs);
		WriteEvidence(fixture, factory, stripped, damage, report, fs, "Read");
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public void RealCorruptionFailsBothExportersAndKeepsIndependentTextAsset(bool stripped, bool primary)
	{
		var fixture = UnityManagedReferenceFixture.Require(); using var manager = fixture.CreateManager();
		ManagedReferenceMutationFactory factory = new(manager, "duplicate"); GameBundle bundle = Load(fixture, factory, stripped);
		GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		FullConfiguration settings = new() { ExportRootPath = "/Output" }; settings.SetProjectSettings(data.ProjectVersion);
		settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings); handler.Process(data);
		VirtualFileSystem fs = new(); CollectionExportReport report;
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(data, settings); exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False); report = exporter.LastReport!;
		}
		else { handler.Export(data, "/Output", fs); Assert.That(handler.LastExportSucceeded, Is.False); report = handler.LastCollectionReport!; }
		Assert.That(data.RawRecovery, Is.Null); AssertReport(report, factory, fs);
		Assert.That(fs.Directory.EnumerateFiles("/Output", "*", SearchOption.AllDirectories).Any(p => p.Contains("GraphExternal", StringComparison.Ordinal)), Is.True);
		WriteEvidence(fixture, factory, stripped, "duplicate", report, fs, primary ? "Primary" : "Unity");
	}

	private static GameBundle Load(UnityManagedReferenceFixture fixture, ManagedReferenceMutationFactory factory, bool stripped)
	{
		string directory = Path.Combine(fixture.DirectoryPath, stripped ? "WithoutTypeTree" : "WithTypeTree");
		GameBundle bundle = GameBundle.FromPaths([Path.Combine(directory, "payload"), Path.Combine(directory, "external")], factory, LocalFileSystem.Instance);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty); return bundle;
	}
	private static string Diagnostic(string damage) => damage switch
	{
		"duplicate" => "Duplicate managed reference ID 101", "root" => "root", "node" => "references.RefIds[1].data.next", _ => "AbsentNode",
	};
	private static void AssertReport(CollectionExportReport report, ManagedReferenceMutationFactory factory, VirtualFileSystem fs)
	{
		Assert.That(report.Completed, Is.True); Assert.That(report.Succeeded, Is.False); Assert.That(report.Failures, Is.Empty);
		Assert.That(report.MonoBehaviourFailures, Has.Count.EqualTo(1));
		var failure = report.MonoBehaviourFailures.Single();
		Assert.That(failure.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(failure.ObjectByteOffset, Is.EqualTo(factory.StructureStart));
		Assert.That(fs.File.ReadAllBytes("/Output/" + failure.PayloadPath), Is.EqualTo(factory.Modified[factory.StructureStart..]));
	}
	private static void WriteEvidence(UnityManagedReferenceFixture fixture, ManagedReferenceMutationFactory factory, bool stripped,
		string damage, CollectionExportReport report, VirtualFileSystem fs, string kind)
	{
		string output = UnityManagedReferenceTests.EvidenceDirectory(stripped, "Invalid-" + kind + "-" + damage);
		UnityManagedReferenceTests.CopyInputs(fixture, output);
		string mutation = Path.Combine(output, "Evidence", "Mutation"); Directory.CreateDirectory(mutation);
		File.WriteAllBytes(Path.Combine(mutation, "original-object.bin"), factory.Original);
		File.WriteAllBytes(Path.Combine(mutation, "modified-object.bin"), factory.Modified);
		foreach (var failure in report.MonoBehaviourFailures)
		{
			string path = Path.Combine(output, "Evidence", failure.PayloadPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, fs.File.ReadAllBytes("/Output/" + failure.PayloadPath));
		}
		File.WriteAllText(Path.Combine(output, CollectionExportReport.FileName), fs.File.ReadAllText("/Output/" + CollectionExportReport.FileName));
		string evidence = Path.Combine(output, "invalid-graph-verification.json");
		File.WriteAllText(evidence, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, fixture.Manifest.UnityVersion, SchemaSource = stripped ? "Assembly" : "TypeTree", Damage = damage,
			OriginalObjectSha256 = FixtureCompiler.Hash(factory.Original), ModifiedObjectSha256 = FixtureCompiler.Hash(factory.Modified),
			factory.MutationOffset, factory.MutationLength, factory.StructureStart, report.ExportKind,
			Input = "CopiedEditorGeneratedObject", Validation = "RejectedAndPreserved", report.MonoBehaviourFailures,
			ExportedProjectEditorImport = "NotRun",
		}, new JsonSerializerOptions { WriteIndented = true }));
		TestContext.AddTestAttachment(evidence, "Invalid real managed-reference object and unchanged corpus evidence");
	}
}
