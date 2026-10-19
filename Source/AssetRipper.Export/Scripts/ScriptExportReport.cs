using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.IO.Files;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Export.Scripts;

public sealed class ScriptExportReport
{
	public const string FileName = "script-export-report.json";

	public int SchemaVersion => 1;
	public ScriptingBackend ScriptingBackend { get; init; }
	public ScriptContentLevel? ScriptContentLevel { get; init; }
	public ScriptLanguageSelection? LanguageSelection { get; init; }
	/// <summary>SDK build scaffold only. This does not identify the recovered game's API profile.</summary>
	public string? ProjectScaffoldFramework { get; init; }
	public List<ScriptDecompilationResult> Assemblies { get; } = [];
	public List<string> PlaceholderScripts { get; } = [];
	public List<AssemblyResolutionDiagnostic> ResolutionDiagnostics { get; } = [];
	public bool Succeeded => Assemblies.All(a => a.Succeeded) && PlaceholderScripts.Count == 0 && ResolutionDiagnostics.All(d => !d.IsFailure);
	public int FailedAssemblyCount => Assemblies.Count(a => !a.Succeeded);

	public void Write(string outputDirectory, FileSystem fileSystem)
	{
		fileSystem.Directory.Create(outputDirectory);
		using Stream stream = fileSystem.File.Create(fileSystem.Path.Join(outputDirectory, FileName));
		JsonSerializer.Serialize(stream, this, ScriptExportReportJsonContext.Default.ScriptExportReport);
	}
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ScriptExportReport))]
internal sealed partial class ScriptExportReportJsonContext : JsonSerializerContext;
