using AsmResolver.DotNet;
using AssetRipper.CIL;
using Cpp2IL.Core;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using System.Collections.Concurrent;
using System.Text;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// Explicit research output. The normal isolated manager never selects this provider.
/// A selected body is kept only after the ISIL gate, the provider placeholder scan, the stack gate and the typed CIL
/// verifier all pass; it is then reported as <c>RecoveredNative</c> with behavior <c>NotRun</c>.
/// Everything else, including bodies shared by generic instantiations, is replaced by the minimal stub.
/// </summary>
internal sealed class Il2CppCandidateOutput(Il2CppCandidateSelection selection) : AsmResolverDllOutputFormat
{
	private const int MaxEvidenceLines = 2048;
	private const int MaxSidecarCharacters = 256 * 1024;

	public override string OutputFormatId => "experimental_checked_candidates";
	public override string OutputFormatName => "Experimental CIL candidates with ISIL, stack and typed CIL gates";
	private readonly HashSet<string> types = selection.TypeNames.ToHashSet(StringComparer.Ordinal);
	private Il2CppAddressLoweringInstructionSet? lowering;
	private string? unsupportedArchitecture;
	private string? lifter;
	// Only lifters whose gated bodies reproduced every native observation on the owned corpora may produce
	// RecoveredNative. The ARMv8 lifter's gated bodies differed from the native oracle there (UBFM shift aliases
	// lifted as right shifts, a loop lifted to a constant), so ARMv8 bodies are kept as evidence only.
	private string? untrustedLifter;
	private Dictionary<MethodAnalysisContext, Il2CppGenericInstantiation[]> instantiations = new(ReferenceEqualityComparer.Instance);
	private int selectedCount;
	public ConcurrentBag<Il2CppWorkerMethod> Methods { get; } = [];
	/// <summary>Sidecar text per method record; the worker host writes and hashes the files.</summary>
	public ConcurrentDictionary<Il2CppWorkerMethod, string> Sidecars { get; } = new(ReferenceEqualityComparer.Instance);

	public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
	{
		// Only the exact pinned lifters are extended. The pinned ARMv8 lifter returns no ISIL at all, so ARMv8 candidates
		// use the provider's newer lifter with 32/64-bit register aliasing. This runs once per fresh worker process.
		if (context.InstructionSet.GetType() == typeof(X86InstructionSet) && !context.Binary.is32Bit)
		{
			context.InstructionSet = lowering = new Il2CppAddressLoweringInstructionSet();
		}
		else if (context.InstructionSet.GetType() == typeof(Arm64InstructionSet) && context.Binary.InstructionSetId.Name == DefaultInstructionSets.ARM_V8.Name)
		{
			context.InstructionSet = new Il2CppArmV8RegisterAliasInstructionSet();
			untrustedLifter = "The ARMv8 lifter produced well-typed bodies that differ from the native oracle on the owned corpus; ARMv8 bodies are evidence only.";
		}
		else
		{
			unsupportedArchitecture = $"{context.Binary.InstructionSetId.Name} ({context.InstructionSet.GetType().Name})";
		}
		lifter = unsupportedArchitecture is null ? context.InstructionSet.GetType().Name : null;
		// Snapshot instantiations before methods are filled in parallel; the provider's dictionary is not concurrent.
		Dictionary<MethodAnalysisContext, List<Il2CppGenericInstantiation>> found = new(ReferenceEqualityComparer.Instance);
		foreach (ConcreteGenericMethodAnalysisContext concrete in context.ConcreteGenericMethodsByRef.Values)
		{
			ulong pointer = concrete.UnderlyingPointer;
			if (!found.TryGetValue(concrete.BaseMethodContext, out List<Il2CppGenericInstantiation>? list))
			{
				found[concrete.BaseMethodContext] = list = [];
			}
			list.Add(new(concrete.TypeGenericParameters.Select(t => t.FullName).ToArray(), concrete.MethodGenericParameters.Select(t => t.FullName).ToArray(),
				pointer, pointer == 0 ? 0 : context.Binary.GetRva(pointer),
				context.MethodsByAddress.TryGetValue(pointer, out List<MethodAnalysisContext>? shared) ? shared.Count : 0));
		}
		instantiations = new(ReferenceEqualityComparer.Instance);
		foreach ((MethodAnalysisContext method, List<Il2CppGenericInstantiation> list) in found)
		{
			instantiations[method] = list.OrderBy(i => string.Join(",", i.TypeArguments.Concat(i.MethodArguments)), StringComparer.Ordinal).ToArray();
		}
		return base.BuildAssemblies(context);
	}

