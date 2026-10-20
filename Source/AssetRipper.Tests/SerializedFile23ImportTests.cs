using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Recovery;
using AssetRipper.Import.Structure;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.Tests.Shared;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("SerializedFile23")]
public class SerializedFile23ImportTests
{
	private static readonly UnityVersion Version = UnityVersion.Parse("6000.4.0f1");
	private const string Text = "Sentetik v23: Merhaba ☃";

	[TestCase(false)]
	[TestCase(true)]
	public void ExternalTreesUseGeneratedEngineReaderAndRemainInformationalInBothExports(bool primary)
	{
		VirtualFileSystem fs = new();
		byte[] payload = TextPayload();
		var fixture = SerializedFile23Fixture.Create(EndianType.LittleEndian, true, true, objectData: payload);
		WriteInput(fs, fixture.Bytes);
		RawObjectStore store = new(fs, "/Recovery");
		using BaseManager manager = new(_ => { });
		GameBundle bundle = GameBundle.FromPaths(["/Input/test.assets"], new GameAssetFactory(manager) { RawObjectSink = store }, fs);
		Assert.That(bundle.FetchFailedFiles(), Is.Empty);
		ITextAsset text = bundle.FetchAssets().OfType<ITextAsset>().Single();
		Assert.That(text.Name.String, Is.EqualTo("Healthy")); Assert.That(text.Script_C49.String, Is.EqualTo(Text));
		SerializedAssetCollection collection = (SerializedAssetCollection)text.Collection;
		Assert.That((int)collection.SerializedFormat, Is.EqualTo(23)); Assert.That(collection.HasTypeTree, Is.True);
		Assert.That(collection.SerializedTypeCount, Is.EqualTo(2)); Assert.That(collection.ExtractedTypeTreeCount, Is.EqualTo(2));
		Assert.That(store.GetSummary().PreservedObjectCount, Is.EqualTo(1)); Assert.That(store.GetSummary().FailedObjectCount, Is.Zero);
		string raw = fs.Directory.EnumerateFiles(store.DirectoryPath + "/Objects").Single();
		Assert.That(fs.File.ReadAllBytes(raw), Is.EqualTo(payload));
		string journal = fs.File.ReadAllText(store.JournalPath);
		var discovered = journal.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
		{
			using var json = JsonDocument.Parse(line); return json.RootElement.Clone();
		}).Single(e => e.GetProperty("Event").GetString() == "CollectionDiscovered");
		Assert.That(discovered.GetProperty("Collection").GetProperty("ExtractedTypeTrees").GetInt32(), Is.EqualTo(2));
		CollectionExportReport report = Export(bundle, manager, fs, primary);
		Assert.That(report.Succeeded, Is.True); Assert.That(report.ImportFailures, Is.Empty);
		Assert.That(report.ExtractedTypeTrees.Single().ExtractedTypeTrees, Is.EqualTo(2));
		Assert.That(report.ExtractedTypeTrees.Single().SerializedTypes, Is.EqualTo(2));
		Assert.That(report.ExtractedTypeTrees.Single().SerializedFormat, Is.EqualTo(23));
		AssertHealthyOutput(fs);
		using var exported = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(exported.RootElement.GetProperty("Succeeded").GetBoolean(), Is.True);
		Assert.That(exported.RootElement.GetProperty("ExtractedTypeTrees")[0].GetProperty("ExtractedTypeTrees").GetInt32(), Is.EqualTo(2));
		Evidence(fs, fixture.Bytes, primary, "ExternalTrees", new { RawObjectSha256 = Hash(payload), Result = "Passed" }, journal);
	}

	[TestCase(false, "future")]
	[TestCase(true, "future")]
	[TestCase(false, "magic")]
	[TestCase(true, "magic")]
	[TestCase(false, "version")]
	[TestCase(true, "version")]
	[TestCase(false, "size")]
	[TestCase(true, "size")]
	public void ActualReadFailuresReachBothReportsAndHealthyAssetsStillExport(bool primary, string kind)
	{
		var fixture = SerializedFile23Fixture.Create(EndianType.LittleEndian, version: kind == "future" ? 24 : 23);
		if (kind != "future")
		{
			int offset = kind == "magic" ? fixture.MagicOffset : kind == "version" ? fixture.TreeVersionOffset : fixture.SizeOffset;
			SerializedFile23Fixture.WriteInt(fixture.Bytes, offset, kind == "version" ? 22 : -1, EndianType.LittleEndian);
		}
		VirtualFileSystem fs = new(); WriteInput(fs, fixture.Bytes);
		using BaseManager manager = new(_ => { });
		GameBundle bundle = GameBundle.FromPaths(["/Input/test.assets"], new GameAssetFactory(manager), fs);
		FailedFile failure = bundle.FetchFailedFiles().Single();
		Assert.That(failure.Stage, Is.EqualTo("ReadFile"));
		Assert.That(failure.StackTrace, Does.Contain(kind == "future" ? "UnsupportedSerializedFileVersionException" : "InvalidDataException"));
		Assert.That(failure.StackTrace, Does.Contain(kind == "future" ? "24" : kind == "size" ? "negative" : kind));
		Assert.That(bundle.FetchAssets(), Is.Empty);
		AddText(bundle.AddNewProcessedCollection("Healthy", Version));
		CollectionExportReport report = Export(bundle, manager, fs, primary);
		Assert.That(report.Completed, Is.True); Assert.That(report.Succeeded, Is.False);
		Assert.That(report.ImportFailures.Single().SourcePath, Is.EqualTo("/Input/test.assets"));
		Assert.That(report.ImportFailures.Single().Diagnostic, Is.EqualTo(failure.StackTrace));
		AssertHealthyOutput(fs);
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(json.RootElement.GetProperty("Succeeded").GetBoolean(), Is.False);
		Assert.That(json.RootElement.GetProperty("ImportFailures").GetArrayLength(), Is.EqualTo(1));
		Evidence(fs, fixture.Bytes, primary, kind, new { Result = "ExpectedFailureReported", failure.Stage });
	}

	[Test]
	[NonParallelizable]
	public void GameStructureWarnsAboutExternalTreeCountWithoutLosingAssets()
	{
		VirtualFileSystem fs = new();
		WriteInput(fs, SerializedFile23Fixture.Create(EndianType.LittleEndian, true, true, objectData: TextPayload()).Bytes);
		CoreConfiguration settings = new(); settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
		CaptureLogger logger = new(); Logger.Add(logger);
		try
		{
			using GameStructure game = GameStructure.Load(["/Input/test.assets"], fs, settings);
			Assert.That(game.IsValid, Is.True);
			Assert.That(game.FileCollection.FetchAssets().OfType<ITextAsset>().Single(t => t.Name.String == "Healthy").Script_C49.String, Is.EqualTo(Text));
			Assert.That(logger.Warnings, Has.Some.Contains("2 of 2 types").And.Some.Contains("SerializedFile 23"));
		}
		finally { Logger.Remove(logger); }
	}

	internal static CollectionExportReport Export(GameBundle bundle, BaseManager manager, FileSystem fs, bool primary)
	{
		FullConfiguration settings = new() { ExportRootPath = "/Output" }; settings.SetProjectSettings(Version);
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(new(bundle, Version, manager, null), settings);
			exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.EqualTo(exporter.LastReport!.Succeeded)); return exporter.LastReport;
		}
		ExportHandler handler = new(settings); handler.Export(new(bundle, Version, manager, null), "/Output", fs);
		Assert.That(handler.LastExportSucceeded, Is.EqualTo(handler.LastCollectionReport!.Succeeded)); return handler.LastCollectionReport;
	}
	private static void AssertHealthyOutput(FileSystem fs)
	{
		string output = fs.Directory.EnumerateFiles("/Output", "*", SearchOption.AllDirectories)
			.Single(p => fs.Path.GetFileName(p) is "Healthy.txt" or "Healthy.bytes");
		Assert.That(fs.File.ReadAllText(output), Is.EqualTo(Text));
	}
	private static byte[] TextPayload()
	{
		GameBundle bundle = new(); var collection = bundle.AddNewProcessedCollection("Source", Version);
		ITextAsset text = AddText(collection); using MemoryStream stream = new();
		using (AssetWriter writer = new(stream, collection)) text.WriteRelease(writer);
		return stream.ToArray();
	}
	private static ITextAsset AddText(ProcessedAssetCollection collection)
	{
		ITextAsset text = collection.CreateAsset(49, TextAsset.Create); text.Name = "Healthy"; text.Script_C49 = Text; return text;
	}
	private static void WriteInput(FileSystem fs, byte[] bytes)
	{
		fs.Directory.Create("/Input");
		using Stream stream = fs.File.Create("/Input/test.assets"); stream.Write(bytes); stream.Position = 0;
	}
	private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
	private static void Evidence(FileSystem fs, byte[] input, bool primary, string kind, object result, string? journal = null)
	{
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "serialized-file23", $"{kind}-{primary}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output, "input.bin"), input);
		File.WriteAllText(Path.Combine(output, "collection-export-report.json"), fs.File.ReadAllText("/Output/collection-export-report.json"));
		if (journal is not null) File.WriteAllText(Path.Combine(output, "objects.jsonl"), journal);
		File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new
		{
			Source = "IndependentSyntheticV23", RealUnity6000_5Validation = "NotRun", InputSha256 = Hash(input), PrimaryContent = primary, result,
		}, new JsonSerializerOptions { WriteIndented = true }));
	}
	private sealed class CaptureLogger : ILogger
	{
		public List<string> Warnings { get; } = [];
		public void Log(LogType type, LogCategory category, string message) { if (type == LogType.Warning) Warnings.Add(message); }
		public void BlankLine(int count) { }
	}
}
