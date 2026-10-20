using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Processing;
using AssetRipper.Processing.Assemblies;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;

namespace AssetRipper.Tests;

[Category("Il2CppCapability")]
[NonParallelizable]
public class Il2CppCapabilityTests
{
	private List<Cpp2IlProcessingLayer>? savedLayers;
	private AsmResolverDllOutputFormat? savedOutput;

	[SetUp]
	public void SaveHooks()
	{
		savedLayers = IL2CppManager.RecoveryProcessingLayers;
		savedOutput = IL2CppManager.RecoveryOutputFormat;
		IL2CppManager.RecoveryProcessingLayers = null;
		IL2CppManager.RecoveryOutputFormat = null;
	}

	[TearDown]
	public void RestoreHooks()
	{
		IL2CppManager.RecoveryProcessingLayers = savedLayers;
		IL2CppManager.RecoveryOutputFormat = savedOutput;
	}

	[TestCase(ScriptContentLevel.Level2)]
	[TestCase(ScriptContentLevel.Level3)]
	public void UnconfiguredSelectionReportsMinimalStubsWithoutClaimingValidation(ScriptContentLevel level)
	{
		using IL2CppManager manager = new(_ => { }, level);
		Il2CppRecoveryReport report = manager.PreviewRecoverySelection();
		Assert.That(report.RequestedContentLevel, Is.EqualTo(level.ToString()));
		Assert.That(report.UsesKnownStubOutput, Is.True);
		Assert.That(report.OutputFormat.TypeName, Is.EqualTo(typeof(AsmResolverDllOutputFormatDefault).FullName));
		Assert.That(report.OutputFormat.Id, Is.EqualTo("dll_default"));
		Assert.That(report.OutputFormat.AssemblyName, Is.EqualTo("Cpp2IL.Core"));
		Assert.That(report.OutputFormat.AssemblyVersion, Is.EqualTo("1.0.9.0"));
		Assert.That(report.ProcessingLayers.Select(p => p.TypeName), Is.EqualTo(IL2CppManager.DefaultProcessingLayers.Select(p => p.GetType().FullName)));
		Assert.That(report.MethodBodies, Is.EqualTo(new Il2CppMethodBodyCounts(0, 0, 0)));
		Assert.That(report.Validation, Is.EqualTo("NotRun"));
		Assert.That(manager.Il2CppRecovery, Is.Null, "A preview is not a completed import.");
	}

	[Test]
	public void Level2IgnoresRecoveryHooks()
	{
		IL2CppManager.RecoveryProcessingLayers = [new ControlledLayer()];
		IL2CppManager.RecoveryOutputFormat = new CustomDefaultOutput();
		using IL2CppManager manager = new(_ => { }, ScriptContentLevel.Level2);
		Il2CppRecoveryReport report = manager.PreviewRecoverySelection();
		Assert.That(report.UsesKnownStubOutput, Is.True);
		Assert.That(report.OutputFormat.TypeName, Is.EqualTo(typeof(AsmResolverDllOutputFormatDefault).FullName));
		Assert.That(report.ProcessingLayers.Any(p => p.Id == "controlled"), Is.False);
	}

