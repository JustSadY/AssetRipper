using AssetRipper.Import.Structure.Assembly.Workers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AssetRipper.Tests.Decompilation;

internal sealed class Il2CppWorkerTestHarness
{
	public string DirectoryPath { get; } = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "il2cpp-worker", "Controlled-" + Guid.NewGuid().ToString("N"));
	public string Native { get; }
	public string Metadata { get; }
	public Il2CppWorkerTestHarness()
	{
		Directory.CreateDirectory(DirectoryPath); Native = Path.Combine(DirectoryPath, "native.input"); Metadata = Path.Combine(DirectoryPath, "metadata.input");
		File.WriteAllBytes(Native, [1, 2, 3]); File.WriteAllBytes(Metadata, [4, 5, 6]);
		using Stream resource = typeof(Il2CppWorkerTestHarness).Assembly.GetManifestResourceStream("AssetRipper.Tests.Fixtures.Il2CppWorkerDouble.cs")!;
		using StreamReader reader = new(resource);
		var compilation = CSharpCompilation.Create("WorkerDouble", [CSharpSyntaxTree.ParseText(reader.ReadToEnd())], FixtureCompiler.References,
			new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, deterministic: true));
		using MemoryStream image = new(); var result = compilation.Emit(image);
		if (!result.Success) throw new InvalidDataException(string.Join("\n", result.Diagnostics));
		foreach (string mode in Modes)
		{
			File.WriteAllBytes(Path.Combine(DirectoryPath, mode + ".dll"), image.ToArray());
			File.WriteAllText(Path.Combine(DirectoryPath, mode + ".runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
		}
		var owned = FixtureCompiler.Compile(new Dictionary<string, string> { ["Owned.cs"] = "public static class Owned { public static int Value() { return 0; } }" }, new(LanguageVersion.CSharp7_3, OptimizationLevel.Release));
		File.WriteAllBytes(Path.Combine(DirectoryPath, "OwnedFixture.dll"), owned.AssemblyBytes!);
	}
	public static string[] Modes { get; } = ["Valid", "Hang", "HangChild", "Exit", "Malformed", "Oversized", "Flood", "Symlink", "WrongJob", "WrongPid", "WrongInput", "WrongOutput", "Traversal", "BehaviorClaim", "UnknownProvider", "NoAssemblies", "Duplicate", "ExtraField", "ExitAfterResponse",
		"OldSchema", "NoPairValidation", "PairFailed", "UnknownBodies", "RecoveredClaim", "WrongCapability", "HeapHog",
		"CandidateMissingGate", "CandidateUntyped", "CandidateGeneric", "CandidateShared", "CandidateCount", "CandidateOldOrigin",
		"EvidenceTraversal", "EvidenceHash"];
	public Il2CppWorkerOptions Options(string mode, int timeout = 10, int? heap = null, bool retainInputs = true)
		=> new(Path.Combine(DirectoryPath, mode + ".dll"), Path.Combine(DirectoryPath, "Jobs"), timeout, heap, retainInputs);
	public static string RealWorker => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../..", "AssetRipper.Tools.Il2CppWorker",
		new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Name, "AssetRipper.Tools.Il2CppWorker.dll"));
}
