using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_196;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;
using System.Buffers.Binary;

namespace AssetRipper.Tests;

[Category("ExportSelection")]
public class ExportSelectionTests
{
	private static readonly UnityVersion Version = new(2022, 3, 0);

	[Test]
	public void AtomicMembersAddTheirDependenciesAndCyclesTerminateAcrossDuplicatePathIds()
	{
		GameBundle bundle = new();
		var first = bundle.AddNewProcessedCollection("first", Version);
		var second = bundle.AddNewProcessedCollection("second", Version);
		GraphAsset root = Add(first, "Root"), sibling = Add(first, "Sibling"), excluded = Add(first, "Excluded"), external = Add(second, "External");
		Assert.That(root.PathID, Is.EqualTo(external.PathID));
		first.AddDependency(second); second.AddDependency(first);
		sibling.References.Add(("external", new(1, external.PathID)));
		external.References.Add(("cycle", new(1, root.PathID)));
		ExportSelectionUnit[] units = [Unit(root, [root, sibling]), Unit(excluded), Unit(external)];
		var plan = ExportSelectionPlan.Create(bundle.FetchAssets().ToArray(), units, a => a == root);
		Assert.That(plan.IncludedAssets, Is.EquivalentTo(new[] { root, sibling, external }));
		Assert.That(plan.CollectionIndices, Is.EqualTo(new[] { 0, 2 }));
		Assert.That(plan.Report.Succeeded, Is.True);
		Assert.That(plan.Report.DependencyCoverage, Is.EqualTo("KnownObjectReferencesOnly"));
		Assert.That(plan.Report.IncludedAssets.Select(a => a.Asset.JobOrdinal).Distinct().Count(), Is.EqualTo(3));
		Assert.That(plan.Report.IncludedAssets.Single(a => a.Asset.Name == "Sibling").Reason, Is.EqualTo("AtomicCollection"));
	}

