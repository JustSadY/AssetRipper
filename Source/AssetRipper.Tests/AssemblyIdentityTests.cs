using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.Scripts;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.Tests.Fixtures;
using ICSharpCode.Decompiler.Metadata;
using System.Security.Cryptography;
using System.Text.Json;
using PrimaryScriptCollection = AssetRipper.Export.PrimaryContent.Scripts.ScriptExportCollection;

namespace AssetRipper.Tests;

public class AssemblyIdentityTests
{
	[Test]
	public void DuplicateFilenamesSurviveDiscoveryAndMonoInitialization()
	{
		VirtualFileSystem fs = new();
		byte[] first = Bytes(Fixture("Twin", 1));
		byte[] second = Bytes(Fixture("Twin", 2));
		Write(fs, "/Input/A/Twin.dll", first);
		Write(fs, "/Input/B/Twin.dll", second);
		MixedGameStructure structure = new(["/Input"], fs);
		Assert.That(structure.AssemblyCandidates.Count(), Is.EqualTo(2));
		Assert.That(structure.RequestAssembly("Twin"), Is.Null);
		using MonoManager manager = new(_ => { });
		manager.Initialize(structure);
		AssemblyDefinition[] twins = manager.GetAssemblies().Where(a => a.Name == "Twin").ToArray();
		Assert.That(twins, Has.Length.EqualTo(2));
		Assert.That(twins.Select(a => a.Version.Major).Order(), Is.EqualTo(new[] { 1, 2 }));
		Assert.That(manager.GetSourceInfo(twins[0])!.OriginalSha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(first))));
		Assert.That(manager.HasAssemblyNameCollisions, Is.True);
		Assert.That(manager.ResolutionDiagnostics.Any(d => d.Kind == "SimpleNameCollision"), Is.True);
	}

	[Test]
	public void FilenameAliasesDoNotDuplicateEnumerationAndMissesDoNotPoisonLaterLoads()
	{
		using MonoManager manager = new(_ => { });
		ScriptIdentifier id = new("Actual", "AssetRipper.Tests.Fixtures", nameof(DecompilationSample));
		Assert.That(manager.IsPresent(id), Is.False);
		Assert.That(manager.IsAssemblyLoaded("Actual"), Is.False);
		AssemblyDefinition loaded = manager.Read(new MemoryStream(Bytes(Fixture("Actual", 1))), "/Input/Renamed.dll");
		Assert.That(manager.GetAssemblies().Count(), Is.EqualTo(1));
		Assert.That(manager.IsAssemblyLoaded("Renamed"), Is.True);
		Assert.That(manager.IsPresent(id), Is.True);
		Assert.That(manager.IsPresent(new("Renamed", id.Namespace, id.Name)), Is.True);
		manager.Add(loaded);
		Assert.That(manager.GetAssemblies().Count(), Is.EqualTo(1));
	}

	[Test]
	public void SameIdentityCandidatesHaveIndependentStreamsAndCannotBeResolvedByReference()
	{
		using BaseManager manager = new(_ => { });
		AssemblyDefinition first = Fixture("Twin", 1), second = Fixture("Twin", 1);
		manager.Add(first); manager.Add(second);
		Assert.That(manager.GetAssemblies().Count(), Is.EqualTo(2));
		Assert.That(manager.GetStreamForAssembly(first), Is.Not.SameAs(manager.GetStreamForAssembly(second)));
		ILSpyAssemblyResolver resolver = new(manager);
		Assert.That(resolver.Resolve(first), Is.Not.SameAs(resolver.Resolve(second)));
		Assert.That(resolver.Resolve(Reference("Twin", 1)), Is.Null);
		Assert.That(resolver.Diagnostics.Single().Kind, Is.EqualTo("AmbiguousIdentity"));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ExactVersionResolutionIsIndependentOfLoadAndCacheOrder(bool reverse)
	{
		using BaseManager manager = new(_ => { });
		AssemblyDefinition first = Fixture("Twin", 1), second = Fixture("Twin", 2);
		foreach (AssemblyDefinition a in reverse ? new[] { second, first } : new[] { first, second }) manager.Add(a);
		ILSpyAssemblyResolver resolver = new(manager);
		Assert.That(resolver.Resolve(Reference("Twin", 2)), Is.SameAs(resolver.Resolve(second)));
		Assert.That(resolver.Resolve(Reference("Twin", 1)), Is.SameAs(resolver.Resolve(first)));
		Assert.That(resolver.Resolve(Reference("Twin", 2)), Is.SameAs(resolver.Resolve(second)));
		Assert.That(resolver.Diagnostics, Is.Empty);
		Assert.That(resolver.ResolveModule(resolver.Resolve(second), "Twin.dll"), Is.SameAs(resolver.Resolve(second)));
		Assert.That(resolver.Resolve(Reference("Twin", 3)), Is.Null);
		Assert.That(resolver.Diagnostics.Single().Kind, Is.EqualTo("AmbiguousVersion"));
	}

	[TestCase("culture")]
	[TestCase("token")]
	public void IdentityMismatchDoesNotUseUniqueSimpleNameFallback(string component)
	{
		using BaseManager manager = new(_ => { });
		manager.Add(Fixture("Unique", 1));
		TestReference reference = Reference("Unique", 1);
		if (component == "culture") reference.Culture = "tr-TR";
		else reference.PublicKeyToken = [1, 2, 3, 4, 5, 6, 7, 8];
		ILSpyAssemblyResolver resolver = new(manager);
		Assert.That(resolver.Resolve(reference), Is.Null);
		Assert.That(resolver.Diagnostics.Single().Kind, Is.EqualTo("IdentityMismatch"));
	}

	[TestCase("culture")]
	[TestCase("token")]
	public void ExactIdentitySelectsTheMatchingCultureOrPublisher(string component)
	{
		using BaseManager manager = new(_ => { });
		AssemblyDefinition first = Fixture("Twin", 1), second = Fixture("Twin", 1);
		if (component == "culture") second.Culture = "tr-TR";
		else second.PublicKey = AssemblyDefinition.FromBytes(Basic.Reference.Assemblies.Net100.ReferenceInfos.SystemRuntime.ImageBytes, createRuntimeContext: false).PublicKey;
		manager.Add(first); manager.Add(second);
		List<AssemblyResolutionDiagnostic> diagnostics = [];
		Assert.That(AssemblyResolution.Resolve(manager, AssemblyIdentity.From(second), false, diagnostics.Add), Is.SameAs(second));
		Assert.That(AssemblyResolution.Resolve(manager, AssemblyIdentity.From(first), false, diagnostics.Add), Is.SameAs(first));
		Assert.That(diagnostics, Is.Empty);
	}

	[Test]
	public void UniqueVersionFallbackIsVisibleAndMissingResolutionIsNotCached()
	{
		using BaseManager manager = new(_ => { });
		ILSpyAssemblyResolver resolver = new(manager);
		Assert.That(resolver.Resolve(Reference("Unique", 1)), Is.Null);
		AssemblyDefinition assembly = Fixture("Unique", 2);
		manager.Add(assembly);
		Assert.That(resolver.Resolve(Reference("Unique", 1)), Is.SameAs(resolver.Resolve(assembly)));
		Assert.That(resolver.Diagnostics.Select(d => d.Kind), Is.EqualTo(new[] { "Missing", "UniqueVersionFallback" }));
		Assert.That(resolver.Diagnostics.All(d => !d.IsFailure), Is.True);
	}

	[TestCase("System.Runtime.InteropServices")]
	[TestCase("System.Runtime.CompilerServices.Unsafe")]
	public void OptionalILSpyProbeDoesNotBecomeARequiredPublisherMismatch(string name)
	{
		using BaseManager manager = new(_ => { });
		byte[] bytes = Basic.Reference.Assemblies.Net100.ReferenceInfos.AllValues.Single(r => r.FileName == name + ".dll").ImageBytes;
		manager.Read(new MemoryStream(bytes, writable: false), name + ".dll");
		ILSpyAssemblyResolver resolver = new(manager);
		Assert.That(resolver.Resolve(AssemblyNameReference.Parse(name + ", Version=10.0.0.0, Culture=neutral")), Is.Null);
		Assert.That(resolver.Diagnostics.Single().Kind, Is.EqualTo("ImplicitFrameworkProbeUnresolved"));
		Assert.That(resolver.Diagnostics.Single().IsFailure, Is.False);
		// A required unsigned reference with the same spelling still cannot select the signed DLL.
		AssemblyDefinition consumer = Fixture("Consumer", 1);
		AsmResolver.DotNet.AssemblyReference required = new(name, new Version(10, 0, 0, 0));
		// The PE builder drops unused references, so encode a use in a type's base signature.
		consumer.ManifestModule!.TopLevelTypes.Single(t => t.Name == nameof(DecompilationSample)).BaseType =
			new TypeReference(consumer.ManifestModule, required, "FixtureDependency", "RequiredBase");
		manager.Add(consumer);
		PEFile consumerFile = resolver.Resolve(consumer);
		var handle = consumerFile.Metadata.AssemblyReferences.Single(h =>
			consumerFile.Metadata.GetString(consumerFile.Metadata.GetAssemblyReference(h).Name) == name);
		Assert.That(resolver.Resolve(new ICSharpCode.Decompiler.Metadata.AssemblyReference(consumerFile, handle)), Is.Null);
		Assert.That(resolver.Diagnostics.Last().Kind, Is.EqualTo("IdentityMismatch"));
		Assert.That(resolver.Diagnostics.Last().IsFailure, Is.True);
	}

	[TestCase("Culture=tr-TR")]
	[TestCase("Culture=neutral, PublicKeyToken=0102030405060708")]
	public void SyntheticQueriesWithExplicitCultureOrPublisherRemainRequired(string identityPart)
	{
		using BaseManager manager = new(_ => { });
		string name = "System.Runtime.InteropServices";
		byte[] bytes = Basic.Reference.Assemblies.Net100.ReferenceInfos.AllValues.Single(r => r.FileName == name + ".dll").ImageBytes;
		manager.Read(new MemoryStream(bytes, writable: false), name + ".dll");
		ILSpyAssemblyResolver resolver = new(manager);
		Assert.That(resolver.Resolve(AssemblyNameReference.Parse(name + ", Version=10.0.0.0, " + identityPart)), Is.Null);
		Assert.That(resolver.Diagnostics.Single().Kind, Is.EqualTo("IdentityMismatch"));
		Assert.That(resolver.Diagnostics.Single().IsFailure, Is.True);
	}

	[Test]
	public void AmbiguousMonoBehaviourTypeDoesNotSelectAFieldSchema()
	{
		using MonoManager manager = new(_ => { });
		manager.Add(Fixture("Twin", 1)); manager.Add(Fixture("Twin", 2));
		ScriptIdentifier id = new("Twin", typeof(DecompilationSample).Namespace!, nameof(DecompilationSample));
		Assert.That(manager.IsPresent(id), Is.False);
		Assert.That(manager.TryGetSerializableType(id, new(2022, 3), out var schema, out string? reason), Is.False);
		Assert.That(schema, Is.Null);
		Assert.That(reason, Does.Contain("collisions"));
	}

	[Test]
	public void CandidateOutputPathsDoNotDependOnEnumerationOrder()
	{
		AssemblyDefinition first = Fixture("Twin", 1), second = Fixture("Twin", 2);
		using BaseManager forward = new(_ => { }), reverse = new(_ => { });
		forward.Add(first); forward.Add(second); reverse.Add(second); reverse.Add(first);
		AssemblyExportLayout a = new(forward), b = new(reverse);
		Assert.That(a.GetStem(first), Is.Not.EqualTo(a.GetStem(second)));
		Assert.That(a.GetStem(first), Is.EqualTo(b.GetStem(first)));
		Assert.That(a.GetStem(second), Is.EqualTo(b.GetStem(second)));
	}

	[Test]
	public void SameIdentityAndMvidWithDifferentBytesRemainSeparateCandidates()
	{
		AssemblyDefinition first = Fixture("Twin", 1), second = Fixture("Twin", 1);
		second.ManifestModule!.Mvid = first.ManifestModule!.Mvid;
		second.ManifestModule.TopLevelTypes.Single(t => t.Name == nameof(DecompilationSample)).Name = "DifferentContent";
		using BaseManager manager = new(_ => { });
		AssemblyDefinition a = manager.Read(new MemoryStream(Bytes(first)), "/A/Twin.dll");
		AssemblyDefinition b = manager.Read(new MemoryStream(Bytes(second)), "/B/Twin.dll");
		Assert.That(manager.GetSourceInfo(a)!.OriginalSha256, Is.Not.EqualTo(manager.GetSourceInfo(b)!.OriginalSha256));
		AssemblyExportLayout layout = new(manager);
		Assert.That(layout.GetStem(a), Is.Not.EqualTo(layout.GetStem(b)));
		Assert.That(manager.GetStreamForAssembly(a), Is.Not.SameAs(manager.GetStreamForAssembly(b)));
	}

	[Test]
	public void PrimaryContentArchivesAssembliesEvenWhenThereAreNoMonoScriptAssets()
	{
		using BaseManager manager = new(_ => { });
		manager.Add(Fixture("Twin", 1)); manager.Add(Fixture("Twin", 2));
		GameBundle bundle = new();
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(new(bundle, new(2022, 3), manager, null), settings);
		VirtualFileSystem fs = new();
		exporter.Export(bundle, settings, fs);
		Assert.That(exporter.LastExportSucceeded, Is.False);
		Assert.That(fs.Directory.EnumerateFiles("/Output/Assemblies", "*.dll").Count(), Is.EqualTo(2));
	}

	[Test]
	public void CollidingJobsDoNotStubMethodBodiesThroughTheSharedContext()
	{
		using MonoManager manager = new(_ => { });
		manager.Initialize(new MixedGameStructure([], LocalFileSystem.Instance));
		AssemblyDefinition a = Fixture("Twin", 1), b = Fixture("Twin", 2);
		manager.Add(a); manager.Add(b);
		byte[] before = manager.GetStreamForAssembly(a).ReadAllBytesForTest();
		MethodDefinition sum = a.ManifestModule!.TopLevelTypes.Single(t => t.Name == nameof(DecompilationSample)).Methods.Single(m => m.Name == "Sum");
		int instructionCount = sum.CilMethodBody!.Instructions.Count;
		GameBundle bundle = new();
		bundle.AddNewProcessedCollection("Fixture", new(2022, 3));
		FullConfiguration settings = new();
		settings.ImportSettings.ScriptContentLevel = AssetRipper.Import.Configuration.ScriptContentLevel.Level1;
		new ExportHandler(settings).Process(new(bundle, new(2022, 3), manager, null));
		Assert.That(manager.GetStreamForAssembly(a).ReadAllBytesForTest(), Is.EqualTo(before));
		Assert.That(sum.CilMethodBody!.Instructions.Count, Is.EqualTo(instructionCount));
	}

	[Test]
	public void PrimaryContentPreservesAndDecompilesBothVersionsWithoutOverwriting()
	{
		using BaseManager manager = new(_ => { });
		manager.Add(Fixture("Twin", 1)); manager.Add(Fixture("Twin", 2));
		VirtualFileSystem fs = new();
		PrimaryScriptCollection collection = new(new(manager));
		Assert.That(collection.Export("/Output", fs), Is.False);
		Assert.That(fs.Directory.EnumerateFiles("/Output/Assemblies", "*.dll").Count(), Is.EqualTo(2));
		Assert.That(collection.LastReport!.Assemblies, Has.Count.EqualTo(2));
		Assert.That(collection.LastReport.Assemblies.Select(a => a.Identity).Distinct().Count(), Is.EqualTo(2));
		string[] sources = fs.Directory.EnumerateFiles("/Output/Scripts", "*.cs", SearchOption.AllDirectories)
			.Where(p => fs.Path.GetFileName(p) == "DecompilationSample.cs").ToArray();
		Assert.That(sources, Has.Length.EqualTo(2));
		Assert.That(sources.All(p => fs.File.ReadAllText(p).Contains("Value += amount", StringComparison.Ordinal)), Is.True);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void BothExportModesKeepAllDllsAndReportContextLimitations(bool primary)
	{
		using MonoManager manager = new(_ => { });
		manager.Initialize(new MixedGameStructure([], LocalFileSystem.Instance));
		manager.Add(Fixture("Twin", 1)); manager.Add(Fixture("Twin", 2));
		GameBundle bundle = new();
		var assets = bundle.AddNewProcessedCollection("Fixture", UnityVersion.Parse("2022.3.0f1"));
		var script = assets.CreateAsset(115, info => MonoScript.Create(info));
		script.AssemblyName = "Twin"; script.Namespace = typeof(DecompilationSample).Namespace!; script.ClassName_R = nameof(DecompilationSample);
		GameData data = new(bundle, assets.Version, manager, null);
		FullConfiguration settings = new() { ExportRootPath = "/Output" };
		VirtualFileSystem fs = new();
		ExportHandler handler = new(settings);
		handler.Process(data);
		if (primary)
		{
			PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(data, settings);
			exporter.Export(bundle, settings, fs);
			Assert.That(exporter.LastExportSucceeded, Is.False);
		}
		else
		{
			handler.Export(data, "/Output", fs);
			Assert.That(handler.LastExportSucceeded, Is.False);
			Assert.That(fs.File.Exists("/Output/ExportedProject/Assets/Scripts/Twin/AssetRipper/Tests/Fixtures/DecompilationSample.cs"), Is.True);
		}
		using JsonDocument report = JsonDocument.Parse(fs.File.ReadAllText("/Output/assembly-resolution-report.json"));
		Assert.That(report.RootElement.GetProperty("SharedContextRecoverySkipped").GetBoolean(), Is.True);
		Assert.That(report.RootElement.GetProperty("Succeeded").GetBoolean(), Is.False);
		JsonElement[] outputs = report.RootElement.GetProperty("Outputs").EnumerateArray().ToArray();
		Assert.That(outputs, Has.Length.EqualTo(3));
		Assert.That(outputs.All(o => fs.File.Exists("/Output/" + o.GetProperty("RelativePath").GetString())), Is.True);
	}

	private static TestReference Reference(string name, int version) => new() { Name = name, Version = new(version, 0, 0, 0) };

	private sealed class TestReference : IAssemblyReference
	{
		public required string Name { get; init; }
		public required Version Version { get; init; }
		public string Culture { get; set; } = "";
		public byte[] PublicKeyToken { get; set; } = [];
		public bool IsWindowsRuntime => false;
		public bool IsRetargetable => false;
		public string FullName => new AssemblyIdentity(Name, Version, Culture, PublicKeyToken).ToString();
	}

	private static AssemblyDefinition Fixture(string name, int version)
	{
		AssemblyDefinition source = AssemblyDefinition.FromFile(typeof(DecompilationSample).Assembly.Location, createRuntimeContext: false);
		AssemblyDefinition assembly = new(name, new Version(version, 0, 0, 0));
		ModuleDefinition module = new(name + ".dll", source.ManifestModule!.AssemblyReferences.First(r => r.Name == "System.Runtime"));
		assembly.Modules.Add(module);
		MemberCloner cloner = new(module);
		cloner.Include(source.ManifestModule.TopLevelTypes.Single(t => t.Name == nameof(DecompilationSample)));
		module.TopLevelTypes.Add(cloner.Clone().ClonedTopLevelTypes.Single());
		return assembly;
	}

	private static byte[] Bytes(AssemblyDefinition assembly)
	{
		using BaseManager manager = new(_ => { });
		using MemoryStream result = new();
		manager.Add(assembly);
		manager.SaveAssembly(assembly, result);
		return result.ToArray();
	}

	private static void Write(VirtualFileSystem fs, string path, byte[] bytes)
	{
		fs.Directory.Create(fs.Path.GetDirectoryName(path)!);
		fs.File.WriteAllBytes(path, bytes);
		using Stream stream = fs.File.OpenRead(path);
		stream.Position = 0;
	}
}

internal static class IdentityTestStreamExtensions
{
	public static byte[] ReadAllBytesForTest(this Stream stream)
	{
		stream.Position = 0;
		using MemoryStream output = new();
		stream.CopyTo(output);
		stream.Position = 0;
		return output.ToArray();
	}
}
