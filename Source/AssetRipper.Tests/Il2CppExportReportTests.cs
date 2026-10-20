using AssetRipper.Export.Configuration;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Primitives;
using AssetRipper.Tests.Decompilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("Il2CppCapability")]
public class Il2CppExportReportTests
{
	[TestCase(false, true)]
	[TestCase(false, false)]
	[TestCase(true, true)]
	[TestCase(true, false)]
	public void ExportPersistsProviderAndBodyProvenanceIndependentlyOfCSharpSuccess(bool primary, bool knownStub)
	{
		var provider = new Il2CppProviderInfo(knownStub ? "DefaultProvider" : "CustomProvider", "ControlledProvider", "1.0.0.0", "controlled");
		Il2CppRecoveryReport provenance = new(1, ScriptContentLevel.Level3.ToString(), provider, [provider], knownStub,
			knownStub ? new(1, 2, 0) : new(0, 2, 1), "NotRun") { Isolation = new("controlled-job", 123, "Process", "Passed") };
		var compiled = FixtureCompiler.Compile(new Dictionary<string, string> { ["Fixture.cs"] = "namespace Controlled { public class Fixture { public int Value() { return 37; } } }" },
			new(LanguageVersion.CSharp7_3, OptimizationLevel.Release));
		Assert.That(compiled.AssemblyBytes, Is.Not.Null);
		using var manager = new ScriptExportFixture(compiled.AssemblyBytes!, FixtureCompiler.ReferenceImages, provenance);
		var export = manager.Export(primary, UnityVersion.Parse("6000.4.0f1"), ScriptLanguageVersion.AutoSafe, "Fixture", "Controlled");
		using var report = JsonDocument.Parse(export.FileSystem.File.ReadAllText(export.ReportPath));
		var json = report.RootElement.GetProperty("Il2CppRecovery");
		Assert.That(export.Succeeded, Is.True, report.RootElement.GetRawText());
		Assert.That(report.RootElement.GetProperty("ScriptingBackend").GetString(), Is.EqualTo("IL2Cpp"));
		Assert.That(json.GetProperty("RequestedContentLevel").GetString(), Is.EqualTo("Level3"));
		Assert.That(json.GetProperty("OutputFormat").GetProperty("TypeName").GetString(), Is.EqualTo(provider.TypeName));
		Assert.That(json.GetProperty("ProcessingLayers")[0].GetProperty("AssemblyVersion").GetString(), Is.EqualTo(provider.AssemblyVersion));
		Assert.That(json.GetProperty("UsesKnownStubOutput").GetBoolean(), Is.EqualTo(knownStub));
		Assert.That(json.GetProperty("MethodBodies").GetProperty(knownStub ? "GeneratedStub" : "Unknown").GetInt32(), Is.EqualTo(1));
		Assert.That(json.GetProperty("Validation").GetString(), Is.EqualTo("NotRun"), "C# emission never implies native behavior validation.");
		Assert.That(json.GetProperty("Isolation").GetProperty("JobId").GetString(), Is.EqualTo("controlled-job"));
		Assert.That(json.GetProperty("Isolation").GetProperty("ProtocolValidation").GetString(), Is.EqualTo("Passed"));
	}
}
