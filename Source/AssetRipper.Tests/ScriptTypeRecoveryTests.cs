using AsmResolver.DotNet;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Scripts;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Tests.Decompilation;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Tests;

[Category("ScriptTypeRecovery")]
public class ScriptTypeRecoveryTests
{
	[TestCase(true, false)]
	[TestCase(false, false)]
	[TestCase(true, true)]
	public void InvalidDecimalMetadataCannotRemoveItsHealthyFileSibling(bool nestedDirectories, bool qualified)
	{
		var export = TypeRecoveryFixture.Create().Decompile(nestedDirectories, qualified);
		Assert.That(export.Result.Succeeded, Is.False);
		Assert.That(export.Result.RecoveredTypeCount, Is.EqualTo(1));
		ScriptFileRecoveryResult recovery = export.Result.TypeRecoveries.Single();
		Assert.That(Path.IsPathRooted(recovery.OriginalFile), Is.False);
		Assert.That(Path.IsPathRooted(recovery.EvidenceFile!), Is.False);
		string source = export.FileSystem.File.ReadAllText("/scripts/" + recovery.OriginalFile);
		Assert.That(source, Does.Contain("Add(int amount)"));
		Assert.That(source, Does.Contain("class Nested"));
		Assert.That(recovery.Types.Single(t => t.Status == ScriptTypeRecoveryStatus.Failed).TypeName, Is.EqualTo("AssetRipper.TypeFixture.SharedName`1"));
		Assert.That(export.Result.Diagnostics.Any(d => d.MetadataToken!.StartsWith("0x04", StringComparison.Ordinal)
			&& d.ContainingType == "AssetRipper.TypeFixture.SharedName`1"), Is.True);
		string evidence = export.FileSystem.File.ReadAllText("/scripts/" + recovery.EvidenceFile);
		Assert.That(evidence, Does.Contain("scale"));
		Assert.That(evidence, Does.Not.Contain("Add(int amount)"));
		Assert.That(FixtureCompiler.Hash(export.FileSystem.File.ReadAllBytes("/scripts/" + recovery.EvidenceFile)), Is.EqualTo(recovery.EvidenceSha256));
		AssertCompiles(ScriptExportFixture.ReadSources(export.FileSystem, "/scripts"));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void BothExportPathsRecoverCompileAndPreserveKnownBehavior(bool primary)
	{
		TypeRecoveryFixture fixture = TypeRecoveryFixture.Create();
		using ScriptExportFixture manager = new(fixture.InputAssembly, FixtureCompiler.ReferenceImages);
		var export = manager.Export(primary, UnityVersion.Parse("2022.3.0f1"), ScriptLanguageVersion.CSharp9_0, "SharedName", "AssetRipper.TypeFixture");
		Assert.That(export.Succeeded, Is.False);
		using JsonDocument report = JsonDocument.Parse(export.FileSystem.File.ReadAllText(export.ReportPath));
		JsonElement assembly = report.RootElement.GetProperty("Assemblies").EnumerateArray().Single();
		Assert.That(assembly.GetProperty("Succeeded").GetBoolean(), Is.False);
		Assert.That(assembly.GetProperty("RecoveredTypeCount").GetInt32(), Is.EqualTo(1));
		Assert.That(assembly.GetProperty("TypeRecoveries")[0].GetProperty("Types").GetArrayLength(), Is.EqualTo(2));
		var sources = ScriptExportFixture.ReadSources(export.FileSystem, export.ScriptDirectory);
		FixtureCompilation candidate = AssertCompiles(sources);
		int[] original = Observe(fixture.OriginalAssembly), restored = Observe(candidate.AssemblyBytes!);
		Assert.That(original, Is.EqualTo(new[] { 3, 2, 2, 15, 20 }));
		Assert.That(restored, Is.EqualTo(original));
		string dll = primary ? "/output/Assemblies/ManagedRoundTrip.dll" : "/output/AuxiliaryFiles/GameAssemblies/ManagedRoundTrip.dll";
		Assert.That(export.FileSystem.File.ReadAllBytes(dll), Is.EqualTo(fixture.InputAssembly));
		if (!primary) Assert.That(export.FileSystem.File.Exists(export.ScriptDirectory + "/AssetRipper/TypeFixture/SharedName.cs.meta"), Is.True);
		WriteEvidence(fixture, export.FileSystem, export.ScriptDirectory, export.ReportPath, candidate, primary, original, restored);
	}

	[Test]
	public void HealthyAssemblyStaysOnTheWholeProjectPath()
	{
		var export = TypeRecoveryFixture.Create(damage: false).Decompile();
		Assert.That(export.Result.Succeeded, Is.True);
		Assert.That(export.Result.TypeRecoveries, Is.Empty);
		Assert.That(export.FileSystem.Directory.Exists("/scripts/_DecompilationEvidence"), Is.False);
		AssertCompiles(ScriptExportFixture.ReadSources(export.FileSystem, "/scripts"));
	}

	[Test]
	public void MultipleRecoveredSiblingsUseDistinctFilesWithoutOverwritingExistingOutput()
	{
		TypeRecoveryFixture fixture = TypeRecoveryFixture.Create(extraType: true);
		AssemblyDefinition input = AssemblyDefinition.FromBytes(fixture.InputAssembly, createRuntimeContext: false);
		string token = input.ManifestModule!.TopLevelTypes.Single(t => t.Name == "SharedName`2").MetadataToken.ToUInt32().ToString("X8");
		VirtualFileSystem fs = new();
		fs.Directory.Create("/scripts/AssetRipper/TypeFixture");
		string existing = $"/scripts/AssetRipper/TypeFixture/SharedName.Recovered-{token}.cs";
		fs.File.WriteAllText(existing, "// existing output");
		var export = fixture.Decompile(fileSystem: fs);
		Assert.That(fs.File.ReadAllText(existing), Is.EqualTo("// existing output"));
		Assert.That(export.Result.RecoveredTypeCount, Is.EqualTo(2));
		var recovered = export.Result.TypeRecoveries.Single().Types.Where(t => t.Status == ScriptTypeRecoveryStatus.Recovered).ToArray();
		Assert.That(recovered.All(t => !Path.IsPathRooted(t.OutputFile!)), Is.True);
		Assert.That(recovered.Select(t => t.OutputFile).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(2));
		Assert.That(recovered.Single(t => t.TypeName.EndsWith("`2", StringComparison.Ordinal)).OutputFile, Does.EndWith("-1.cs"));
		FixtureCompilation candidate = AssertCompiles(ScriptExportFixture.ReadSources(fs, "/scripts"));
		Assert.That(Observe(candidate.AssemblyBytes!), Is.EqualTo(new[] { 3, 2, 2, 15, 20 }));
	}

	[Test]
	public void AllFailedTypesLeaveTheOriginalErrorFileUntouched()
	{
		var export = TypeRecoveryFixture.Create(damageAll: true).Decompile();
		ScriptFileRecoveryResult recovery = export.Result.TypeRecoveries.Single();
		Assert.That(export.Result.Succeeded, Is.False);
		Assert.That(export.Result.RecoveredTypeCount, Is.Zero);
		Assert.That(recovery.Types, Has.Count.EqualTo(2));
		Assert.That(recovery.Types.All(t => t.Status == ScriptTypeRecoveryStatus.Failed), Is.True);
		Assert.That(recovery.EvidenceFile, Is.Null);
		Assert.That(export.FileSystem.File.ReadAllText("/scripts/" + recovery.OriginalFile).TrimStart(), Does.StartWith("// ILSpy could not decompile this."));
	}

	[Test]
	public void EvidenceDirectoryConflictDoesNotDeleteExistingOutput()
	{
		VirtualFileSystem fs = new();
		fs.Directory.Create("/scripts");
		fs.File.WriteAllText("/scripts/_DecompilationEvidence", "existing output");
		Assert.Throws<IOException>(() => TypeRecoveryFixture.Create().Decompile(fileSystem: fs));
		Assert.That(fs.File.ReadAllText("/scripts/_DecompilationEvidence"), Is.EqualTo("existing output"));
		Assert.That(fs.File.ReadAllText("/scripts/AssetRipper/TypeFixture/SharedName.cs").TrimStart(), Does.StartWith("// ILSpy could not decompile this."));
	}

	[Test]
	public void ExistingPartialSourceIsNotReplacedAfterAMethodBodyFailure()
	{
		var export = TypeRecoveryFixture.Create(damage: false).Decompile(createDecompiler: (s, r, fs) => new MethodFailureDecompiler(s, r, fs));
		Assert.That(export.Result.Succeeded, Is.False);
		Assert.That(export.Result.TypeRecoveries, Is.Empty);
		string source = export.FileSystem.File.ReadAllText("/scripts/AssetRipper/TypeFixture/SharedName.cs");
		Assert.That(source, Does.Contain("class SharedName<T>"));
		Assert.That(source, Does.Contain("class Nested"));
		Assert.That(export.Result.Diagnostics.Any(d => d.MetadataToken!.StartsWith("0x06", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public void ResourceBackedPartialTypesAreExplicitlySkipped()
	{
		var export = TypeRecoveryFixture.Create(resource: true).Decompile();
		ScriptFileRecoveryResult recovery = export.Result.TypeRecoveries.Single();
		Assert.That(recovery.SkipReason, Does.Contain("companion map"));
		Assert.That(recovery.Types, Is.Empty);
		Assert.That(export.Result.Succeeded, Is.False);
		Assert.That(export.Result.RecoveredTypeCount, Is.Zero);
	}

	[TestCase("io")]
	[TestCase("cancel")]
	[TestCase("access")]
	[TestCase("oom")]
	[TestCase("wrapped")]
	public void FatalRecoveryWriteFailuresEscape(string kind)
	{
		Exception failure = kind switch
		{
			"io" => new IOException("Injected recovery write failure"),
			"cancel" => new OperationCanceledException("Injected recovery write failure"),
			"access" => new UnauthorizedAccessException("Injected recovery write failure"),
			"oom" => new OutOfMemoryException("Injected recovery write failure"),
			_ => new AggregateException(new InvalidOperationException("wrapper", new IOException("Injected recovery write failure"))),
		};
		Exception? actual = Assert.Catch(() => TypeRecoveryFixture.Create().Decompile(createDecompiler: (s, r, fs) => new WriteFailureDecompiler(s, r, fs, failure)));
		Assert.That(actual, Is.SameAs(failure));
	}

	[Test]
	public void RecoverableWriteFailureIsReportedAndAnotherSiblingCanContinue()
	{
		var export = TypeRecoveryFixture.Create(extraType: true).Decompile(createDecompiler: (s, r, fs) => new WriteFailureDecompiler(s, r, fs, new InvalidOperationException("recoverable writer failure")));
		Assert.That(export.Result.Succeeded, Is.False);
		Assert.That(export.Result.RecoveredTypeCount, Is.EqualTo(1));
		Assert.That(export.Result.TypeRecoveries.Single().Types.Any(t => t.Diagnostics.Any(d => d.Stage == "TypeRecoveryWrite")), Is.True);
	}

	private static FixtureCompilation AssertCompiles(IReadOnlyDictionary<string, string> sources)
	{
		FixtureCompilation candidate = FixtureCompiler.Compile(sources, new(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp9, OptimizationLevel.Release));
		Assert.That(candidate.Check.Status, Is.EqualTo(CheckStatus.Passed), JsonSerializer.Serialize(candidate.Check));
		return candidate;
	}

	private static int[] Observe(byte[] bytes)
	{
		AssemblyLoadContext context = new("source-owned-type-recovery-fixture", isCollectible: true);
		try
		{
			using MemoryStream stream = new(bytes, writable: false);
			Assembly assembly = context.LoadFromStream(stream);
			Type type = assembly.GetType("AssetRipper.TypeFixture.SharedName", throwOnError: true)!;
			object instance = Activator.CreateInstance(type)!;
			MethodInfo add = type.GetMethod("Add")!;
			return [(int)add.Invoke(instance, [3])!, (int)add.Invoke(instance, [-1])!, (int)type.GetField("Value")!.GetValue(instance)!,
				(int)type.GetNestedType("Nested")!.GetMethod("Evaluate")!.Invoke(null, [5])!,
				(int)assembly.GetType("AssetRipper.TypeFixture.Independent")!.GetMethod("Read")!.Invoke(null, [9])!];
		}
		finally { context.Unload(); }
	}

	private static void WriteEvidence(TypeRecoveryFixture fixture, VirtualFileSystem fs, string scriptDirectory, string reportPath,
		FixtureCompilation candidate, bool primary, int[] original, int[] restored)
	{
		string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "type-recovery", $"{(primary ? "Primary" : "Unity")}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "original.cs"), fixture.Source);
		File.WriteAllBytes(Path.Combine(directory, "original.dll"), fixture.OriginalAssembly);
		File.WriteAllBytes(Path.Combine(directory, "damaged.dll"), fixture.InputAssembly);
		File.WriteAllBytes(Path.Combine(directory, "recompiled.dll"), candidate.AssemblyBytes!);
		foreach (string file in fs.Directory.EnumerateFiles(scriptDirectory, "*", SearchOption.AllDirectories))
		{
			string target = Path.Combine(directory, "recovered", file[(scriptDirectory.Length + 1)..]);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllBytes(target, fs.File.ReadAllBytes(file));
		}
		File.WriteAllText(Path.Combine(directory, "script-export-report.json"), fs.File.ReadAllText(reportPath));
		JsonSerializerOptions options = new() { WriteIndented = true };
		options.Converters.Add(new JsonStringEnumConverter());
		string path = Path.Combine(directory, "type-recovery-verification.json");
		File.WriteAllText(path, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, Profile = FixtureCompiler.ReferenceProfile, candidate.Check,
			OriginalSha256 = FixtureCompiler.Hash(fixture.OriginalAssembly), InputSha256 = FixtureCompiler.Hash(fixture.InputAssembly),
			RecompiledSha256 = FixtureCompiler.Hash(candidate.AssemblyBytes!), OriginalBehavior = original, RecompiledBehavior = restored,
			AssemblyRecoveryComplete = false, UnityEditorValidation = CheckStatus.NotRun,
		}, options));
		TestContext.AddTestAttachment(path, "Partial type recovery compile and source-owned behavior evidence");
	}

	private sealed class WriteFailureDecompiler(DecompilerSettings settings, ILSpyAssemblyResolver resolver, VirtualFileSystem fs, Exception failure)
		: ILSpyWholeProjectDecompiler(settings, resolver, NullProjectFileWriter.Instance, fs)
	{
		private int writes;
		protected override TextWriter CreateFile(string path)
		{
			if (path.EndsWith("SharedName.cs", StringComparison.Ordinal) && Interlocked.Increment(ref writes) == 2) throw failure;
			return base.CreateFile(path);
		}
	}

	private sealed class MethodFailureDecompiler(DecompilerSettings settings, ILSpyAssemblyResolver resolver, VirtualFileSystem fs)
		: ILSpyWholeProjectDecompiler(settings, resolver, NullProjectFileWriter.Instance, fs)
	{
		protected override CSharpDecompiler CreateDecompiler(DecompilerTypeSystem ts)
		{
			CSharpDecompiler decompiler = base.CreateDecompiler(ts);
			decompiler.ILTransforms.Insert(0, new FailAddBody());
			return decompiler;
		}
	}

	private sealed class FailAddBody : IILTransform
	{
		public void Run(ILFunction function, ILTransformContext context)
		{
			if (function.Method?.Name == "Add") throw new InvalidOperationException("Injected method body failure");
		}
	}
}
