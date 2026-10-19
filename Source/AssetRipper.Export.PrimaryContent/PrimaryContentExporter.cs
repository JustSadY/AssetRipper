using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Scripts;
using AssetRipper.Export.PrimaryContent.Audio;
using AssetRipper.Export.PrimaryContent.DeletedAssets;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Export.PrimaryContent.Scripts;
using AssetRipper.Export.PrimaryContent.Textures;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1032;
using AssetRipper.SourceGenerated.Classes.ClassID_1101;
using AssetRipper.SourceGenerated.Classes.ClassID_1102;
using AssetRipper.SourceGenerated.Classes.ClassID_1107;
using AssetRipper.SourceGenerated.Classes.ClassID_1109;
using AssetRipper.SourceGenerated.Classes.ClassID_111;
using AssetRipper.SourceGenerated.Classes.ClassID_1111;
using AssetRipper.SourceGenerated.Classes.ClassID_1120;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_128;
using AssetRipper.SourceGenerated.Classes.ClassID_150;
using AssetRipper.SourceGenerated.Classes.ClassID_152;
using AssetRipper.SourceGenerated.Classes.ClassID_156;
using AssetRipper.SourceGenerated.Classes.ClassID_189;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_206;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_221;
using AssetRipper.SourceGenerated.Classes.ClassID_238;
using AssetRipper.SourceGenerated.Classes.ClassID_3;
using AssetRipper.SourceGenerated.Classes.ClassID_329;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_72;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Classes.ClassID_93;
using AssetRipper.SourceGenerated.Classes.ClassID_95;

namespace AssetRipper.Export.PrimaryContent;

public sealed class PrimaryContentExporter
{
	public bool? LastExportSucceeded { get; private set; }
	public CollectionExportReport? LastReport { get; private set; }

	private readonly ObjectHandlerStack<IContentExtractor> exporters = new();
	private readonly GameData gameData;

	private PrimaryContentExporter(GameData gameData)
	{
		this.gameData = gameData;
	}

	public void RegisterHandler<T>(IContentExtractor handler, bool allowInheritance = true) where T : IUnityObjectBase
	{
		exporters.OverrideHandler(typeof(T), handler, allowInheritance);
	}

	public void RegisterHandler(Type type, IContentExtractor handler, bool allowInheritance = true)
	{
		exporters.OverrideHandler(type, handler, allowInheritance);
	}

	public static PrimaryContentExporter CreateDefault(GameData gameData, FullConfiguration settings)
	{
		PrimaryContentExporter exporter = new(gameData);
		exporter.RegisterDefaultHandlers(settings);
		return exporter;
	}

	private void RegisterDefaultHandlers(FullConfiguration settings)
	{
		RegisterHandler<IUnityObjectBase>(new JsonContentExtractor());

		RegisterEmptyHandler<IAnimation>();
		RegisterEmptyHandler<IAnimationClip>();
		RegisterEmptyHandler<IAnimator>();
		RegisterEmptyHandler<IAnimatorController>();
		RegisterEmptyHandler<IAnimatorOverrideController>();
		RegisterEmptyHandler<IAnimatorState>();
		RegisterEmptyHandler<IAnimatorStateMachine>();
		RegisterEmptyHandler<IAnimatorStateTransition>();
		RegisterEmptyHandler<IAnimatorTransition>();
		RegisterEmptyHandler<IAnimatorTransitionBase>();
		RegisterEmptyHandler<IAvatar>();
		RegisterEmptyHandler<IBlendTree>();
		RegisterEmptyHandler<IComponent>();
		RegisterEmptyHandler<IComputeShader>();
		RegisterEmptyHandler<ILightingDataAsset>();
		RegisterEmptyHandler<IMaterial>();
		RegisterEmptyHandler<IPreloadData>();
		RegisterEmptyHandler<IRuntimeAnimatorController>();
		RegisterEmptyHandler<ISceneAsset>();
		RegisterEmptyHandler<SpriteInformationObject>();

		GlbModelExporter modelExporter = new();
		RegisterHandler<GameObjectHierarchyObject>(modelExporter);
		RegisterHandler<IGameObject>(modelExporter);
		RegisterHandler<IComponent>(modelExporter);
		RegisterHandler<ILevelGameManager>(modelExporter);

		RegisterHandler<IMesh>(new GlbMeshExporter());

		RegisterHandler<INavMeshData>(new GlbNavMeshExporter());
		RegisterHandler<ITerrainData>(new GlbTerrainExporter());

		RegisterHandler<ITextAsset>(BinaryAssetContentExtractor.Instance);
		RegisterHandler<IFont>(BinaryAssetContentExtractor.Instance);
		RegisterHandler<IMovieTexture>(BinaryAssetContentExtractor.Instance);
		RegisterHandler<IVideoClip>(BinaryAssetContentExtractor.Instance);

		RegisterHandler<IAudioClip>(new AudioContentExtractor());

		RegisterHandler<IImageTexture>(new TextureExporter(settings.ExportSettings.ImageExportFormat, settings.ExportSettings.PreferOriginalTextureExtension));

		RegisterHandler<IMonoScript>(new ScriptContentExtractor(gameData.AssemblyManager,
			ScriptLanguageSelection.ForUnity(settings.ExportSettings.ScriptLanguageVersion, gameData.ProjectVersion)));

		// Deleted assets
		// This must be the last handler
		RegisterHandler<IUnityObjectBase>(DeletedAssetsExporter.Instance);
	}

