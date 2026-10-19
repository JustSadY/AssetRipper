using System.Reflection;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record FixtureCase(string Name, string Method, object?[] Arguments, Observation Expected, Type? GenericArgument = null);

internal static class ManagedFixtureOracle
{
	public static IReadOnlyList<FixtureCase> Cases { get; } =
	[
		new("state-events-positive", "StateAndEvents", [5, 3], new("[8,9,18,1,16]", "[5,3]")),
		new("state-events-negative", "StateAndEvents", [-5, -3], new("[-8,-7,-14,1,-16]", "[-5,-3]")),
		new("clamp-negative", "Clamp", [-7], new("0", "[-7]")),
		new("clamp-zero", "Clamp", [0], new("0", "[0]")),
		new("clamp-positive", "Clamp", [19], new("19", "[19]")),
		new("loop-empty", "Sum", [0], new("0", "[0]")),
		new("loop-ten", "Sum", [10], new("45", "[10]")),
		new("iterator-empty", "Sequence", [0], new("[]", "[0]")),
		new("iterator-five", "Sequence", [5], new("[0,1,4,9,16]", "[5]")),
		new("iterator-throw", "Sequence", [-1], new("null", "[-1]", "System.ArgumentOutOfRangeException", "count")),
		new("async-result", "Asynchronous", [7, false], new("21", "[7,false]")),
		new("async-throw", "Asynchronous", [-2, true], new("null", "[-2,true]", "System.ArgumentOutOfRangeException", "value")),
		new("generic-first", "Choose", [true, "left", "right"], new("\"left\"", "[true,\"left\",\"right\"]"), typeof(string)),
		new("generic-null", "Choose", [false, "left", null], new("null", "[false,\"left\",null]"), typeof(string)),
		new("explicit-interface", "InterfaceDispatch", [9], new("16", "[9]")),
		new("ref-out", "RefOut", [3, 5, null], new("7", "[3,8,16]")),
		new("try-finally", "ExceptionFlow", [12, 3], new("[4,0,1]", "[12,3]")),
		new("catch-finally", "ExceptionFlow", [12, 0], new("[0,1,1]", "[12,0]")),
		new("unicode", "Ölç", [41], new("42", "[41]")),
		new("checked-overflow", "Ölç", [int.MaxValue], new("null", "[2147483647]", "System.OverflowException")),
	];

	public static BehaviorCheck Compare(Assembly original, Assembly recompiled, FixtureCase test)
	{
		Observation before = Observe(original, test);
		Observation after = Observe(recompiled, test);
		// The hand-written oracle also checks the original fixture, preventing two equally wrong results from passing.
		bool oracleMatched = before.ReturnJson == test.Expected.ReturnJson && before.ArgumentsJson == test.Expected.ArgumentsJson
			&& before.ExceptionType == test.Expected.ExceptionType && before.ExceptionParameter == test.Expected.ExceptionParameter;
		return new(test.Name, test.Expected, before, after, oracleMatched, before == after);
	}

	private static Observation Observe(Assembly assembly, FixtureCase test)
	{
		Type type = assembly.GetType("AssetRipper.ManagedFixture.Sample", throwOnError: true)!;
		MethodInfo method = type.GetMethod(test.Method, BindingFlags.Public | BindingFlags.Static)
			?? throw new MissingMethodException(type.FullName, test.Method);
		if (test.GenericArgument is not null) method = method.MakeGenericMethod(test.GenericArgument);
		object?[] arguments = (object?[])test.Arguments.Clone();
		try
		{
			object? value = method.Invoke(null, arguments);
			if (value is Task<int> task) value = task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
			if (value is IEnumerable<int> sequence) value = sequence.ToArray();
			return new(JsonSerializer.Serialize(value), JsonSerializer.Serialize(arguments));
		}
		catch (Exception exception) when (AssetRipper.IO.Files.Exceptions.ExceptionPolicy.IsRecoverable(exception))
		{
			while (exception is TargetInvocationException { InnerException: not null }) exception = exception.InnerException;
			return new("null", JsonSerializer.Serialize(arguments), exception.GetType().FullName,
				(exception as ArgumentException)?.ParamName, exception.Message);
		}
	}

	public static ContractCheck CompareContract(Assembly original, Assembly recompiled)
	{
		string[] before = PublicContract(original);
		string[] after = PublicContract(recompiled);
		string[] missing = before.Except(after, StringComparer.Ordinal).ToArray();
		string[] unexpected = after.Except(before, StringComparer.Ordinal).ToArray();
		return new(missing.Length == 0 && unexpected.Length == 0 ? CheckStatus.Passed : CheckStatus.Failed, missing, unexpected);
	}

	private static string[] PublicContract(Assembly assembly)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		List<string> result = [];
		foreach (Type type in assembly.GetExportedTypes())
		{
			string owner = TypeName(type);
			result.Add($"type {owner} base={TypeName(type.BaseType)} interface={type.IsInterface} abstract={type.IsAbstract} sealed={type.IsSealed}{Constraints(type.GetGenericArguments())}");
			foreach (Type contract in type.GetInterfaces()) result.Add($"interface {owner}: {TypeName(contract)}");
			foreach (FieldInfo field in type.GetFields(flags))
				result.Add($"field {owner}.{field.Name}: {TypeName(field.FieldType)} static={field.IsStatic} readonly={field.IsInitOnly} literal={field.IsLiteral}");
			foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
			{
				string returns = method is MethodInfo info ? TypeName(info.ReturnType) : "void";
				string parameters = string.Join(", ", method.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name} in={p.IsIn} out={p.IsOut} optional={p.IsOptional}"));
				Type[] generic = method.IsGenericMethod ? method.GetGenericArguments() : [];
				result.Add($"method {owner}.{method.Name}`{generic.Length}({parameters}): {returns} static={method.IsStatic} virtual={method.IsVirtual} final={method.IsFinal} abstract={method.IsAbstract}{Constraints(generic)}");
			}
			foreach (PropertyInfo property in type.GetProperties(flags)) result.Add($"property {owner}.{property.Name}: {TypeName(property.PropertyType)} get={property.GetMethod?.IsPublic == true} set={property.SetMethod?.IsPublic == true}");
			foreach (EventInfo eventInfo in type.GetEvents(flags)) result.Add($"event {owner}.{eventInfo.Name}: {TypeName(eventInfo.EventHandlerType)}");
		}
		return result.Order(StringComparer.Ordinal).ToArray();
	}

	private static string Constraints(IEnumerable<Type> parameters) => string.Concat(parameters.Where(t => t.IsGenericParameter).Select(t =>
		$" where {TypeName(t)}:{t.GenericParameterAttributes}[{string.Join(',', t.GetGenericParameterConstraints().Select(TypeName).Order(StringComparer.Ordinal))}]"));

	private static string TypeName(Type? type)
	{
		if (type is null) return "<none>";
		if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
		if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
		if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition;
		return type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(',', type.GetGenericArguments().Select(TypeName)) + ">" : type.FullName!;
	}
}