	[Test]
	public void ExplicitDefaultRemainsKnownAtLevel3()
	{
		IL2CppManager.RecoveryOutputFormat = new AsmResolverDllOutputFormatDefault();
		IL2CppManager.RecoveryProcessingLayers = [];
		using IL2CppManager manager = new(_ => { }, ScriptContentLevel.Level3);
		Il2CppRecoveryReport report = manager.PreviewRecoverySelection();
		Assert.That(report.UsesKnownStubOutput, Is.True);
		Assert.That(report.ProcessingLayers, Is.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void CustomAndRecoveryProvidersRemainUnverified(bool builtInRecovery)
	{
		IL2CppManager.RecoveryOutputFormat = builtInRecovery ? new AsmResolverDllOutputFormatIlRecovery() : new CustomDefaultOutput();
		IL2CppManager.RecoveryProcessingLayers = [new ControlledLayer()];
		using IL2CppManager manager = new(_ => { }, ScriptContentLevel.Level3);
		Il2CppRecoveryReport report = manager.PreviewRecoverySelection();
		Assert.That(report.UsesKnownStubOutput, Is.False);
		Assert.That(report.OutputFormat.TypeName, Is.EqualTo(IL2CppManager.RecoveryOutputFormat.GetType().FullName));
		Assert.That(report.ProcessingLayers.Single().TypeName, Is.EqualTo(typeof(ControlledLayer).FullName));
		Assert.That(report.ProcessingLayers.Single().Id, Is.EqualTo("controlled"));
		Assert.That(report.Validation, Is.EqualTo("NotRun"));
	}

	[Test]
	public void SelectionSnapshotsBothHooksBeforeProviderCallbacks()
	{
		List<Cpp2IlProcessingLayer> originalLayers = [new ControlledLayer()];
		IL2CppManager.RecoveryProcessingLayers = originalLayers;
		IL2CppManager.RecoveryOutputFormat = new MutatingOutput();
		using IL2CppManager manager = new(_ => { }, ScriptContentLevel.Level3);
		Il2CppRecoveryReport report = manager.PreviewRecoverySelection();
		Assert.That(report.OutputFormat.TypeName, Is.EqualTo(typeof(MutatingOutput).FullName));
		Assert.That(report.OutputFormat.Id, Is.EqualTo("mutating"));
		Assert.That(report.ProcessingLayers.Single().Id, Is.EqualTo("controlled"));
		originalLayers.Clear();
		Assert.That(report.ProcessingLayers, Has.Count.EqualTo(1));
		Assert.That(manager.PreviewRecoverySelection().OutputFormat.TypeName, Is.EqualTo(typeof(AsmResolverDllOutputFormatDefault).FullName));
		Assert.That(manager.PreviewRecoverySelection().ProcessingLayers, Is.Empty);
	}

	[TestCase(true)]
	[TestCase(false)]
	public void CountsDistinguishBodiesByDesignAndNeverInferCustomRecovery(bool knownStub)
	{
		AssemblyDefinition assembly = CreateAssembly();
		TypeDefinition type = assembly.ManifestModule!.TopLevelTypes.Single(t => t.Name == "Fixture");
		MethodDefinition present = AddMethod(type, "Present");
		present.CilMethodBody = new();
		present.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4, 37);
		present.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		AddMethod(type, "Missing");
		AddMethod(type, "Empty").CilMethodBody = new();
		AddMethod(type, "Abstract").IsAbstract = true;
		AddMethod(type, "PInvoke").IsPInvokeImpl = true;
		AddMethod(type, "Internal").IsInternalCall = true;
		AddMethod(type, "Native").IsNative = true;
		AddMethod(type, "Runtime").IsRuntime = true;
		TypeDefinition nested = new("", "Nested", TypeAttributes.NestedPublic);
		type.NestedTypes.Add(nested);
		MethodDefinition nestedMethod = AddMethod(nested, "NestedMethod");
		nestedMethod.CilMethodBody = new();
		nestedMethod.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_0);
		nestedMethod.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		Il2CppRecoveryReport report = Selection(knownStub).WithMethodBodies([assembly]);
		Assert.That(report.MethodBodies, Is.EqualTo(knownStub ? new Il2CppMethodBodyCounts(2, 5, 2) : new Il2CppMethodBodyCounts(0, 5, 4)));
		Assert.That(report.Validation, Is.EqualTo("NotRun"));
	}

	[Test]
	public void NonIl2CppManagersHaveNoRecoveryReport()
	{
		using BaseManager unknown = new(_ => { });
		using MonoManager mono = new(_ => { });
		Assert.That(((IAssemblyManager)unknown).Il2CppRecovery, Is.Null);
		Assert.That(((IAssemblyManager)mono).Il2CppRecovery, Is.Null);
	}

