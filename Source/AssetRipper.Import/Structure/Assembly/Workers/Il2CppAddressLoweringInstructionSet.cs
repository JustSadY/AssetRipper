using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using System.Collections.Concurrent;
using IcedRegister = Iced.Intel.Register;
using Isil = Cpp2IL.Core.ISIL;
using NativeInstruction = Iced.Intel.Instruction;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// <c>Applied</c>, <c>Partial</c> (some LEA forms were left unchanged), <c>NotApplicable</c>,
/// <c>AlignmentFailed</c> or <c>Failed</c>. Nothing is rewritten unless the alignment is exact.
/// </summary>
public sealed record Il2CppAddressLowering(int Lowered, string Status, string? Diagnostic);

/// <summary>
/// The pinned x86 lifter turns <c>lea r, [base+index*scale+disp]</c> into the same ISIL move as a memory load,
/// so the IL generator later emits an "Unmanaged memory load" placeholder. This lifter keeps every provider rule
/// and rewrites only LEA moves into address arithmetic, before SSA and type propagation run.
/// It is installed only in a fresh worker process for explicitly selected experimental candidates.
/// </summary>
internal sealed class Il2CppAddressLoweringInstructionSet : X86InstructionSet
{
	private const string TemporaryRegister = "addr_tmp";

	// Mnemonics that the pinned lifter turns into "Move op0, op1" with a converted memory operand.
	private static readonly HashSet<Mnemonic> MemoryMoves =
	[
		Mnemonic.Mov, Mnemonic.Movzx, Mnemonic.Movsx, Mnemonic.Movsxd, Mnemonic.Movaps, Mnemonic.Movups, Mnemonic.Movss,
		Mnemonic.Movd, Mnemonic.Movq, Mnemonic.Movsd, Mnemonic.Movdqa, Mnemonic.Cvtdq2ps, Mnemonic.Cvtps2pd,
		Mnemonic.Cvttsd2si, Mnemonic.Movdqu, Mnemonic.Lea,
		Mnemonic.Cmove, Mnemonic.Cmovne, Mnemonic.Cmova, Mnemonic.Cmovg, Mnemonic.Cmovae, Mnemonic.Cmovge,
		Mnemonic.Cmovb, Mnemonic.Cmovl, Mnemonic.Cmovbe, Mnemonic.Cmovle, Mnemonic.Cmovs, Mnemonic.Cmovns,
	];

	private readonly ConcurrentDictionary<MethodAnalysisContext, Il2CppAddressLowering> results = new(ReferenceEqualityComparer.Instance);

	public Il2CppAddressLowering? GetResult(MethodAnalysisContext context) => results.TryGetValue(context, out Il2CppAddressLowering? result) ? result : null;

	public override List<Isil.Instruction> GetIsilFromMethod(MethodAnalysisContext context)
	{
		List<Isil.Instruction> isil = base.GetIsilFromMethod(context);
		Il2CppAddressLowering result;
		try
		{
			result = Lower(context, isil);
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			// Lower only mutates after every check passed, so the provider's ISIL is intact here.
			result = new(0, "Failed", e.GetType().Name + ": " + e.Message);
		}
		results[context] = result;
		return isil;
	}

	private readonly record struct MemorySource(NativeInstruction Native, string Destination);