	public void Export(GameBundle fileCollection, FullConfiguration settings, FileSystem fileSystem)
	{
		LastExportSucceeded = false;
		CollectionExportReport report = LastReport = CollectionExportReport.Create("PrimaryContent", fileCollection);
		gameData.RawRecovery?.WriteReference(settings.ExportRootPath, fileSystem);
		List<PreparedCollection> collections = CreateCollections(fileCollection, report);
		report.Write(settings.ExportRootPath, fileSystem);

		for (int i = 0; i < collections.Count; i++)
		{
			PreparedCollection prepared = collections[i];
			ExportCollectionBase collection = prepared.Collection;
			if (prepared.Exportable)
			{
				report.AttemptedCollectionCount++;
				int failuresBefore = report.Failures.Count;
				Logger.Info(LogCategory.ExportProgress, $"({i + 1}/{collections.Count}) Exporting '{prepared.Name}'");
				try
				{
					if (collection.Export(settings.ExportRootPath, fileSystem))
					{
						report.ExportedCollectionCount++;
					}
					else
					{
						report.AddFailure("Export", prepared.Name, prepared.Seed);
						Logger.Warning(LogCategory.ExportProgress, $"Failed to export '{prepared.Name}'");
					}
				}
				catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception))
				{
					report.AddFailure("Export", prepared.Name, prepared.Seed, exception);
					Logger.Error(LogCategory.Export, $"Failed to export '{prepared.Name}'. Continuing with independent collections.", exception);
				}
				if (report.Failures.Count != failuresBefore)
				{
					report.Write(settings.ExportRootPath, fileSystem);
				}
			}
		}
		bool incompleteRecovery = gameData.RawRecovery?.GetSummary().HasIncompleteObjects == true;
		report.Completed = true;
		report.Write(settings.ExportRootPath, fileSystem);
		if (!collections.Any(c => c.Collection is Scripts.ScriptExportCollection))
		{
			AssemblyExportLayout layout = new(gameData.AssemblyManager);
			foreach (AsmResolver.DotNet.AssemblyDefinition assembly in gameData.AssemblyManager.GetAssemblies())
			{
				string directory = fileSystem.Path.Join(settings.ExportRootPath, "Assemblies");
				fileSystem.Directory.Create(directory);
				gameData.AssemblyManager.SaveAssembly(assembly, fileSystem.Path.Join(directory, layout.GetStem(assembly) + ".dll"), fileSystem);
			}
		}
		AssemblyResolutionReport assemblyReport = AssemblyResolutionReport.Create(gameData.AssemblyManager, "Assemblies");
		assemblyReport.Write(settings.ExportRootPath, fileSystem);
		if (!assemblyReport.Succeeded) Logger.Warning(LogCategory.Export, "Assembly identity conflicts prevented a complete export. See assembly-resolution-report.json.");
		bool succeeded = report.Succeeded && !incompleteRecovery && assemblyReport.Succeeded;
		gameData.RawRecovery?.WriteSummary(succeeded ? "PrimaryContentExported" : "PrimaryContentExportedWithErrors");
		gameData.RawRecovery?.WriteReference(settings.ExportRootPath, fileSystem);
		if (incompleteRecovery)
		{
			Logger.Warning(LogCategory.Export, "Some preserved objects could not be fully parsed. See raw-recovery.json and the recovery journal.");
		}
		LastExportSucceeded = succeeded;
	}

	private List<PreparedCollection> CreateCollections(GameBundle fileCollection, CollectionExportReport report)
	{
		List<PreparedCollection> collections = new();
		HashSet<IUnityObjectBase> queued = new();

		foreach (IUnityObjectBase asset in fileCollection.FetchAssets())
		{
			if (queued.Contains(asset))
			{
				// Skip duplicates
				continue;
			}

			try
			{
				ExportCollectionBase collection = CreateCollection(asset);
				IUnityObjectBase[] members = collection.Assets.ToArray();
				if (members.Distinct().Count() != members.Length || members.Any(queued.Contains))
				{
					throw new InvalidOperationException("The export collection contains duplicate or already queued assets.");
				}
				PreparedCollection prepared = new(collection, asset, collection.Name, collection.Exportable);
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

		return collections;
	}

	private sealed record PreparedCollection(ExportCollectionBase Collection, IUnityObjectBase Seed, string Name, bool Exportable);

	private ExportCollectionBase CreateCollection(IUnityObjectBase asset)
	{
		foreach (IContentExtractor exporter in exporters.GetHandlerStack(asset.GetType()))
		{
			if (exporter.TryCreateCollection(asset, out ExportCollectionBase? collection))
			{
				return collection;
			}
		}
		throw new Exception($"There is no exporter that can handle '{asset}'");
	}

	private void RegisterEmptyHandler<T>() where T : IUnityObjectBase
	{
		RegisterHandler<T>(EmptyContentExtractor.Instance);
	}
}
