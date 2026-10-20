using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.IO.Files;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Export.Collections;

public sealed class CollectionExportReport
{
	public const string FileName = "collection-export-report.json";
	public int SchemaVersion => 1;
	public required string ExportKind { get; init; }
	public bool Completed { get; set; }
	public int AttemptedCollectionCount { get; set; }
	public int ExportedCollectionCount { get; set; }
	public List<ImportFailure> ImportFailures { get; } = [];
	public List<CollectionExportFailure> Failures { get; } = [];
	public List<MonoBehaviourStructureFailure> MonoBehaviourFailures { get; } = [];
	/// <summary>Prefab object provenance, independent of export-loop success or authoring validation.</summary>
	public List<PrefabReconstructionInfo> PrefabReconstructions { get; } = [];
	/// <summary>
	/// Collections whose source file stores some type trees outside itself (SerializedFile format 23+). Their objects
	/// are read with schemas that do not need the file's type tree; this is reported, not counted as a failure.
	/// </summary>
	public List<ExtractedTypeTreeCollection> ExtractedTypeTrees { get; } = [];
	public ExportSelectionReport? Selection { get; set; }
	public bool Succeeded => Completed && ImportFailures.Count == 0 && Failures.Count == 0 && MonoBehaviourFailures.Count == 0
		&& Selection?.Succeeded != false;

	/// <summary>Resolve deferred reads before export; failed script fields cannot imply complete dependencies.</summary>
	public void CaptureMonoBehaviourFailures(GameBundle bundle, string outputDirectory, FileSystem fileSystem)
		=> CaptureMonoBehaviourFailures(bundle.FetchAssets(), outputDirectory, fileSystem);

	public void CaptureMonoBehaviourFailures(IEnumerable<IUnityObjectBase> assets, string outputDirectory, FileSystem fileSystem)
	{
		foreach (IMonoBehaviour asset in assets.OfType<IMonoBehaviour>())
		{
			asset.LoadStructure();
			if (asset.Structure is not UnreadableStructure failed) continue;
			string hash = Convert.ToHexStringLower(SHA256.HashData(failed.Data));
			string relative = Path.Join("MonoBehaviourRecovery", hash + ".bin");
			string directory = fileSystem.Path.Join(outputDirectory, "MonoBehaviourRecovery");
			if (fileSystem.File.Exists(directory)) throw new IOException($"Recovery directory conflicts with an existing file: {directory}");
			fileSystem.Directory.Create(directory);
			string path = fileSystem.Path.Join(outputDirectory, relative);
			if (fileSystem.Directory.Exists(path)) throw new IOException($"Recovery payload conflicts with an existing directory: {path}");
			if (fileSystem.File.Exists(path))
			{
				if (!fileSystem.File.ReadAllBytes(path).AsSpan().SequenceEqual(failed.Data))
					throw new InvalidDataException($"Existing MonoBehaviour recovery payload has different bytes: {path}");
			}
			else
			{
				fileSystem.File.WriteAllBytes(path, failed.Data.ToArray());
			}
			MonoBehaviourFailures.Add(new(asset.Collection.Name, asset.Collection.FilePath, asset.PathID,
				failed.SchemaSource, failed.PayloadScope, failed.ObjectByteOffset, failed.ReadResult.BytesRead, failed.Data.Count,
				relative, hash, failed.DependencyCoverage, failed.ReadResult.Diagnostic ?? "MonoBehaviour structure could not be read."));
		}
	}

	public static CollectionExportReport Create(string exportKind, GameBundle bundle)
	{
		CollectionExportReport report = new() { ExportKind = exportKind };
		foreach (FailedFile file in bundle.FetchFailedFiles())
		{
			report.ImportFailures.Add(new(file.Name, file.FilePath, file.Stage, file.DeclaredObjectCount, file.StackTrace));
		}
		foreach (SerializedAssetCollection collection in bundle.FetchAssetCollections().OfType<SerializedAssetCollection>().Where(c => c.ExtractedTypeTreeCount > 0))
		{
			report.ExtractedTypeTrees.Add(new(collection.Name, collection.FilePath ?? "", (int)collection.SerializedFormat,
				collection.SerializedTypeCount, collection.ExtractedTypeTreeCount));
		}
		foreach (PrefabHierarchyObject hierarchy in bundle.FetchAssets().OfType<PrefabHierarchyObject>())
		{
			string origin = hierarchy.PrefabWasGenerated ? "GeneratedFromHierarchy"
				: hierarchy.Prefab.Collection is SerializedAssetCollection ? "SourceSerializedObject" : "Unknown";
			report.PrefabReconstructions.Add(new(hierarchy.Root.Name.String, hierarchy.Root.Collection.Name, hierarchy.Root.PathID,
				hierarchy.Prefab.Collection.Name, hierarchy.Prefab.PathID, origin));
		}
		return report;
	}

	public void AddFailure(string phase, string name, IUnityObjectBase asset, Exception? exception = null)
	{
		Failures.Add(new(phase, name, asset.Collection.Name, asset.Collection.FilePath, asset.PathID, asset.ClassID,
			exception?.GetType().FullName, exception?.ToString() ?? "The collection exporter returned false.") { SeedAsset = asset });
	}

	/// <summary>Preparation is global; only failures within the requested closure affect a selective export.</summary>
	public void ApplySelection(ExportSelectionPlan plan)
	{
		Selection = plan.Report;
		Failures.RemoveAll(f => f.Phase == "Preparation" && f.SeedAsset is not null && !plan.IncludedAssets.Contains(f.SeedAsset));
	}

	public void Write(string outputDirectory, FileSystem fileSystem)
	{
		fileSystem.Directory.Create(outputDirectory);
		using Stream stream = fileSystem.File.Create(fileSystem.Path.Join(outputDirectory, FileName));
		JsonSerializer.Serialize(stream, this, CollectionExportReportJsonContext.Default.CollectionExportReport);
	}
}

public sealed record ImportFailure(string Name, string SourcePath, string Phase, int? DeclaredObjectCount, string Diagnostic);

public sealed record ExtractedTypeTreeCollection(string CollectionName, string SourcePath, int SerializedFormat, int SerializedTypes, int ExtractedTypeTrees);

/// <summary>The asset identifies the preparation seed, not every member of a grouped export.</summary>
public sealed record CollectionExportFailure(string Phase, string CollectionName, string SourceCollection, string SourcePath,
	long PathId, int ClassId, string? ExceptionType, string Diagnostic)
{
	[JsonIgnore]
	internal IUnityObjectBase? SeedAsset { get; init; }
}

/// <summary>Raw payload paths are relative to the export root. Missing fields/references are not inferred.</summary>
public sealed record MonoBehaviourStructureFailure(string SourceCollection, string SourcePath, long PathId,
	string SchemaSource, string PayloadScope, int? ObjectByteOffset, int BytesRead, int ByteLength,
	string PayloadPath, string Sha256, string DependencyCoverage, string Diagnostic);

/// <summary>These identities are job-local. A generated prefab does not prove recovery of original variant/nested/override links.</summary>
public sealed record PrefabReconstructionInfo(string RootName, string RootCollection, long RootPathId,
	string PrefabCollection, long PrefabPathId, string PrefabOrigin)
{
	public string AuthoringRelationsValidation => "NotRun";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CollectionExportReport))]
internal sealed partial class CollectionExportReportJsonContext : JsonSerializerContext;
