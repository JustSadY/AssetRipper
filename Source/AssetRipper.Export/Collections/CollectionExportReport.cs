using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.IO.Files;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
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
	public bool Succeeded => Completed && ImportFailures.Count == 0 && Failures.Count == 0 && MonoBehaviourFailures.Count == 0;

	/// <summary>Resolve deferred reads before export; failed script fields cannot imply complete dependencies.</summary>
	public void CaptureMonoBehaviourFailures(GameBundle bundle, string outputDirectory, FileSystem fileSystem)
	{
		foreach (IMonoBehaviour asset in bundle.FetchAssets().OfType<IMonoBehaviour>())
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
		return report;
	}

	public void AddFailure(string phase, string name, IUnityObjectBase asset, Exception? exception = null)
	{
		Failures.Add(new(phase, name, asset.Collection.Name, asset.Collection.FilePath, asset.PathID, asset.ClassID,
			exception?.GetType().FullName, exception?.ToString() ?? "The collection exporter returned false."));
	}

	public void Write(string outputDirectory, FileSystem fileSystem)
	{
		fileSystem.Directory.Create(outputDirectory);
		using Stream stream = fileSystem.File.Create(fileSystem.Path.Join(outputDirectory, FileName));
		JsonSerializer.Serialize(stream, this, CollectionExportReportJsonContext.Default.CollectionExportReport);
	}
}

public sealed record ImportFailure(string Name, string SourcePath, string Phase, int? DeclaredObjectCount, string Diagnostic);

/// <summary>The asset identifies the preparation seed, not every member of a grouped export.</summary>
public sealed record CollectionExportFailure(string Phase, string CollectionName, string SourceCollection, string SourcePath,
	long PathId, int ClassId, string? ExceptionType, string Diagnostic);

/// <summary>Raw payload paths are relative to the export root. Missing fields/references are not inferred.</summary>
public sealed record MonoBehaviourStructureFailure(string SourceCollection, string SourcePath, long PathId,
	string SchemaSource, string PayloadScope, int? ObjectByteOffset, int BytesRead, int ByteLength,
	string PayloadPath, string Sha256, string DependencyCoverage, string Diagnostic);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CollectionExportReport))]
internal sealed partial class CollectionExportReportJsonContext : JsonSerializerContext;
