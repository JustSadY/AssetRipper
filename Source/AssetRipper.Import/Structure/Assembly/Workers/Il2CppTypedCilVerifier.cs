using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// Abstract interpretation over ECMA-335 verification types (int32, int64, native int, F, O, &amp;, value types and
/// generic parameters) for the opcode subset the pinned IL generator emits. Every reachable path must keep a
/// consistent stack, and every store, argument, field access and return must match its declared type.
/// Anything outside that subset is rejected rather than assumed. With a resolution context, an object reference is
/// accepted only where its resolved type, a base type or an implemented interface is the declared type; the same
/// check applies to call receivers and field owners. Without one, reference-type hierarchy is not checked.
/// Passing is a typing result only.
/// </summary>
public static class Il2CppTypedCilVerifier
{
	public static Il2CppCandidateGateResult Verify(MethodDefinition method, RuntimeContext? types = null)
	{
		if (method.CilMethodBody is not { Instructions.Count: > 0 } body || method.Signature is null || method.DeclaringModule is null)
		{
			return new("RejectedTypedCil", "No body, signature or module.");
		}
		if (body.ExceptionHandlers.Count > 0)
		{
			return new("RejectedTypedCil", "Exception regions are not verified.");
		}
		try
		{
			new Interpreter(method, body, types).Run();
			return new("TypedCilChecked", null);
		}
		catch (VerificationException e)
		{
			return new("RejectedTypedCil", e.Message);
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			// An operand AsmResolver cannot interpret (for example an out-of-range argument index) is a rejection too.
			return new("RejectedTypedCil", e.GetType().Name + ": " + e.Message);
		}
	}

	private enum Kind : byte
	{
		Int32,
		Int64,
		NativeInt,
		Float,
		ObjRef,
		Null,
		ManagedPtr,
		ValueType,
		Generic,
	}

	/// <summary><see cref="Type"/> is the referenced type for ObjRef/ValueType/Generic and the pointee for ManagedPtr.</summary>
	private readonly record struct Slot(Kind Kind, TypeSignature? Type);

	private sealed class VerificationException(string message) : Exception(message);

	private sealed class Interpreter
	{
		private readonly MethodDefinition method;
		private readonly CilMethodBody body;
		private readonly RuntimeContext? types;
		private readonly ModuleDefinition module;
		private readonly IList<CilInstruction> code;
		private readonly Dictionary<int, int> indexByOffset = [];
		private readonly Slot[]?[] states;
		private readonly Queue<int> work = new();

		public Interpreter(MethodDefinition method, CilMethodBody body, RuntimeContext? types)
		{
			this.method = method;
			this.body = body;
			this.types = types;
			module = method.DeclaringModule!;
			code = body.Instructions;
			states = new Slot[]?[code.Count];
		}

		public void Run()
		{
			body.Instructions.CalculateOffsets();
			for (int i = 0; i < code.Count; i++)
			{
				indexByOffset[code[i].Offset] = i;
			}
			Merge(0, []);
			// Joins only widen object references, so each state changes a bounded number of times.
			int budget = code.Count * 16 + 64;
			while (work.TryDequeue(out int index))
			{
				if (--budget < 0)
				{
					throw new VerificationException("Stack states did not converge.");
				}
				Step(index, states[index]!);
			}
		}

		private void Step(int index, Slot[] entry)
		{
			CilInstruction instruction = code[index];
			List<Slot> stack = [.. entry];
			Execute(instruction, stack);
			if (stack.Count > body.MaxStack)
			{
				throw Fail(instruction, $"stack depth {stack.Count} exceeds MaxStack {body.MaxStack}");
			}
			switch (instruction.OpCode.FlowControl)
			{
				case CilFlowControl.Return:
				case CilFlowControl.Throw:
					return;
				case CilFlowControl.Branch:
					Merge(Target(instruction, instruction.Operand), stack);
					return;
				case CilFlowControl.ConditionalBranch:
					if (instruction.Operand is IList<ICilLabel> labels)
					{
						foreach (ICilLabel label in labels)
						{
							Merge(Target(instruction, label), stack);
						}
					}
					else
					{
						Merge(Target(instruction, instruction.Operand), stack);
					}
					Merge(Next(index), stack);
					return;
				case CilFlowControl.Next:
				case CilFlowControl.Call:
					Merge(Next(index), stack);
					return;
				default:
					throw Fail(instruction, "unsupported control flow");
			}
		}

