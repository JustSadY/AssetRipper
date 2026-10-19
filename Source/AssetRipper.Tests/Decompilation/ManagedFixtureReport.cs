using AssetRipper.Export.Scripts;
using AssetRipper.Import.Structure.Assembly;
using System.Text.Json.Serialization;

namespace AssetRipper.Tests.Decompilation;

[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
internal enum CheckStatus { NotRun, Passed, Failed }

internal sealed record CompilerDiagnostic(string Id, string Severity, string Message, string? File, int? Line, int? Column);
internal sealed record CompilationCheck(CheckStatus Status, IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
	public static CompilationCheck NotRun { get; } = new(CheckStatus.NotRun, []);
}

internal sealed record ReferenceImage(string File, string Sha256);
internal sealed record ReferenceProfile(string Name, string Package, string Version, IReadOnlyList<ReferenceImage> Images);
internal sealed record SourceImage(string File, string Sha256);
internal sealed record Observation(string ReturnJson, string ArgumentsJson, string? ExceptionType = null, string? ExceptionParameter = null, string? ExceptionMessage = null);
internal sealed record BehaviorCheck(string Name, Observation Expected, Observation Original, Observation Recompiled, bool OracleMatched, bool Matched);
internal sealed record ContractCheck(CheckStatus Status, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);

/// <summary>Test-only evidence. Passing this controlled .NET fixture is not Unity Editor validation.</summary>
internal sealed class ManagedFixtureReport
{
	public int SchemaVersion => 1;
	public required string Fixture { get; init; }
	public required string FixtureSha256 { get; init; }
	public required ReferenceProfile References { get; init; }
	public required string LanguageVersion { get; init; }
	public required string Optimization { get; init; }
	public bool CheckOverflow => true;
	public bool AllowUnsafe => false;
	public string NullableContext => "Disable";
	public required string CompilerVersion { get; init; }
	public required string DecompilerVersion { get; init; }
	public required string Runtime { get; init; }
	public required string OperatingSystem { get; init; }
	public required string Mutation { get; init; }
	public required int ExpectedBehaviorCount { get; init; }
	public CompilationCheck OriginalCompilation { get; set; } = CompilationCheck.NotRun;
	public ScriptDecompilationResult? Decompilation { get; set; }
	public IReadOnlyList<AssemblyResolutionDiagnostic> ResolutionDiagnostics { get; set; } = [];
	public CheckStatus DecompilationStatus => Decompilation is null ? CheckStatus.NotRun : Decompilation.Succeeded ? CheckStatus.Passed : CheckStatus.Failed;
	public CompilationCheck Recompilation { get; set; } = CompilationCheck.NotRun;
	public ContractCheck Contract { get; set; } = new(CheckStatus.NotRun, [], []);
	public List<BehaviorCheck> Behaviors { get; } = [];
	public List<SourceImage> GeneratedSources { get; } = [];
	public string? OriginalAssemblySha256 { get; set; }
	public string? RecompiledAssemblySha256 { get; set; }
	public string? HarnessError { get; set; }
	public CheckStatus BehaviorStatus => Behaviors.Count == 0 ? CheckStatus.NotRun
		: Behaviors.Count == ExpectedBehaviorCount && Behaviors.All(b => b.OracleMatched && b.Matched) ? CheckStatus.Passed : CheckStatus.Failed;
	public CheckStatus UnityEditorValidation => CheckStatus.NotRun;
	public bool Succeeded => HarnessError is null && OriginalCompilation.Status == CheckStatus.Passed
		&& DecompilationStatus == CheckStatus.Passed && Recompilation.Status == CheckStatus.Passed
		&& Contract.Status == CheckStatus.Passed && BehaviorStatus == CheckStatus.Passed;
}
