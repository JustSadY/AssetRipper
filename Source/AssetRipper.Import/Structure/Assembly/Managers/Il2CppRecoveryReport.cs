using AsmResolver.DotNet;
using AssetRipper.CIL;

namespace AssetRipper.Import.Structure.Assembly.Managers;

/// <summary>
/// Describes the selected IL2CPP pipeline and its output without asserting behavioral recovery.
/// </summary>
public sealed record Il2CppRecoveryReport(
	int SchemaVersion,
	string RequestedContentLevel,
	Il2CppProviderInfo OutputFormat,
	IReadOnlyList<Il2CppProviderInfo> ProcessingLayers,
	bool UsesKnownStubOutput,
	Il2CppMethodBodyCounts MethodBodies,
	string Validation)
{
	public Il2CppIsolationInfo? Isolation { get; init; }
	/// <summary>
	/// <c>NotRequested</c>, <c>RecoveryUnavailable</c> (Level3 with the known stub provider),
	/// <c>ExperimentalCandidates</c> or <c>UnverifiedCustomProvider</c>. Never a behavioral claim.
	/// </summary>
	public string? RecoveryCapability { get; init; }
	/// <summary>Structural binary/metadata cross-check; null when the import did not run it.</summary>
	public Il2CppPairValidation? PairValidation { get; init; }
	/// <summary>
	/// Counts output bodies using the selected provider's known contract. A custom provider's managed
	/// methods remain unknown even when their output contains CIL instructions.
	/// </summary>
	public Il2CppRecoveryReport WithMethodBodies(IEnumerable<AssemblyDefinition> assemblies)
	{
		int generatedStub = 0, noBodyByDesign = 0, unknown = 0;
		foreach (MethodDefinition method in assemblies.SelectMany(a => a.Modules).SelectMany(m => m.GetAllTypes()).SelectMany(t => t.Methods))
		{
			if (!method.IsManagedMethodWithBody())
			{
				noBodyByDesign++;
			}
			else if (UsesKnownStubOutput && method.CilMethodBody is { Instructions.Count: > 0 })
			{
				generatedStub++;
			}
			else
			{
				unknown++;
			}
		}
		return this with { MethodBodies = new(generatedStub, noBodyByDesign, unknown) };
	}
}

public sealed record Il2CppProviderInfo(string TypeName, string AssemblyName, string AssemblyVersion, string Id);

public sealed record Il2CppMethodBodyCounts(int GeneratedStub, int NoBodyByDesign, int Unknown)
{
	/// <summary>Bodies that passed every structural gate of an experimental provider. Behavior is still unverified.</summary>
	public int RecoveredNative { get; init; }
}

/// <summary>
/// Independent counts that a native binary and its global metadata must agree on before any analysis.
/// Count agreement cannot distinguish two builds with identical shapes.
/// </summary>
public sealed record Il2CppPairValidation(string Status, string InstructionSet, int PointerSize, float MetadataVersion,
	int Images, int TypeDefinitions, int MethodDefinitions, int CheckedModules, long MethodPointers);

public sealed record Il2CppIsolationInfo(string JobId, int ProcessId, string Mode, string ProtocolValidation);