		private int Next(int index)
		{
			if (index + 1 >= code.Count)
			{
				throw Fail(code[index], "execution falls off the end of the body");
			}
			return index + 1;
		}

		private int Target(CilInstruction instruction, object? operand)
		{
			return operand is ICilLabel label && indexByOffset.TryGetValue(label.Offset, out int target)
				? target
				: throw Fail(instruction, "branch target is not an instruction of this body");
		}

		private void Merge(int index, List<Slot> stack)
		{
			Slot[] incoming = stack.ToArray();
			if (states[index] is not { } existing)
			{
				states[index] = incoming;
				work.Enqueue(index);
				return;
			}
			if (existing.Length != incoming.Length)
			{
				throw Fail(code[index], $"incoming stack depths {existing.Length} and {incoming.Length} differ");
			}
			bool changed = false;
			Slot[] merged = new Slot[existing.Length];
			for (int i = 0; i < existing.Length; i++)
			{
				merged[i] = Join(existing[i], incoming[i]) ?? throw Fail(code[index], $"incompatible stack slot {i}: {Describe(existing[i])} and {Describe(incoming[i])}");
				changed |= !Same(merged[i], existing[i]);
			}
			if (changed)
			{
				states[index] = merged;
				work.Enqueue(index);
			}
		}

		private static Slot? Join(Slot a, Slot b)
		{
			if (Same(a, b))
			{
				return a;
			}
			if (a.Kind is Kind.ObjRef or Kind.Null && b.Kind is Kind.ObjRef or Kind.Null)
			{
				return new Slot(Kind.ObjRef, a.Kind == Kind.ObjRef && b.Kind == Kind.ObjRef ? null : a.Type ?? b.Type);
			}
			return null;
		}

		private static bool Same(Slot a, Slot b) => a.Kind == b.Kind && SameType(a.Type, b.Type);

		private static bool SameType(TypeSignature? a, TypeSignature? b) => a is null || b is null ? a is null && b is null : SignatureComparer.Default.Equals(a, b);

