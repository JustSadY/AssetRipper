using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.IO.Files;
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
	public bool Succeeded => Completed && ImportFailures.Count == 0 && Failures.Count == 0;

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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CollectionExportReport))]
internal sealed partial class CollectionExportReportJsonContext : JsonSerializerContext;
