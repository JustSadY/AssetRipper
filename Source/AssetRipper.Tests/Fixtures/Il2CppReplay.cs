// Runs only in a bounded child process, against the configured source-owned fixture.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

public static class Il2CppReplay
{
	public static int Main(string[] args)
	{
		if (args.Length != 4) return 2;
		if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[0]))) != args[3]) return 3;
		var oracle = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement.GetProperty("Cases");
		var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
		var outcomes = oracle.EnumerateArray().Select(c => Run(assembly, c)).ToArray();
		File.WriteAllText(args[2], JsonSerializer.Serialize(outcomes, new JsonSerializerOptions { WriteIndented = true }));
		// The same Counter observations through the abstract base and the interface prove the recovered override is wired
		// into the output's virtual and interface dispatch, not only callable directly.
		var dispatch = oracle.EnumerateArray().Where(c => c.GetProperty("Method").GetString() == "Step")
			.SelectMany(c => new[] { Dispatch(assembly, c, "AbstractCounter", "virtual"), Dispatch(assembly, c, "ICounter", "interface") })
			.Where(outcome => outcome != null).ToArray();
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2])), "dispatch.json"), JsonSerializer.Serialize(dispatch, new JsonSerializerOptions { WriteIndented = true }));
		return 0;
	}
	private static object Dispatch(Assembly assembly, JsonElement c, string declaringType, string kind)
	{
		// A stripped build may remove an interface nobody calls through; there is no dispatch surface to check then.
		if (assembly.GetType("AssetRipper.Il2CppFixture." + declaringType, false) == null) return null;
		string id = c.GetProperty("Id").GetString() + "/" + kind;
		int a = c.GetProperty("A").GetInt32(), b = c.GetProperty("B").GetInt32(), expected = c.GetProperty("Value").GetInt32();
		int? actual = null; string error = null;
		try
		{
			Type counter = assembly.GetType("AssetRipper.Il2CppFixture.Counter", true);
			MethodInfo method = assembly.GetType("AssetRipper.Il2CppFixture." + declaringType, true).GetMethod("Step", BindingFlags.Public | BindingFlags.Instance);
			object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(counter);
			counter.GetField("value").SetValue(instance, a);
			actual = (int)method.Invoke(instance, new object[] { b });
		}
		catch (Exception e) { var cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; error = cause.GetType().FullName + ": " + cause.Message; }
		return new { Id = id, Method = "Step", Expected = expected, Actual = actual, Error = error, Matched = error == null && actual == expected };
	}
	private static object Run(Assembly assembly, JsonElement c)
	{
		string id = c.GetProperty("Id").GetString(); string methodName = c.GetProperty("Method").GetString();
		int a = c.GetProperty("A").GetInt32(), b = c.GetProperty("B").GetInt32(), expected = c.GetProperty("Value").GetInt32();
		int? actual = null; string error = null;
		try
		{
			Type type = assembly.GetType("AssetRipper.Il2CppFixture." + (methodName == "Step" ? "Counter" : "PureMethods"), true);
			MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
			object instance = null; object[] parameters;
			if (methodName == "Step")
			{
				// Do not let a recovered constructor's unrelated failure hide a field/virtual-method trial.
				instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
				type.GetField("value").SetValue(instance, a); parameters = new object[] { b };
			}
			else if (methodName == "Divide") parameters = new object[] { a, b };
			else { if (methodName == "Echo") method = method.MakeGenericMethod(typeof(int)); parameters = new object[] { a }; }
			actual = (int)method.Invoke(instance, parameters);
		}
		catch (Exception e) { var cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; error = cause.GetType().FullName + ": " + cause.Message; }
		return new { Id = id, Method = methodName, Expected = expected, Actual = actual, Error = error, Matched = error == null && actual == expected };
	}
}