		private void Execute(CilInstruction instruction, List<Slot> stack)
		{
			switch (instruction.OpCode.Code)
			{
				case CilCode.Nop:
					return;
				case CilCode.Pop:
					Pop(instruction, stack);
					return;
				case CilCode.Dup:
					{
						Slot value = Pop(instruction, stack);
						stack.Add(value);
						stack.Add(value);
						return;
					}

				case CilCode.Ldarg_0:
				case CilCode.Ldarg_1:
				case CilCode.Ldarg_2:
				case CilCode.Ldarg_3:
				case CilCode.Ldarg_S:
				case CilCode.Ldarg:
					stack.Add(FromSignature(instruction, Parameter(instruction)));
					return;
				case CilCode.Ldarga_S:
				case CilCode.Ldarga:
					stack.Add(new Slot(Kind.ManagedPtr, Parameter(instruction)));
					return;
				case CilCode.Starg_S:
				case CilCode.Starg:
					Store(instruction, stack, Parameter(instruction));
					return;

				case CilCode.Ldloc_0:
				case CilCode.Ldloc_1:
				case CilCode.Ldloc_2:
				case CilCode.Ldloc_3:
				case CilCode.Ldloc_S:
				case CilCode.Ldloc:
					stack.Add(FromSignature(instruction, Local(instruction)));
					return;
				case CilCode.Ldloca_S:
				case CilCode.Ldloca:
					stack.Add(new Slot(Kind.ManagedPtr, Local(instruction)));
					return;
				case CilCode.Stloc_0:
				case CilCode.Stloc_1:
				case CilCode.Stloc_2:
				case CilCode.Stloc_3:
				case CilCode.Stloc_S:
				case CilCode.Stloc:
					Store(instruction, stack, Local(instruction));
					return;

				case CilCode.Ldnull:
					stack.Add(new Slot(Kind.Null, null));
					return;
				case CilCode.Ldstr:
					stack.Add(new Slot(Kind.ObjRef, module.CorLibTypeFactory.String));
					return;
				case CilCode.Ldc_I4_M1:
				case CilCode.Ldc_I4_0:
				case CilCode.Ldc_I4_1:
				case CilCode.Ldc_I4_2:
				case CilCode.Ldc_I4_3:
				case CilCode.Ldc_I4_4:
				case CilCode.Ldc_I4_5:
				case CilCode.Ldc_I4_6:
				case CilCode.Ldc_I4_7:
				case CilCode.Ldc_I4_8:
				case CilCode.Ldc_I4_S:
				case CilCode.Ldc_I4:
					stack.Add(new Slot(Kind.Int32, null));
					return;
				case CilCode.Ldc_I8:
					stack.Add(new Slot(Kind.Int64, null));
					return;
				case CilCode.Ldc_R4:
				case CilCode.Ldc_R8:
					stack.Add(new Slot(Kind.Float, null));
					return;

				case CilCode.Add:
				case CilCode.Sub:
				case CilCode.Mul:
				case CilCode.Div:
				case CilCode.Rem:
					Binary(instruction, stack, allowFloat: true);
					return;
				case CilCode.Div_Un:
				case CilCode.Rem_Un:
				case CilCode.And:
				case CilCode.Or:
				case CilCode.Xor:
				case CilCode.Add_Ovf:
				case CilCode.Add_Ovf_Un:
				case CilCode.Sub_Ovf:
				case CilCode.Sub_Ovf_Un:
				case CilCode.Mul_Ovf:
				case CilCode.Mul_Ovf_Un:
					Binary(instruction, stack, allowFloat: false);
					return;
				case CilCode.Shl:
				case CilCode.Shr:
				case CilCode.Shr_Un:
					{
						Slot amount = Pop(instruction, stack);
						Slot value = Pop(instruction, stack);
						Require(instruction, value.Kind is Kind.Int32 or Kind.Int64 or Kind.NativeInt && amount.Kind is Kind.Int32 or Kind.NativeInt,
							$"cannot shift {Describe(value)} by {Describe(amount)}");
						stack.Add(new Slot(value.Kind, null));
						return;
					}
				case CilCode.Neg:
					{
						Slot value = Pop(instruction, stack);
						Require(instruction, value.Kind is Kind.Int32 or Kind.Int64 or Kind.NativeInt or Kind.Float, $"cannot negate {Describe(value)}");
						stack.Add(new Slot(value.Kind, null));
						return;
					}
				case CilCode.Not:
					{
						Slot value = Pop(instruction, stack);
						Require(instruction, value.Kind is Kind.Int32 or Kind.Int64 or Kind.NativeInt, $"cannot complement {Describe(value)}");
						stack.Add(new Slot(value.Kind, null));
						return;
					}

				case CilCode.Ceq:
				case CilCode.Cgt_Un:
					Compare(instruction, stack, allowReferences: true);
					stack.Add(new Slot(Kind.Int32, null));
					return;
				case CilCode.Cgt:
				case CilCode.Clt:
				case CilCode.Clt_Un:
					Compare(instruction, stack, allowReferences: false);
					stack.Add(new Slot(Kind.Int32, null));
					return;

				case CilCode.Conv_I1:
				case CilCode.Conv_I2:
				case CilCode.Conv_I4:
				case CilCode.Conv_U1:
				case CilCode.Conv_U2:
				case CilCode.Conv_U4:
					Convert(instruction, stack, Kind.Int32);
					return;
				case CilCode.Conv_I8:
				case CilCode.Conv_U8:
					Convert(instruction, stack, Kind.Int64);
					return;
				case CilCode.Conv_I:
				case CilCode.Conv_U:
					Convert(instruction, stack, Kind.NativeInt);
					return;
				case CilCode.Conv_R4:
				case CilCode.Conv_R8:
				case CilCode.Conv_R_Un:
					Convert(instruction, stack, Kind.Float);
					return;

				case CilCode.Br_S:
				case CilCode.Br:
					return;
				case CilCode.Brtrue_S:
				case CilCode.Brtrue:
				case CilCode.Brfalse_S:
				case CilCode.Brfalse:
					{
						Slot value = Pop(instruction, stack);
						Require(instruction, value.Kind is not (Kind.Float or Kind.ValueType or Kind.Generic), $"cannot test {Describe(value)}");
						return;
					}
				case CilCode.Beq_S:
				case CilCode.Beq:
				case CilCode.Bne_Un_S:
				case CilCode.Bne_Un:
				case CilCode.Bgt_Un_S:
				case CilCode.Bgt_Un:
					Compare(instruction, stack, allowReferences: true);
					return;
				case CilCode.Bge_S:
				case CilCode.Bge:
				case CilCode.Bgt_S:
				case CilCode.Bgt:
				case CilCode.Ble_S:
				case CilCode.Ble:
				case CilCode.Blt_S:
				case CilCode.Blt:
				case CilCode.Bge_Un_S:
				case CilCode.Bge_Un:
				case CilCode.Ble_Un_S:
				case CilCode.Ble_Un:
				case CilCode.Blt_Un_S:
				case CilCode.Blt_Un:
					Compare(instruction, stack, allowReferences: false);
					return;
				case CilCode.Switch:
					{
						Slot value = Pop(instruction, stack);
						Require(instruction, value.Kind is Kind.Int32 or Kind.NativeInt, $"cannot switch on {Describe(value)}");
						return;
					}

				case CilCode.Ret:
					{
						TypeSignature returnType = method.Signature!.ReturnType;
						if (returnType.ElementType != ElementType.Void)
						{
							Store(instruction, stack, returnType);
						}
						Require(instruction, stack.Count == 0, $"{stack.Count} values remain on the stack at return");
						return;
					}

				case CilCode.Call:
				case CilCode.Callvirt:
					Call(instruction, stack, constructor: false);
					return;
				case CilCode.Newobj:
					Call(instruction, stack, constructor: true);
					return;
				case CilCode.Throw:
					{
						Slot value = Pop(instruction, stack);
						Require(instruction, value.Kind is Kind.ObjRef or Kind.Null, $"cannot throw {Describe(value)}");
						return;
					}

				case CilCode.Ldfld:
				case CilCode.Ldflda:
					{
						TypeSignature fieldType = Field(instruction, out bool isStatic);
						Require(instruction, !isStatic, "instance field access to a static field");
						RequireOwner(instruction, RequireInstance(instruction, Pop(instruction, stack)), ((IFieldDescriptor)instruction.Operand!).DeclaringType, "field owner");
						stack.Add(instruction.OpCode.Code == CilCode.Ldflda ? new Slot(Kind.ManagedPtr, fieldType) : FromSignature(instruction, fieldType));
						return;
					}
				case CilCode.Stfld:
					{
						TypeSignature fieldType = Field(instruction, out bool isStatic);
						Require(instruction, !isStatic, "instance field store to a static field");
						Store(instruction, stack, fieldType);
						RequireOwner(instruction, RequireInstance(instruction, Pop(instruction, stack)), ((IFieldDescriptor)instruction.Operand!).DeclaringType, "field owner");
						return;
					}
				case CilCode.Ldsfld:
				case CilCode.Ldsflda:
					{
						TypeSignature fieldType = Field(instruction, out bool isStatic);
						Require(instruction, isStatic, "static field access to an instance field");
						stack.Add(instruction.OpCode.Code == CilCode.Ldsflda ? new Slot(Kind.ManagedPtr, fieldType) : FromSignature(instruction, fieldType));
						return;
					}
				case CilCode.Stsfld:
					{
						TypeSignature fieldType = Field(instruction, out bool isStatic);
						Require(instruction, isStatic, "static field store to an instance field");
						Store(instruction, stack, fieldType);
						return;
					}

				case CilCode.Newarr:
					{
						RequireIndex(instruction, Pop(instruction, stack));
						TypeSignature element = ArrayElement(instruction, instruction.Operand);
						stack.Add(new Slot(Kind.ObjRef, element.MakeSzArrayType()));
						return;
					}
				case CilCode.Ldlen:
					RequireArray(instruction, Pop(instruction, stack));
					stack.Add(new Slot(Kind.NativeInt, null));
					return;
				case CilCode.Ldelema:
					{
						TypeSignature element = ArrayElement(instruction, instruction.Operand);
						RequireIndex(instruction, Pop(instruction, stack));
						RequireArray(instruction, Pop(instruction, stack));
						stack.Add(new Slot(Kind.ManagedPtr, element));
						return;
					}
				case CilCode.Ldelem:
					{
						TypeSignature element = ArrayElement(instruction, instruction.Operand);
						RequireIndex(instruction, Pop(instruction, stack));
						RequireArray(instruction, Pop(instruction, stack));
						stack.Add(FromSignature(instruction, element));
						return;
					}
				case CilCode.Stelem:
					{
						TypeSignature element = ArrayElement(instruction, instruction.Operand);
						Store(instruction, stack, element);
						RequireIndex(instruction, Pop(instruction, stack));
						RequireArray(instruction, Pop(instruction, stack));
						return;
					}

				case CilCode.Ldftn:
					stack.Add(new Slot(Kind.NativeInt, null));
					return;

				default:
					throw Fail(instruction, "unsupported opcode");
			}
		}

