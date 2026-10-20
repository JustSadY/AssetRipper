using Cpp2IL.Core.Model.Contexts;
using Isil = Cpp2IL.Core.ISIL;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// Rejects analyzed ISIL that the pinned IL generator would lower without a visible placeholder but with
/// different meaning: dropped or redirected memory stores, dereferences turned into plain local loads,
/// default values for missing call arguments, lost return values, unresolved constructors and type operands
/// that become fabricated <c>newobj</c> instructions. Passing proves none of the remaining lowering is correct.
/// </summary>
internal static class Il2CppIsilGate
{
	public static Il2CppCandidateGateResult Check(MethodAnalysisContext context)
	{
		if (context.ControlFlowGraph is not { } graph)
		{
			return new("RejectedIsil", "No control flow graph after analysis.");
		}
		if (context.AnalysisWarnings.Count > 0)
		{
			return new("RejectedIsil", "Analysis warning: " + context.AnalysisWarnings[0]);
		}
		List<Isil.Instruction> instructions = graph.Blocks.SelectMany(block => block.Instructions).ToList();
		foreach (Isil.Instruction instruction in instructions)
		{
			if (Reject(context, graph, instruction) is { } reason)
			{
				return new("RejectedIsil", $"{reason} ({instruction})");
			}
		}
		return new("IsilChecked", null);
	}

	private static string? Reject(MethodAnalysisContext context, Cpp2IL.Core.Graphs.ISILControlFlowGraph graph, Isil.Instruction instruction)
	{
		switch (instruction.OpCode)
		{
			case Isil.OpCode.Nop:
			case Isil.OpCode.Jump:
				return null;
			case Isil.OpCode.Invalid:
			case Isil.OpCode.NotImplemented:
			case Isil.OpCode.Interrupt:
			case Isil.OpCode.Phi:
			case Isil.OpCode.IndirectCall:
			case Isil.OpCode.IndirectJump:
			case Isil.OpCode.ShiftStack:
				return "UnsupportedIsil:" + instruction.OpCode;

			case Isil.OpCode.Move:
				if (instruction.Operands.Count != 2)
				{
					return "MalformedMove";
				}
				return instruction.Operands[0] is Isil.MemoryOperand ? "MemoryStore" : Store(instruction.Operands[0]) ?? Load(instruction.Operands[1]);

			case Isil.OpCode.Add:
			case Isil.OpCode.Subtract:
			case Isil.OpCode.Multiply:
			case Isil.OpCode.Divide:
			case Isil.OpCode.ShiftLeft:
			case Isil.OpCode.ShiftRight:
			case Isil.OpCode.And:
			case Isil.OpCode.Or:
			case Isil.OpCode.Xor:
			case Isil.OpCode.CheckEqual:
			case Isil.OpCode.CheckGreater:
			case Isil.OpCode.CheckLess:
			case Isil.OpCode.CheckNotEqual:
			case Isil.OpCode.CheckGreaterOrEqual:
			case Isil.OpCode.CheckLessOrEqual:
				if (instruction.Operands.Count != 3)
				{
					return "MalformedBinaryOperation";
				}
				return Store(instruction.Operands[0]) ?? Load(instruction.Operands[1]) ?? Load(instruction.Operands[2]);

			case Isil.OpCode.Not:
			case Isil.OpCode.Negate:
				if (instruction.Operands.Count != 2)
				{
					return "MalformedUnaryOperation";
				}
				return Store(instruction.Operands[0]) ?? Load(instruction.Operands[1]);

			case Isil.OpCode.ConditionalJump:
				return instruction.Operands.Count != 2 ? "MalformedConditionalJump" : Load(instruction.Operands[1]);

			case Isil.OpCode.Return:
				if (context.IsVoid)
				{
					return null;
				}
				return instruction.Operands.Count != 1 ? "LostReturnValue" : Load(instruction.Operands[0]);

			case Isil.OpCode.Call:
			case Isil.OpCode.CallVoid:
				return Call(context, instruction);

			case Isil.OpCode.Newobj:
				// The fused constructor call is checked as an ordinary CallVoid when the loop reaches it.
				return FindConstructorCall(graph, instruction) is null ? "UnresolvedConstructor" : Store(instruction.Operands[0]);

			case Isil.OpCode.NewArr:
				return instruction.Operands is [var destination, SzArrayTypeAnalysisContext { ElementType: not null }, var length]
					? Store(destination) ?? Load(length)
					: "UnresolvedArrayType";

			case Isil.OpCode.Throw:
				// The generator throws a fresh parameterless instance of the type; any other operand becomes "throw null".
				return instruction.Operands is [TypeAnalysisContext type] && type.Methods.Any(m => m.Name == ".ctor" && m.Parameters.Count == 0)
					? null
					: "UnresolvedThrow";

			default:
				return "UnknownIsil:" + instruction.OpCode;
		}
	}