	[Test]
	public void KnownEdgesSurviveAnIteratorFailureAndUnresolvedReferenceIsExplicit()
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		GraphAsset root = Add(assets, "Root"), target = Add(assets, "Target");
		root.References.Add(("known", new(target.PathID))); root.References.Add(("missing", new(8, 998)));
		root.Failure = new InvalidOperationException("Injected dependency iterator failure");
		var plan = ExportSelectionPlan.Create([root, target], [Unit(root), Unit(target)], a => a == root);
		Assert.That(plan.IncludedAssets, Is.EquivalentTo(new[] { root, target }));
		Assert.That(plan.Report.DependencyCoverage, Is.EqualTo("Incomplete"));
		Assert.That(plan.Report.Issues.Select(i => i.Kind), Is.EquivalentTo(new[] { "UnresolvedReference", "DependencyReadFailure" }));
		var missing = plan.Report.Issues.Single(i => i.Kind == "UnresolvedReference");
		Assert.That((missing.Field, missing.FileId, missing.PathId), Is.EqualTo(("missing", 8, 998L)));
	}

	[TestCase("io")]
	[TestCase("cancel")]
	[TestCase("oom")]
	[TestCase("wrapped")]
	public void FatalDependencyErrorsEscape(string kind)
	{
		GameBundle bundle = new(); GraphAsset root = Add(bundle.AddNewProcessedCollection("Fixture", Version), "Root");
		root.Failure = Fatal(kind);
		Assert.That(Assert.Catch(() => ExportSelectionPlan.Create([root], [Unit(root)], _ => true)), Is.SameAs(root.Failure));
	}

	[TestCase("none")]
	[TestCase("unsupported")]
	[TestCase("owner")]
	public void EmptyOrUnownedSelectionCannotSucceed(string kind)
	{
		GameBundle bundle = new(); GraphAsset root = Add(bundle.AddNewProcessedCollection("Fixture", Version), "Root");
		ExportSelectionUnit[] units = kind == "owner" ? [] : [Unit(root) with { Exportable = kind != "unsupported" }];
		var plan = ExportSelectionPlan.Create([root], units, _ => kind != "none");
		Assert.That(plan.Report.Succeeded, Is.False);
		Assert.That(plan.Report.Issues.Any(i => i.Kind == (kind == "none" ? "NoMatches" : kind == "owner" ? "MissingExportOwner" : "NoExportableCollections")), Is.True);
	}

	[Test]
	public void ReusedOwnerIsAtomicButUnsupportedSingletonDoesNotPullOtherSeeds()
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		GraphAsset a = Add(assets, "A"), b = Add(assets, "B"), c = Add(assets, "C"), d = Add(assets, "D");
		object shared = new(), empty = new();
		ExportSelectionUnit[] units = [new(shared, a, [], "Scripts", true), new(shared, b, [], "Scripts", true),
			new(empty, c, [], "Empty", false), new(empty, d, [], "Empty", false)];
		var plan = ExportSelectionPlan.Create([a, b, c, d], units, asset => asset == b || asset == c);
		Assert.That(plan.CollectionIndices, Is.EqualTo(new[] { 0, 2 }));
		Assert.That(plan.IncludedAssets, Is.EquivalentTo(new[] { a, b, c }));
		Assert.That(plan.Report.Issues.Single().Kind, Is.EqualTo("NonExportableRoot"), "A second exportable root cannot hide a requested asset that has no standalone output.");
	}

	[Test]
	public void FiltersUseOrdinalExactAlternativesAndSnapshotTheirValues()
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		GraphAsset root = Add(assets, "Root"), other = Add(assets, "Other");
		Assert.That(new ExportSelectionSettings().CreatePredicate(), Is.Null);
		ExportSelectionSettings filters = new() { ClassIds = [49, 999], Names = ["Root", "Also"], PathIds = [root.PathID], Collections = ["Fixture"] };
		var predicate = filters.CreatePredicate()!;
		filters.Names[0] = "Mutated";
		Assert.That(predicate(root), Is.True); Assert.That(predicate(other), Is.False);
		root.OverrideName = "root"; Assert.That(predicate(root), Is.False);
	}

	[Test]
	public void RawObjectsCannotClaimDependencyCoverage()
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		var raw = assets.CreateAsset(999, info => new UnknownObject(info, [1, 2, 3]));
		var plan = ExportSelectionPlan.Create([raw], [Unit(raw)], _ => true);
		Assert.That(plan.Report.Issues.Single().Kind, Is.EqualTo("UnknownObjectDependencies"));
	}

	[TestCase("4.7.0f1", false)]
	[TestCase("4.7.0f1", true)]
	[TestCase("6000.4.0f1", false)]
	[TestCase("6000.4.0f1", true)]
	public void NavMeshSettingsReferencesAreFiniteForBothFieldLayouts(string version, bool hasTarget)
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", UnityVersion.Parse(version));
		var settings = assets.CreateAsset(196, NavMeshSettings.Create); var target = Add(assets, "Target");
		byte[] pointerBytes = new byte[12];
		BinaryPrimitives.WriteInt64LittleEndian(pointerBytes.AsSpan(4), hasTarget ? target.PathID : 0);
		EndianSpanReader reader = new(pointerBytes, EndianType.LittleEndian);
		if (settings.Has_NavMesh()) settings.NavMesh.ReadRelease(ref reader);
		else settings.NavMeshData!.ReadRelease(ref reader);
		var plan = ExportSelectionPlan.Create([settings, target], [Unit(settings), Unit(target)], a => a == settings);
		Assert.That(plan.Report.Succeeded, Is.True);
		Assert.That(plan.IncludedAssets.Count, Is.EqualTo(hasTarget ? 2 : 1));
		Assert.That(plan.CollectionIndices.Count, Is.EqualTo(hasTarget ? 2 : 1));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void UnreadableScriptSelectionKeepsRawPayloadAndMarksIncomplete(bool primary)
	{
		using MonoBehaviourPayloadFixture fixture = new();
		var broken = fixture.Read(fixture.Fields()[..^1], true);
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		settings.ExportSettings.Selection = new() { ClassIds = [114] };
		VirtualFileSystem fs = new(); GraphExporter fake = new();
		CollectionExportReport report;
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(new(fixture.Bundle, Version, fixture.Manager, null), settings);
			exporter.RegisterHandler<IUnityObjectBase>(fake); exporter.Export(fixture.Bundle, settings, fs); report = exporter.LastReport!;
		}
		else
		{
			var exporter = new ProjectExporter(settings, fixture.Manager); exporter.OverrideExporter<IUnityObjectBase>(fake);
			exporter.Export(fixture.Bundle, settings, fs); report = exporter.LastReport!;
		}
		Assert.That(report.Succeeded, Is.False);
		Assert.That(report.Selection!.Issues.Any(i => i.Kind == "UnknownScriptDependencies"), Is.True);
		var failure = report.MonoBehaviourFailures.Single();
		Assert.That(fs.File.ReadAllBytes("/Output/" + failure.PayloadPath), Has.Length.EqualTo(failure.ByteLength));
		Assert.That(failure.PathId, Is.EqualTo(broken.PathID));
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public void BothExportersOmitUnrelatedAssetsAndOnlyRetainSelectedPreparationFailures(bool primary, bool selectBad)
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		GraphAsset bad = Add(assets, "Bad"), root = Add(assets, "Root"), sibling = Add(assets, "Sibling"), dep = Add(assets, "Dependency"), ignored = Add(assets, "Ignored");
		sibling.References.Add(("dependency", new(dep.PathID)));
		GraphExporter fake = new() { PreparationFailure = bad, Group = [root, sibling] };
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		settings.ExportSettings.Selection = new() { Names = [selectBad ? "Bad" : "Root"] };
		using BaseManager manager = new(_ => { }); VirtualFileSystem fs = new(); CollectionExportReport report;
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(new(bundle, Version, manager, null), settings);
			exporter.RegisterHandler<GraphAsset>(fake); exporter.Export(bundle, settings, fs); report = exporter.LastReport!;
			Assert.That(exporter.LastExportSucceeded, Is.EqualTo(!selectBad));
		}
		else
		{
			var exporter = new ProjectExporter(settings, manager); exporter.OverrideExporter<GraphAsset>(fake);
			exporter.Export(bundle, settings, fs); report = exporter.LastReport!;
			Assert.That(exporter.LastExportSucceeded, Is.EqualTo(!selectBad));
		}
		Assert.That(report.Completed, Is.True);
		Assert.That(report.Failures, Has.Count.EqualTo(selectBad ? 1 : 0));
		if (!selectBad)
		{
			Assert.That(fake.Exported, Is.EquivalentTo(new[] { "Root", "Sibling", "Dependency" }));
			Assert.That(report.ExportedCollectionCount, Is.EqualTo(2));
			Assert.That(fs.Directory.EnumerateFiles("/Output", "*.txt", SearchOption.AllDirectories).Any(p => fs.Path.GetFileName(p) == "Ignored.txt"), Is.False);
		}
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/collection-export-report.json"));
		Assert.That(json.RootElement.GetProperty("Selection").GetProperty("Succeeded").GetBoolean(), Is.EqualTo(!selectBad));
	}

	[Test]
	public void PrimaryScriptOwnerContainsALaterSelectedMonoScript()
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		var first = assets.CreateAsset(115, MonoScript.Create); var second = assets.CreateAsset(115, MonoScript.Create);
		using BaseManager manager = new(_ => { });
		var extractor = new AssetRipper.Export.PrimaryContent.Scripts.ScriptContentExtractor(manager);
		extractor.TryCreateCollection(first, out var collection);
		extractor.TryCreateCollection(second, out var secondCollection);
		Assert.That(secondCollection, Is.SameAs(collection));
		Assert.That(collection!.Assets, Is.EquivalentTo(new[] { first, second }));
		var plan = ExportSelectionPlan.Create([first, second], [new(collection, first, collection.Assets.ToArray(), "Scripts", true)], a => a == second);
		Assert.That(plan.CollectionIndices, Is.EqualTo(new[] { 0 }));
		Assert.That(plan.IncludedAssets, Has.Count.EqualTo(2));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void SelectiveHandlerSkipsBulkStreamingAssetsButFullExportStillCopiesIt(bool selective)
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		var text = assets.CreateAsset(49, TextAsset.Create); text.Name = "Selected"; text.Script_C49 = "payload";
		VirtualFileSystem fs = new(); fs.Directory.Create("/Input/StreamingAssets");
		fs.File.WriteAllBytes("/Input/StreamingAssets/unrelated.bin", [1, 2, 3]);
		using BaseManager manager = new(_ => { }); FullConfiguration settings = new();
		settings.ExportSettings.Selection = new() { Names = selective ? ["Selected"] : [] };
		ExportHandler handler = new(settings);
		handler.Export(new(bundle, Version, manager, new FixturePlatform(fs)), "/Output", fs);
		Assert.That(handler.LastExportSucceeded, Is.True);
		Assert.That(fs.File.Exists("/Output/ExportedProject/Assets/StreamingAssets/unrelated.bin"), Is.EqualTo(!selective));
		Assert.That(handler.LastCollectionReport!.Selection is not null, Is.EqualTo(selective));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void CustomPredicateCombinesWithConfiguredFilters(bool primary)
	{
		GameBundle bundle = new(); var assets = bundle.AddNewProcessedCollection("Fixture", Version);
		GraphAsset root = Add(assets, "Root"), other = Add(assets, "Other");
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		settings.ExportSettings.Selection = new() { Names = ["Root", "Other"] };
		using BaseManager manager = new(_ => { }); VirtualFileSystem fs = new(); GraphExporter fake = new();
		if (primary)
		{
			var exporter = PrimaryContentExporter.CreateDefault(new(bundle, Version, manager, null), settings);
			exporter.RegisterHandler<GraphAsset>(fake); exporter.Export(bundle, settings, fs, a => a == other);
			Assert.That(exporter.LastExportSucceeded, Is.True);
		}
		else
		{
			var exporter = new ProjectExporter(settings, manager); exporter.OverrideExporter<GraphAsset>(fake);
			exporter.Export(bundle, settings, fs, a => a == other); Assert.That(exporter.LastExportSucceeded, Is.True);
		}
		Assert.That(fake.Exported, Is.EqualTo(new[] { "Other" }));
	}

	private sealed class FixturePlatform : PlatformGameStructure
	{
		public FixturePlatform(FileSystem fs) : base(fs) => StreamingAssetsPath = "/Input/StreamingAssets";
	}

	private static Exception Fatal(string kind) => kind switch
	{
		"io" => new IOException("disk"), "cancel" => new OperationCanceledException("cancel"),
		"oom" => new OutOfMemoryException("oom"), _ => new AggregateException(new IOException("wrapped disk")),
	};
	private static GraphAsset Add(ProcessedAssetCollection assets, string name)
	{
		var asset = assets.CreateAsset(49, info => new GraphAsset(info)); asset.OverrideName = name; return asset;
	}
	private static ExportSelectionUnit Unit(IUnityObjectBase seed, IUnityObjectBase[]? members = null) => new(new object(), seed, members ?? [seed], seed.GetBestName(), true);
	private sealed class GraphAsset(AssetInfo info) : UnityObjectBase(info)
	{
		public List<(string, PPtr)> References { get; } = [];
		public Exception? Failure { get; set; }
		public override IEnumerable<(string, PPtr)> FetchDependencies()
		{
			foreach (var edge in References) yield return edge;
			if (Failure is not null) throw Failure;
		}
	}
	private sealed class GraphExporter : IAssetExporter, IContentExtractor
	{
		public IUnityObjectBase? PreparationFailure { get; init; }
		public IUnityObjectBase[] Group { get; init; } = [];
		public HashSet<string> Exported { get; } = [];
		private IUnityObjectBase[] Members(IUnityObjectBase asset)
		{
			if (asset == PreparationFailure) throw new InvalidOperationException("Injected preparation failure");
			return Group.Contains(asset) ? Group : [asset];
		}
		public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
		{
			exportCollection = new UnityCollection(this, Members(asset)); return true;
		}
		bool IContentExtractor.TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out ExportCollectionBase? exportCollection)
		{
			exportCollection = new ContentCollection(this, Members(asset)); return true;
		}
		private bool Export(IUnityObjectBase[] members, string directory, FileSystem fs)
		{
			fs.Directory.Create(directory);
			foreach (var asset in members) { Exported.Add(asset.GetBestName()); fs.File.WriteAllText(fs.Path.Join(directory, asset.GetBestName() + ".txt"), "selected"); }
			return true;
		}
		public AssetType ToExportType(IUnityObjectBase asset) => AssetType.Meta;
		public bool ToUnknownExportType(Type type, out AssetType assetType) { assetType = AssetType.Meta; return true; }
		private sealed class UnityCollection(GraphExporter owner, IUnityObjectBase[] members) : IExportCollection
		{
			public bool Export(IExportContainer container, string directory, FileSystem fs) => owner.Export(members, directory, fs);
			public bool Contains(IUnityObjectBase other) => members.Contains(other);
			public long GetExportID(IExportContainer container, IUnityObjectBase asset) => asset.PathID;
			public MetaPtr CreateExportPointer(IExportContainer container, IUnityObjectBase asset, bool isLocal) => MetaPtr.NullPtr;
			public AssetCollection File => members[0].Collection;
			public TransferInstructionFlags Flags => File.Flags;
			public IEnumerable<IUnityObjectBase> Assets => members;
			public string Name => members[0].GetBestName();
		}
		private sealed class ContentCollection(GraphExporter owner, IUnityObjectBase[] members) : ExportCollectionBase
		{
			public override bool Export(string directory, FileSystem fs) => owner.Export(members, directory, fs);
			public override bool Contains(IUnityObjectBase asset) => members.Contains(asset);
			public override IContentExtractor ContentExtractor => owner;
			public override IEnumerable<IUnityObjectBase> Assets => members;
			public override string Name => members[0].GetBestName();
		}
	}
}