	[TestCase(ScriptContentLevel.Level3, ScriptingBackend.IL2Cpp, true, true)]
	[TestCase(ScriptContentLevel.Level3, ScriptingBackend.IL2Cpp, false, false)]
	[TestCase(ScriptContentLevel.Level3, ScriptingBackend.IL2Cpp, null, false)]
	[TestCase(ScriptContentLevel.Level2, ScriptingBackend.IL2Cpp, null, true)]
	[TestCase(ScriptContentLevel.Level3, ScriptingBackend.Mono, true, false)]
	[TestCase(ScriptContentLevel.Level2, ScriptingBackend.Mono, null, false)]
	[TestCase(ScriptContentLevel.Level1, ScriptingBackend.Mono, null, true)]
	public void ByReferenceRepairPreservesCustomLevel3Bodies(ScriptContentLevel level, ScriptingBackend backend, bool? knownStub, bool expectRepair)
	{
		using ControlledManager manager = new(backend, knownStub.HasValue ? Selection(knownStub.Value) : null);
		AssemblyDefinition assembly = CreateAssembly();
		TypeDefinition type = assembly.ManifestModule!.TopLevelTypes.Single(t => t.Name == "Fixture");
		MethodDefinition method = new("ReturnReference", MethodAttributes.Public | MethodAttributes.Static,
			MethodSignature.CreateStatic(new ByReferenceTypeSignature(assembly.ManifestModule.CorLibTypeFactory.Int32)));
		type.Methods.Add(method);
		CilMethodBody originalBody = new();
		originalBody.Instructions.Add(CilOpCodes.Ldnull);
		originalBody.Instructions.Add(CilOpCodes.Ret);
		method.CilMethodBody = originalBody;
		manager.Add(assembly);
		GameData data = new(new GameBundle(), new(6000, 4, 0), manager, null);
		new NullRefReturnProcessor(level).Process(data);
		if (expectRepair)
		{
			Assert.That(method.CilMethodBody, Is.Not.SameAs(originalBody));
			Assert.That(method.CilMethodBody!.Instructions.Select(i => i.OpCode), Is.EqualTo(new[] { CilOpCodes.Ldnull, CilOpCodes.Throw }));
		}
		else
		{
			Assert.That(method.CilMethodBody, Is.SameAs(originalBody));
		}
	}

	private static Il2CppRecoveryReport Selection(bool knownStub)
	{
		IL2CppManager.RecoveryOutputFormat = knownStub ? null : new CustomDefaultOutput();
		using IL2CppManager manager = new(_ => { }, ScriptContentLevel.Level3);
		return manager.PreviewRecoverySelection();
	}

	private static AssemblyDefinition CreateAssembly()
	{
		AssemblyDefinition source = AssemblyDefinition.FromFile(typeof(Il2CppCapabilityTests).Assembly.Location, createRuntimeContext: false);
		AssemblyDefinition assembly = new("CapabilityFixture", new(1, 0, 0, 0));
		ModuleDefinition module = new("CapabilityFixture.dll", source.ManifestModule!.AssemblyReferences.First(a => a.Name == "System.Runtime"));
		assembly.Modules.Add(module);
		module.TopLevelTypes.Add(new("", "Fixture", TypeAttributes.Public));
		return assembly;
	}

	private static MethodDefinition AddMethod(TypeDefinition type, string name)
	{
		MethodDefinition method = new(name, MethodAttributes.Public | MethodAttributes.Static,
			MethodSignature.CreateStatic(type.DeclaringModule!.CorLibTypeFactory.Int32));
		type.Methods.Add(method);
		return method;
	}

	private sealed class CustomDefaultOutput : AsmResolverDllOutputFormatDefault;

	private sealed class MutatingOutput : AsmResolverDllOutputFormatDefault
	{
		public override string OutputFormatId
		{
			get
			{
				IL2CppManager.RecoveryOutputFormat = new AsmResolverDllOutputFormatDefault();
				IL2CppManager.RecoveryProcessingLayers = [];
				return "mutating";
			}
		}
	}

	private sealed class ControlledLayer : Cpp2IlProcessingLayer
	{
		public override string Name => "Controlled layer";
		public override string Id => "controlled";
		public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null) => throw new InvalidOperationException("This test must not analyze a native binary.");
	}

	private sealed class ControlledManager(ScriptingBackend backend, Il2CppRecoveryReport? report) : BaseManager(_ => { })
	{
		public override ScriptingBackend ScriptingBackend => backend;
		public override Il2CppRecoveryReport? Il2CppRecovery => report;
	}
}
