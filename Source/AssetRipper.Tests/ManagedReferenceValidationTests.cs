using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.Cloning;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.Tests.Decompilation;

namespace AssetRipper.Tests;

[Category("ManagedReferenceValidation")]
public class ManagedReferenceValidationTests
{
	[TestCase("duplicate", "Duplicate managed reference ID 101")]
	[TestCase("root", "root")]
	[TestCase("array", "items[0]")]
	[TestCase("inline", "inline.child")]
	[TestCase("node", "references.RefIds[1].data.next")]
	[TestCase("negative", "-3")]
	[TestCase("missing-type", "AbsentNode")]
	[TestCase("missing-class", "incomplete type")]
	[TestCase("missing-assembly", "incomplete type")]
	public void InvalidRegistryRetainsPayloadAndIncompleteDependencies(string variant, string diagnostic)
	{
		using ManagedReferencePayloadFixture fixture = new();
		byte[] fields = fixture.Fields(variant); CountingSnapshot snapshot = new();
		IMonoBehaviour asset = fixture.Read(fields, snapshot);
		Assert.That(asset.LoadStructure(), Is.Null, "Fully consuming bytes must not accept a corrupt managed graph.");
		Assert.That(asset.Structure, Is.TypeOf<UnreadableStructure>());
		UnreadableStructure failed = (UnreadableStructure)asset.Structure!;
		Assert.That(failed.Data.ToArray(), Is.EqualTo(fields));
		Assert.That(failed.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(failed.ReadResult.Diagnostic, Does.Contain(diagnostic));
		Assert.That(failed.FetchDependencies(), Is.Empty);
		Assert.That(snapshot.Last, Is.EqualTo(RawObjectReadStatus.Partial));
		int count = snapshot.Count; asset.LoadStructure(); _ = asset.FetchDependencies().ToArray();
		Assert.That(snapshot.Count, Is.EqualTo(count));
		Assert.That(failed.DeepClone(new PPtrConverter(asset)), Is.SameAs(failed));
	}

	[TestCase(EndianType.LittleEndian, "valid")]
	[TestCase(EndianType.BigEndian, "valid")]
	[TestCase(EndianType.LittleEndian, "null")]
	[TestCase(EndianType.BigEndian, "unknown")]
	[TestCase(EndianType.LittleEndian, "global-namespace")]
	[TestCase(EndianType.LittleEndian, "zero")]
	public void ForwardEdgesCyclesAndReservedSentinelsRemainReadable(EndianType endian, string variant)
	{
		using ManagedReferencePayloadFixture fixture = new();
		var type = fixture.Seed.ScriptP!.GetBehaviourType(fixture.Manager, out _)!;
		SerializableStructure structure = type.CreateSerializableStructure();
		byte[] fields = fixture.Fields(variant, endian); EndianSpanReader reader = new(fields, endian);
		Assert.That(structure.TryRead(ref reader, fixture.Seed, fixture.Manager, out var result), Is.True, result.Diagnostic);
		Assert.That(result.BytesRead, Is.EqualTo(fields.Length));
		Assert.That(structure["root"]["rid"].AsInt64, Is.EqualTo(variant == "null" ? -2 : variant == "unknown" ? -1 : variant == "zero" ? 0 : 101));
		Assert.That(structure["references"]["RefIds"].AsAssetArray.Length, Is.EqualTo(3));
		Assert.That(structure.FetchDependencies().Count(), Is.EqualTo(2));
	}

	[TestCase("duplicate")]
	[TestCase("node")]
	public void BigEndianCorruptionAlsoFailsAfterSchemaResolution(string variant)
	{
		using ManagedReferencePayloadFixture fixture = new();
		SerializableStructure structure = fixture.Seed.ScriptP!.GetBehaviourType(fixture.Manager, out _)!.CreateSerializableStructure();
		EndianSpanReader reader = new(fixture.Fields(variant, EndianType.BigEndian), EndianType.BigEndian);
		Assert.That(structure.TryRead(ref reader, fixture.Seed, fixture.Manager, out var result), Is.False);
		Assert.That(result.Diagnostic, Does.Contain(variant == "duplicate" ? "Duplicate managed reference ID" : "data.next"));
	}

	[Test]
	public void IdsCanRepeatAcrossHostsButCannotResolveFromAnotherHostsRegistry()
	{
		using ManagedReferencePayloadFixture fixture = new();
		IMonoBehaviour first = fixture.Read(fixture.Fields());
		IMonoBehaviour second = fixture.Read(fixture.Fields(), newHost: true);
		Assert.That(first.LoadStructure(), Is.Not.Null); Assert.That(second.LoadStructure(), Is.Not.Null);
		Assert.That(first.LoadStructure()!["root"]["rid"].AsInt64, Is.EqualTo(second.LoadStructure()!["root"]["rid"].AsInt64));
		IMonoBehaviour orphan = fixture.Read(fixture.Fields("host-scope"), newHost: true);
		Assert.That(orphan.LoadStructure(), Is.Null, "The other host's RID table is not in scope.");
		Assert.That((orphan.Structure as UnreadableStructure)?.ReadResult.Diagnostic, Does.Contain("4294967419"));
		Assert.That(first.LoadStructure(), Is.Not.Null);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void BothExportersFailAndPreserveCorruptGraphWhileHealthyAssetContinues(bool primary)
	{
		using ManagedReferencePayloadFixture fixture = new();
		byte[] fields = fixture.Fields("duplicate"); fixture.Read(fields);
		GameData data = new(fixture.Bundle, ManagedReferencePayloadFixture.Version, fixture.Manager, null);
		FullConfiguration settings = new() { ExportRootPath = "/Output" }; settings.SetProjectSettings(data.ProjectVersion);
		VirtualFileSystem fs = new(); CollectionExportReport report;
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(data, settings); exporter.Export(fixture.Bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False); report = exporter.LastReport!;
		}
		else
		{
			ExportHandler exporter = new(settings); exporter.Export(data, "/Output", fs);
			Assert.That(exporter.LastExportSucceeded, Is.False); report = exporter.LastCollectionReport!;
		}
		Assert.That(data.RawRecovery, Is.Null); Assert.That(report.Completed, Is.True); Assert.That(report.Failures, Is.Empty);
		Assert.That(report.MonoBehaviourFailures, Has.Count.EqualTo(1));
		var failure = report.MonoBehaviourFailures.Single();
		Assert.That(failure.Diagnostic, Does.Contain("Duplicate managed reference ID 101"));
		Assert.That(failure.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(fs.File.ReadAllBytes("/Output/" + failure.PayloadPath), Is.EqualTo(fields));
		Assert.That(failure.Sha256, Is.EqualTo(FixtureCompiler.Hash(fields)));
		Assert.That(fs.Directory.EnumerateFiles("/Output", "*", SearchOption.AllDirectories).Any(p => p.Contains("GraphHealthy", StringComparison.Ordinal)), Is.True);
	}

	private sealed class CountingSnapshot : IRawObjectSnapshot
	{
		public RawObjectReadStatus Last { get; private set; } public int Count { get; private set; }
		public void ReportReadStatus(RawObjectReadStatus status, string? diagnostic = null) { Last = status; Count++; }
	}
}
