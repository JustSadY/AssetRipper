using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record Il2CppReplayOutcome(string Id, string Method, int Expected, int? Actual, string? Error, bool Matched);
internal sealed record Il2CppReplayResult(string Status, int? ExitCode, Il2CppReplayOutcome[] Cases, string? Error)
{
	/// <summary>Counter observations replayed through the abstract base method and the interface method.</summary>
	public Il2CppReplayOutcome[] Dispatch { get; init; } = [];
}

internal static class Il2CppReplayRunner
{
	public static Il2CppReplayResult Run(string assemblyPath, string oraclePath, string output)
	{
		Directory.CreateDirectory(output);
		using var resource = typeof(Il2CppReplayRunner).Assembly.GetManifestResourceStream("AssetRipper.Tests.Fixtures.Il2CppReplay.cs")!;
		using StreamReader reader = new(resource);
		string source = reader.ReadToEnd(); File.WriteAllText(Path.Combine(output, "Il2CppReplay.cs"), source);
		var compilation = CSharpCompilation.Create("Il2CppReplay", [CSharpSyntaxTree.ParseText(source)], FixtureCompiler.References,
			new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, deterministic: true));
		string runner = Path.Combine(output, "Il2CppReplay.dll");
		using (FileStream file = File.Create(runner))
		{
			var result = compilation.Emit(file);
			if (!result.Success) throw new InvalidDataException(string.Join("\n", result.Diagnostics));
		}
		File.WriteAllText(Path.Combine(output, "Il2CppReplay.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
		string resultPath = Path.Combine(output, "behavior.json");
		ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = output };
		foreach (string arg in new[] { runner, Path.GetFullPath(assemblyPath), Path.GetFullPath(oraclePath), resultPath, FixtureCompiler.Hash(File.ReadAllBytes(assemblyPath)) }) start.ArgumentList.Add(arg);
		using Process process = Process.Start(start)!;
		Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
		bool exited = process.WaitForExit(30_000);
		if (!exited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
		File.WriteAllText(Path.Combine(output, "stdout.log"), stdout.GetAwaiter().GetResult());
		string errors = stderr.GetAwaiter().GetResult(); File.WriteAllText(Path.Combine(output, "stderr.log"), errors);
		if (!exited) return new("Failed", null, [], "Owned replay exceeded 30 seconds.");
		if (process.ExitCode != 0 || !File.Exists(resultPath)) return new("Failed", process.ExitCode, [], errors);
		var cases = JsonSerializer.Deserialize<Il2CppReplayOutcome[]>(File.ReadAllText(resultPath))!;
		string dispatchPath = Path.Combine(output, "dispatch.json");
		var dispatch = File.Exists(dispatchPath) ? JsonSerializer.Deserialize<Il2CppReplayOutcome[]>(File.ReadAllText(dispatchPath))! : [];
		return new(cases.Length == 20 && cases.All(c => c.Matched) ? "Passed" : "Failed", process.ExitCode, cases, null) { Dispatch = dispatch };
	}
}
