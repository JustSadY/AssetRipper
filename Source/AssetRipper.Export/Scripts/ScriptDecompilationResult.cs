namespace AssetRipper.Export.Scripts;

public sealed record ScriptDecompilationDiagnostic(string? FileName, string? Entity, string Message)
{
	public string Stage { get; init; } = "WholeProject";
	public string? MetadataToken { get; init; }
	public string? ContainingType { get; init; }
}

public enum ScriptTypeRecoveryStatus { Recovered, Failed }

public sealed record ScriptTypeRecoveryResult(string TypeName, string MetadataToken, ScriptTypeRecoveryStatus Status,
	string? OutputFile, IReadOnlyList<ScriptDecompilationDiagnostic> Diagnostics);

/// <summary>Paths are relative to the assembly output directory. Recovered means emitted without decompiler errors, not compile/behavior verified.</summary>
public sealed record ScriptFileRecoveryResult(string OriginalFile, string? EvidenceFile, string? EvidenceSha256,
	IReadOnlyList<ScriptTypeRecoveryResult> Types, string? SkipReason = null)
{
	public int RecoveredTypeCount => Types.Count(t => t.Status == ScriptTypeRecoveryStatus.Recovered);
}

/// <summary>
/// Records decompiler failures, including errors that ILSpy reports without throwing.
/// Success does not imply that generated code compiles or preserves runtime behavior.
/// </summary>
public sealed record ScriptDecompilationResult(
	string AssemblyName,
	string OutputDirectory,
	IReadOnlyList<ScriptDecompilationDiagnostic> Diagnostics)
{
	public string? Identity { get; init; }
	public IReadOnlyList<ScriptFileRecoveryResult> TypeRecoveries { get; init; } = [];
	public int RecoveredTypeCount => TypeRecoveries.Sum(r => r.RecoveredTypeCount);
	public bool Succeeded => Diagnostics.Count == 0;
}
