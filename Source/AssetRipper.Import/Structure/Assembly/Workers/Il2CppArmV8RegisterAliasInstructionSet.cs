using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Isil = Cpp2IL.Core.ISIL;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// The pinned ARMv8 lifter names registers after their encoding, so <c>W0</c> and <c>X0</c> become two unrelated ISIL
/// registers. In hardware a <c>Wn</c> write is the low half of <c>Xn</c> with the upper half cleared. Without aliasing,
/// a 32-bit result never reaches the 64-bit return or argument register and is removed as dead code; the resulting
/// body passes every structural and typed gate while computing the wrong value. This lifter keeps every provider rule
/// and only renames 32-bit general-purpose registers to their 64-bit names before SSA, like the x86 lifter's full
/// register names. It is installed only in a fresh worker process for explicitly selected experimental candidates.
/// </summary>
internal sealed class Il2CppArmV8RegisterAliasInstructionSet : NewArmV8InstructionSet
{
	public override List<Isil.Instruction> GetIsilFromMethod(MethodAnalysisContext context)
	{
		List<Isil.Instruction> isil = base.GetIsilFromMethod(context);
		foreach (Isil.Instruction instruction in isil)
		{
			for (int i = 0; i < instruction.Operands.Count; i++)
			{
				if (Alias(instruction.Operands[i]) is { } replaced)
				{
					instruction.SetOperand(i, replaced);
				}
			}
		}
		return isil;
	}

	private static Isil.IOperand? Alias(Isil.IOperand? operand) => operand switch
	{
		Isil.Register register when Wide(register.Name) is { } name => new Isil.Register(null, name),
		Isil.MemoryOperand memory when Alias(memory.Base) is not null || Alias(memory.Index) is not null
			=> new Isil.MemoryOperand(Alias(memory.Base) ?? memory.Base, Alias(memory.Index) ?? memory.Index, memory.Addend, memory.Scale),
		_ => null,
	};

	private static string? Wide(string name) => name switch
	{
		"WZR" => "XZR",
		"WSP" => "SP",
		{ Length: > 1 } when name[0] == 'W' && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 => "X" + name[1..],
		_ => null,
	};
}
