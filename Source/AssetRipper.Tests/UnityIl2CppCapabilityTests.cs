using AsmResolver.DotNet;
using AssetRipper.CIL;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Tests.Decompilation;
using Cpp2IL.Core;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("Il2CppNative")]
[NonParallelizable]
public class UnityIl2CppCapabilityTests
{
	[Test]
	public void OwnedNativeFixtureMeasuresStubsAndRecoveryCandidatesSeparately()
	{
		UnityIl2CppFixture fixture = UnityIl2CppFixture.Require();
		string output = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "il2cpp-capability", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(output);
		File.Copy(Path.Combine(fixture.DirectoryPath, "fixture-manifest.json"), Path.Combine(output, "fixture-manifest.json"));
		string oracle = Path.Combine(output, "oracle.json"); File.Copy(Path.Combine(fixture.DirectoryPath, "oracle.json"), oracle);
		Il2CppReplayResult mono = Il2CppReplayRunner.Run(Path.Combine(fixture.DirectoryPath, UnityIl2CppFixture.MonoAssemblyPath), oracle, Path.Combine(output, "MonoReplay"));
		Assert.That(mono.Status, Is.EqualTo("Passed"), JsonSerializer.Serialize(mono));
		var oldOutput = IL2CppManager.RecoveryOutputFormat; var oldLayers = IL2CppManager.RecoveryProcessingLayers;
		try
		{
			IL2CppManager.RecoveryOutputFormat = null; IL2CppManager.RecoveryProcessingLayers = null;
			using IL2CppManager manager = new(_ => { }, ScriptContentLevel.Level3);
			manager.Initialize(fixture.CreateStructure());
			var selection = manager.Il2CppRecovery!;
			Assert.That(selection.UsesKnownStubOutput, Is.True);
			Assert.That(selection.MethodBodies.GeneratedStub, Is.GreaterThan(0));
			Assert.That(selection.Validation, Is.EqualTo("NotRun"));
			var stubAssembly = manager.GetAssemblies().Single(a => a.Name == "Assembly-CSharp");
			string stubPath = Path.Combine(output, "Default", "Assembly-CSharp.dll"); Directory.CreateDirectory(Path.GetDirectoryName(stubPath)!);
			stubAssembly.Write(stubPath);
			var stubs = Il2CppReplayRunner.Run(stubPath, oracle, Path.Combine(output, "DefaultReplay"));
			Assert.That(stubs.Status, Is.EqualTo("Failed"), "Minimal bodies must fail the independent behavior oracle.");
			var provider = new OwnedProbeOutput();
			var candidates = provider.BuildAssemblies(Cpp2IlApi.CurrentAppContext!);
			string candidatePath = Path.Combine(output, "Candidate", "Assembly-CSharp.dll"); Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);
			candidates.Single(a => a.Name == "Assembly-CSharp").Write(candidatePath);
			var replay = Il2CppReplayRunner.Run(candidatePath, oracle, Path.Combine(output, "CandidateReplay"));
			// Retain the provider's untouched output above. Measure a separate, explicit structural repair.
			var stackAssembly = AssemblyDefinition.FromBytes(File.ReadAllBytes(candidatePath), createRuntimeContext: false);
			List<object> stackChecks = [];
			foreach (var method in stackAssembly.ManifestModule!.GetAllTypes().Where(t => t.FullName is "AssetRipper.Il2CppFixture.PureMethods" or "AssetRipper.Il2CppFixture.Counter")
				.SelectMany(t => t.Methods).Where(m => m.Name != ".ctor" && m.CilMethodBody is not null))
			{
				var body = method.CilMethodBody!; int before = body.MaxStack;
				try
				{
					body.MaxStack = body.ComputeMaxStack(); body.ComputeMaxStackOnBuild = false;
					stackChecks.Add(new { Method = method.Name?.ToString(), Before = before, After = body.MaxStack, Status = "StackHeightChecked", Error = (string?)null });
				}
				catch (Exception e) { stackChecks.Add(new { Method = method.Name?.ToString(), Before = before, After = body.MaxStack, Status = "FailedUnchanged", Error = e.Message }); }
			}
			string stackPath = Path.Combine(output, "StackRebuilt", "Assembly-CSharp.dll"); Directory.CreateDirectory(Path.GetDirectoryName(stackPath)!); stackAssembly.Write(stackPath);
			var stackReplay = Il2CppReplayRunner.Run(stackPath, oracle, Path.Combine(output, "StackRebuiltReplay"));
			var methods = provider.Methods.OrderBy(m => m.Signature, StringComparer.Ordinal).ToArray();
			List<EditorFixtureFile> evidence = [];
			foreach (var item in new[] { (Source: Path.Combine(fixture.DirectoryPath, UnityIl2CppFixture.MonoAssemblyPath), Name: "Assemblies/Mono.dll"),
				(Source: stubPath, Name: "Assemblies/Default.dll"), (Source: candidatePath, Name: "Assemblies/Candidate.dll"), (Source: stackPath, Name: "Assemblies/StackRebuilt.dll"),
				(Source: Path.Combine(fixture.DirectoryPath, "Sources/Corpus.cs"), Name: "Sources/Corpus.cs"),
				(Source: Path.Combine(fixture.DirectoryPath, "Sources/Il2CppFixtureBuilder.cs"), Name: "Sources/Il2CppFixtureBuilder.cs"),
				(Source: Path.Combine(output, "CandidateReplay/Il2CppReplay.cs"), Name: "Sources/Il2CppReplay.cs") })
			{
				string path = Path.Combine(output, "Evidence", item.Name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(item.Source, path);
				evidence.Add(new("Evidence/" + item.Name, FixtureCompiler.Hash(File.ReadAllBytes(path))));
			}
			var providerAssembly = typeof(AsmResolverDllOutputFormat).Assembly;
			File.WriteAllText(Path.Combine(output, "capability.json"), JsonSerializer.Serialize(new
			{
				SchemaVersion = 1, UnityVersion = fixture.Manifest.UnityVersion, Architecture = "x86_64", InputManifest = "fixture-manifest.json",
				DefaultPipeline = selection, MonoReplay = mono, DefaultReplay = stubs,
				ProviderIdentity = new { Name = providerAssembly.GetName().Name, Version = providerAssembly.GetName().Version?.ToString(), Mvid = providerAssembly.ManifestModule.ModuleVersionId,
					Sha256 = FixtureCompiler.Hash(File.ReadAllBytes(providerAssembly.Location)) }, EvidenceFiles = evidence,
				ReplayReferenceProfile = FixtureCompiler.ReferenceProfile,
				CandidateProvider = "Instrumented subset of Cpp2IL.Core.IlGenerator; non-fixture methods remain minimal stubs",
				CandidateMethods = methods, CandidateReplay = replay, StackChecks = stackChecks, StackRebuiltReplay = stackReplay,
				PerMethodObservations = stackReplay.Cases.GroupBy(c => c.Method).Select(g => new { Method = g.Key, Total = g.Count(), Matched = g.Count(c => c.Matched), Status = g.All(c => c.Matched) ? "PassedForObservedInputs" : "Failed" }).ToArray(),
				ValidatedScope = "20 observations; constructor execution, exception-region reconstruction and general engine behavior are not established",
			}, new JsonSerializerOptions { WriteIndented = true }));
			Assert.That(methods.Select(m => m.Name), Is.EquivalentTo(new[] { "Affine", "Branch", "Sum", "Divide", "Echo", "Step" }));
			Assert.That(methods.All(m => m.Status != "NotRun"), Is.True);
			Assert.That(replay.ExitCode, Is.EqualTo(0), JsonSerializer.Serialize(replay));
			Assert.That(replay.Cases, Has.Length.EqualTo(20), "A candidate may fail behavior, but every oracle case must be observed.");
			Assert.That(stackReplay.ExitCode, Is.EqualTo(0), JsonSerializer.Serialize(stackReplay));
			Assert.That(stackReplay.Cases, Has.Length.EqualTo(20));
			var simple = stackReplay.Cases.Where(c => c.Method is "Step" or "Echo").ToArray();
			Assert.That(simple, Has.Length.EqualTo(4));
			Assert.That(simple.All(c => c.Matched), Is.True, "The pinned provider plus checked stack reconstruction must preserve the four observed field/generic cases.");
			TestContext.Out.WriteLine($"Evidence: {output}; raw candidate matched {replay.Cases.Count(c => c.Matched)}/20, stack-rebuilt candidate {stackReplay.Cases.Count(c => c.Matched)}/20; this test measures capability and does not assert full recovery.");
		}
		finally { IL2CppManager.RecoveryOutputFormat = oldOutput; IL2CppManager.RecoveryProcessingLayers = oldLayers; }
	}

