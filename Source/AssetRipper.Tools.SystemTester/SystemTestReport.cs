using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Recovery;
using AssetRipper.Import.Structure.Assembly.Workers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace AssetRipper.Tools.SystemTester;

internal sealed class SystemTestReport
{
	public int SchemaVersion => 1;
	public string AssetRipperVersion => typeof(ExportHandler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
	public string RuntimeVersion => Environment.Version.ToString();
	public string OperatingSystem => RuntimeInformation.OSDescription;
	public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();
	public DateTime StartedUtc { get; } = DateTime.UtcNow;
	public List<SystemTestResult> Results { get; } = [];
	public List<string> Errors { get; } = [];
	public bool Succeeded => Errors.Count == 0 && Results.Count > 0 && Results.All(r => r.Succeeded);
}

internal sealed record SystemTestResult(
	string Name,
	IReadOnlyList<string> InputPaths,
	string? OutputDirectory,
	string? UnityVersion,
	int LoadedObjectCount,
	int ProcessedObjectCount,
	int AssemblyCount,
	double DurationSeconds,
	int WarningCount,
	int ErrorCount,
	bool Succeeded,
	string? Error,
	RawRecoverySummary? RawRecovery,
	CollectionExportReport? CollectionExport,
	AssemblyResolutionReport? AssemblyResolution,
	Il2CppWorkerRun? Il2CppWorker = null);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SystemTestReport))]
internal sealed partial class SystemTestReportJsonContext : JsonSerializerContext
{
}
