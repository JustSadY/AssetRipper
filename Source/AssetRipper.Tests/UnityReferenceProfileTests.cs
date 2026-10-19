using AsmResolver.DotNet;
using AssetRipper.Export.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Tests.Decompilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace AssetRipper.Tests;

[Category("UnityReferenceProfile")]
public class UnityReferenceProfileTests
{
	[TestCase(false, ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp9)]
	[TestCase(true, ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp9)]
	[TestCase(false, ScriptLanguageVersion.CSharp7_3, LanguageVersion.CSharp7_3)]
	[TestCase(true, ScriptLanguageVersion.CSharp7_3, LanguageVersion.CSharp7_3)]
	public void BothExportPathsRecompileAgainstPinnedUnityReferences(bool primary, ScriptLanguageVersion requested, LanguageVersion expected)
	{
		PinnedUnityReferences references = PinnedUnityReferences.RequireInstalledProfile();
		using Stream resource = typeof(UnityReferenceProfileTests).Assembly.GetManifestResourceStream("AssetRipper.Tests.Fixtures.UnityScriptSample.cs")!;
		using StreamReader reader = new(resource);
		string source = reader.ReadToEnd();
		UnityReferenceCompileReport report = new()
		{
			Profile = references.Manifest, ExportMode = primary ? "PrimaryContent" : "UnityProject",
			Requested = requested, EffectiveLanguage = expected.ToString(),
			OriginalSourceSha256 = FixtureCompiler.Hash(Encoding.UTF8.GetBytes(source)),
		};
		string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "unity-reference",
			$"{report.ExportMode}-{requested}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "original.cs"), source);
		try
		{
			FixtureCompilation original = FixtureCompiler.Compile(new Dictionary<string, string> { ["original.cs"] = source },
				new(references.Manifest.LanguageVersion, OptimizationLevel.Release), references.MetadataReferences);
			report.OriginalCompilation = original.Check;
			Assert.That(original.Check.Status, Is.EqualTo(CheckStatus.Passed), JsonSerializer.Serialize(original.Check));
			report.OriginalAssemblySha256 = FixtureCompiler.Hash(original.AssemblyBytes!);
			File.WriteAllBytes(Path.Combine(directory, "original.dll"), original.AssemblyBytes!);
			using ScriptExportFixture manager = new(original.AssemblyBytes!, references.Images);
			var export = manager.Export(primary, UnityVersion.Parse(references.Manifest.UnityVersion), requested, "UnityScriptSample", "AssetRipper.UnityFixture");
			report.ExportSucceeded = export.Succeeded;
			string scriptReport = export.FileSystem.File.ReadAllText(export.ReportPath);
			File.WriteAllText(Path.Combine(directory, "script-export-report.json"), scriptReport);
			using JsonDocument document = JsonDocument.Parse(scriptReport);
			JsonElement selection = document.RootElement.GetProperty("LanguageSelection").Clone();
			report.LanguageSelection = selection;
			Assert.That(export.Succeeded, Is.True, scriptReport);
			Assert.That(selection.GetProperty("Requested").GetString(), Is.EqualTo(requested.ToString()));
			Assert.That(selection.GetProperty("UnityVersion").GetString(), Is.EqualTo(references.Manifest.UnityVersion));
			Assert.That(selection.GetProperty("Effective").GetString(), Is.EqualTo(requested.ToCSharpLanguageVersion(UnityVersion.Parse(references.Manifest.UnityVersion)).ToString()));
			report.LanguageCheck = CheckStatus.Passed;
			Dictionary<string, string> sources = ScriptExportFixture.ReadSources(export.FileSystem, export.ScriptDirectory);
			foreach (var file in sources)
			{
				string path = Path.Combine(directory, "recovered", file.Key);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, file.Value);
				report.GeneratedSources.Add(new(file.Key, FixtureCompiler.Hash(Encoding.UTF8.GetBytes(file.Value))));
			}
			if (primary)
			{
				string project = export.FileSystem.File.ReadAllText(export.ScriptDirectory + "/ManagedRoundTrip.csproj");
				File.WriteAllText(Path.Combine(directory, "ManagedRoundTrip.csproj"), project);
				string projectLanguage = XDocument.Parse(project).Descendants("LangVersion").Single().Value;
				Assert.That(LanguageVersionFacts.TryParse(projectLanguage, out LanguageVersion parsed), Is.True);
				Assert.That(parsed, Is.EqualTo(expected));
				report.ProjectScaffoldFramework = document.RootElement.GetProperty("ProjectScaffoldFramework").GetString();
			}
			FixtureCompilation candidate = FixtureCompiler.Compile(sources, new(expected, OptimizationLevel.Release), references.MetadataReferences);
			report.Recompilation = candidate.Check;
			Assert.That(candidate.Check.Status, Is.EqualTo(CheckStatus.Passed), JsonSerializer.Serialize(candidate.Check));
			report.RecompiledAssemblySha256 = FixtureCompiler.Hash(candidate.AssemblyBytes!);
			File.WriteAllBytes(Path.Combine(directory, "recompiled.dll"), candidate.AssemblyBytes!);
			report.OriginalSerializedFields = SerializedFields(original.AssemblyBytes!);
			report.RecompiledSerializedFields = SerializedFields(candidate.AssemblyBytes!);
			Assert.That(report.OriginalSerializedFields, Is.EqualTo(new[] { "offset:UnityEngine.Vector3:private=True", "score:System.Int32:private=True" }));
			Assert.That(report.RecompiledSerializedFields, Is.EqualTo(report.OriginalSerializedFields));
			report.SerializedFieldCheck = CheckStatus.Passed;
			Assert.That(report.CompileAndFieldChecksPassed, Is.True);
		}
		catch (Exception exception)
		{
			report.Error = exception.ToString();
			throw;
		}
		finally
		{
			string path = Path.Combine(directory, "unity-reference-report.json");
			JsonSerializerOptions options = new() { WriteIndented = true };
			options.Converters.Add(new JsonStringEnumConverter());
			File.WriteAllText(path, JsonSerializer.Serialize(report, options));
			TestContext.AddTestAttachment(path, "Pinned Unity reference compile evidence; Editor and behavior NotRun");
			TestContext.Out.WriteLine($"Unity reference evidence: {directory}");
		}
	}

	[Test]
	public void DotNet10ApiCannotLeakIntoTheUnityProfile()
	{
		PinnedUnityReferences references = PinnedUnityReferences.RequireInstalledProfile();
		Dictionary<string, string> source = new() { ["modern-api.cs"] = "public class ModernApi { public int Pid => System.Environment.ProcessId; }" };
		FixtureProfile language = new(LanguageVersion.CSharp9, OptimizationLevel.Release);
		Assert.That(FixtureCompiler.Compile(source, language).Check.Status, Is.EqualTo(CheckStatus.Passed));
		FixtureCompilation unity = FixtureCompiler.Compile(source, language, references.MetadataReferences);
		Assert.That(unity.Check.Status, Is.EqualTo(CheckStatus.Failed));
		Assert.That(unity.Check.Diagnostics.Any(d => d.Id == "CS0117" && d.Message.Contains("ProcessId", StringComparison.Ordinal)), Is.True,
			JsonSerializer.Serialize(unity.Check));
	}

	[Test]
	public void ChangedReferenceIsRejectedBeforeCompilation()
	{
		byte[] bytes = [1, 2, 3];
		PinnedUnityReferenceManifest manifest = new(1, "Mismatch", "6000.4.0f1", ".NET Standard 2.1", LanguageVersion.CSharp9,
			[new("ref.dll", FixtureCompiler.Hash(bytes))]);
		VirtualFileSystem fs = new();
		fs.Directory.Create("/profile");
		fs.File.WriteAllBytes("/profile/ref.dll", [1, 2, 4]);
		Assert.That(() => PinnedUnityReferences.Load(manifest, "/profile", fs), Throws.TypeOf<InvalidDataException>().With.Message.Contains("hash mismatch"));
	}

	[Test]
	public void MissingReferenceIsNotSubstitutedFromTheHost()
	{
		VirtualFileSystem fs = new();
		fs.Directory.Create("/profile");
		PinnedUnityReferenceManifest manifest = new(1, "Missing", "6000.4.0f1", ".NET Standard 2.1", LanguageVersion.CSharp9,
			[new("netstandard.dll", new string('0', 64))]);
		Assert.That(() => PinnedUnityReferences.Load(manifest, "/profile", fs), Throws.TypeOf<FileNotFoundException>());
	}

	private static string[] SerializedFields(byte[] bytes) => AssemblyDefinition.FromBytes(bytes, createRuntimeContext: false).ManifestModule!
		.TopLevelTypes.Single(t => t.Name == "UnityScriptSample").Fields
		.Where(f => f.CustomAttributes.Any(a => a.Constructor?.DeclaringType?.FullName == "UnityEngine.SerializeField"))
		.Select(f => $"{f.Name}:{f.Signature!.FieldType.FullName}:private={f.IsPrivate}").Order(StringComparer.Ordinal).ToArray();
}

