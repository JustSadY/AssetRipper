using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Workers;
using AssetRipper.Tests.Decompilation;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects.Configuration;
using AssetRipper.Processing.Configuration;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using System.Diagnostics;

namespace AssetRipper.Tests;

[Category("Il2CppWorker")]
public class Il2CppWorkerTests
{
	private Il2CppWorkerTestHarness harness = null!;
	[OneTimeSetUp] public void Setup() => harness = new();
	[TestCase("Valid")]
	[TestCase("Flood")]
	public async Task ValidProtocolCompletesAndPipesRemainBounded(string mode)
	{
		var result = await Run(mode); Assert.That(result.Completed, Is.True, result.Error);
		Assert.That(result.ProcessId, Is.Not.EqualTo(Environment.ProcessId));
		Assert.That(result.Response!.Recovery!.Validation, Is.EqualTo("NotRun"));
		Assert.That(File.ReadAllText(harness.Native), Is.Not.Empty);
		Assert.That(result.StdoutTruncated, Is.EqualTo(mode == "Flood")); Assert.That(result.StderrTruncated, Is.EqualTo(mode == "Flood"));
		Assert.That(new FileInfo(Path.Combine(result.JobDirectory, "stdout.log")).Length, Is.LessThanOrEqualTo(64 * 1024));
	}
	[TestCase("Malformed")]
	[TestCase("Oversized")]
	[TestCase("WrongJob")]
	[TestCase("WrongPid")]
	[TestCase("WrongInput")]
	[TestCase("WrongOutput")]
	[TestCase("Traversal")]
	[TestCase("BehaviorClaim")]
	[TestCase("UnknownProvider")]
	[TestCase("NoAssemblies")]
	[TestCase("Duplicate")]
	[TestCase("ExtraField")]
	[TestCase("OldSchema")]
	[TestCase("NoPairValidation")]
	[TestCase("PairFailed")]
	[TestCase("UnknownBodies")]
	[TestCase("RecoveredClaim")]
	[TestCase("WrongCapability")]
	public async Task InvalidResponseIsNeverAccepted(string mode)
	{
		var result = await Run(mode); Assert.That(result.Status, Is.EqualTo("InvalidResponse"), result.Error);
		Assert.That(result.Completed, Is.False); Assert.That(result.Error, Is.Not.Empty);
		Assert.That(File.Exists(Path.Combine(result.JobDirectory, "run.json")), Is.True);
	}
	[Test]
	public async Task ValidCandidateResponseCarriesGatesAndVerifiedSidecar()
	{
		var result = await RunCandidates("Valid"); Assert.That(result.Completed, Is.True, result.Error);
		var method = result.Response!.Methods.Single();
		Assert.That(method.BodyOrigin, Is.EqualTo("RecoveredNative")); Assert.That(method.BehaviorValidation, Is.EqualTo("NotRun"));
		Assert.That(result.Response.Recovery!.MethodBodies.RecoveredNative, Is.EqualTo(1));
		Assert.That(File.Exists(Il2CppWorkerFiles.ResolveEvidence(result.JobDirectory, method.Sidecar!.Path)), Is.True);
	}
	[TestCase("CandidateMissingGate")]
	[TestCase("CandidateUntyped")]
	[TestCase("CandidateGeneric")]
	[TestCase("CandidateShared")]
	[TestCase("CandidateCount")]
	[TestCase("CandidateOldOrigin")]
	[TestCase("EvidenceTraversal")]
	[TestCase("EvidenceHash")]
	public async Task RecoveredClaimsWithoutTheirGatesOrEvidenceAreRejected(string mode)
	{
		var result = await RunCandidates(mode); Assert.That(result.Status, Is.EqualTo("InvalidResponse"), result.Error);
		Assert.That(result.Completed, Is.False);
	}
	[Test]
	public async Task HeapLimitReachesTheChildAsHexadecimalBytes()
	{
		var result = await Il2CppWorkerClient.AnalyzeAsync(harness.Options("Valid", heap: 64), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3);
		Assert.That(result.Completed, Is.True, result.Error);
		Assert.That(File.ReadAllText(Path.Combine(result.JobDirectory, "heap-limit.txt")), Is.EqualTo("0x4000000"));
	}
	[Test]
	public async Task WithoutAHeapLimitTheVariableIsNotSet()
	{
		var result = await Run("Valid"); Assert.That(result.Completed, Is.True, result.Error);
		Assert.That(File.ReadAllText(Path.Combine(result.JobDirectory, "heap-limit.txt")), Is.EqualTo("unset"));
	}
	[Test]
	public async Task HeapHogIsStoppedByTheLimitAndNeverSucceeds()
	{
		var result = await Il2CppWorkerClient.AnalyzeAsync(harness.Options("HeapHog", 60, heap: 64), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3);
		Assert.That(result.Status, Is.EqualTo("ExitedWithError"), result.Error); Assert.That(result.ExitCode, Is.Not.EqualTo(0));
		Assert.That(Directory.Exists(Path.Combine(result.JobDirectory, "Input")), Is.True);
	}
	[TestCase(63)]
	[TestCase(1048577)]
	public void HeapLimitOutsideItsRangeIsRejectedBeforeLaunch(int heap)
	{
		Assert.That(() => Il2CppWorkerClient.AnalyzeAsync(harness.Options("Valid", heap: heap), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3),
			Throws.ArgumentException);
	}
	[Test]
	public async Task DiscardedInputsAreDeletedOnlyAfterACompletedJob()
	{
		var completed = await Il2CppWorkerClient.AnalyzeAsync(harness.Options("Valid", retainInputs: false), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3);
		Assert.That(completed.Completed, Is.True, completed.Error); Assert.That(completed.InputSnapshotsRetained, Is.False);
		Assert.That(Directory.Exists(Path.Combine(completed.JobDirectory, "Input")), Is.False);
		Assert.That(File.ReadAllText(Path.Combine(completed.JobDirectory, "run.json")), Does.Contain("\"InputSnapshotsRetained\": false"));
		var failed = await Il2CppWorkerClient.AnalyzeAsync(harness.Options("Exit", retainInputs: false), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3);
		Assert.That(failed.Status, Is.EqualTo("ExitedWithError")); Assert.That(failed.InputSnapshotsRetained, Is.True);
		Assert.That(Directory.Exists(Path.Combine(failed.JobDirectory, "Input")), Is.True);
	}
	[TestCase("Exit")]
	[TestCase("ExitAfterResponse")]
	public async Task NonzeroExitCannotBecomeSuccessFromAResponseFile(string mode)
	{
		var result = await Run(mode); Assert.That(result.Status, Is.EqualTo("ExitedWithError")); Assert.That(result.ExitCode, Is.EqualTo(37));
	}
	[TestCase("Hang")]
	[TestCase("HangChild")]
	public async Task DeadlineStopsOnlyTheOwnedProcessTree(string mode)
	{
		var result = await Run(mode, 1); Assert.That(result.Status, Is.EqualTo("TimedOut")); AssertStopped(result.ProcessId!.Value);
		if (mode == "HangChild") AssertStopped(int.Parse(File.ReadAllText(Path.Combine(result.JobDirectory, "child.pid"))));
	}
	[Test]
	public async Task CancellationDuringAnalysisStopsTheWorker()
	{
		using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(500));
		var result = await Run("Hang", token: cancel.Token); Assert.That(result.Status, Is.EqualTo("Cancelled")); AssertStopped(result.ProcessId!.Value);
	}
	[Test]
	public async Task CancellationBeforeLaunchCreatesNoChild()
	{
		var result = await Run("Valid", token: new CancellationToken(canceled: true)); Assert.That(result.Status, Is.EqualTo("Cancelled")); Assert.That(result.ProcessId, Is.Null);
	}
	[Test]
	public async Task MissingExecutableHasAnExplicitFailureAndEvidence()
	{
		var result = await Il2CppWorkerClient.AnalyzeAsync(new(Path.Combine(harness.DirectoryPath, "missing-worker"), harness.DirectoryPath), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3);
		Assert.That(result.Status, Is.EqualTo("StartFailed")); Assert.That(result.Completed, Is.False);
	}
	[Test]
	public async Task OutputSymlinksAreRejected()
	{
		if (OperatingSystem.IsWindows()) Assert.Ignore("This link test needs Unix symlink semantics.");
		var result = await Run("Symlink"); Assert.That(result.Status, Is.EqualTo("InvalidResponse")); Assert.That(result.Error, Does.Contain("links"));
	}
	[Test]
	public async Task SourceMutationCannotChangeTheCapturedJobInputs()
	{
		var result = await Run("Valid"); var snapshot = result.Response!.Native;
		byte[] original = File.ReadAllBytes(harness.Native);
		try
		{
			File.WriteAllBytes(harness.Native, [9, 9, 9]);
			Assert.That(snapshot.Path, Is.Not.EqualTo(harness.Native));
			Assert.That(File.ReadAllBytes(snapshot.Path), Is.EqualTo(original));
			Assert.That(Il2CppWorkerFiles.Identify(snapshot.Path), Is.EqualTo(snapshot));
		}
		finally { File.WriteAllBytes(harness.Native, original); }
	}
	[Test]
	public async Task ArgumentsPreserveSpacesUnicodeAndShellCharacters()
	{
		string path = Path.Combine(harness.DirectoryPath, "boşluk ; $() ' test"); Directory.CreateDirectory(path);
		foreach (string file in new[] { "Valid.dll", "Valid.runtimeconfig.json", "OwnedFixture.dll" }) File.Copy(Path.Combine(harness.DirectoryPath, file), Path.Combine(path, file));
		var result = await Il2CppWorkerClient.AnalyzeAsync(new(Path.Combine(path, "Valid.dll"), Path.Combine(path, "Jobs")), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level2);
		Assert.That(result.Completed, Is.True, result.Error);
	}
	[Test]
	public async Task RealWorkerRejectsBadMetadataBeforeNativeAnalysisWithoutACorpus()
	{
		var result = await Il2CppWorkerClient.AnalyzeAsync(new(Il2CppWorkerTestHarness.RealWorker, harness.DirectoryPath), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level2);
		Assert.That(result.Status, Is.EqualTo("ExitedWithError")); Assert.That(result.Response!.Status, Is.EqualTo("RejectedInput"));
		Assert.That(result.Response.Recovery, Is.Null); Assert.That(result.Response.Assemblies, Is.Empty);
	}
	[Test]
	public async Task MissingVersionEvidenceFailsBeforeLaunchingAChild()
	{
		var result = await Il2CppWorkerClient.AnalyzeAsync(harness.Options("Valid"), harness.Native, harness.Metadata, null, ScriptContentLevel.Level2);
		Assert.That(result.Status, Is.EqualTo("InputFailed")); Assert.That(result.ProcessId, Is.Null);
	}
	[Test]
	public void ManagerStagesAllAssembliesBeforeExposingThem()
	{
		using IsolatedIL2CppManager manager = new(_ => { }, ScriptContentLevel.Level2, harness.Options("Valid"));
		Assert.That(() => manager.Initialize(new ControlledStructure(harness)), Throws.TypeOf<Il2CppWorkerException>());
		Assert.That(manager.WorkerRun!.Status, Is.EqualTo("InvalidAssembly"));
		Assert.That(manager.GetAssemblies(), Is.Empty); Assert.That(manager.Il2CppRecovery, Is.Null);
	}
	[Test]
	public void WorkerOptionsRoundTripThroughProductionSettingsSerialization()
	{
		string path = Path.Combine(harness.DirectoryPath, "settings.json");
		ImportSettings import = new() { Il2CppWorker = harness.Options("Valid", 7, heap: 512, retainInputs: false) };
		new SerializedSettings(import, new ProcessingSettings(), new ExportSettings()).Save(path);
		Assert.That(SerializedSettings.Load(path).Import.Il2CppWorker, Is.EqualTo(import.Il2CppWorker));
	}
	[TestCase(ScriptingBackend.IL2Cpp, true, typeof(IsolatedIL2CppManager))]
	[TestCase(ScriptingBackend.IL2Cpp, false, typeof(IL2CppManager))]
	[TestCase(ScriptingBackend.Mono, true, typeof(MonoManager))]
	[TestCase(ScriptingBackend.Unknown, true, typeof(BaseManager))]
	public void WorkerSelectionIsExplicitAndBackendSpecific(ScriptingBackend backend, bool configured, Type expected)
	{
		ImportSettings settings = new() { Il2CppWorker = configured ? harness.Options("Valid") : null };
		using var manager = AssemblyManagerFactory.Create(backend, _ => { }, settings); Assert.That(manager.GetType(), Is.EqualTo(expected));
	}
	[TestCase("Unmanaged memory load: []")]
	[TestCase("Method not found @123")]
	[TestCase("Not implemented instruction: idiv")]
	public void ProviderDiagnosticsCannotPassAsCandidateCode(string diagnostic)
	{
		var method = Method(); method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldstr, diagnostic); method.CilMethodBody.Instructions.Add(CilOpCodes.Pop);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_0); method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		Assert.That(Il2CppCandidateBodyGate.Check(method).Status, Is.EqualTo("RejectedProviderPlaceholder"));
	}
	[Test]
	public void StructuralGateRebuildsStackWithoutClaimingBehavior()
	{
		var method = Method(); method.CilMethodBody!.ComputeMaxStackOnBuild = false; method.CilMethodBody.MaxStack = 0;
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4, 37); method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		Assert.That(Il2CppCandidateBodyGate.Check(method).Status, Is.EqualTo("StackHeightChecked")); Assert.That(method.CilMethodBody.MaxStack, Is.EqualTo(1));
	}
	[Test]
	public void StructuralGateRejectsStackUnderflow()
	{
		var method = Method(); method.CilMethodBody!.Instructions.Add(CilOpCodes.Pop); method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		Assert.That(Il2CppCandidateBodyGate.Check(method).Status, Is.EqualTo("RejectedInvalidStackOrBranch"));
	}
	[Test]
	public void StructuralGateRejectsAnExternalBranchTarget()
	{
		var method = Method(); method.CilMethodBody!.Instructions.Add(CilOpCodes.Br, new CilInstructionLabel(new CilInstruction(CilOpCodes.Ret)));
		Assert.That(Il2CppCandidateBodyGate.Check(method).Status, Is.EqualTo("RejectedInvalidStackOrBranch"));
	}
	[Test]
	public void StructuralGateRejectsEmptyCandidates()
	{
		Assert.That(Il2CppCandidateBodyGate.Check(Method()).Status, Is.EqualTo("RejectedEmptyBody"));
	}
	[Test]
	public void StackHeightSuccessDoesNotEstablishTypedCilValidity()
	{
		var method = Method(); method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldstr, "A string cannot satisfy an Int32 return contract.");
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		Assert.That(Il2CppCandidateBodyGate.Check(method).Status, Is.EqualTo("StackHeightChecked"), "Even this structurally balanced candidate must remain unverified.");
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Status, Is.EqualTo("RejectedTypedCil"), "The typed gate must reject a string returned as Int32.");
	}
	[Test]
	public void TypedGateAcceptsWellTypedArithmetic()
	{
		var method = Method(module => [module.CorLibTypeFactory.Int32]);
		Emit(method, (CilOpCodes.Ldarg_0, null), (CilOpCodes.Ldc_I4, 7), (CilOpCodes.Mul, null), (CilOpCodes.Ldc_I4, 11), (CilOpCodes.Sub, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method), Is.EqualTo(new Il2CppCandidateGateResult("TypedCilChecked", null)));
	}
	[Test]
	public void TypedGateRejectsDifferentStackDepthsAtAJoin()
	{
		var method = Method(module => [module.CorLibTypeFactory.Int32]); var join = new CilInstruction(CilOpCodes.Ldc_I4_2);
		var body = method.CilMethodBody!.Instructions;
		body.Add(CilOpCodes.Ldarg_0); body.Add(CilOpCodes.Brtrue, new CilInstructionLabel(join)); body.Add(CilOpCodes.Ldc_I4_1); body.Add(join); body.Add(CilOpCodes.Ret);
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Diagnostic, Does.Contain("stack depths"));
	}
	[Test]
	public void TypedGateRejectsIncompatibleTypesAtAJoin()
	{
		// The join pops its value, so only the merge itself can reject the int32/int64 mix.
		var method = Method(module => [module.CorLibTypeFactory.Int32]); var other = new CilInstruction(CilOpCodes.Ldc_I8, 0L); var join = new CilInstruction(CilOpCodes.Pop);
		var body = method.CilMethodBody!.Instructions;
		body.Add(CilOpCodes.Ldarg_0); body.Add(CilOpCodes.Brtrue, new CilInstructionLabel(other)); body.Add(CilOpCodes.Ldc_I4_0); body.Add(CilOpCodes.Br, new CilInstructionLabel(join));
		body.Add(other); body.Add(join); body.Add(CilOpCodes.Ldc_I4_0); body.Add(CilOpCodes.Ret);
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Diagnostic, Does.Contain("incompatible stack slot"));
	}
	[Test]
	public void TypedGateRejectsAnUnboxedIntegerInAnObjectLocal()
	{
		var method = Method(); method.CilMethodBody!.LocalVariables.Add(new CilLocalVariable(method.DeclaringModule!.CorLibTypeFactory.Object));
		Emit(method, (CilOpCodes.Ldc_I4_1, null), (CilOpCodes.Stloc_0, null), (CilOpCodes.Ldc_I4_0, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Diagnostic, Does.Contain("System.Object"));
	}
	[Test]
	public void TypedGateRejectsValuesLeftOnTheStackAtReturn()
	{
		var method = Method(); Emit(method, (CilOpCodes.Ldc_I4_1, null), (CilOpCodes.Ldc_I4_2, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Diagnostic, Does.Contain("remain on the stack"));
	}
	[Test]
	public void TypedGateRejectsAnArgumentOfTheWrongWidth()
	{
		var method = Method(); var module = method.DeclaringModule!;
		var wide = new MethodDefinition("Wide", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.Int64]));
		wide.CilMethodBody = new(); wide.CilMethodBody.Instructions.Add(CilOpCodes.Ret); method.DeclaringType!.Methods.Add(wide);
		Emit(method, (CilOpCodes.Ldc_I4_1, null), (CilOpCodes.Call, wide), (CilOpCodes.Ldc_I4_0, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Diagnostic, Does.Contain("System.Int64"));
	}
	[Test]
	public void TypedGateTreatsALocalEnumAsItsUnderlyingInteger()
	{
		var method = Method(); var module = method.DeclaringModule!;
		var kind = new TypeDefinition("", "Kind", TypeAttributes.Public | TypeAttributes.Sealed, module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "Enum"));
		kind.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RuntimeSpecialName, module.CorLibTypeFactory.Int32));
		module.TopLevelTypes.Add(kind); method.CilMethodBody!.LocalVariables.Add(new CilLocalVariable(kind.ToTypeSignature(true)));
		Emit(method, (CilOpCodes.Ldc_I4_1, null), (CilOpCodes.Stloc_0, null), (CilOpCodes.Ldloc_0, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Status, Is.EqualTo("TypedCilChecked"));
	}
	[Test]
	public void TypedGateRejectsOpcodesOutsideItsSubset()
	{
		var method = Method(); Emit(method, (CilOpCodes.Ldc_I4_1, null), (CilOpCodes.Box, method.DeclaringModule!.CorLibTypeFactory.Int32.ToTypeDefOrRef()), (CilOpCodes.Pop, null), (CilOpCodes.Ldc_I4_0, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Diagnostic, Does.Contain("unsupported opcode"));
	}
	[TestCase("Derived", "Base", true)]
	[TestCase("Derived", "IThing", true)]
	[TestCase("Other", "Base", false)]
	[TestCase("Base", "Derived", false)]
	public void TypedGateChecksReferenceHierarchyWithAResolutionContext(string argument, string returned, bool accepted)
	{
		var (method, context) = Hierarchy(argument, returned);
		Emit(method, (CilOpCodes.Ldarg_0, null), (CilOpCodes.Ret, null));
		var result = Il2CppTypedCilVerifier.Verify(method, context);
		Assert.That(result.Status, Is.EqualTo(accepted ? "TypedCilChecked" : "RejectedTypedCil"), result.Diagnostic);
	}
	[Test]
	public void WithoutAResolutionContextReferenceHierarchyIsNotChecked()
	{
		var (method, _) = Hierarchy("Other", "Base");
		Emit(method, (CilOpCodes.Ldarg_0, null), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method).Status, Is.EqualTo("TypedCilChecked"));
	}
	[Test]
	public void TypedGateRejectsAnUnrelatedReceiver()
	{
		var (method, context) = Hierarchy("Other", "Base");
		var module = method.DeclaringModule!; var baseType = module.TopLevelTypes.Single(t => t.Name == "Base");
		var get = new MethodDefinition("Get", MethodAttributes.Public, MethodSignature.CreateInstance(baseType.ToTypeSignature(false)));
		get.CilMethodBody = new(); get.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0); get.CilMethodBody.Instructions.Add(CilOpCodes.Ret); baseType.Methods.Add(get);
		Emit(method, (CilOpCodes.Ldarg_0, null), (CilOpCodes.Call, get), (CilOpCodes.Ret, null));
		Assert.That(Il2CppTypedCilVerifier.Verify(method, context).Diagnostic, Does.Contain("receiver"));
	}
	private static (MethodDefinition Method, RuntimeContext Context) Hierarchy(string argument, string returned)
	{
		AssemblyDefinition assembly = new("Hierarchy", new Version(1, 0)); ModuleDefinition module = new("Hierarchy.dll"); assembly.Modules.Add(module);
		var corlib = module.CorLibTypeFactory;
		TypeDefinition thing = new("", "IThing", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
		TypeDefinition baseType = new("", "Base", TypeAttributes.Public, corlib.Object.ToTypeDefOrRef());
		TypeDefinition derived = new("", "Derived", TypeAttributes.Public, baseType); derived.Interfaces.Add(new InterfaceImplementation(thing));
		TypeDefinition other = new("", "Other", TypeAttributes.Public, corlib.Object.ToTypeDefOrRef());
		TypeDefinition gate = new("", "Gate", TypeAttributes.Public, corlib.Object.ToTypeDefOrRef());
		foreach (var type in new[] { thing, baseType, derived, other, gate }) module.TopLevelTypes.Add(type);
		TypeSignature Find(string name) => module.TopLevelTypes.Single(t => t.Name == name).ToTypeSignature(false);
		MethodDefinition method = new("Value", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(Find(returned), [Find(argument)]));
		gate.Methods.Add(method); method.CilMethodBody = new() { MaxStack = 8, ComputeMaxStackOnBuild = false };
		RuntimeContext context = new(DotNetRuntimeInfo.NetCoreApp(10, 0), (bool?)null, (AssemblyDescriptor)corlib.CorLibScope);
		context.AddAssembly(assembly);
		return (method, context);
	}
	private Task<Il2CppWorkerRun> Run(string mode, int timeout = 10, CancellationToken token = default) => Il2CppWorkerClient.AnalyzeAsync(harness.Options(mode, timeout), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3, cancellationToken: token);
	private Task<Il2CppWorkerRun> RunCandidates(string mode) => Il2CppWorkerClient.AnalyzeAsync(harness.Options(mode), harness.Native, harness.Metadata, "6000.4.0f1", ScriptContentLevel.Level3,
		new Il2CppCandidateSelection("ManagedRoundTrip", ["Owned"]));
	private static MethodDefinition Method(Func<ModuleDefinition, TypeSignature[]>? parameters = null)
	{
		AssemblyDefinition assembly = new("Gate", new Version(1, 0)); ModuleDefinition module = new("Gate.dll"); assembly.Modules.Add(module);
		TypeDefinition type = new("", "Gate", TypeAttributes.Public); module.TopLevelTypes.Add(type);
		MethodDefinition method = new("Value", MethodAttributes.Public | MethodAttributes.Static, MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32, parameters?.Invoke(module) ?? []));
		// The pipeline computes MaxStack in the stack gate before typed verification; a fixed bound keeps unbalanced cases reachable here.
		type.Methods.Add(method); method.CilMethodBody = new() { MaxStack = 8, ComputeMaxStackOnBuild = false }; return method;
	}
	private static void Emit(MethodDefinition method, params (CilOpCode OpCode, object? Operand)[] instructions)
	{
		foreach (var (opCode, operand) in instructions) method.CilMethodBody!.Instructions.Add(new CilInstruction(opCode, operand));
	}
	private static void AssertStopped(int pid)
	{
		try { using Process process = Process.GetProcessById(pid); Assert.That(process.WaitForExit(2000), Is.True, $"Owned worker {pid} did not stop."); }
		catch (ArgumentException) { /* Already reaped. */ }
	}
	private sealed class ControlledStructure : PlatformGameStructure
	{
		public ControlledStructure(Il2CppWorkerTestHarness fixture) : base(fixture.DirectoryPath, LocalFileSystem.Instance)
		{
			GameDataPath = fixture.DirectoryPath; Il2CppGameAssemblyPath = fixture.Native; Il2CppMetaDataPath = fixture.Metadata; Version = UnityVersion.Parse("6000.4.0f1");
		}
	}
}
