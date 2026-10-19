using AsmResolver.DotNet;
using AsmResolver.DotNet.Cloning;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Scripts;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Platforms;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.Tests.Fixtures;
using ICSharpCode.Decompiler;
using System.Text.Json;
using PrimaryScriptCollection = AssetRipper.Export.PrimaryContent.Scripts.ScriptExportCollection;
using ScriptContentExtractor = AssetRipper.Export.PrimaryContent.Scripts.ScriptContentExtractor;

namespace AssetRipper.Tests;

public class ScriptDecompilationTests
{
	[Test]
	public void ManagedFixtureProducesSourceWithoutDiagnostics()
	{
		using MonoManager manager = CreateManager();
		AssemblyDefinition assembly = AddFixture(manager, "Healthy");
		VirtualFileSystem fs = new();
		fs.Directory.Create("/scripts");
		ILSpyAssemblyResolver resolver = new(manager);
		ILSpyWholeProjectDecompiler decompiler = new(new DecompilerSettings { UseNestedDirectoriesForNamespaces = true }, resolver, NullProjectFileWriter.Instance, fs);
		ScriptDecompilationResult result = ScriptDecompilation.Run(decompiler, resolver, assembly, "/scripts", TextWriter.Null);
		Assert.That(result.Succeeded);
		Assert.That(result.Diagnostics, Is.Empty);
		string source = fs.File.ReadAllText("/scripts/AssetRipper/Tests/Fixtures/DecompilationSample.cs");
		Assert.That(source, Does.Contain("Value += amount"));
		Assert.That(source, Does.Contain("Sum(int count)"));
	}

	[Test]
	public void PrimaryContentReportsDecompilationFailureAndContinuesWithNextAssembly()
	{
		using MonoManager manager = CreateManager();
		AddFixture(manager, "Damaged");
		AddFixture(manager, "Healthy");
		FaultingFileSystem fs = new();
		PrimaryScriptCollection collection = new(new ScriptContentExtractor(manager));
		Assert.That(collection.Export("/output", fs), Is.False);
		Assert.That(collection.LastReport!.FailedAssemblyCount, Is.EqualTo(1),
			string.Join('\n', collection.LastReport.Assemblies.SelectMany(a => a.Diagnostics.Select(d => a.AssemblyName + ": " + d.Message))));
		Assert.That(collection.LastReport.Assemblies.Single(r => r.AssemblyName == "Healthy").Succeeded);
		Assert.That(collection.LastReport.Assemblies.Single(r => r.AssemblyName == "Damaged").Diagnostics, Is.Not.Empty);
		Assert.That(fs.File.Exists("/output/Assemblies/Damaged.dll"));
		using JsonDocument report = JsonDocument.Parse(fs.File.ReadAllText("/output/script-export-report.json"));
		Assert.That(report.RootElement.GetProperty("Succeeded").GetBoolean(), Is.False);
	}

