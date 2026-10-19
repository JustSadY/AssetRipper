using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Export.Scripts;

public sealed class AssemblyResolutionReport
{
	public const string FileName = "assembly-resolution-report.json";
	public int SchemaVersion => 1;
	public required AssemblyCandidateInfo[] Candidates { get; init; }
	public required AssemblyResolutionDiagnostic[] Diagnostics { get; init; }
	public bool SharedContextRecoverySkipped { get; init; }
	public required AssemblyOutputInfo[] Outputs { get; init; }
	public bool Succeeded => !SharedContextRecoverySkipped && Diagnostics.All(d => !d.IsFailure);

	public static AssemblyResolutionReport Create(IAssemblyManager manager, string dllDirectory) => new()
	{
		Candidates = AssemblyResolution.Describe(manager, manager.GetAssemblies()),
		Diagnostics = manager.ResolutionDiagnostics.ToArray(),
		SharedContextRecoverySkipped = manager.HasAssemblyNameCollisions,
		Outputs = GetOutputs(manager, dllDirectory),
	};

	private static AssemblyOutputInfo[] GetOutputs(IAssemblyManager manager, string dllDirectory)
	{
		AssemblyExportLayout layout = new(manager);
		return manager.GetAssemblies().Select(a => new AssemblyOutputInfo(AssemblyIdentity.From(a).ToString(),
			manager.GetSourceInfo(a)?.SourcePath, a.ManifestModule?.Mvid.ToString(), dllDirectory + "/" + layout.GetStem(a) + ".dll")).ToArray();
	}

	public void Write(string directory, FileSystem fs)
	{
		fs.Directory.Create(directory);
		using Stream stream = fs.File.Create(fs.Path.Join(directory, FileName));
		JsonSerializer.Serialize(stream, this, AssemblyResolutionReportJsonContext.Default.AssemblyResolutionReport);
	}
}

public sealed record AssemblyOutputInfo(string Identity, string? SourcePath, string? ModuleMvid, string RelativePath);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AssemblyResolutionReport))]
internal sealed partial class AssemblyResolutionReportJsonContext : JsonSerializerContext;
