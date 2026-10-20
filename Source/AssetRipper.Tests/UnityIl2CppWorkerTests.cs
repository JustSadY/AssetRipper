using AssetRipper.Export.Scripts;
using AsmResolver.DotNet;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Workers;
using AssetRipper.IO.Files;
using AssetRipper.Tests.Decompilation;
using Cpp2IL.Core;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("Il2CppWorkerNative")]
[NonParallelizable]
public class UnityIl2CppWorkerTests
{
	[Test]
	public void NormalGameLoadUsesTheWorkerAndDiscoversVersionInTheChild()
	{
		var fixture = UnityIl2CppFixture.Require(); string output = Output("GameLoad"); var before = Cpp2IlApi.CurrentAppContext;
		CoreConfiguration settings = new(); settings.ImportSettings.Il2CppWorker = new(Il2CppWorkerTestHarness.RealWorker, output, 30);
		using var game = GameStructure.Load([Path.Combine(fixture.DirectoryPath, "IL2CPP")], LocalFileSystem.Instance, settings);
		Assert.That(game.AssemblyManager, Is.TypeOf<IsolatedIL2CppManager>());
		var manager = (IsolatedIL2CppManager)game.AssemblyManager;
		Assert.That(manager.WorkerRun!.Response!.UnityVersion, Is.EqualTo(fixture.Manifest.UnityVersion));
		Assert.That(manager.WorkerRun.Response.VersionFiles, Has.Length.EqualTo(1));
		Assert.That(Path.GetFileName(manager.WorkerRun.Response.VersionFiles[0].Path), Is.EqualTo("globalgamemanagers"));
		Assert.That(game.IsValid, Is.True); Assert.That(ReferenceEquals(Cpp2IlApi.CurrentAppContext, before), Is.True);
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(manager.WorkerRun, new JsonSerializerOptions { WriteIndented = true }));
	}
	[Test]
	public void ConfiguredWorkerFailureCannotSilentlySwitchTheGameToUnknownBackend()
	{
		var fixture = UnityIl2CppFixture.Require(); string output = Output("GameFailure");
		CoreConfiguration settings = new(); settings.ImportSettings.Il2CppWorker = new(Path.Combine(output, "missing-worker"), output, 5);
		Assert.That(() => GameStructure.Load([Path.Combine(fixture.DirectoryPath, "IL2CPP")], LocalFileSystem.Instance, settings),
			Throws.TypeOf<Il2CppWorkerException>().With.Property(nameof(Il2CppWorkerException.Run)).Property(nameof(Il2CppWorkerRun.Status)).EqualTo("StartFailed"));
	}
	[Test]
	public async Task RealManagerImportsThroughFreshProcessesAndLeavesParentAnalysisStateAlone()
	{
		UnityIl2CppFixture fixture = UnityIl2CppFixture.Require(); string output = Output("Metadata");
		var before = Cpp2IlApi.CurrentAppContext;
		List<Il2CppWorkerRun> runs = [];
		foreach (int _ in new[] { 1, 2 })
		{
			using IsolatedIL2CppManager manager = new(_ => { }, ScriptContentLevel.Level3, new(Il2CppWorkerTestHarness.RealWorker, output, 30));
			await manager.InitializeAsync(fixture.CreateStructure()); runs.Add(manager.WorkerRun!);
			Assert.That(manager.GetAssemblies().Count(), Is.EqualTo(49));
			Assert.That(manager.Il2CppRecovery!.MethodBodies, Is.EqualTo(new Il2CppMethodBodyCounts(16000, 827, 0)));
			Assert.That(manager.Il2CppRecovery.RecoveryCapability, Is.EqualTo("RecoveryUnavailable"), "Level3 with the known stub provider recovers nothing.");
			// Measured on the owned corpus; every count comes from the binary's own tables or the metadata, independently.
			Assert.That(manager.Il2CppRecovery.PairValidation, Is.EqualTo(new Il2CppPairValidation("Passed", "x86_64", 8, 39, 49, 3400, 16827, 49, 14848)));
			Assert.That(manager.Il2CppRecovery.Isolation!.ProcessId, Is.Not.EqualTo(Environment.ProcessId));
			Assert.That(manager.Il2CppRecovery.Isolation.ProtocolValidation, Is.EqualTo("Passed"));
			Assert.That(manager.Il2CppRecovery.Validation, Is.EqualTo("NotRun"));
			Assert.That(ReferenceEquals(Cpp2IlApi.CurrentAppContext, before), Is.True, "Only the child may initialize the native analysis context.");
			var type = manager.GetTypeDefinition(new ScriptIdentifier("Assembly-CSharp", "AssetRipper.Il2CppFixture", "Counter"));
			Assert.That(type.BaseType!.Resolve(manager.RuntimeContext!)!.Name?.ToString(), Is.EqualTo("AbstractCounter"));
			VirtualFileSystem fs = new(); new ScriptExportReport { ScriptingBackend = ScriptingBackend.IL2Cpp, Il2CppRecovery = manager.Il2CppRecovery }.Write("/Report", fs);
			using var report = JsonDocument.Parse(fs.File.ReadAllText("/Report/script-export-report.json"));
			Assert.That(report.RootElement.GetProperty("Il2CppRecovery").GetProperty("Isolation").GetProperty("JobId").GetString(), Is.EqualTo(manager.WorkerRun!.Response!.JobId));
		}
		Assert.That(runs[0].ProcessId, Is.Not.EqualTo(runs[1].ProcessId));
		Assert.That(runs[0].JobDirectory, Is.Not.EqualTo(runs[1].JobDirectory));
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
	}
	[Test]
	public async Task RealWorkerContainsMalformedNativeInputAndTheFollowingJobStillWorks()
	{
		var fixture = UnityIl2CppFixture.Require(); string output = Output("Malformed"); var before = Cpp2IlApi.CurrentAppContext;
		string bad = Path.Combine(output, "bad-native.bin"); File.WriteAllBytes(bad, new byte[64]);
		var failed = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 30), bad,
			Path.Combine(fixture.DirectoryPath, UnityIl2CppFixture.MetadataPath), fixture.Manifest.UnityVersion, ScriptContentLevel.Level3);
		Assert.That(failed.Status, Is.EqualTo("ExitedWithError"), failed.Error); Assert.That(failed.ProcessId, Is.Not.EqualTo(Environment.ProcessId));
		Assert.That(ReferenceEquals(Cpp2IlApi.CurrentAppContext, before), Is.True);
		var next = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 30),
			Path.Combine(fixture.DirectoryPath, UnityIl2CppFixture.NativePath), Path.Combine(fixture.DirectoryPath, UnityIl2CppFixture.MetadataPath), fixture.Manifest.UnityVersion, ScriptContentLevel.Level3);
		Assert.That(next.Completed, Is.True, next.Error); Assert.That(next.ProcessId, Is.Not.EqualTo(failed.ProcessId));
		Assert.That(ReferenceEquals(Cpp2IlApi.CurrentAppContext, before), Is.True);
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(new { Failed = failed, Next = next }, new JsonSerializerOptions { WriteIndented = true }));
	}
	[Test]
	public Task ExperimentalWorkerKeepsOnlyFullyGatedBodiesAndEveryKeptBodyMatchesTheOracle()
		=> CandidateTrial(UnityIl2CppFixture.Require(), "Candidates", RecoveredMethods, "Il2CppAddressLoweringInstructionSet");
	[Test]
	public Task StrippedBuildKeepsTheSameGuarantees()
		=> CandidateTrial(UnityIl2CppFixture.RequireStripped(), "StrippedCandidates", StrippedRecoveredMethods, "Il2CppAddressLoweringInstructionSet");
	[Test]
	public Task Arm64BuildKeepsTheSameGuarantees()
		=> CandidateTrial(UnityIl2CppFixture.RequireArm64(), "Arm64Candidates", Arm64RecoveredMethods, "Il2CppArmV8RegisterAliasInstructionSet");

	private static async Task CandidateTrial(UnityIl2CppFixture fixture, string kind, string[] expectedRecovered, string expectedLifter)
	{
		string output = Output(kind); var before = Cpp2IlApi.CurrentAppContext;
		Il2CppWorkerOptions options = new(Il2CppWorkerTestHarness.RealWorker, output, 60);
		var reference = await Il2CppWorkerClient.AnalyzeAsync(options, fixture.NativeFile, fixture.MetadataFile, fixture.Manifest.UnityVersion, ScriptContentLevel.Level3);
		var run = await Il2CppWorkerClient.AnalyzeAsync(options, fixture.NativeFile, fixture.MetadataFile, fixture.Manifest.UnityVersion, ScriptContentLevel.Level3,
			new("Assembly-CSharp", ["AssetRipper.Il2CppFixture.PureMethods", "AssetRipper.Il2CppFixture.Counter"]));
		Il2CppReplayResult? replay = null;
		if (run.Completed)
		{
			var assembly = run.Response!.Assemblies.Single(a => a.Name == "Assembly-CSharp");
			replay = Il2CppReplayRunner.Run(Il2CppWorkerFiles.ResolveOutput(run.JobDirectory, assembly.Path), Path.Combine(fixture.DirectoryPath, "oracle.json"), Path.Combine(output, "Replay"));
		}
		// Evidence first, so a failed assertion still leaves the measured runs and replay behind.
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(new { Reference = reference, Run = run, Replay = replay }, new JsonSerializerOptions { WriteIndented = true }));

		Assert.That(reference.Completed, Is.True, reference.Error); Assert.That(run.Completed, Is.True, run.Error);
		var response = run.Response!; var recovery = response.Recovery!; var stubs = reference.Response!.Recovery!.MethodBodies;
		Assert.That(recovery.UsesKnownStubOutput, Is.False); Assert.That(recovery.RecoveryCapability, Is.EqualTo("ExperimentalCandidates"));
		Assert.That(recovery.PairValidation, Is.EqualTo(reference.Response.Recovery.PairValidation)); Assert.That(recovery.PairValidation!.Status, Is.EqualTo("Passed"));
		Il2CppWorkerMethod[] recovered = response.Methods.Where(m => m.BodyOrigin == "RecoveredNative").ToArray();
		Assert.That(recovery.MethodBodies.Unknown, Is.Zero); Assert.That(recovery.MethodBodies.NoBodyByDesign, Is.EqualTo(stubs.NoBodyByDesign));
		Assert.That(recovery.MethodBodies.GeneratedStub + recovery.MethodBodies.RecoveredNative, Is.EqualTo(stubs.GeneratedStub), "Every managed body is a stub or a gated candidate.");
		Assert.That(recovery.MethodBodies.RecoveredNative, Is.EqualTo(recovered.Length));
		Assert.That(recovered.Select(m => m.Name), Is.EquivalentTo(expectedRecovered), "Measured on this corpus; any change needs new evidence.");
		Assert.That(recovered.All(m => m.Gates.SequenceEqual(new[] { "IsilChecked", "StackHeightChecked", "TypedCilChecked" }) && !m.IsGeneric), Is.True);
		Assert.That(response.Methods.Where(m => m.Name is not (".ctor" or "Echo")).All(m => m.Lifter == expectedLifter), Is.True);
		string[] untrusted = response.Methods.Where(m => m.Gates.LastOrDefault() == "UntrustedLifter").Select(m => m.Name).ToArray();
		Assert.That(untrusted, Is.EquivalentTo(kind == "Arm64Candidates" ? Arm64UntrustedMethods : []), "Untrusted bodies passed every gate but stay stubs.");
		Assert.That(response.Methods.Where(m => m.Gates.LastOrDefault() == "UntrustedLifter").All(m => m.BodyOrigin == "GeneratedStub"
			&& m.Gates.SequenceEqual(new[] { "IsilChecked", "StackHeightChecked", "TypedCilChecked", "UntrustedLifter" })), Is.True);

		// Generic sharing: Echo<T> keeps the stub, and its concrete instantiation is reported with its own body.
		var echo = response.Methods.Single(m => m.Name == "Echo");
		Assert.That(echo.StructuralValidation, Is.EqualTo("SharedGenericBody"));
		Assert.That(echo.GenericInstantiations, Is.Not.Null.And.Some.Matches<Il2CppGenericInstantiation>(i => i!.MethodArguments.SequenceEqual(new[] { "System.Int32" }) && i.Pointer != 0));
		Assert.That(response.Methods.Single(m => m.Name == ".ctor").StructuralValidation, Is.EqualTo("UnsupportedConstructor"));
		Assert.That(response.Methods.All(m => m.BehaviorValidation == "NotRun"), Is.True);
		foreach (var method in response.Methods.Where(m => m.Sidecar is not null))
		{
			Assert.That(File.ReadAllText(Il2CppWorkerFiles.ResolveEvidence(run.JobDirectory, method.Sidecar!.Path)), Does.StartWith("# " + method.Signature));
		}
		Assert.That(ReferenceEquals(Cpp2IlApi.CurrentAppContext, before), Is.True);

		// The behavior gate: a body kept as recovered must reproduce every native observation of its method,
		// and a recovered override must also be reached through the abstract base and the interface.
		Assert.That(replay!.ExitCode, Is.EqualTo(0), replay.Error); Assert.That(replay.Cases, Has.Length.EqualTo(20));
		foreach (var method in recovered)
		{
			Il2CppReplayOutcome[] cases = replay.Cases.Where(c => c.Method == method.Name).ToArray();
			Assert.That(cases, Is.Not.Empty, $"{method.Name} has no oracle observations.");
			Assert.That(cases.Where(c => !c.Matched).Select(c => c.Id), Is.Empty, $"{method.Name} was kept as recovered but differs from the native oracle.");
		}
		if (recovered.Any(m => m.Name == "Step"))
		{
			// Count the dispatch surfaces this build kept; a stripped build can drop an interface nobody calls through.
			var recoveredAssembly = AssemblyDefinition.FromFile(Il2CppWorkerFiles.ResolveOutput(run.JobDirectory, response.Assemblies.Single(a => a.Name == "Assembly-CSharp").Path));
			string[] surfaces = recoveredAssembly.ManifestModule!.TopLevelTypes.Select(t => t.FullName).Intersect(["AssetRipper.Il2CppFixture.AbstractCounter", "AssetRipper.Il2CppFixture.ICounter"]).ToArray();
			Assert.That(surfaces, Does.Contain("AssetRipper.Il2CppFixture.AbstractCounter"));
			Assert.That(surfaces, Has.Length.EqualTo(ExpectedDispatchSurfaces(kind)), "Measured: the stripped build drops the unused ICounter.");
			Assert.That(replay.Dispatch, Has.Length.EqualTo(3 * surfaces.Length));
			Assert.That(replay.Dispatch.Where(c => !c.Matched).Select(c => c.Id + ": " + c.Error), Is.Empty, "The recovered override must be reached by virtual and interface dispatch.");
		}
	}
	private static readonly string[] RecoveredMethods = ["Step", "Affine", "Branch", "Sum"];
	private static readonly string[] StrippedRecoveredMethods = ["Step", "Affine", "Branch", "Sum"];
	private static int ExpectedDispatchSurfaces(string kind) => kind == "StrippedCandidates" ? 1 : 2;
	// The ARMv8 lifter's gated Affine/Branch/Sum differed from the native oracle (1/5, 2/4, 2/4), so they stay stubs.
	private static readonly string[] Arm64RecoveredMethods = [];
	private static readonly string[] Arm64UntrustedMethods = ["Affine", "Branch", "Sum"];

	// Metadata with one method fewer than the binary keeps every token in range, so LibCpp2IL would silently map
	// methods to shifted pointers; only the pair check rejects it. With one method more, LibCpp2IL fails by itself.
	[Test]
	public async Task MetadataWithOneMethodMoreFailsInsideLibCpp2IL()
	{
		var original = UnityIl2CppFixture.Require(); var variant = UnityIl2CppFixture.RequirePairVariant(); string output = Output("PairLonger");
		var run = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 30), Path.Combine(original.DirectoryPath, UnityIl2CppFixture.NativePath),
			Path.Combine(variant.DirectoryPath, UnityIl2CppFixture.MetadataPath), original.Manifest.UnityVersion, ScriptContentLevel.Level2);
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
		Assert.That(run.Status, Is.EqualTo("ExitedWithError")); Assert.That(run.Response!.Status, Is.EqualTo("AnalysisFailed"), run.Error);
		Assert.That(run.Response.Error, Does.Contain("LibCpp2ILInitializationException")); Assert.That(run.Response.Assemblies, Is.Empty);
	}
	[Test]
	public async Task MetadataWithOneMethodFewerIsRejectedAsAForeignPair()
	{
		const bool variantBinary = true;
		var original = UnityIl2CppFixture.Require(); var variant = UnityIl2CppFixture.RequirePairVariant(); string output = Output("Pair");
		string native = Path.Combine((variantBinary ? variant : original).DirectoryPath, UnityIl2CppFixture.NativePath);
		string metadata = Path.Combine((variantBinary ? original : variant).DirectoryPath, UnityIl2CppFixture.MetadataPath);
		var run = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 30), native, metadata, original.Manifest.UnityVersion, ScriptContentLevel.Level2);
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
		Assert.That(run.Status, Is.EqualTo("ExitedWithError")); Assert.That(run.ExitCode, Is.EqualTo(3));
		Assert.That(run.Response!.Status, Is.EqualTo("RejectedPair"), run.Error);
		Assert.That(run.Response.Error, Does.Contain("Module 'Assembly-CSharp.dll' has"), "The single extra method must be named as the cause.");
		Assert.That(run.Response.Assemblies, Is.Empty); Assert.That(run.Response.Recovery, Is.Null);
	}
	[TestCase(false)]
	[TestCase(true)]
	public async Task CrossArchitecturePairIsNeverAccepted(bool armBinary)
	{
		var linux = UnityIl2CppFixture.Require(); var arm = UnityIl2CppFixture.RequireArm64(); string output = Output("CrossArchitecture");
		var run = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 60), armBinary ? arm.NativeFile : linux.NativeFile,
			armBinary ? linux.MetadataFile : arm.MetadataFile, linux.Manifest.UnityVersion, ScriptContentLevel.Level2);
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
		Assert.That(run.Completed, Is.False); Assert.That(run.Status, Is.EqualTo("ExitedWithError"), run.Error);
		// Measured: shapes this different stop LibCpp2IL's registration search before the pair check is reached;
		// the pair check exists for near-identical shapes (see the pair-variant tests).
		Assert.That(run.Response!.Status, Is.EqualTo("AnalysisFailed"), run.Error);
		Assert.That(run.Response.Error, Does.Contain("Failed to find code registration or metadata registration"));
		Assert.That(run.Response.Assemblies, Is.Empty);
	}
	[Test]
	public async Task PairVariantWithItsOwnBinaryIsAValidPair()
	{
		var original = UnityIl2CppFixture.Require(); var variant = UnityIl2CppFixture.RequirePairVariant(); string output = Output("PairVariant");
		var run = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 30), Path.Combine(variant.DirectoryPath, UnityIl2CppFixture.NativePath),
			Path.Combine(variant.DirectoryPath, UnityIl2CppFixture.MetadataPath), variant.Manifest.UnityVersion, ScriptContentLevel.Level2);
		var reference = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, output, 30), Path.Combine(original.DirectoryPath, UnityIl2CppFixture.NativePath),
			Path.Combine(original.DirectoryPath, UnityIl2CppFixture.MetadataPath), original.Manifest.UnityVersion, ScriptContentLevel.Level2);
		File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(new { Variant = run, Original = reference }, new JsonSerializerOptions { WriteIndented = true }));
		Assert.That(run.Completed, Is.True, run.Error); Assert.That(reference.Completed, Is.True, reference.Error);
		var variantPair = run.Response!.Recovery!.PairValidation!; var originalPair = reference.Response!.Recovery!.PairValidation!;
		Assert.That(variantPair.Status, Is.EqualTo("Passed")); Assert.That(originalPair.Status, Is.EqualTo("Passed"));
		Assert.That(variantPair with { MethodDefinitions = 0, MethodPointers = 0 }, Is.EqualTo(originalPair with { MethodDefinitions = 0, MethodPointers = 0 }),
			"Images, types and modules are identical; only the method count differs.");
		Assert.That(variantPair.MethodDefinitions, Is.EqualTo(originalPair.MethodDefinitions + 1));
	}
	private static string Output(string kind)
	{
		string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "il2cpp-worker", kind + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
	}
}