	private static Il2CppAddressLowering Lower(MethodAnalysisContext context, List<Isil.Instruction> isil)
	{
		List<MemorySource> sources = [];
		foreach (NativeInstruction native in X86Utils.Iterate(context))
		{
			if (native.OpCount != 2 || !MemoryMoves.Contains(native.Mnemonic) && native.Mnemonic != Mnemonic.Xchg)
			{
				continue;
			}
			string destination;
			if (native.Op0Kind == OpKind.Register && native.Op1Kind == OpKind.Memory)
			{
				destination = X86Utils.GetRegisterName(native.Op0Register);
			}
			else if (native.Mnemonic == Mnemonic.Xchg && native.Op0Kind == OpKind.Memory && native.Op1Kind == OpKind.Register)
			{
				destination = "TEMP"; // The lifter's first xchg move is "TEMP = op0".
			}
			else
			{
				continue;
			}
			if (ConvertsToRegisterMemory(native))
			{
				sources.Add(new(native, destination));
			}
		}
		if (!sources.Any(s => s.Native.Mnemonic == Mnemonic.Lea))
		{
			return new(0, "NotApplicable", null);
		}

		List<Isil.Instruction> moves = isil.Where(i => i is { OpCode: Isil.OpCode.Move, Operands: [Isil.Register, Isil.MemoryOperand] }).ToList();
		if (moves.Count != sources.Count)
		{
			return new(0, "AlignmentFailed", $"{moves.Count} ISIL register-memory moves; {sources.Count} native ones.");
		}

		List<(Isil.Instruction Move, NativeInstruction Native)> leas = [];
		for (int k = 0; k < moves.Count; k++)
		{
			Isil.Instruction move = moves[k];
			NativeInstruction native = sources[k].Native;
			Isil.Register destination = (Isil.Register)move.Operands[0];
			Isil.MemoryOperand memory = (Isil.MemoryOperand)move.Operands[1];
			if (destination.Name != sources[k].Destination || !Matches(memory, native))
			{
				return new(0, "AlignmentFailed", $"ISIL move {move.Index} does not match the native instruction at 0x{native.IP:X}.");
			}
			if (native.Mnemonic == Mnemonic.Lea)
			{
				leas.Add((move, native));
			}
		}

		// Plan every rewrite before touching the provider's instructions.
		List<(Isil.Instruction Move, List<(Isil.OpCode OpCode, List<Isil.IOperand> Operands)> Operations)> plan = [];
		foreach ((Isil.Instruction move, NativeInstruction native) in leas)
		{
			if (Plan(move, native) is { } operations)
			{
				plan.Add((move, operations));
			}
		}
		if (plan.Count == 0)
		{
			return new(0, "Partial", $"{leas.Count} LEA forms are outside the supported 64-bit integer address forms.");
		}

		Dictionary<Isil.Instruction, List<Isil.Instruction>> appended = new(ReferenceEqualityComparer.Instance);
		foreach ((Isil.Instruction move, List<(Isil.OpCode OpCode, List<Isil.IOperand> Operands)> operations) in plan)
		{
			// The first operation reuses the instruction object so existing branch targets stay valid.
			move.OpCode = operations[0].OpCode;
			move.SetOperands(operations[0].Operands);
			appended[move] = operations.Skip(1).Select(o => new Isil.Instruction(0, o.OpCode, o.Operands)).ToList();
		}
		List<Isil.Instruction> rebuilt = new(isil.Count + appended.Values.Sum(a => a.Count));
		foreach (Isil.Instruction instruction in isil)
		{
			rebuilt.Add(instruction);
			if (appended.TryGetValue(instruction, out List<Isil.Instruction>? extra))
			{
				rebuilt.AddRange(extra);
			}
		}
		for (int index = 0; index < rebuilt.Count; index++)
		{
			rebuilt[index].Index = index;
		}
		isil.Clear();
		isil.AddRange(rebuilt);

		int skipped = leas.Count - plan.Count;
		return new(plan.Count, skipped == 0 ? "Applied" : "Partial", skipped == 0 ? null : $"{skipped} LEA forms were left unchanged.");
	}

	/// <summary>The pinned ConvertOperand turns every non-stack memory operand into a MemoryOperand.</summary>
	private static bool ConvertsToRegisterMemory(NativeInstruction native) => native.MemoryBase != IcedRegister.RSP;

