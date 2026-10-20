using Cpp2IL.Core.Model.Contexts;
using Isil = Cpp2IL.Core.ISIL;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// The pinned type propagation leaves some flag and scratch temporaries untyped; the IL generator then declares
/// them as <c>object</c> and stores integers into them. This pass types such a local only when every definition
/// agrees: integer arithmetic over operands of one known Int32/Int64 type (immediates match either), or a
/// comparison (Boolean). Anything else leaves the local untyped, and the typed CIL gate decides.
/// </summary>
internal static class Il2CppLocalTypeInference
{
	public static int Run(MethodAnalysisContext context)
	{
		if (context.ControlFlowGraph is not { } graph)
		{
			return 0;
		}
		SystemTypesContext system = context.AppContext.SystemTypes;
		TypeAnalysisContext[] integers = [system.SystemInt32Type, system.SystemInt64Type];
		List<Isil.Instruction> instructions = graph.Blocks.SelectMany(block => block.Instructions).ToList();
		Dictionary<Isil.LocalVariable, List<Isil.Instruction>> definitions = new(ReferenceEqualityComparer.Instance);
		foreach (Isil.Instruction instruction in instructions)
		{
			if (instruction.OpCode is not (Isil.OpCode.Call or Isil.OpCode.CallVoid or Isil.OpCode.Return or Isil.OpCode.Jump or Isil.OpCode.ConditionalJump)
				&& instruction.Operands.Count > 0 && instruction.Operands[0] is Isil.LocalVariable { Type: null } destination)
			{
				if (!definitions.TryGetValue(destination, out List<Isil.Instruction>? list))
				{
					definitions[destination] = list = [];
				}
				list.Add(instruction);
			}
			else if (instruction.OpCode == Isil.OpCode.Call && instruction.Operands.Count > 1 && instruction.Operands[1] is Isil.LocalVariable { Type: null } returned)
			{
				// A call result is defined by its callee; it is never inferred here.
				definitions[returned] = [instruction];
			}
		}

		int typed = 0;
		bool changed = true;
		while (changed)
		{
			changed = false;
			foreach ((Isil.LocalVariable local, List<Isil.Instruction> defined) in definitions)
			{
				if (local.Type is not null)
				{
					continue;
				}
				TypeAnalysisContext? agreed = null;
				bool consistent = true;
				foreach (Isil.Instruction definition in defined)
				{
					TypeAnalysisContext? type = Infer(definition, system, integers);
					if (type is null || agreed is not null && agreed != type)
					{
						consistent = false;
						break;
					}
					agreed = type;
				}
				if (consistent && agreed is not null)
				{
					local.Type = agreed;
					typed++;
					changed = true;
				}
			}
		}
		return typed;
	}

	private static TypeAnalysisContext? Infer(Isil.Instruction instruction, SystemTypesContext system, TypeAnalysisContext[] integers)
	{
		switch (instruction.OpCode)
		{
			case Isil.OpCode.CheckEqual:
			case Isil.OpCode.CheckNotEqual:
			case Isil.OpCode.CheckGreater:
			case Isil.OpCode.CheckGreaterOrEqual:
			case Isil.OpCode.CheckLess:
			case Isil.OpCode.CheckLessOrEqual:
				return instruction.Operands.Count == 3 ? system.SystemBooleanType : null;
			case Isil.OpCode.And:
			case Isil.OpCode.Or:
			case Isil.OpCode.Xor:
				if (instruction.Operands.Count == 3 && IsBoolean(instruction.Operands[1], system) && IsBoolean(instruction.Operands[2], system))
				{
					return system.SystemBooleanType;
				}
				goto case Isil.OpCode.Add;
			case Isil.OpCode.Add:
			case Isil.OpCode.Subtract:
			case Isil.OpCode.Multiply:
				return instruction.Operands.Count == 3 ? Common(integers, instruction.Operands[1], instruction.Operands[2]) : null;
			case Isil.OpCode.ShiftLeft:
			case Isil.OpCode.ShiftRight:
				// The result has the shifted value's type; the amount does not contribute.
				return instruction.Operands.Count == 3 ? Common(integers, instruction.Operands[1]) : null;
			case Isil.OpCode.Not:
			case Isil.OpCode.Move:
				// The generator lowers Not of a Boolean to "== 0", so the result stays Boolean.
				if (instruction.Operands.Count == 2 && IsBoolean(instruction.Operands[1], system))
				{
					return system.SystemBooleanType;
				}
				goto case Isil.OpCode.Negate;
			case Isil.OpCode.Negate:
				return instruction.Operands.Count == 2 ? Common(integers, instruction.Operands[1]) : null;
			default:
				return null;
		}
	}

	private static bool IsBoolean(Isil.IOperand operand, SystemTypesContext system) => operand is Isil.LocalVariable { Type: { } type } && type == system.SystemBooleanType;

	/// <summary>The single known integer type of the operands; immediates fit either width. All-immediate is unknown.</summary>
	private static TypeAnalysisContext? Common(TypeAnalysisContext[] integers, params Isil.IOperand[] operands)
	{
		TypeAnalysisContext? common = null;
		foreach (Isil.IOperand operand in operands)
		{
			switch (operand)
			{
				case Isil.Immediate:
					continue;
				case Isil.LocalVariable { Type: { } type } when integers.Contains(type):
					if (common is not null && common != type)
					{
						return null;
					}
					common = type;
					break;
				default:
					return null;
			}
		}
		return common;
	}
}