	protected override void FillMethodBody(MethodDefinition definition, MethodAnalysisContext context)
	{
		bool selected = context.DeclaringType?.DeclaringAssembly.Name == selection.AssemblyName && types.Contains(context.DeclaringType.FullName);
		if (selected && Interlocked.Increment(ref selectedCount) > 4096) throw new InvalidDataException("Candidate selection exceeds 4096 methods.");
		if (!definition.IsManagedMethodWithBody())
		{
			if (selected) Add(definition, context, "NoBodyByDesign", ["NoBodyByDesign"], null, 0, [], null, null);
			return;
		}
		definition.ReplaceMethodBodyWithMinimalImplementation();
		if (!selected) return;

		List<string> gates = [];
		int isil = 0; string origin = "GeneratedStub"; string? diagnostic = null; string[] cil = [];
		Il2CppAddressLowering? addressLowering = null;
		int inferred = 0;
		StringBuilder sidecar = new();
		bool generic = definition.GenericParameters.Count > 0 || definition.DeclaringType?.GenericParameters.Count > 0;
		try
		{
			if (unsupportedArchitecture is not null) Gate(gates, "UnsupportedArchitecture", ref diagnostic, unsupportedArchitecture);
			else if (context.Name is ".ctor" or ".cctor") Gate(gates, "UnsupportedConstructor", ref diagnostic, null);
			else if (context.UnderlyingPointer == 0) Gate(gates, "NoNativeBody", ref diagnostic, null);
			else if (generic || SharedAddressCount(context) > 1)
			{
				// A native body shared by generic instantiations is not mapped onto one original method.
				Gate(gates, "SharedGenericBody", ref diagnostic, $"Generic={generic}; methods at this address={SharedAddressCount(context)}; "
					+ $"concrete instantiations={(instantiations.TryGetValue(context, out var known) ? known.Length : 0)}.");
				AppendDisassembly(sidecar, context);
			}
			else
			{
				AppendDisassembly(sidecar, context);
				context.Analyze(); isil = context.ConvertedIsil?.Count ?? 0;
				addressLowering = lowering?.GetResult(context);
				inferred = Il2CppLocalTypeInference.Run(context);
				AppendIsil(sidecar, context);
				if (isil == 0) Gate(gates, "EmptyIsil", ref diagnostic, null);
				else if (Pass(gates, Il2CppIsilGate.Check(context), ref diagnostic))
				{
					IlGenerator.GenerateIl(context, definition);
					definition.CilMethodBody!.Instructions.CalculateOffsets();
					cil = definition.CilMethodBody.Instructions.Take(256).Select(i => i.ToString()).ToArray();
					AppendCil(sidecar, definition);
					if (Pass(gates, Il2CppCandidateBodyGate.Check(definition), ref diagnostic)
						&& Pass(gates, Il2CppTypedCilVerifier.Verify(definition, context.AppContext.GetExtraData<RuntimeContext>("AsmResolverRuntimeContext")), ref diagnostic))
					{
						if (untrustedLifter is null) origin = "RecoveredNative";
						else Gate(gates, "UntrustedLifter", ref diagnostic, untrustedLifter);
					}
				}
			}
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			Gate(gates, "AnalysisFailed", ref diagnostic, e.GetType().Name + ": " + e.Message);
		}
		finally
		{
			Add(definition, context, origin, gates, diagnostic, isil, cil, addressLowering, sidecar, inferred);
			if (origin == "GeneratedStub") definition.ReplaceMethodBodyWithMinimalImplementation();
			context.ReleaseAnalysisData();
		}
	}

	private static void Gate(List<string> gates, string status, ref string? diagnostic, string? reason)
	{
		gates.Add(status);
		diagnostic ??= reason;
	}

	private static bool Pass(List<string> gates, Il2CppCandidateGateResult result, ref string? diagnostic)
	{
		gates.Add(result.Status);
		if (result.Status is "IsilChecked" or "StackHeightChecked" or "TypedCilChecked") return true;
		diagnostic ??= result.Diagnostic;
		return false;
	}

	private static int SharedAddressCount(MethodAnalysisContext context)
		=> context.AppContext.MethodsByAddress.TryGetValue(context.UnderlyingPointer, out List<MethodAnalysisContext>? shared) ? shared.Count : 0;

