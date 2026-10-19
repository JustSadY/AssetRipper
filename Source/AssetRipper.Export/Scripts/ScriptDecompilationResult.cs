namespace AssetRipper.Export.Scripts;

public sealed record ScriptDecompilationDiagnostic(string? FileName, string? Entity, string Message);

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
	public bool Succeeded => Diagnostics.Count == 0;
}
