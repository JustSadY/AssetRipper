using AssetRipper.Assets.Bundles;
using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Assembly.TypeTrees;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SerializationLogic;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.Tests.Decompilation;
using System.Collections;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("MonoBehaviourPayload")]
public class MonoBehaviourPayloadTests
{
	[Test]
	public void MissingScriptMustRetainTheUnparsedPayloadEvenWithoutRawCapture()
	{
		GameBundle bundle = new();
		var assets = bundle.AddNewProcessedCollection("payload.assets", UnityVersion.Parse("2022.3.0f1"));
		assets.SetLayout(assets.Version, BuildTarget.StandaloneWin64Player, TransferInstructionFlags.SerializeGameRelease);
		IMonoBehaviour seed = assets.CreateAsset(114, MonoBehaviour.Create);
		using MemoryStream bytes = new();
		using (AssetWriter writer = new(bytes, assets)) seed.WriteRelease(writer);
		byte[] data = bytes.ToArray().Concat(new byte[] { 42, 0, 0, 0 }).ToArray();
		using BaseManager manager = new(_ => { });
		IMonoBehaviour parsed = (IMonoBehaviour)new GameAssetFactory(manager).ReadAsset(seed.AssetInfo, data, null, [])!;
		Assert.That(parsed.LoadStructure(), Is.Null);
		Assert.That(parsed.Structure, Is.Not.Null, "A failed lazy read must retain its raw payload and incomplete dependency state.");
		UnreadableStructure failed = (UnreadableStructure)parsed.Structure!;
		Assert.That(failed.Data.ToArray(), Is.EqualTo(new byte[] { 42, 0, 0, 0 }));
		Assert.That(failed.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(parsed.LoadStructure(), Is.Null);
		Assert.That(parsed.Structure, Is.SameAs(failed));
		Assert.That(failed.DeepClone(default), Is.SameAs(failed));
		Assert.Throws<NotSupportedException>(() => { using AssetWriter writer = new(new MemoryStream(), assets); failed.WriteRelease(writer); });
	}

	[TestCase(false)]
	[TestCase(true)]
	public void BothSchemaPathsPreserveKnownFieldsAndPointers(bool typeTree)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		IMonoBehaviour parsed = fixture.Read(fixture.Fields(), typeTree);
		SerializableStructure structure = parsed.LoadStructure()!;
		Assert.That(structure, Is.Not.Null);
		AssertOracle(structure, fixture);
		WriteEvidence(fixture, structure, typeTree ? "TypeTree" : "Assembly", fixture.Fields());
	}

