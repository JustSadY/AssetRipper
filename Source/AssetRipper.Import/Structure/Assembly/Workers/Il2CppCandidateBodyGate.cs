using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;

namespace AssetRipper.Import.Structure.Assembly.Workers;

public sealed record Il2CppCandidateGateResult(string Status, string? Diagnostic);

/// <summary>A conservative structural gate. Passing never establishes typed CIL or native semantics.</summary>
public static class Il2CppCandidateBodyGate
{
	// Every diagnostic string the pinned IlGenerator emits in place of code, including the analysis warnings
	// it appends after the last instruction.
	private static readonly string[] PlaceholderPrefixes =
	[
		"Unmanaged memory load:", "Method not found @", "Not implemented instruction:", "Invalid instruction:",
		"Phi opcodes should not exist", "Unknown call target operand:", "Non static method called without 'this' param",
		"Indirect call:", "Indirect jump:", "Stack shift:", "Unknown instruction:", "Unknown operand:",
		"Constructor not found for:", "Constructor param:", "Store into unknown operand:", "Warning: ", "<T>",
	];

	public static Il2CppCandidateGateResult Check(MethodDefinition method)
	{
		if (method.CilMethodBody is not { Instructions.Count: > 0 } body) return new("RejectedEmptyBody", "No candidate CIL instructions.");
		foreach (var instruction in body.Instructions)
		{
			if (instruction.OpCode.Code == CilCode.Ldstr && instruction.Operand is string text
				&& PlaceholderPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)))
				return new("RejectedProviderPlaceholder", text);
		}
		try
		{
			body.VerifyLabels();
			body.MaxStack = body.ComputeMaxStack();
			body.ComputeMaxStackOnBuild = false;
			return new("StackHeightChecked", null);
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			return new("RejectedInvalidStackOrBranch", e.GetType().Name + ": " + e.Message);
		}
	}
}
