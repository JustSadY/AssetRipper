using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Recovery;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using System.Text.Json;

namespace AssetRipper.Tests;

public class CollectionIsolationTests
{
	private static readonly UnityVersion Version = UnityVersion.Parse("2022.3.0f1");

	[TestCase(false)]
	[TestCase(true)]
	public void FailedImportCollectionIsQuarantinedAndLaterCollectionsSurvive(bool nested)
	{
		VirtualFileSystem fs = new();
		RawObjectStore store = new(fs, "/Recovery");
		FaultingFactory factory = new() { RawObjectSink = store };
		SerializedFile bad = BuildFile("bad.assets", 3);
		SerializedFile good = BuildFile("good.assets", 1, true);
		Bundle result;
		if (nested)
		{
			FileStreamBundleFile parent = new() { Name = "parent" };
			FileStreamBundleFile child = new() { Name = "child" };
			parent.AddSerializedFile(bad);
			child.AddSerializedFile(good);
			parent.AddFileContainer(child);
			result = SerializedBundle.FromFileContainer(parent, factory);
		}
		else
		{
			WriteFile(fs, bad);
			WriteFile(fs, good);
			// Files are consumed as a stack: the damaged one must be attempted first.
			result = GameBundle.FromPaths([good.FilePath, bad.FilePath], factory, fs);
			Assert.That(result.FetchAssetCollections().Single().Dependencies, Has.Count.EqualTo(2));
			Assert.That(result.FetchAssetCollections().Single().Dependencies[1], Is.Null);
		}
		Assert.That(result.FetchAssetCollections().Select(c => c.Name), Is.EqualTo(new[] { "good.assets" }));
		Assert.That(result.FetchAssets().Count(), Is.EqualTo(1));
		Assert.That(result.ResolveCollection("bad.assets"), Is.Null);
		FailedFile failure = result.FetchFailedFiles().Single();
		Assert.That(failure.Stage, Is.EqualTo("CreateAssetCollection"));
		Assert.That(failure.DeclaredObjectCount, Is.EqualTo(3));
		Assert.That(failure.StackTrace, Does.Contain("Injected factory failure"));
		Assert.That(store.GetSummary().PreservedObjectCount, Is.EqualTo(4));
		Assert.That(store.GetSummary().FailedObjectCount, Is.EqualTo(2));
		Assert.That(store.GetSummary().PendingObjectCount, Is.Zero);
		Assert.That(fs.File.ReadAllText(store.JournalPath), Does.Contain("CollectionAborted"));
	}

	[TestCase("io")]
	[TestCase("cancel")]
	[TestCase("oom")]
	[TestCase("wrapped")]
	public void FatalImportFailureEscapesAfterRemovingThePartialCollection(string kind)
	{
		GameBundle bundle = new();
		FaultingFactory factory = new() { Failure = Fatal(kind) };
		Exception? error = Assert.Catch(() => bundle.AddCollectionFromSerializedFile(BuildFile("bad.assets", 3), factory));
		Assert.That(error, Is.SameAs(factory.Failure));
		Assert.That(bundle.Collections, Is.Empty);
		VirtualFileSystem fs = new();
		SerializedFile file = BuildFile("bad.assets", 3);
		WriteFile(fs, file);
		Assert.That(Assert.Catch(() => GameBundle.FromPaths([file.FilePath], factory, fs)), Is.SameAs(factory.Failure));
	}

	[Test]
	public void ValidatedUnityMembershipIsNotEnumeratedAgainByTheReferenceContainer()
	{
		GameBundle bundle = new();
		ITextAsset asset = AddText(bundle.AddNewProcessedCollection("Fixture", Version), "Healthy");
		FaultingExporter fault = new(asset, asset) { Phase = "Once" };
		using BaseManager manager = new(_ => { });
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		ProjectExporter exporter = new(settings, manager);
		exporter.OverrideExporter<ITextAsset>(fault);
		VirtualFileSystem fs = new();
		exporter.Export(bundle, settings, fs);
		Assert.That(exporter.LastExportSucceeded, Is.True);
		Assert.That(fault.ExportedHealthy, Is.True);
	}