internal sealed class UnityReferenceCompileReport
{
	public int SchemaVersion => 1;
	public required PinnedUnityReferenceManifest Profile { get; init; }
	public required string ExportMode { get; init; }
	public required ScriptLanguageVersion Requested { get; init; }
	public required string EffectiveLanguage { get; init; }
	public string Runtime => RuntimeInformation.FrameworkDescription;
	public string CompilerVersion => typeof(CSharpCompilation).Assembly.GetName().Version!.ToString();
	public string DecompilerVersion => typeof(ICSharpCode.Decompiler.DecompilerSettings).Assembly.GetName().Version!.ToString();
	public required string OriginalSourceSha256 { get; init; }
	public string? OriginalAssemblySha256 { get; set; }
	public string? RecompiledAssemblySha256 { get; set; }
	public CompilationCheck OriginalCompilation { get; set; } = CompilationCheck.NotRun;
	public bool ExportSucceeded { get; set; }
	public JsonElement? LanguageSelection { get; set; }
	public string? ProjectScaffoldFramework { get; set; }
	public string Optimization => "Release";
	public bool CheckOverflow => true;
	public bool AllowUnsafe => false;
	public string NullableContext => "Disable";
	public CheckStatus LanguageCheck { get; set; }
	public CompilationCheck Recompilation { get; set; } = CompilationCheck.NotRun;
	public IReadOnlyList<string> OriginalSerializedFields { get; set; } = [];
	public IReadOnlyList<string> RecompiledSerializedFields { get; set; } = [];
	public CheckStatus SerializedFieldCheck { get; set; }
	public List<SourceImage> GeneratedSources { get; } = [];
	public CheckStatus Behavior => CheckStatus.NotRun;
	public CheckStatus UnityEditorValidation => CheckStatus.NotRun;
	public string? Error { get; set; }
	public bool CompileAndFieldChecksPassed => Error is null && ExportSucceeded && OriginalCompilation.Status == CheckStatus.Passed
		&& LanguageCheck == CheckStatus.Passed && Recompilation.Status == CheckStatus.Passed && SerializedFieldCheck == CheckStatus.Passed;
}
