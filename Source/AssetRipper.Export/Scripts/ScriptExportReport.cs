using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
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
	/// <summary>Selected native-to-managed provider and body provenance; independent of C# emission success.</summary>
	public Il2CppRecoveryReport? Il2CppRecovery { get; init; }
	public ScriptLanguageSelection? LanguageSelection { get; init; }
	/// <summary>SDK build scaffold only. This does not identify the recovered game's API profile.</summary>
	public string? ProjectScaffoldFramework { get; init; }
	public List<ScriptDecompilationResult> Assemblies { get; } = [];
	public List<string> PlaceholderScripts { get; } = [];
	public List<AssemblyResolutionDiagnostic> ResolutionDiagnostics { get; } = [];
	/// <summary>Missing output artifacts or invalid predefined-assembly references, independent of C# emission.</summary>
	public List<UnityAssemblyReferenceFailure> UnityAssemblyReferenceFailures { get; } = [];
	public bool Succeeded => Assemblies.All(a => a.Succeeded) && PlaceholderScripts.Count == 0 && ResolutionDiagnostics.All(d => !d.IsFailure)
		&& UnityAssemblyReferenceFailures.Count == 0;
	public int FailedAssemblyCount => Assemblies.Count(a => !a.Succeeded);

	public void Write(string outputDirectory, FileSystem fileSystem)
	{
		fileSystem.Directory.Create(outputDirectory);
		using Stream stream = fileSystem.File.Create(fileSystem.Path.Join(outputDirectory, FileName));
		JsonSerializer.Serialize(stream, this, ScriptExportReportJsonContext.Default.ScriptExportReport);
	}
}

public sealed record UnityAssemblyReferenceFailure(string AssemblyName, string ReferenceIdentity, string Reason);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ScriptExportReport))]
internal sealed partial class ScriptExportReportJsonContext : JsonSerializerContext;
