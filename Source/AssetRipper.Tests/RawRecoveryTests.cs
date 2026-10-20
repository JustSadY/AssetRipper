using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Recovery;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssetRipper.Tests;

public class RawRecoveryTests
{
	private static readonly UnityVersion Version = UnityVersion.Parse("2022.3.0f1");

	[Test]
	public void NullFactoryKeepsOriginalBytesBeforeFactoryRuns()
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		byte[] data = [1, 2, 3, 4];
		NullFactory factory = new(() => data[0] = 99) { RawObjectSink = store };
		SerializedAssetCollection collection = new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, data), factory);
		Assert.That(collection.Count, Is.Zero);
		Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
		Assert.That(store.GetSummary().PreservedObjectCount, Is.EqualTo(1));
		Assert.That(store.GetSummary().FailedObjectCount, Is.EqualTo(1));
		Assert.That(Events(store, fs).Last().GetProperty("ReadStatus").GetString(), Is.EqualTo("FactoryReturnedNull"));
	}

	[Test]
	public void FactoryExceptionLeavesDurablePayloadAndDiagnostic()
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		NullFactory factory = new(() => throw new InvalidOperationException("Injected factory failure.")) { RawObjectSink = store };
		Assert.Throws<InvalidOperationException>(() => new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, [1, 2, 3]), factory));
		Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(new byte[] { 1, 2, 3 }));
		JsonElement last = Events(store, fs).Last();
		Assert.That(last.GetProperty("ReadStatus").GetString(), Is.EqualTo("FactoryException"));
		Assert.That(last.GetProperty("Diagnostic").GetString(), Does.Contain("Injected factory failure."));
	}

	[Test]
	public void IdenticalPayloadsAreDeduplicatedWithoutMergingObjectIdentities()
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		NullFactory factory = new() { RawObjectSink = store };
		new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, [1, 2, 3]), factory);
		new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, [1, 2, 3]), factory);
		Assert.That(fs.Directory.EnumerateFiles(fs.Path.Join(store.DirectoryPath, "Objects"), "*", SearchOption.TopDirectoryOnly).Count(), Is.EqualTo(1));
		JsonElement[] discovered = Events(store, fs).Where(e => e.GetProperty("Event").GetString() == "ObjectDiscovered").ToArray();
		Assert.That(discovered.Select(e => e.GetProperty("ObjectId").GetString()).Distinct().Count(), Is.EqualTo(2));
		Assert.That(discovered.All(e => e.GetProperty("Object").GetProperty("PathId").GetInt64() == 1));
		Assert.That(store.GetSummary().PreservedObjectCount, Is.EqualTo(2));
	}

	[Test]
	public void ExistingCorruptedPayloadIsRejected()
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		NullFactory factory = new() { RawObjectSink = store };
		new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, [1, 2, 3]), factory);
		fs.File.WriteAllBytes(OnlyPayload(store, fs), [9, 9, 9]);
		Assert.Throws<InvalidDataException>(() => new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, [1, 2, 3]), factory));
		Assert.That(factory.ReadCount, Is.EqualTo(1));
		Assert.That(store.GetSummary().DiscoveredObjectCount, Is.EqualTo(2));
		Assert.That(store.GetSummary().PreservedObjectCount, Is.EqualTo(1));
	}

	[TestCase(999999, typeof(UnknownObject), "UnknownType")]
	[TestCase(49, typeof(UnreadableObject), "Unreadable")]
	public void UnknownAndUnreadableObjectsHaveDistinctRecoveryStatuses(int classId, Type expectedType, string expectedStatus)
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		using BaseManager manager = new(_ => { });
		GameAssetFactory factory = new(manager) { RawObjectSink = store };
		SerializedAssetCollection collection = new GameBundle().AddCollectionFromSerializedFile(BuildFile(classId, [255]), factory);
		Assert.That(collection.Single(), Is.TypeOf(expectedType));
		Assert.That(Events(store, fs).Last().GetProperty("ReadStatus").GetString(), Is.EqualTo(expectedStatus));
		Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(new byte[] { 255 }));
	}

	[Test]
	public void TruncatedMonoBehaviourKeepsItsWholePayload()
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		using BaseManager manager = new(_ => { });
		GameAssetFactory factory = new(manager) { RawObjectSink = store };
		new GameBundle().AddCollectionFromSerializedFile(BuildFile(114, [123]), factory);
		Assert.That(Events(store, fs).Last().GetProperty("ReadStatus").GetString(), Is.EqualTo("Partial"));
		Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(new byte[] { 123 }));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void TypeTreeMonoBehaviourRecordsParsedOrPartialWithoutLosingBytes(bool truncated)
	{
		ProcessedAssetCollection source = new GameBundle().AddNewProcessedCollection("Fixture", Version);
		IMonoBehaviour original = source.CreateAsset(114, info => MonoBehaviour.Create(info));
		byte[] payload = Serialize(original).Concat(new byte[] { 42, 0, 0, 0 }).ToArray();
		SerializedType type = new() { TypeID = 114, ScriptTypeIndex = -1 };
		type.OldType.Nodes.Add(new TypeTreeNode("MonoBehaviour", "Base", 0, true));
		type.OldType.Nodes.Add(new TypeTreeNode("string", "m_Name", 1, true));
		type.OldType.Nodes.Add(new TypeTreeNode("int", "value", 1, false));
		if (truncated)
		{
			type.OldType.Nodes.Add(new TypeTreeNode("int", "missingValue", 1, false));
		}
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		using BaseManager manager = new(_ => { });
		GameAssetFactory factory = new(manager) { RawObjectSink = store };
		SerializedAssetCollection collection = new GameBundle().AddCollectionFromSerializedFile(BuildFile(114, payload, type), factory);
		IMonoBehaviour parsed = (IMonoBehaviour)collection.Single();
		Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(payload));
		Assert.That(Events(store, fs).Last().GetProperty("ReadStatus").GetString(), Is.EqualTo(truncated ? "Partial" : "Parsed"));
		Assert.That(store.GetSummary().PendingObjectCount, Is.Zero);
		Assert.That(store.GetSummary().FailedObjectCount, Is.EqualTo(truncated ? 1 : 0));
		if (truncated)
		{
			Assert.That(parsed.Structure, Is.TypeOf<UnreadableStructure>());
		}
		else
		{
			Assert.That(((SerializableStructure)parsed.Structure!)["value"].AsInt32, Is.EqualTo(42));
		}
	}

	[Test]
	public void DeferredMonoBehaviourFailureUpdatesTheOriginalRecoveryRecord()
	{
		ProcessedAssetCollection source = new GameBundle().AddNewProcessedCollection("Fixture", Version);
		IMonoBehaviour original = source.CreateAsset(114, info => MonoBehaviour.Create(info));
		byte[] payload = Serialize(original).Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		using BaseManager manager = new(_ => { });
		GameAssetFactory factory = new(manager) { RawObjectSink = store };
		SerializedAssetCollection collection = new GameBundle().AddCollectionFromSerializedFile(BuildFile(114, payload), factory);
		IMonoBehaviour parsed = (IMonoBehaviour)collection.Single();
		Assert.That(parsed.Structure, Is.TypeOf<UnloadedStructure>());
		Assert.That(store.GetSummary().PendingObjectCount, Is.EqualTo(1));
		Assert.That(((UnloadedStructure)parsed.Structure!).LoadStructure(), Is.Null);
		Assert.That(parsed.Structure, Is.TypeOf<UnreadableStructure>());
		Assert.That(store.GetSummary().PendingObjectCount, Is.Zero);
		Assert.That(store.GetSummary().FailedObjectCount, Is.EqualTo(1));
		Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(payload));
		JsonElement[] reads = Events(store, fs).Where(e => e.GetProperty("Event").GetString() == "ObjectRead").ToArray();
		Assert.That(reads.Select(e => e.GetProperty("ReadStatus").GetString()), Is.EqualTo(new[] { "Deferred", "Partial" }));
		Assert.That(reads.Select(e => e.GetProperty("ObjectId").GetString()).Distinct().Count(), Is.EqualTo(1));
	}

	[Test]
	public void RecoveryWriteFailureStopsBeforeFactoryRuns()
	{
		NullFactory factory = new() { RawObjectSink = new FailingSink() };
		Assert.Throws<IOException>(() => new GameBundle().AddCollectionFromSerializedFile(BuildFile(49, [1]), factory));
		Assert.That(factory.ReadCount, Is.Zero);
	}

	[Test]
	public void MonoBehaviourDoesNotSwallowRecoveryStatusWriteFailure()
	{
		ProcessedAssetCollection assets = new GameBundle().AddNewProcessedCollection("Fixture", Version);
		using BaseManager manager = new(_ => { });
		GameAssetFactory factory = new(manager);
		Assert.Throws<IOException>(() => factory.ReadAsset(new AssetInfo(assets, 1, 114), new byte[] { 1 }, null, [], new FailingSnapshot()));
	}

	[Test]
	public void NormalModeDoesNotCreateRecoveryStore()
	{
		VirtualFileSystem fs = new();
		WriteInput(fs, BuildTextFile());
		GameData data = new ExportHandler(new()).Load(["/Input/sample.assets"], fs);
		using (data.AssemblyManager)
		{
			Assert.That(data.RawRecovery, Is.Null);
			Assert.That(fs.Directory.Exists("/Recovery"), Is.False);
		}
	}

	[Test]
	public void EndToEndRecoveryRecordsOnlySourceObjectsAndExportsAReference()
	{
		VirtualFileSystem fs = new();
		SerializedFile source = BuildTextFile();
		WriteInput(fs, source);
		FullConfiguration settings = new();
		settings.ImportSettings.RawRecoveryDirectory = "/Recovery";
		ExportHandler handler = new(settings);
		GameData data = handler.Load(["/Input/sample.assets"], fs);
		using (data.AssemblyManager)
		{
			RawObjectStore store = data.RawRecovery!;
			Assert.That(store.GetSummary().PreservedObjectCount, Is.EqualTo(1));
			Assert.That(data.GameBundle.FetchAssets().Count(), Is.GreaterThan(1));
			handler.Process(data);
			handler.Export(data, "/Output", fs);
			Assert.That(handler.LastExportSucceeded, Is.True);
			Assert.That(fs.File.ReadAllBytes(OnlyPayload(store, fs)), Is.EqualTo(source.Objects[0].ObjectData));
			using JsonDocument reference = JsonDocument.Parse(fs.File.ReadAllText("/Output/raw-recovery.json"));
			Assert.That(reference.RootElement.GetProperty("PreservedObjectCount").GetInt64(), Is.EqualTo(1));
			JsonElement obj = Events(store, fs).Single(e => e.GetProperty("Event").GetString() == "ObjectDiscovered");
			long offset = obj.GetProperty("Object").GetProperty("ByteOffset").GetInt64();
			byte[] fileBytes = fs.File.ReadAllBytes("/Input/sample.assets");
			Assert.That(fileBytes.AsSpan((int)offset, source.Objects[0].ObjectData.Length).ToArray(), Is.EqualTo(source.Objects[0].ObjectData));
			Assert.That(obj.GetProperty("Sha256").GetString(), Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(source.Objects[0].ObjectData))));
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public void IncompleteRawObjectParsingPropagatesToBothExportResults(bool primary)
	{
		VirtualFileSystem fs = new();
		WriteInput(fs, BuildFile(999999, [1, 2, 3]));
		FullConfiguration settings = new();
		settings.ImportSettings.RawRecoveryDirectory = "/Recovery";
		ExportHandler handler = new(settings);
		GameData data = handler.Load(["/Input/sample.assets"], fs);
		using (data.AssemblyManager)
		{
			handler.Process(data);
			if (primary)
			{
				settings.ExportRootPath = "/Output";
				PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(data, settings);
				exporter.Export(data.GameBundle, settings, fs);
				Assert.That(exporter.LastExportSucceeded, Is.False);
			}
			else
			{
				handler.Export(data, "/Output", fs);
				Assert.That(handler.LastExportSucceeded, Is.False);
			}
			Assert.That(fs.File.Exists("/Output/raw-recovery.json"));
			Assert.That(data.RawRecovery!.GetSummary().FailedObjectCount, Is.EqualTo(1));
		}
	}

	private static SerializedFile BuildTextFile()
	{
		ProcessedAssetCollection assets = new GameBundle().AddNewProcessedCollection("Fixture", Version);
		ITextAsset asset = assets.CreateAsset(49, info => TextAsset.Create(info));
		asset.Name = "Sample";
		asset.Script_C49 = "Recovery test";
		return BuildFile(49, Serialize(asset));
	}

	private static byte[] Serialize(IUnityObjectBase asset)
	{
		using MemoryStream data = new();
		using (AssetWriter writer = new(data, asset.Collection))
		{
			asset.WriteRelease(writer);
		}
		return data.ToArray();
	}

	private static SerializedFile BuildFile(int classId, byte[] payload, SerializedType? serializedType = null)
	{
		SerializedType type = serializedType ?? new() { TypeID = classId, ScriptTypeIndex = -1 };
		SerializedFileBuilder builder = new()
		{
			Generation = FormatVersion.LargeFilesSupport, Version = Version,
			Platform = BuildTarget.StandaloneWin64Player, EndianType = EndianType.LittleEndian, HasTypeTree = type.OldType.Nodes.Count > 0,
		};
		builder.Types.Add(type);
		builder.Objects.Add(new ObjectInfo(type) { FileID = 1, SerializedTypeIndex = 0, ObjectData = payload });
		SerializedFile file = builder.Build();
		file.Name = "sample.assets";
		file.FilePath = "/Input/sample.assets";
		return file;
	}

	private static void WriteInput(VirtualFileSystem fs, SerializedFile file)
	{
		fs.Directory.Create("/Input");
		using Stream output = fs.File.Create("/Input/sample.assets");
		file.Write(output);
		output.Position = 0;
	}

	private static string OnlyPayload(RawObjectStore store, FileSystem fs) => fs.Directory.EnumerateFiles(fs.Path.Join(store.DirectoryPath, "Objects"), "*", SearchOption.TopDirectoryOnly).Single();

	private static JsonElement[] Events(RawObjectStore store, FileSystem fs) => fs.File.ReadAllText(store.JournalPath)
		.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
		{
			using JsonDocument document = JsonDocument.Parse(line);
			return document.RootElement.Clone();
		}).ToArray();

	private sealed class NullFactory(Action? onRead = null) : AssetFactoryBase
	{
		public int ReadCount { get; private set; }
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> refs)
		{
			ReadCount++;
			onRead?.Invoke();
			return null;
		}
	}

	private sealed class FailingSink : IRawObjectSink, IRawObjectCollection
	{
		public IRawObjectCollection BeginCollection(RawCollectionInfo source) => this;
		public IRawObjectSnapshot Preserve(RawObjectInfo source, ReadOnlySpan<byte> data) => throw new IOException("Injected raw write failure.");
	}

	private sealed class FailingSnapshot : IRawObjectSnapshot
	{
		public void ReportReadStatus(RawObjectReadStatus status, string? diagnostic = null) => throw new IOException("Injected journal write failure.");
	}
}