	private static string? Call(MethodAnalysisContext context, Isil.Instruction instruction)
	{
		if (instruction.Operands.Count == 0 || instruction.Operands[0] is not MethodAnalysisContext target)
		{
			if (instruction.Operands.Count > 0 && instruction.Operands[0] is Isil.Immediate address && NameAt(context, address.UnsignedValue) is { } name)
			{
				// A native throw unwinds through C++ exception tables that ISIL does not model, so no catch region exists.
				return name.Contains("raise_exception", StringComparison.OrdinalIgnoreCase) || name.Contains("Throw", StringComparison.Ordinal)
					? $"NativeExceptionThrow:{name}"
					: $"UnresolvedCallTarget:0x{address.UnsignedValue:X} ({name})";
			}
			return "UnresolvedCallTarget";
		}
		bool returnsValue = instruction.OpCode == Isil.OpCode.Call;
		int thisIndex = returnsValue ? 2 : 1;
		if (returnsValue)
		{
			if (instruction.Operands.Count < 2)
			{
				return "MissingCallDestination";
			}
			if (Store(instruction.Operands[1]) is { } badDestination)
			{
				return badDestination;
			}
		}
		if (!target.IsStatic)
		{
			if (instruction.Operands.Count - 1 < thisIndex)
			{
				return "MissingThisArgument";
			}
			if (Load(instruction.Operands[thisIndex]) is { } badThis)
			{
				return badThis;
			}
		}
		int firstArgument = returnsValue ? (target.IsStatic ? 2 : 3) : (target.IsStatic ? 1 : 2);
		if (instruction.Operands.Count - firstArgument < target.Parameters.Count)
		{
			return "MissingCallArguments"; // The generator would push defaults for the missing ones.
		}
		for (int i = 0; i < target.Parameters.Count; i++)
		{
			Isil.IOperand argument = instruction.Operands[firstArgument + i];
			// A runtime method info is a real value only as the native-pointer argument of a delegate constructor.
			if (argument is RuntimeMethodInfoAnalysisContext && target.Parameters[i].ParameterType.FullName == "System.IntPtr")
			{
				continue;
			}
			if (Load(argument) is { } badArgument)
			{
				return badArgument;
			}
		}
		return null;
	}

	/// <summary>Names a native call target from the provider's throw helpers, key IL2CPP functions or binary exports.</summary>
	private static string? NameAt(MethodAnalysisContext context, ulong address)
	{
		ApplicationAnalysisContext application = context.AppContext;
		try
		{
			if (application.ThrowHelperNamesByAddress.TryGetValue(address, out string? helper) && helper is not null)
			{
				return helper;
			}
			foreach (KeyValuePair<string, ulong> pair in application.GetOrCreateKeyFunctionAddresses().Pairs)
			{
				if (pair.Value == address)
				{
					return pair.Key;
				}
			}
			return application.Binary.TryGetExportedFunctionName(address, out string? exported) ? exported : null;
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>Operands the generator can load with their own meaning.</summary>
	private static string? Load(Isil.IOperand operand) => operand switch
	{
		Isil.Immediate or Isil.FloatLiteral or Isil.DoubleLiteral or Isil.StringLiteral => null,
		Isil.LocalVariable => null,
		Isil.FieldReference => null,
		Isil.ArrayLength { Array.Type: SzArrayTypeAnalysisContext } => null,
		Isil.ArrayAccess { Array.Type: SzArrayTypeAnalysisContext } access => Load(access.Index),
		Isil.AddressOf { Target: Isil.LocalVariable } => null,
		Isil.AddressOf { Target: Isil.ArrayAccess { Array.Type: SzArrayTypeAnalysisContext } access } => Load(access.Index),
		// Every remaining memory form is either a placeholder or "[local]", which the generator loads as the local itself.
		Isil.MemoryOperand => "MemoryLoad",
		RuntimeMethodInfoAnalysisContext => "RuntimeMethodInfoValue",
		TypeAnalysisContext => "TypeOperandValue",
		Isil.Register or Isil.StackOffset => "UnresolvedStorage",
		_ => "UnsupportedOperand:" + operand.GetType().Name,
	};

	/// <summary>Destinations the generator can store to.</summary>
	private static string? Store(Isil.IOperand operand) => operand switch
	{
		Isil.LocalVariable => null,
		Isil.FieldReference => null,
		Isil.ArrayAccess { Array.Type: SzArrayTypeAnalysisContext } access => Load(access.Index),
		Isil.MemoryOperand => "MemoryStore",
		_ => "UnsupportedDestination:" + operand.GetType().Name,
	};

	// Same search as the pinned generator: the constructor call follows the newobj in the same block.
	private static Isil.Instruction? FindConstructorCall(Cpp2IL.Core.Graphs.ISILControlFlowGraph graph, Isil.Instruction newobj)
	{
		if (newobj.Operands.Count == 0)
		{
			return null;
		}
		Isil.IOperand created = newobj.Operands[0];
		foreach (Cpp2IL.Core.Graphs.Block block in graph.Blocks)
		{
			int index = block.Instructions.IndexOf(newobj);
			if (index < 0)
			{
				continue;
			}
			for (int i = index + 1; i < block.Instructions.Count; i++)
			{
				Isil.Instruction candidate = block.Instructions[i];
				if (candidate is { OpCode: Isil.OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, _, ..] }
					&& ReferenceEquals(candidate.Operands[1], created))
				{
					return candidate;
				}
			}
			return null;
		}
		return null;
	}
}