		private void Binary(CilInstruction instruction, List<Slot> stack, bool allowFloat)
		{
			Slot right = Pop(instruction, stack);
			Slot left = Pop(instruction, stack);
			Kind? result = (left.Kind, right.Kind) switch
			{
				(Kind.Int32, Kind.Int32) => Kind.Int32,
				(Kind.Int64, Kind.Int64) => Kind.Int64,
				(Kind.NativeInt, Kind.Int32) or (Kind.Int32, Kind.NativeInt) or (Kind.NativeInt, Kind.NativeInt) => Kind.NativeInt,
				(Kind.Float, Kind.Float) when allowFloat => Kind.Float,
				_ => null,
			};
			stack.Add(new Slot(result ?? throw Fail(instruction, $"invalid operands {Describe(left)} and {Describe(right)}"), null));
		}

		private void Compare(CilInstruction instruction, List<Slot> stack, bool allowReferences)
		{
			Slot right = Pop(instruction, stack);
			Slot left = Pop(instruction, stack);
			bool valid = (left.Kind, right.Kind) switch
			{
				(Kind.Int32, Kind.Int32) or (Kind.Int64, Kind.Int64) or (Kind.Float, Kind.Float) => true,
				(Kind.NativeInt, Kind.Int32) or (Kind.Int32, Kind.NativeInt) or (Kind.NativeInt, Kind.NativeInt) => true,
				(Kind.ObjRef or Kind.Null, Kind.ObjRef or Kind.Null) => allowReferences,
				(Kind.ManagedPtr, Kind.ManagedPtr) => allowReferences,
				_ => false,
			};
			Require(instruction, valid, $"cannot compare {Describe(left)} with {Describe(right)}");
		}