	private sealed record ProbeMethod(string Name, string Signature, uint OriginalToken, ulong Pointer, ulong Rva, long? RawOffset,
		int SharedAddressMethods, int IsilInstructions, string Status, string? Error, string[] Cil);
	private sealed class OwnedProbeOutput : AsmResolverDllOutputFormat
	{
		public override string OutputFormatId => "owned_fixture_probe";
		public override string OutputFormatName => "Owned fixture only, instrumented IL candidate";
		public ConcurrentBag<ProbeMethod> Methods { get; } = [];
		protected override void FillMethodBody(MethodDefinition definition, MethodAnalysisContext context)
		{
			if (!definition.IsManagedMethodWithBody()) return;
			definition.ReplaceMethodBodyWithMinimalImplementation();
			if (context.DeclaringType?.DeclaringAssembly.Name != "Assembly-CSharp"
				|| !(context.DeclaringType.FullName == "AssetRipper.Il2CppFixture.PureMethods" && context.Name is "Affine" or "Branch" or "Sum" or "Divide" or "Echo"
					|| context.DeclaringType.FullName == "AssetRipper.Il2CppFixture.Counter" && context.Name == "Step")) return;
			string status = "NotRun"; string? error = null; int isil = 0;
			ulong pointer = context.UnderlyingPointer;
			long? raw = context.AppContext.Binary.TryMapVirtualAddressToRaw(pointer, out long offset) ? offset : null;
			int sharing = context.AppContext.MethodsByAddress.TryGetValue(pointer, out var shared) ? shared.Count : 0;
			try
			{
				if (pointer == 0) status = "NoNativeBody";
				else
				{
					context.Analyze(); isil = context.ConvertedIsil.Count;
					if (isil == 0) status = "EmptyIsilStub";
					else { definition.CilMethodBody = new(); IlGenerator.GenerateIl(context, definition); status = "Candidate"; }
				}
			}
			catch (Exception e) { status = "AnalysisFailedStub"; error = e.GetType().FullName + ": " + e.Message; definition.ReplaceMethodBodyWithMinimalImplementation(); }
			finally
			{
				definition.CilMethodBody?.Instructions.CalculateOffsets();
				Methods.Add(new(context.Name, context.FullNameWithSignature, context.Token, pointer, context.Rva, raw, sharing, isil, status, error,
					definition.CilMethodBody?.Instructions.Select(i => i.ToString()).ToArray() ?? []));
				context.ReleaseAnalysisData();
			}
		}
	}
}