	[TestCase(EndianType.LittleEndian)]
	[TestCase(EndianType.BigEndian)]
	public void IndependentPayloadOracleAlsoCoversEndianAndAlignment(EndianType endian)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		Assert.That(TypeTreeNodeStruct.TryMakeFromTypeTree(MonoBehaviourPayloadFixture.Tree().OldType, out var root), Is.True);
		SerializableStructure structure = SerializableTreeType.FromRootNode(root, true).CreateSerializableStructure();
		byte[] bytes = fixture.Fields(endian);
		EndianSpanReader reader = new(bytes, endian);
		Assert.That(structure.TryRead(ref reader, fixture.Seed, ITypeResolver.Null, out var result), Is.True);
		Assert.That(result.BytesRead, Is.EqualTo(bytes.Length));
		Assert.That(result.ByteLength, Is.EqualTo(bytes.Length));
		AssertOracle(structure, fixture);
	}

	[TestCase(false, "truncated")]
	[TestCase(true, "truncated")]
	[TestCase(false, "trailing")]
	[TestCase(true, "trailing")]
	[TestCase(false, "negative-array")]
	[TestCase(true, "negative-array")]
	public void LayoutMismatchRetainsOnlyRawEvidenceAndNoGuessedFields(bool typeTree, string damage)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		byte[] original = fixture.Fields();
		byte[] input = damage == "truncated" ? original[..^1] : damage == "trailing" ? [..original, 123] : (byte[])original.Clone();
		if (damage == "negative-array") System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(28), -1);
		CountingSnapshot snapshot = new();
		IMonoBehaviour parsed = fixture.Read(input, typeTree, snapshot);
		Assert.That(parsed.LoadStructure(), Is.Null);
		UnreadableStructure failed = (UnreadableStructure)parsed.Structure!;
		Assert.That(failed.Data.ToArray(), Is.EqualTo(input));
		Assert.That(failed.ReadResult.Succeeded, Is.False);
		Assert.That(failed.ReadResult.ByteLength, Is.EqualTo(input.Length));
		Assert.That(failed.ReadResult.Diagnostic, Is.Not.Empty);
		Assert.That(failed.SchemaSource, Is.EqualTo(typeTree ? "TypeTree" : "Assembly"));
		Assert.That(failed.FetchDependencies(), Is.Empty);
		Assert.That(failed.DependencyCoverage, Is.EqualTo("Incomplete"));
		int reports = snapshot.Count;
		parsed.LoadStructure(); _ = parsed.FetchDependencies().ToArray();
		Assert.That(snapshot.Count, Is.EqualTo(reports));
		Assert.That(snapshot.Last, Is.EqualTo(RawObjectReadStatus.Partial));
	}

	[Test]
	public void TruncatedHeaderRetainsTheWholeObjectAndItsScope()
	{
		GameBundle bundle = new();
		var assets = bundle.AddNewProcessedCollection("header.assets", MonoBehaviourPayloadFixture.Version);
		IMonoBehaviour seed = assets.CreateAsset(114, MonoBehaviour.Create);
		using BaseManager manager = new(_ => { });
		IMonoBehaviour parsed = (IMonoBehaviour)new GameAssetFactory(manager).ReadAsset(seed.AssetInfo, new byte[] { 123 }, null, [])!;
		UnreadableStructure failed = (UnreadableStructure)parsed.Structure!;
		Assert.That(failed.Data.ToArray(), Is.EqualTo(new byte[] { 123 }));
		Assert.That(failed.PayloadScope, Is.EqualTo("WholeObject"));
		Assert.That(failed.SchemaSource, Is.EqualTo("Header"));
		Assert.That(failed.ObjectByteOffset, Is.Zero);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void BothExportersReportIncompleteDependenciesWithoutOptInRawCapture(bool primary)
	{
		GameBundle bundle = new();
		var assets = bundle.AddNewProcessedCollection("payload.assets", MonoBehaviourPayloadFixture.Version);
		assets.SetLayout(assets.Version, BuildTarget.StandaloneWin64Player, TransferInstructionFlags.SerializeGameRelease);
		IMonoBehaviour seed = assets.CreateAsset(114, MonoBehaviour.Create);
		using MemoryStream bytes = new();
		using (AssetWriter writer = new(bytes, assets)) seed.WriteRelease(writer);
		using BaseManager manager = new(_ => { });
		IMonoBehaviour parsed = (IMonoBehaviour)new GameAssetFactory(manager).ReadAsset(seed.AssetInfo, bytes.ToArray().Concat(new byte[] { 42 }).ToArray(), null, [])!;
		assets.ReplaceAsset(parsed);
		ITextAsset healthy = assets.CreateAsset(49, TextAsset.Create); healthy.Name = "Healthy"; healthy.Script_C49 = "Independent payload";
		GameData data = new(bundle, MonoBehaviourPayloadFixture.Version, manager, null);
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		settings.SetProjectSettings(data.ProjectVersion);
		VirtualFileSystem fs = new();
		CollectionExportReport report;
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(data, settings);
			exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False);
			report = exporter.LastReport!;
		}
		else
		{
			ExportHandler exporter = new(settings);
			exporter.Export(data, "/Output", fs);
			Assert.That(exporter.LastExportSucceeded, Is.False);
			report = exporter.LastCollectionReport!;
		}
		Assert.That(data.RawRecovery, Is.Null);
		Assert.That(report.Completed, Is.True);
		Assert.That(report.Failures, Is.Empty, "Failure must be attributable to the incomplete script payload, not another exporter.");
		Assert.That(report.MonoBehaviourFailures, Has.Count.EqualTo(1));
		var failure = report.MonoBehaviourFailures.Single();
		Assert.That(failure.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(Path.IsPathRooted(failure.PayloadPath), Is.False);
		Assert.That(fs.File.ReadAllBytes("/Output/" + failure.PayloadPath), Is.EqualTo(new byte[] { 42 }));
		Assert.That(failure.Sha256, Is.EqualTo(FixtureCompiler.Hash(new byte[] { 42 })));
		using JsonDocument json = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(json.RootElement.GetProperty("Succeeded").GetBoolean(), Is.False);
		Assert.That(json.RootElement.GetProperty("MonoBehaviourFailures").GetArrayLength(), Is.EqualTo(1));
		Assert.That(fs.Directory.EnumerateFiles("/Output", "*", SearchOption.AllDirectories).Any(p => fs.Path.GetFileName(p).StartsWith("Healthy", StringComparison.Ordinal)), Is.True);
		string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "monobehaviour-payload", $"{(primary ? "Primary" : "Unity")}-failure-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string reportPath = Path.Combine(directory, CollectionExportReport.FileName);
		File.WriteAllText(reportPath, fs.File.ReadAllText("/Output/collection-export-report.json"));
		string payloadPath = Path.Combine(directory, failure.PayloadPath);
		Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
		File.WriteAllBytes(payloadPath, fs.File.ReadAllBytes("/Output/" + failure.PayloadPath));
		TestContext.AddTestAttachment(reportPath, "Incomplete MonoBehaviour payload and export failure evidence");
	}

	[TestCase("io")]
	[TestCase("cancel")]
	[TestCase("access")]
	[TestCase("oom")]
	[TestCase("wrapped")]
	public void FatalStructureReadErrorsCannotBecomeLayoutFailures(string kind)
	{
		Exception failure = kind switch
		{
			"io" => new IOException("read failure"), "cancel" => new OperationCanceledException("cancel"),
			"access" => new UnauthorizedAccessException("access"), "oom" => new OutOfMemoryException("memory"),
			_ => new AggregateException(new InvalidOperationException("wrapped", new IOException("read failure"))),
		};
		using MonoBehaviourPayloadFixture fixture = new();
		Assert.That(Assert.Catch(() => ReadThrowingSchema(fixture.Seed, failure)), Is.SameAs(failure));
	}

	[Test]
	public void EndOfInputIsARecordedLayoutFailure()
	{
		using MonoBehaviourPayloadFixture fixture = new();
		Assert.That(ReadThrowingSchema(fixture.Seed, new EndOfStreamException("short payload")), Is.False);
	}

	[TestCase("recoverable")]
	[TestCase("io")]
	[TestCase("wrapped")]
	public void DeferredSchemaResolutionDoesNotHideFatalFailures(string kind)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		IMonoBehaviour parsed = fixture.Read(fixture.Fields(), false);
		Exception failure = kind switch
		{
			"recoverable" => new InvalidOperationException("schema unavailable"), "io" => new IOException("assembly read failed"),
			_ => new AggregateException(new InvalidOperationException("wrapped", new IOException("assembly read failed"))),
		};
		using MonoManager manager = new(_ => throw failure);
		parsed.Structure = new UnloadedStructure(parsed, manager, fixture.Fields());
		if (kind == "recoverable")
		{
			Assert.That(parsed.LoadStructure(), Is.Null);
			UnreadableStructure failed = (UnreadableStructure)parsed.Structure!;
			Assert.That(failed.Data.ToArray(), Is.EqualTo(fixture.Fields()));
			Assert.That(failed.ReadResult.Diagnostic, Does.Contain("schema unavailable"));
		}
		else Assert.That(Assert.Catch(() => parsed.LoadStructure()), Is.SameAs(failure));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void DeferredStatusJournalIoCannotBecomeAScriptLayoutFailure(bool valid)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		IOException failure = new("journal unavailable");
		byte[] fields = fixture.Fields();
		IMonoBehaviour parsed = fixture.Read(valid ? fields : fields[..^1], false, new ThrowingSnapshot(failure));
		Assert.That(Assert.Catch(() => parsed.LoadStructure()), Is.SameAs(failure));
		Assert.That(parsed.Structure, valid ? Is.TypeOf<SerializableStructure>() : Is.TypeOf<UnreadableStructure>());
	}

	[Test]
	public void ValidPayloadIsNotReportedAsAnIncompleteRead()
	{
		using MonoBehaviourPayloadFixture fixture = new();
		fixture.Read(fixture.Fields(), false);
		var report = CollectionExportReport.Create("Fixture", fixture.Bundle);
		VirtualFileSystem fs = new();
		report.CaptureMonoBehaviourFailures(fixture.Bundle, "/Output", fs);
		report.Completed = true;
		Assert.That(report.Succeeded, Is.True);
		Assert.That(report.MonoBehaviourFailures, Is.Empty);
		Assert.That(fs.Directory.Exists("/Output/MonoBehaviourRecovery"), Is.False);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RecoveryEvidenceCannotOverwriteConflictingOutput(bool directoryConflict)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		byte[] fields = fixture.Fields()[..^1]; fixture.Read(fields, true);
		VirtualFileSystem fs = new(); fs.Directory.Create("/Output");
		string path = "/Output/MonoBehaviourRecovery";
		if (!directoryConflict) { fs.Directory.Create(path); path += "/" + FixtureCompiler.Hash(fields) + ".bin"; }
		fs.File.WriteAllText(path, "existing output");
		var report = CollectionExportReport.Create("Fixture", fixture.Bundle);
		Assert.Catch(() => report.CaptureMonoBehaviourFailures(fixture.Bundle, "/Output", fs));
		Assert.That(fs.File.ReadAllText(path), Is.EqualTo("existing output"));
		Assert.That(report.Succeeded, Is.False);
	}

	private static bool ReadThrowingSchema(IMonoBehaviour asset, Exception failure)
	{
		SerializableStructure structure = new ThrowingType(failure).CreateSerializableStructure();
		EndianSpanReader reader = new(new byte[4], EndianType.LittleEndian);
		return structure.TryRead(ref reader, asset, ITypeResolver.Null);
	}

	private static void AssertOracle(SerializableStructure structure, MonoBehaviourPayloadFixture fixture)
	{
		Assert.That(structure.Type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "inherited", "hidden", "enabled", "label", "values", "direct", "links", "nested" }));
		Assert.That(structure["inherited"].AsInt32, Is.EqualTo(-17));
		Assert.That(structure["hidden"].AsInt16, Is.EqualTo(-1234));
		Assert.That(structure["enabled"].AsBoolean, Is.True);
		Assert.That(structure["label"].AsString, Is.EqualTo("Merhaba ☃"));
		Assert.That(structure["values"].AsInt32Array, Is.EqualTo(new[] { 7, -9, 42 }));
		Assert.That(new PPtr(structure["direct"].AsPPtr), Is.EqualTo(new PPtr(0, fixture.Local.PathID)));
		Assert.That(structure["nested"].AsStructure["count"].AsInt32, Is.EqualTo(91));
		Assert.That(new PPtr(structure["nested"].AsStructure["target"].AsPPtr), Is.EqualTo(new PPtr(0, 0x100000001L)));
		PPtr[] pointers = structure.FetchDependencies().Select(p => p.Item2).ToArray();
		Assert.That(structure.FetchDependencies().Select(p => p.Item1), Is.EqualTo(new[] { "direct", "links[0]", "links[1]", "links[2]", "links[3]", "nested.target" }));
		Assert.That(pointers, Is.EqualTo(new[] { new PPtr(0, fixture.Local.PathID), new(0, fixture.Local.PathID), new(1, fixture.External.PathID), new(0, 0), new(2, 999), new(0, 0x100000001L) }));
		Assert.That(fixture.Assets.TryGetAsset(pointers[0]), Is.SameAs(fixture.Local));
		Assert.That(fixture.Assets.TryGetAsset(pointers[2]), Is.SameAs(fixture.External));
		Assert.That(pointers[3].IsNull, Is.True);
		Assert.That(fixture.Assets.TryGetAsset(pointers[4]), Is.Null);
		Assert.That(fixture.Assets.TryGetAsset(pointers[5]), Is.Null);
	}

	private static void WriteEvidence(MonoBehaviourPayloadFixture fixture, SerializableStructure structure, string schema, byte[] bytes)
	{
		string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "monobehaviour-payload", schema + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "fixture.cs"), MonoBehaviourPayloadFixture.Source);
		File.WriteAllBytes(Path.Combine(directory, "fixture.dll"), fixture.AssemblyBytes);
		File.WriteAllBytes(Path.Combine(directory, "script-fields.bin"), bytes);
		string report = Path.Combine(directory, "payload-verification.json");
		File.WriteAllText(report, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, Scope = "ControlledFixture.ScriptFields", SchemaSource = schema, FixtureCompiler.ReferenceProfile,
			PayloadSha256 = FixtureCompiler.Hash(bytes), AssemblySha256 = FixtureCompiler.Hash(fixture.AssemblyBytes),
			Fields = structure.Type.Fields.Select(f => f.Name), FieldAndPointerOracle = "Passed",
			ObservedValues = new { Inherited = structure["inherited"].AsInt32, Hidden = structure["hidden"].AsInt16,
				Enabled = structure["enabled"].AsBoolean, Label = structure["label"].AsString, Values = structure["values"].AsInt32Array,
				NestedCount = structure["nested"].AsStructure["count"].AsInt32 },
			Pointers = structure.FetchDependencies().Select(p => new { Field = p.Item1, p.Item2.FileID, p.Item2.PathID }),
			UnityEditorValidation = "NotRun", EditorGeneratedPayload = false,
		}, new JsonSerializerOptions { WriteIndented = true }));
		TestContext.AddTestAttachment(report, "Controlled MonoBehaviour field and pointer oracle; Editor NotRun");
	}

	private sealed class CountingSnapshot : IRawObjectSnapshot
	{
		public int Count { get; private set; }
		public RawObjectReadStatus Last { get; private set; }
		public void ReportReadStatus(RawObjectReadStatus status, string? diagnostic = null) { Count++; Last = status; }
	}
	private sealed class ThrowingSnapshot(Exception failure) : IRawObjectSnapshot
	{
		private int count;
		public void ReportReadStatus(RawObjectReadStatus status, string? diagnostic = null) { if (++count == 2) throw failure; }
	}

	private sealed class ThrowingType : SerializableType
	{
		public ThrowingType(Exception failure) : base("Fixture", PrimitiveType.Complex, "Throwing") { Fields = new ThrowingFields(failure); MaxDepth = 1; }
	}
	private sealed class ThrowingFields(Exception failure) : IReadOnlyList<SerializableType.Field>
	{
		public int Count => 1;
		public SerializableType.Field this[int index] => throw failure;
		public IEnumerator<SerializableType.Field> GetEnumerator() => throw new NotSupportedException();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