		private void Convert(CilInstruction instruction, List<Slot> stack, Kind target)
		{
			Slot value = Pop(instruction, stack);
			Require(instruction, value.Kind is Kind.Int32 or Kind.Int64 or Kind.NativeInt or Kind.Float, $"cannot convert {Describe(value)}");
			stack.Add(new Slot(target, null));
		}

		private void Call(CilInstruction instruction, List<Slot> stack, bool constructor)
		{
			if (instruction.Operand is not IMethodDescriptor target || target.Signature is not { } signature)
			{
				throw Fail(instruction, "call target is not a method");
			}
			// Generic instantiation would need substitution of !0/!!0; keep those outside the verified subset.
			if (target is MethodSpecification || target.DeclaringType is TypeSpecification)
			{
				throw Fail(instruction, "generic call targets are not verified");
			}
			if (signature.HasSentinelParameterTypes || signature.ExplicitThis)
			{
				throw Fail(instruction, "vararg or explicit-this calls are not verified");
			}
			for (int i = signature.ParameterTypes.Count - 1; i >= 0; i--)
			{
				Store(instruction, stack, signature.ParameterTypes[i]);
			}
			if (constructor)
			{
				Require(instruction, signature.HasThis && target.Name == ".ctor", "newobj target is not an instance constructor");
				stack.Add(target.DeclaringType is TypeDefinition { IsValueType: true } valueType
					? new Slot(Kind.ValueType, valueType.ToTypeSignature(true))
					: new Slot(Kind.ObjRef, target.DeclaringType is ITypeDefOrRef owner ? owner.ToTypeSignature(false) : null));
				return;
			}
			if (signature.HasThis)
			{
				Slot self = Pop(instruction, stack);
				bool valid = instruction.OpCode.Code == CilCode.Callvirt
					? self.Kind is Kind.ObjRef or Kind.Null
					: self.Kind is Kind.ObjRef or Kind.Null or Kind.ManagedPtr;
				Require(instruction, valid, $"invalid 'this' {Describe(self)}");
				RequireOwner(instruction, self, target.DeclaringType, "receiver");
			}
			if (signature.ReturnType.ElementType != ElementType.Void)
			{
				stack.Add(FromSignature(instruction, signature.ReturnType));
			}
		}

