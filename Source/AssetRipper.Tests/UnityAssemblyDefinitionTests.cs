using AsmResolver.DotNet;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.Export.UnityProjects.Scripts.AssemblyDefinitions;
using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("UnityAssemblyGraph")]
public class UnityAssemblyDefinitionTests
{
	[Test]
	public void SourceAndDllDependenciesUseTheirRespectiveUnityReferenceFields()
	{
		VirtualFileSystem fs = new(); fs.Directory.Create("/Output");
		var details = Details("Data", "Codec", "UnityEngine.CoreModule");
		var failures = AssemblyDefinitionExporter.Export(details, fs, new() { ["UnityEngine.CoreModule"] = default },
			new Dictionary<string, AssemblyExportType> { ["Data"] = AssemblyExportType.Decompile, ["Codec"] = AssemblyExportType.Save });
		Assert.That(failures, Is.Empty);
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/Subject.asmdef"));
		Assert.That(json.RootElement.GetProperty("references").EnumerateArray().Select(v => v.GetString()), Is.EqualTo(new[] { "Data" }));
		Assert.That(json.RootElement.GetProperty("precompiledReferences").EnumerateArray().Select(v => v.GetString()), Is.EqualTo(new[] { "Codec.dll" }));
		Assert.That(json.RootElement.GetProperty("overrideReferences").GetBoolean(), Is.True);
	}
	[Test]
	public void MissingOutputArtifactKeepsTheRelationAndReportsItsOwningAssembly()
	{
		VirtualFileSystem fs = new(); fs.Directory.Create("/Output");
		var failure = AssemblyDefinitionExporter.Export(Details("Missing"), fs, [], new Dictionary<string, AssemblyExportType>()).Single();
		Assert.That(failure.AssemblyName, Is.EqualTo("Subject")); Assert.That(failure.ReferenceIdentity, Does.StartWith("missing, Version=1.0.0.0,"));
		Assert.That(failure.Reason, Is.EqualTo("MissingAssemblyArtifact"));
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/Subject.asmdef"));
		Assert.That(json.RootElement.GetProperty("references")[0].GetString(), Is.EqualTo("Missing"));
	}
	[Test]
	public void APredefinedSourceAssemblyCannotBeInventedAsAnAsmdefReference()
	{
		VirtualFileSystem fs = new(); fs.Directory.Create("/Output");
		var failure = AssemblyDefinitionExporter.Export(Details("Assembly-CSharp"), fs, [],
			new Dictionary<string, AssemblyExportType> { ["Assembly-CSharp"] = AssemblyExportType.Decompile }).Single();
		Assert.That(failure.Reason, Is.EqualTo("PredefinedAssemblyCannotBeReferenced"));
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/Subject.asmdef"));
		Assert.That(json.RootElement.GetProperty("references").GetArrayLength(), Is.Zero);
	}
	[Test]
	public void ASourceOnlyDefinitionDoesNotEnableDllReferenceOverrides()
	{
		VirtualFileSystem fs = new(); fs.Directory.Create("/Output");
		var failures = AssemblyDefinitionExporter.Export(Details("Data"), fs, [], new Dictionary<string, AssemblyExportType> { ["Data"] = AssemblyExportType.Decompile });
		Assert.That(failures, Is.Empty);
		using var json = JsonDocument.Parse(fs.File.ReadAllText("/Output/Subject.asmdef"));
		Assert.That(json.RootElement.TryGetProperty("overrideReferences", out _), Is.False);
		Assert.That(json.RootElement.TryGetProperty("precompiledReferences", out _), Is.False);
	}
	private static AssemblyDefinitionDetails Details(params string[] references)
	{
		AssemblyDefinition assembly = new("Subject", new Version(1, 0, 0, 0)); ModuleDefinition module = new("Subject.dll"); assembly.Modules.Add(module);
		module.AssemblyReferences.Clear(); // Exercise only the explicit relations, without the constructor's implicit mscorlib.
		foreach (string reference in references) module.AssemblyReferences.Add(new AssemblyReference(reference, new Version(1, 0, 0, 0)));
		return new(assembly, "/Output");
	}
}