	/// <summary>
	/// Compares a lifted operand with its native source, including the lossy forms: RIP-relative and absolute addresses
	/// become constants, and an index without base or displacement is dropped entirely.
	/// </summary>
	private static bool Matches(Isil.MemoryOperand memory, NativeInstruction native)
	{
		if (native.IsIPRelativeMemoryOperand)
		{
			return memory.Base is null && memory.Index is null && memory.Addend == unchecked((long)native.IPRelativeMemoryAddress);
		}
		if (native.MemoryBase == IcedRegister.None && (native.MemoryIndex == IcedRegister.None || native.MemoryDisplacement64 == 0))
		{
			long expected = native.MemoryIndex == IcedRegister.None ? unchecked((long)native.MemoryDisplacement64) : 0;
			return memory.Base is null && memory.Index is null && memory.Addend == expected;
		}
		return NameOf(memory.Base) == X86Utils.GetRegisterName(native.MemoryBase)
			&& NameOf(memory.Index) == X86Utils.GetRegisterName(native.MemoryIndex)
			&& (native.MemoryIndex == IcedRegister.None || memory.Scale == native.MemoryIndexScale);
	}

	private static List<(Isil.OpCode, List<Isil.IOperand>)>? Plan(Isil.Instruction move, NativeInstruction native)
	{
		int size = native.Op0Register.GetSize();
		if (size is not (4 or 8) || native.IsIPRelativeMemoryOperand
			|| native.MemoryBase == IcedRegister.None && native.MemoryIndex == IcedRegister.None
			|| native.MemoryBase != IcedRegister.None && !native.MemoryBase.IsGPR64()
			|| native.MemoryIndex != IcedRegister.None && !native.MemoryIndex.IsGPR64())
		{
			return null;
		}

		// Pre-SSA registers are identified by name, so registers rebuilt from the native operand equal the lifted ones;
		// this also recovers the index that the lifter drops when there is neither base nor displacement.
		Isil.Register d = (Isil.Register)move.Operands[0];
		Isil.IOperand? b = native.MemoryBase == IcedRegister.None ? null : new Isil.Register(null, X86Utils.GetRegisterName(native.MemoryBase));
		Isil.IOperand? i = native.MemoryIndex == IcedRegister.None ? null : new Isil.Register(null, X86Utils.GetRegisterName(native.MemoryIndex));
		int scale = native.MemoryIndex == IcedRegister.None ? 1 : native.MemoryIndexScale;
		long displacement = unchecked((long)native.MemoryDisplacement64); // Sign-extended for 64-bit addressing.
		List<(Isil.OpCode, List<Isil.IOperand>)> operations = [];

		if (i is null)
		{
			operations.Add(displacement == 0 ? (Isil.OpCode.Move, [d, b!]) : (Isil.OpCode.Add, [d, b!, Imm(displacement)]));
			return operations;
		}
		if (b is null)
		{
			operations.Add(scale == 1 ? (Isil.OpCode.Move, [d, i]) : (Isil.OpCode.Multiply, [d, i, Imm(scale)]));
		}
		else if (scale == 1)
		{
			operations.Add((Isil.OpCode.Add, [d, b, i]));
		}
		else if (NameOf(b) == NameOf(i))
		{
			operations.Add((Isil.OpCode.Multiply, [d, b, Imm(scale + 1)])); // r + r*s == r*(s+1)
		}
		else if (NameOf(b) != d.Name)
		{
			operations.Add((Isil.OpCode.Multiply, [d, i, Imm(scale)]));
			operations.Add((Isil.OpCode.Add, [d, d, b]));
		}
		else
		{
			// The destination is the base, so the scaled index needs its own register.
			Isil.Register temporary = new(null, TemporaryRegister);
			operations.Add((Isil.OpCode.Multiply, [temporary, i, Imm(scale)]));
			operations.Add((Isil.OpCode.Add, [d, b, temporary]));
		}
		if (displacement != 0)
		{
			operations.Add((Isil.OpCode.Add, [d, d, Imm(displacement)]));
		}
		return operations;
	}

	private static Isil.Immediate Imm(long value) => new(value);

	private static string NameOf(Isil.IOperand? operand) => operand switch
	{
		null => "",
		Isil.Register register => register.Name,
		_ => "?" + operand,
	};
}