		private TypeSignature Field(CilInstruction instruction, out bool isStatic)
		{
			if (instruction.Operand is not IFieldDescriptor field || field.Signature is not { } signature)
			{
				throw Fail(instruction, "field operand is not a field");
			}
			if (field.DeclaringType is TypeSpecification)
			{
				throw Fail(instruction, "fields of generic instances are not verified");
			}
			// Field signatures carry no HASTHIS bit; only a local definition says whether the field is static.
			isStatic = field is FieldDefinition definition ? definition.IsStatic : instruction.OpCode.Code is CilCode.Ldsfld or CilCode.Ldsflda or CilCode.Stsfld;
			return signature.FieldType;
		}

		private Slot RequireInstance(CilInstruction instruction, Slot instance)
		{
			Require(instruction, instance.Kind is Kind.ObjRef or Kind.Null or Kind.ManagedPtr or Kind.ValueType, $"invalid field owner {Describe(instance)}");
			return instance;
		}

		/// <summary>An object reference used as receiver or field owner must be of the member's declaring type or derive from it.</summary>
		private void RequireOwner(CilInstruction instruction, Slot instance, ITypeDescriptor? owner, string role)
		{
			if (instance.Kind != Kind.ObjRef || owner is not ITypeDefOrRef declaring || declaring is TypeDefinition { IsValueType: true })
			{
				return;
			}
			TypeSignature expected = declaring.ToTypeSignature(false);
			Require(instruction, ReferenceAssignable(instance.Type, expected), $"{role} {Describe(instance)} is not a {expected.FullName}");
		}

		private bool ReferenceAssignable(TypeSignature? value, TypeSignature? target)
		{
			if (types is null || target is null || target.ElementType == ElementType.Object)
			{
				return true;
			}
			if (value is null)
			{
				return false; // A merged or unknown reference cannot be shown to have the declared type.
			}
			if (SameType(value, target))
			{
				return true;
			}
			if (value is SzArrayTypeSignature array)
			{
				if (target is SzArrayTypeSignature targetArray)
				{
					bool references = FromElement(array.BaseType) && FromElement(targetArray.BaseType);
					return references ? ReferenceAssignable(array.BaseType, targetArray.BaseType) : SameType(array.BaseType, targetArray.BaseType);
				}
				return target.FullName == "System.Array";
			}
			if (value.ElementType == ElementType.GenericInst || target.ElementType == ElementType.GenericInst)
			{
				return false; // Only identical instantiations are accepted.
			}
			TypeDefinition? expected = Resolve(target);
			if (expected is null)
			{
				return false;
			}
			int depth = 0;
			for (TypeDefinition? current = Resolve(value); current is not null && depth < 64; current = current.BaseType is { } baseType ? Resolve(baseType) : null, depth++)
			{
				if (ReferenceEquals(current, expected) || Implements(current, expected, 0))
				{
					return true;
				}
			}
			return false;
		}

		private bool Implements(TypeDefinition type, TypeDefinition expected, int depth)
		{
			if (depth > 16)
			{
				return false;
			}
			foreach (InterfaceImplementation implementation in type.Interfaces)
			{
				if (implementation.Interface is not { } candidate)
				{
					continue;
				}
				TypeDefinition? definition = Resolve(candidate);
				if (ReferenceEquals(definition, expected) || definition is not null && Implements(definition, expected, depth + 1))
				{
					return true;
				}
			}
			return false;
		}

		private static bool FromElement(TypeSignature element) => element.ElementType is ElementType.Class or ElementType.Object or ElementType.String or ElementType.SzArray or ElementType.Array;

		private TypeDefinition? Resolve(ITypeDescriptor type)
		{
			if (type is TypeDefinition definition)
			{
				return definition;
			}
			try
			{
				return type.TryResolve(types!, out TypeDefinition? resolved) ? resolved : null;
			}
			catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
			{
				return null;
			}
		}

		private void RequireIndex(CilInstruction instruction, Slot index)
		{
			Require(instruction, index.Kind is Kind.Int32 or Kind.NativeInt, $"invalid array index or length {Describe(index)}");
		}

		private void RequireArray(CilInstruction instruction, Slot array)
		{
			Require(instruction, array.Kind is Kind.Null || array.Kind is Kind.ObjRef && array.Type is null or SzArrayTypeSignature, $"invalid array {Describe(array)}");
		}

		private TypeSignature ArrayElement(CilInstruction instruction, object? operand)
		{
			return operand switch
			{
				TypeSpecification { Signature: { } signature } => signature,
				ITypeDefOrRef type when module.CorLibTypeFactory.FromType(type) is { } corLib => corLib,
				TypeDefinition definition => definition.ToTypeSignature(definition.IsValueType),
				_ => throw Fail(instruction, "unresolved array element type"),
			};
		}