	[Test]
	public void UnityProjectPropagatesDecompilationFailure()
	{
		using MonoManager manager = CreateManager();
		AddFixture(manager, "Damaged");
		ProcessedAssetCollection assets = new GameBundle().AddNewProcessedCollection("Fixture", UnityVersion.Parse("2022.3.0f1"));
		IMonoScript script = assets.CreateAsset(115, info => MonoScript.Create(info));
		script.ClassName_R = nameof(DecompilationSample);
		script.Namespace = typeof(DecompilationSample).Namespace!;
		script.AssemblyName = "Damaged";
		FullConfiguration settings = new();
		settings.ExportSettings.ScriptExportMode = AssetRipper.Export.Configuration.ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings);
		FaultingFileSystem fs = new();
		handler.Export(new((AssetRipper.Assets.Bundles.GameBundle)assets.Bundle, assets.Version, manager, null), "/output", fs);
		Assert.That(handler.LastExportSucceeded, Is.False);
		using JsonDocument report = JsonDocument.Parse(fs.File.ReadAllText("/output/ExportedProject/script-export-report.json"));
		Assert.That(report.RootElement.GetProperty("FailedAssemblyCount").GetInt32(), Is.EqualTo(1));
	}

	[Test]
	public void MissingAssembliesProduceReportedPlaceholders()
	{
		using BaseManager manager = new(_ => { });
		ProcessedAssetCollection assets = new GameBundle().AddNewProcessedCollection("Fixture", UnityVersion.Parse("2022.3.0f1"));
		IMonoScript script = assets.CreateAsset(115, info => MonoScript.Create(info));
		script.ClassName_R = "MissingBehaviour";
		script.AssemblyName = "MissingAssembly";
		ExportHandler handler = new(new());
		VirtualFileSystem fs = new();
		handler.Export(new((GameBundle)assets.Bundle, assets.Version, manager, null), "/output", fs);
		Assert.That(handler.LastExportSucceeded, Is.False);
		using JsonDocument report = JsonDocument.Parse(fs.File.ReadAllText("/output/ExportedProject/script-export-report.json"));
		Assert.That(report.RootElement.GetProperty("PlaceholderScripts").GetArrayLength(), Is.EqualTo(1));
		Assert.That(fs.File.ReadAllText("/output/ExportedProject/Assets/Scripts/MissingAssembly/MissingBehaviour.cs"), Does.Contain("MissingBehaviour"));
	}

	private static MonoManager CreateManager()
	{
		MonoManager manager = new(_ => { });
		manager.Initialize(new MixedGameStructure([], LocalFileSystem.Instance));
		return manager;
	}

	[Test]
	public void NonThrowingDecompilerErrorsAreReported()
	{
		using MonoManager manager = CreateManager();
		AssemblyDefinition assembly = AddFixture(manager, "Damaged");
		FaultingFileSystem fs = new();
		fs.Directory.Create("/Damaged");
		ILSpyAssemblyResolver resolver = new(manager);
		ILSpyWholeProjectDecompiler decompiler = new(new DecompilerSettings(), resolver, NullProjectFileWriter.Instance, fs);
		ScriptDecompilationResult result = ScriptDecompilation.Run(decompiler, resolver, assembly, "/Damaged", TextWriter.Null);
		Assert.That(decompiler.Errors, Is.Not.Empty);
		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.Diagnostics, Is.Not.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FatalFailuresAreNotReportedAsRecoverableDecompilationErrors(bool cancel)
	{
		using MonoManager manager = CreateManager();
		AssemblyDefinition assembly = AddFixture(manager, "Damaged");
		FaultingFileSystem fs = new()
		{
			Failure = cancel ? new OperationCanceledException("Injected fatal failure.") : new IOException("Injected fatal failure."),
		};
		fs.Directory.Create("/Damaged");
		ILSpyAssemblyResolver resolver = new(manager);
		ILSpyWholeProjectDecompiler decompiler = new(new DecompilerSettings(), resolver, NullProjectFileWriter.Instance, fs);
		Exception? exception = Assert.Catch(() => ScriptDecompilation.Run(decompiler, resolver, assembly, "/Damaged", TextWriter.Null));
		Assert.That(exception!.ToString(), Does.Contain("Injected fatal failure."));
	}

	[Test]
	public void MissingTypeInLoadedAssemblyProducesReportedPlaceholder()
	{
		using MonoManager manager = CreateManager();
		AddFixture(manager, "Healthy");
		ProcessedAssetCollection assets = new GameBundle().AddNewProcessedCollection("Fixture", UnityVersion.Parse("2022.3.0f1"));
		IMonoScript script = assets.CreateAsset(115, info => MonoScript.Create(info));
		script.ClassName_R = "MissingBehaviour";
		script.AssemblyName = "Healthy";
		FullConfiguration settings = new();
		settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings);
		VirtualFileSystem fs = new();
		handler.Export(new((GameBundle)assets.Bundle, assets.Version, manager, null), "/output", fs);
		Assert.That(handler.LastExportSucceeded, Is.False);
		using JsonDocument report = JsonDocument.Parse(fs.File.ReadAllText("/output/ExportedProject/script-export-report.json"));
		Assert.That(report.RootElement.GetProperty("PlaceholderScripts").GetArrayLength(), Is.EqualTo(1));
	}

	private static AssemblyDefinition AddFixture(MonoManager manager, string name)
	{
		AssemblyDefinition source = AssemblyDefinition.FromFile(typeof(DecompilationSample).Assembly.Location, createRuntimeContext: false);
		ModuleDefinition sourceModule = source.ManifestModule!;
		AssemblyDefinition assembly = new(name, new Version(1, 0, 0, 0));
		ModuleDefinition module = new(name + ".dll", sourceModule.AssemblyReferences.First(r => r.Name == "System.Runtime"));
		assembly.Modules.Add(module);
		MemberCloner cloner = new(module);
		cloner.Include(sourceModule.TopLevelTypes.Single(t => t.Name == nameof(DecompilationSample)));
		TypeDefinition type = cloner.Clone().ClonedTopLevelTypes.Single();
		module.TopLevelTypes.Add(type);
		manager.Add(assembly);
		return assembly;
	}

	private sealed class FaultingFileSystem : FileSystem
	{
		private readonly VirtualFileSystem inner = new();
		public Exception Failure { get; init; } = new InvalidOperationException("Injected decompiler file failure.");
		public override FileImplementation File { get; }
		public override DirectoryImplementation Directory => inner.Directory;
		public override PathImplementation Path => inner.Path;
		public override string TemporaryDirectory { get => inner.TemporaryDirectory; set => inner.TemporaryDirectory = value; }

		public FaultingFileSystem() => File = new FaultingFileImplementation(this);

		private sealed class FaultingFileImplementation(FaultingFileSystem owner) : FileImplementation(owner)
		{
			public override Stream Create(string path)
			{
				if (path.Contains("/Damaged/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
				{
					throw owner.Failure;
				}
				return owner.inner.File.Create(path);
			}

			public override bool Exists(string path) => owner.inner.File.Exists(path);
			public override void Delete(string path) => owner.inner.File.Delete(path);
			public override Stream OpenRead(string path) => owner.inner.File.OpenRead(path);
			public override Stream OpenWrite(string path) => owner.inner.File.OpenWrite(path);
			public override byte[] ReadAllBytes(string path) => owner.inner.File.ReadAllBytes(path);
			public override string ReadAllText(string path) => owner.inner.File.ReadAllText(path);
			public override string ReadAllText(string path, System.Text.Encoding encoding) => owner.inner.File.ReadAllText(path, encoding);
			public override void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => owner.inner.File.WriteAllBytes(path, bytes);
			public override void WriteAllText(string path, ReadOnlySpan<char> text) => owner.inner.File.WriteAllText(path, text);
			public override void WriteAllText(string path, ReadOnlySpan<char> text, System.Text.Encoding encoding) => owner.inner.File.WriteAllText(path, text, encoding);
		}
	}
}