	[TestCase(false, "Preparation")]
	[TestCase(true, "Preparation")]
	[TestCase(false, "Membership")]
	[TestCase(true, "Membership")]
	[TestCase(false, "Duplicate")]
	[TestCase(true, "Duplicate")]
	[TestCase(false, "Export")]
	[TestCase(true, "Export")]
	[TestCase(false, "ReturnFalse")]
	[TestCase(true, "ReturnFalse")]
	public void ExportFailuresAreReportedAndIndependentAssetsStillReachDisk(bool primary, string phase)
	{
		GameBundle bundle = new();
		ProcessedAssetCollection assets = bundle.AddNewProcessedCollection("Fixture", Version);
		ITextAsset bad = AddText(assets, "Bad");
		ITextAsset good = AddText(assets, "Healthy");
		FaultingExporter fault = new(bad, good) { Phase = phase };
		VirtualFileSystem fs = new();
		using BaseManager manager = new(_ => { });
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		settings.SetProjectSettings(Version);
		CollectionExportReport report;
		if (primary)
		{
			PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(new(bundle, Version, manager, null), settings);
			exporter.RegisterHandler<ITextAsset>(fault);
			exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False);
			report = exporter.LastReport!;
		}
		else
		{
			ProjectExporter exporter = new(settings, manager);
			exporter.OverrideExporter<ITextAsset>(fault);
			exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False);
			report = exporter.LastReport!;
			if (phase is "Preparation" or "Membership" or "Duplicate")
			{
				Assert.That(fault.ObservedMissingReference, Is.True);
			}
		}
		string output = primary ? "/Output" : "/Output/ExportedProject";
		Assert.That(fs.File.ReadAllText(output + "/Healthy.txt"), Is.EqualTo("Healthy payload"));
		Assert.That(report.Completed, Is.True);
		Assert.That(report.Succeeded, Is.False);
		Assert.That(report.ExportedCollectionCount, Is.EqualTo(1));
		Assert.That(report.Failures, Has.Count.EqualTo(1));
		Assert.That(report.Failures.Single().Phase, Is.EqualTo(phase is "Preparation" or "Membership" or "Duplicate" ? "Preparation" : "Export"));
		Assert.That(report.Failures.Single().PathId, Is.EqualTo(bad.PathID));
		using JsonDocument json = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(json.RootElement.GetProperty("Succeeded").GetBoolean(), Is.False);
	}

	[TestCase(false, "Preparation", "wrapped")]
	[TestCase(true, "Preparation", "cancel")]
	[TestCase(false, "Export", "io")]
	[TestCase(true, "Export", "wrapped")]
	[TestCase(false, "Export", "oom")]
	[TestCase(true, "Export", "access")]
	public void FatalExportFailuresStopTheJob(bool primary, string phase, string kind)
	{
		GameBundle bundle = new();
		ProcessedAssetCollection assets = bundle.AddNewProcessedCollection("Fixture", Version);
		ITextAsset bad = AddText(assets, "Bad");
		ITextAsset good = AddText(assets, "Healthy");
		FaultingExporter fault = new(bad, good) { Phase = phase, Failure = Fatal(kind) };
		VirtualFileSystem fs = new();
		using BaseManager manager = new(_ => { });
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		CollectionExportReport report;
		if (primary)
		{
			PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(new(bundle, Version, manager, null), settings);
			exporter.RegisterHandler<ITextAsset>(fault);
			Assert.That(Assert.Catch(() => exporter.Export(bundle, settings, fs)), Is.SameAs(fault.Failure));
			Assert.That(exporter.LastExportSucceeded, Is.False);
			report = exporter.LastReport!;
		}
		else
		{
			ProjectExporter exporter = new(settings, manager);
			exporter.OverrideExporter<ITextAsset>(fault);
			Assert.That(Assert.Catch(() => exporter.Export(bundle, settings, fs)), Is.SameAs(fault.Failure));
			Assert.That(exporter.LastExportSucceeded, Is.False);
			report = exporter.LastReport!;
		}
		Assert.That(fault.ExportedHealthy, Is.False);
		Assert.That(report.Completed, Is.False);
		Assert.That(report.Succeeded, Is.False);
		Assert.That(report.Failures, Is.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ExistingImportFailuresMakeBothExportResultsUnsuccessfulEvenWithoutRawCapture(bool primary)
	{
		GameBundle bundle = new();
		ProcessedAssetCollection assets = bundle.AddNewProcessedCollection("Fixture", Version);
		AddText(assets, "Healthy");
		bundle.AddFailed(new() { Name = "broken.assets", FilePath = "/Input/broken.assets", StackTrace = "Bad serialized file." });
		using BaseManager manager = new(_ => { });
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		VirtualFileSystem fs = new();
		if (primary)
		{
			PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(new(bundle, Version, manager, null), settings);
			exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False);
		}
		else
		{
			ExportHandler handler = new(settings);
			handler.Export(new(bundle, Version, manager, null), "/Output", fs);
			Assert.That(handler.LastExportSucceeded, Is.False);
			Assert.That(handler.LastCollectionReport!.ImportFailures, Has.Count.EqualTo(1));
		}
		Assert.That(fs.Directory.EnumerateFiles("/Output", "*", SearchOption.AllDirectories).Any(p => fs.Path.GetFileName(p).StartsWith("Healthy", StringComparison.Ordinal)), Is.True);
		using JsonDocument report = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(report.RootElement.GetProperty("ImportFailures").GetArrayLength(), Is.EqualTo(1));
	}

	private static Exception Fatal(string kind) => kind switch
	{
		"io" => new IOException("Injected disk failure."),
		"cancel" => new OperationCanceledException("Injected cancellation."),
		"oom" => new OutOfMemoryException("Injected memory failure."),
		"access" => new UnauthorizedAccessException("Injected access failure."),
		_ => new AggregateException(new InvalidOperationException("Decoder failed."), new Exception("Wrapped fatal error.", new IOException("Injected disk failure."))),
	};

	private static ITextAsset AddText(ProcessedAssetCollection assets, string name)
	{
		ITextAsset text = assets.CreateAsset(49, TextAsset.Create);
		text.Name = name;
		text.Script_C49 = name + " payload";
		return text;
	}

	private static SerializedFile BuildFile(string name, int count, bool dependency = false)
	{
		SerializedType type = new() { TypeID = 49, ScriptTypeIndex = -1 };
		SerializedFileBuilder builder = new() { Generation = FormatVersion.LargeFilesSupport, Version = Version, EndianType = EndianType.LittleEndian, HasTypeTree = false };
		builder.Types.Add(type);
		for (int i = 1; i <= count; i++)
		{
			builder.Objects.Add(new ObjectInfo(type) { FileID = i, SerializedTypeIndex = 0, ObjectData = [(byte)i] });
		}
		if (dependency)
		{
			builder.Dependencies.Add(new() { PathName = "bad.assets", PathNameOrigin = "bad.assets", AssetPath = "" });
		}
		SerializedFile file = builder.Build();
		file.Name = name;
		file.FilePath = "/Input/" + name;
		return file;
	}

	private static void WriteFile(VirtualFileSystem fs, SerializedFile file)
	{
		fs.Directory.Create("/Input");
		using Stream stream = fs.File.Create(file.FilePath);
		file.Write(stream);
		stream.Position = 0;
	}

	private sealed class FaultingFactory : AssetFactoryBase
	{
		public Exception Failure { get; init; } = new InvalidOperationException("Injected factory failure.");
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> refs)
		{
			if (info.Collection.Name == "bad.assets" && info.PathID == 2)
			{
				throw Failure;
			}
			return new UnknownObject(info, data.ToArray());
		}
	}

	private sealed class FaultingExporter(IUnityObjectBase bad, IUnityObjectBase good) : IAssetExporter, IContentExtractor
	{
		private readonly HashSet<IUnityObjectBase> enumerated = [];
		public string Phase { get; init; } = "Export";
		public Exception Failure { get; init; } = new InvalidOperationException("Injected collection failure.");
		public bool ExportedHealthy { get; private set; }
		public bool ObservedMissingReference { get; private set; }

		private void Prepare(IUnityObjectBase asset)
		{
			if (asset == bad && Phase == "Preparation") throw Failure;
		}

		public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
		{
			Prepare(asset);
			exportCollection = new UnityCollection(this, asset);
			return true;
		}

		bool IContentExtractor.TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out ExportCollectionBase? exportCollection)
		{
			Prepare(asset);
			exportCollection = new ContentCollection(this, asset);
			return true;
		}

		private IEnumerable<IUnityObjectBase> Members(IUnityObjectBase asset)
		{
			if (Phase == "Once" && !enumerated.Add(asset)) throw Failure;
			if (asset == bad && Phase == "Duplicate") yield return asset;
			if (asset == bad && Phase == "Membership")
			{
				yield return good;
				throw Failure;
			}
			yield return asset;
		}

		private bool Export(IUnityObjectBase asset, string directory, FileSystem fs, IExportContainer? container = null)
		{
			if (asset == bad)
			{
				if (Phase == "Export") throw Failure;
				if (Phase == "ReturnFalse") return false;
			}
			fs.Directory.Create(directory);
			fs.File.WriteAllText(fs.Path.Join(directory, "Healthy.txt"), "Healthy payload");
			ExportedHealthy = true;
			if (container is not null && Phase is "Preparation" or "Membership" or "Duplicate")
			{
				ObservedMissingReference = container.CreateExportPointer(bad).GUID == UnityGuid.MissingReference;
			}
			return true;
		}

		public AssetType ToExportType(IUnityObjectBase asset) => AssetType.Meta;
		public bool ToUnknownExportType(Type type, out AssetType assetType) { assetType = AssetType.Meta; return true; }

		private sealed class UnityCollection(FaultingExporter owner, IUnityObjectBase asset) : IExportCollection
		{
			public bool Export(IExportContainer container, string directory, FileSystem fs) => owner.Export(asset, directory, fs, container);
			public bool Contains(IUnityObjectBase other) => other == asset;
			public long GetExportID(IExportContainer container, IUnityObjectBase other) => ExportIdHandler.GetMainExportID(other);
			public MetaPtr CreateExportPointer(IExportContainer container, IUnityObjectBase other, bool isLocal) => MetaPtr.NullPtr;
			public AssetCollection File => asset.Collection;
			public TransferInstructionFlags Flags => File.Flags;
			public IEnumerable<IUnityObjectBase> Assets => owner.Members(asset);
			public string Name => asset.GetBestName();
		}

		private sealed class ContentCollection(FaultingExporter owner, IUnityObjectBase asset) : ExportCollectionBase
		{
			public override bool Export(string directory, FileSystem fs) => owner.Export(asset, directory, fs);
			public override bool Contains(IUnityObjectBase other) => other == asset;
			public override IContentExtractor ContentExtractor => owner;
			public override IEnumerable<IUnityObjectBase> Assets => owner.Members(asset);
			public override string Name => asset.GetBestName();
		}
	}
}