		private TypeSignature Parameter(CilInstruction instruction)
		{
			return instruction.GetParameter(method.Parameters).ParameterType;
		}

		private TypeSignature Local(CilInstruction instruction)
		{
			return instruction.GetLocalVariable(body.LocalVariables).VariableType;
		}

		private void Store(CilInstruction instruction, List<Slot> stack, TypeSignature target)
		{
			Slot value = Pop(instruction, stack);
			Require(instruction, Assignable(instruction, value, target), $"cannot store {Describe(value)} into {target.FullName}");
		}

		private bool Assignable(CilInstruction instruction, Slot value, TypeSignature targetSignature)
		{
			Slot target = FromSignature(instruction, targetSignature);
			return target.Kind switch
			{
				Kind.Int32 => value.Kind is Kind.Int32 or Kind.NativeInt,
				Kind.Int64 => value.Kind is Kind.Int64,
				Kind.NativeInt => value.Kind is Kind.NativeInt or Kind.Int32,
				Kind.Float => value.Kind is Kind.Float,
				Kind.ObjRef => value.Kind is Kind.Null || value.Kind is Kind.ObjRef && ReferenceAssignable(value.Type, target.Type),
				Kind.ManagedPtr => value.Kind is Kind.ManagedPtr && SameType(value.Type, target.Type),
				Kind.ValueType or Kind.Generic => value.Kind == target.Kind && SameType(value.Type, target.Type),
				_ => false,
			};
		}

		private Slot FromSignature(CilInstruction instruction, TypeSignature signature)
		{
			TypeSignature type = signature;
			while (type is CustomModifierTypeSignature or PinnedTypeSignature)
			{
				type = ((TypeSpecificationSignature)type).BaseType;
			}
			switch (type.ElementType)
			{
				case ElementType.Boolean:
				case ElementType.Char:
				case ElementType.I1:
				case ElementType.U1:
				case ElementType.I2:
				case ElementType.U2:
				case ElementType.I4:
				case ElementType.U4:
					return new Slot(Kind.Int32, null);
				case ElementType.I8:
				case ElementType.U8:
					return new Slot(Kind.Int64, null);
				case ElementType.I:
				case ElementType.U:
				case ElementType.Ptr:
				case ElementType.FnPtr:
					return new Slot(Kind.NativeInt, null);
				case ElementType.R4:
				case ElementType.R8:
					return new Slot(Kind.Float, null);
				case ElementType.String:
				case ElementType.Object:
				case ElementType.Class:
				case ElementType.SzArray:
				case ElementType.Array:
					return new Slot(Kind.ObjRef, type);
				case ElementType.GenericInst:
					return new Slot(type.IsValueType ? Kind.ValueType : Kind.ObjRef, type);
				case ElementType.ValueType:
					// Enums are their underlying integer type when the definition is local; otherwise they stay opaque.
					if (type is TypeDefOrRefSignature { Type: TypeDefinition { IsEnum: true } enumType } && enumType.GetEnumUnderlyingType() is { } underlying)
					{
						return FromSignature(instruction, underlying);
					}
					return new Slot(Kind.ValueType, type);
				case ElementType.Var:
				case ElementType.MVar:
					return new Slot(Kind.Generic, type);
				case ElementType.ByRef:
					return new Slot(Kind.ManagedPtr, ((ByReferenceTypeSignature)type).BaseType);
				default:
					throw Fail(instruction, "unsupported type " + type.FullName);
			}
		}

		private Slot Pop(CilInstruction instruction, List<Slot> stack)
		{
			if (stack.Count == 0)
			{
				throw Fail(instruction, "stack underflow");
			}
			Slot value = stack[^1];
			stack.RemoveAt(stack.Count - 1);
			return value;
		}

		private static void Require(CilInstruction instruction, bool condition, string reason)
		{
			if (!condition)
			{
				throw Fail(instruction, reason);
			}
		}

		private static VerificationException Fail(CilInstruction instruction, string reason) => new($"IL_{instruction.Offset:X4} {instruction.OpCode.Mnemonic}: {reason}.");

		private static string Describe(Slot slot) => slot.Type is null ? slot.Kind.ToString() : $"{slot.Kind}({slot.Type.FullName})";
	}
}
