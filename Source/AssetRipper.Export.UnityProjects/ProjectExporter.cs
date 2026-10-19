using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Collections;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.SourceGenerated;

namespace AssetRipper.Export.UnityProjects;

public sealed partial class ProjectExporter
{
	public bool? LastExportSucceeded { get; private set; }
	public CollectionExportReport? LastReport { get; private set; }

	public event Action? EventExportPreparationStarted;
	public event Action? EventExportPreparationFinished;
	public event Action? EventExportStarted;
	public event Action<int, int>? EventExportProgressUpdated;
	public event Action? EventExportFinished;

	private readonly ObjectHandlerStack<IAssetExporter> assetExporterStack = new();

	/// <summary>Adds an exporter to the stack of exporters for this asset type.</summary>
	/// <typeparam name="T">The c sharp type of this asset type. Any inherited types also get this exporter.</typeparam>
	/// <param name="exporter">The new exporter. If it doesn't work, the next one in the stack is used.</param>
	/// <param name="allowInheritance">Should types that inherit from this type also use the exporter?</param>
	public void OverrideExporter<T>(IAssetExporter exporter, bool allowInheritance = true)
	{
		assetExporterStack.OverrideHandler(typeof(T), exporter, allowInheritance);
	}

	/// <summary>Adds an exporter to the stack of exporters for this asset type.</summary>
	/// <param name="type">The c sharp type of this asset type. Any inherited types also get this exporter.</param>
	/// <param name="exporter">The new exporter. If it doesn't work, the next one in the stack is used.</param>
	/// <param name="allowInheritance">Should types that inherit from this type also use the exporter?</param>
	public void OverrideExporter(Type type, IAssetExporter exporter, bool allowInheritance)
	{
		assetExporterStack.OverrideHandler(type, exporter, allowInheritance);
	}

	/// <summary>
	/// Use the <see cref="DummyExporter"/> for the specified class type.
	/// </summary>
	/// <typeparam name="T">The base type for assets of that <paramref name="classType"/>.</typeparam>
	/// <param name="isEmptyCollection">
	/// True: an exception will be thrown if the asset is referenced by another asset.<br/>
	/// False: any references to this asset will be replaced with a missing reference.
	/// </param>
	/// <param name="isMetaType"><see cref="AssetType.Meta"/> or <see cref="AssetType.Serialized"/>?</param>
	private void OverrideDummyExporter<T>(bool isEmptyCollection, bool isMetaType)
	{
		OverrideExporter<T>(DummyAssetExporter.Get(isEmptyCollection, isMetaType), true);
	}

	public AssetType ToExportType(Type type)
	{
		foreach (IAssetExporter exporter in assetExporterStack.GetHandlerStack(type))
		{
			if (exporter.ToUnknownExportType(type, out AssetType assetType))
			{
				return assetType;
			}
		}
		throw new NotSupportedException($"There is no exporter that know {nameof(AssetType)} for unknown asset '{type}'");
	}

	private IExportCollection CreateCollection(IUnityObjectBase asset)
	{
		foreach (IAssetExporter exporter in assetExporterStack.GetHandlerStack(asset.GetType()))
		{
			if (exporter.TryCreateCollection(asset, out IExportCollection? collection))
			{
				return collection;
			}
		}
		throw new Exception($"There is no exporter that can handle '{asset}'");
	}

	public void Export(GameBundle fileCollection, CoreConfiguration options, FileSystem fileSystem)
	{
		LastExportSucceeded = false;
		CollectionExportReport report = LastReport = CollectionExportReport.Create("UnityProject", fileCollection);
		EventExportPreparationStarted?.Invoke();
		List<PreparedExportCollection> collections = CreateCollections(fileCollection, report);
		report.Write(options.ExportRootPath, fileSystem);
		EventExportPreparationFinished?.Invoke();

		EventExportStarted?.Invoke();
		ProjectAssetContainer container = new(this, options, fileCollection.FetchAssets(),
			collections.Select(c => (c.Collection, (IEnumerable<IUnityObjectBase>)c.Assets)).ToArray());
		int exportableCount = collections.Count(c => c.Exportable);
		int currentExportable = 0;

		for (int i = 0; i < collections.Count; i++)
		{
			PreparedExportCollection prepared = collections[i];
			IExportCollection collection = prepared.Collection;
			container.CurrentCollection = collection;
			if (prepared.Exportable)
			{
				currentExportable++;
				report.AttemptedCollectionCount++;
				int failuresBefore = report.Failures.Count;
				Logger.Info(LogCategory.ExportProgress, $"({currentExportable}/{exportableCount}) Exporting '{prepared.Name}'");
				try
				{
					if (collection.Export(container, options.ProjectRootPath, fileSystem))
					{
						report.ExportedCollectionCount++;
					}
					else
					{
						report.AddFailure("Export", prepared.Name, prepared.Seed);
						Logger.Warning(LogCategory.ExportProgress, $"Failed to export '{prepared.Name}' ({collection.GetType().Name})");
					}
				}
				catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception))
				{
					report.AddFailure("Export", prepared.Name, prepared.Seed, exception);
					Logger.Error(LogCategory.Export, $"Failed to export '{prepared.Name}'. Continuing with independent collections.", exception);
				}
				if (report.Failures.Count != failuresBefore)
				{
					report.Write(options.ExportRootPath, fileSystem);
				}
			}
			EventExportProgressUpdated?.Invoke(i, collections.Count);
		}
		report.Completed = true;
		report.Write(options.ExportRootPath, fileSystem);
		LastExportSucceeded = report.Succeeded;
		EventExportFinished?.Invoke();
	}

	private List<PreparedExportCollection> CreateCollections(GameBundle fileCollection, CollectionExportReport report)
	{
		List<PreparedExportCollection> collections = new();
		HashSet<IUnityObjectBase> queued = new();

		foreach (IUnityObjectBase asset in fileCollection.FetchAssets())
		{
			if (!queued.Contains(asset))
			{
				try
				{
					IExportCollection collection = CreateCollection(asset);
					IUnityObjectBase[] members = collection.Assets.ToArray();
					if (members.Distinct().Count() != members.Length || members.Any(queued.Contains))
					{
						throw new InvalidOperationException("The export collection contains duplicate or already queued assets.");
					}
					PreparedExportCollection prepared = new(collection, asset, members, collection.Name, collection.Exportable);
					collections.Add(prepared);
					queued.UnionWith(members);
				}
				catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception))
				{
					report.AddFailure("Preparation", asset.ClassName, asset, exception);
					Logger.Error(LogCategory.Export, $"Failed to prepare {asset.ClassName} ({asset.PathID}) in '{asset.Collection.Name}'.", exception);
				}
				queued.Add(asset);
			}
		}

		return collections;
	}
}