	private Il2CppWorkerMethod Add(MethodDefinition definition, MethodAnalysisContext context, string bodyOrigin, List<string> gates, string? reason,
		int isilCount, string[] instructions, Il2CppAddressLowering? addressLowering, StringBuilder? sidecar, int inferredLocals = 0)
	{
		ulong pointer = context.UnderlyingPointer;
		long? raw = pointer != 0 && context.AppContext.Binary.TryMapVirtualAddressToRaw(pointer, out long offset) ? offset : null;
		// Counts describe the generated candidate CIL, never the stub that replaces a rejected body.
		int count = instructions.Length == 0 ? 0 : definition.CilMethodBody?.Instructions.Count ?? 0;
		Il2CppWorkerMethod record = new(context.Token, context.DeclaringType!.FullName, context.Name, context.FullNameWithSignature, pointer,
			pointer == 0 ? 0 : context.Rva, raw, SharedAddressCount(context), bodyOrigin, gates.Count == 0 ? "NotRun" : gates[^1], "NotRun", reason,
			isilCount, count, instructions, count > instructions.Length)
		{
			Gates = gates.ToArray(),
			IsGeneric = definition.GenericParameters.Count > 0 || definition.DeclaringType?.GenericParameters.Count > 0,
			AddressLowering = addressLowering,
			InferredLocalTypes = inferredLocals,
			Lifter = lifter,
			GenericInstantiations = instantiations.TryGetValue(context, out Il2CppGenericInstantiation[]? concrete) ? concrete : null,
		};
		Methods.Add(record);
		if (sidecar is { Length: > 0 })
		{
			Sidecars[record] = Header(record) + (sidecar.Length > MaxSidecarCharacters ? sidecar.ToString(0, MaxSidecarCharacters) + "\n[truncated]\n" : sidecar.ToString());
		}
		return record;
	}

	private static string Header(Il2CppWorkerMethod record)
	{
		return $"# {record.Signature}\n# token 0x{record.OriginalToken:X8} pointer 0x{record.Pointer:X} rva 0x{record.Rva:X} raw {record.RawOffset?.ToString("X") ?? "-"}\n"
			+ $"# origin {record.BodyOrigin}; gates {string.Join(" > ", record.Gates)}; behavior {record.BehaviorValidation}\n"
			+ $"# lifter {record.Lifter ?? "-"}; address lowering {record.AddressLowering?.Status ?? "-"} ({record.AddressLowering?.Lowered ?? 0}); inferred local types {record.InferredLocalTypes}\n"
			+ string.Concat((record.GenericInstantiations ?? []).Select(i => $"# instantiation <{string.Join(", ", i.TypeArguments.Concat(i.MethodArguments))}> pointer 0x{i.Pointer:X} rva 0x{i.Rva:X} methods at address {i.MethodsAtAddress}\n"))
			+ (record.Diagnostic is null ? "" : $"# diagnostic {record.Diagnostic}\n");
	}

	private static void AppendDisassembly(StringBuilder sidecar, MethodAnalysisContext context)
	{
		string text;
		try
		{
			text = context.AppContext.InstructionSet.PrintAssembly(context);
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			text = "Disassembly failed: " + e.GetType().Name + ": " + e.Message;
		}
		AppendSection(sidecar, "native", text.Split('\n'));
	}

	private static void AppendIsil(StringBuilder sidecar, MethodAnalysisContext context)
	{
		IEnumerable<string> lines = context.ControlFlowGraph is { } graph
			? graph.Blocks.SelectMany(block => block.Instructions).Select(instruction => instruction.ToString())
			: context.ConvertedIsil?.Select(instruction => instruction.ToString()) ?? [];
		AppendSection(sidecar, "isil", lines);
	}

	private static void AppendCil(StringBuilder sidecar, MethodDefinition definition)
	{
		AppendSection(sidecar, "cil", definition.CilMethodBody?.Instructions.Select(instruction => instruction.ToString()) ?? []);
	}

	private static void AppendSection(StringBuilder sidecar, string name, IEnumerable<string> lines)
	{
		sidecar.Append("## ").Append(name).Append('\n');
		int count = 0;
		foreach (string line in lines)
		{
			if (++count > MaxEvidenceLines)
			{
				sidecar.Append("[truncated]\n");
				break;
			}
			sidecar.Append(line.TrimEnd('\r')).Append('\n');
		}
	}
}
