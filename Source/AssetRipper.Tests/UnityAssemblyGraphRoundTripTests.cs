using AssetRipper.Export.Configuration;
using AsmResolver.DotNet;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AssetRipper.Tests;

[Category("UnityAssemblyGraph"), NonParallelizable]
public class UnityAssemblyGraphRoundTripTests
{
	[Test]
	public void UnusedMissingAssemblyReferenceCannotProduceSuccessfulUnityProject()
	{
		var fixture = UnityAssemblyGraphFixture.Require(); using var manager = fixture.CreateManager();
		var assembly = manager.GetAssemblies().Single(a => a.Name == "AssetRipper.GraphComponents");
		assembly.ManifestModule!.AssemblyReferences.Add(new AssemblyReference("Missing.Unused.Dependency", new Version(1, 0, 0, 0)));
		var bundle = UnityAssemblyGraphTests.Load(fixture, manager, false);
		string output = fixture.CopyInputs("MissingUnusedMetadataReference");
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		ExportHandler handler = new(settings); GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		handler.Process(data); handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.False, "An asmdef cannot reference an assembly that was never emitted, even if no C# member uses it.");
		using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "ExportedProject", "script-export-report.json")));
		Assert.That(report.RootElement.GetProperty("Assemblies").EnumerateArray().All(a => a.GetProperty("Succeeded").GetBoolean()), Is.True,
			"This regression isolates assembly-artifact validation from C# decompilation failures.");
		var failure = report.RootElement.GetProperty("UnityAssemblyReferenceFailures").EnumerateArray().Single();
		Assert.That(failure.GetProperty("AssemblyName").GetString(), Is.EqualTo("AssetRipper.GraphComponents"));
		Assert.That(failure.GetProperty("ReferenceIdentity").GetString(), Does.StartWith("missing.unused.dependency,"));
		Assert.That(failure.GetProperty("Reason").GetString(), Is.EqualTo("MissingAssemblyArtifact"));
	}
	[TestCase(false, ScriptExportMode.Decompiled, "None")]
	[TestCase(true, ScriptExportMode.Decompiled, "None")]
	[TestCase(false, ScriptExportMode.Hybrid, "None")]
	[TestCase(true, ScriptExportMode.Hybrid, "None")]
	[TestCase(false, ScriptExportMode.Decompiled, "MissingAsmdefReference")]
	[TestCase(false, ScriptExportMode.Hybrid, "MissingDependencyDll")]
	[TestCase(false, ScriptExportMode.Hybrid, "WrongDllFileId")]
	public async Task ThreeAssemblyProjectReimportsWithBindingsAndKnownBehavior(bool stripped, ScriptExportMode mode, string damage)
	{
		var fixture = UnityAssemblyGraphFixture.Require();
		string? executable = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR_EXECUTABLE");
		if (string.IsNullOrWhiteSpace(executable)) Assert.Ignore("Assembly graph Editor import NotRun: set ASSETRIPPER_UNITY_EDITOR_EXECUTABLE.");
		Assert.That(File.Exists(executable), Is.True, "Configured Editor executable is missing.");
		using var manager = fixture.CreateManager();
		var bundle = UnityAssemblyGraphTests.Load(fixture, manager, stripped);
		string output = fixture.CopyInputs($"Editor-{stripped}-{mode}-{damage}");
		FullConfiguration settings = new(); settings.ExportSettings.ScriptExportMode = mode;
		ExportHandler handler = new(settings); GameData data = new(bundle, bundle.GetMaxUnityVersion(), manager, null);
		handler.Process(data); handler.Export(data, output, LocalFileSystem.Instance);
		Assert.That(handler.LastExportSucceeded, Is.True, output);
		string project = Path.Combine(output, "ExportedProject"), assets = Path.Combine(project, "Assets");
		AssertExportLayout(assets, mode);
		string validatorDirectory = Path.Combine(assets, "Editor", "AssetRipperValidation"); Directory.CreateDirectory(validatorDirectory);
		using (var source = typeof(UnityAssemblyGraphRoundTripTests).Assembly.GetManifestResourceStream("UnityAssemblyGraphValidator.cs")!)
		using (var target = File.Create(Path.Combine(validatorDirectory, "AssetRipperAssemblyGraphValidator.cs"))) source.CopyTo(target);
		string oraclePath = Path.Combine(output, "oracle.json"); var oracle = JsonNode.Parse(File.ReadAllText(oraclePath))!;
		oracle["ExportMode"] = mode.ToString(); File.WriteAllText(oraclePath, oracle.ToJsonString());
		object? mutation = Mutate(project, output, damage);
		object[] snapshot = UnityEditorRoundTripRunner.Snapshot(project, output);
		File.WriteAllText(Path.Combine(output, "roundtrip-inputs.json"), JsonSerializer.Serialize(new
		{
			fixture.Manifest.UnityVersion, SchemaSource = stripped ? "Assembly" : "TypeTree", ExportMode = mode.ToString(), Damage = damage,
			OriginalAssemblyCSharpCopiedIntoAssets = false, CustomAssemblies = mode == ScriptExportMode.Hybrid ? "SavedDll" : "DecompiledSource",
			Mutation = mutation, BeforeImport = snapshot, ExportSucceeded = handler.LastExportSucceeded,
		}, new JsonSerializerOptions { WriteIndented = true }));
		if (damage is "MissingAsmdefReference" or "MissingDependencyDll")
		{
			await UnityEditorRoundTripRunner.ExpectCompilerFailure(executable!, project, output, "AssetRipperAssemblyGraphValidator",
				damage == "MissingAsmdefReference" ? "GraphNode.cs" : "AssemblyBridge.cs");
		}
		else
		{
			var result = await UnityEditorRoundTripRunner.Run(executable!, project, output, "AssetRipperAssemblyGraphValidator");
			using var report = result.Report;
			bool expectedSuccess = damage == "None";
			Assert.That(report.RootElement.GetProperty("Passed").GetBoolean(), Is.EqualTo(expectedSuccess), report.RootElement.ToString());
			Assert.That(result.ExitCode, Is.EqualTo(expectedSuccess ? 0 : 1));
			if (expectedSuccess)
			{
				Assert.That(report.RootElement.GetProperty("NodeResults").EnumerateArray().Select(v => v.GetInt32()), Is.EqualTo(fixture.Oracle.NodeResults));
				Assert.That(report.RootElement.GetProperty("BridgeResults").EnumerateArray().Select(v => v.GetInt32()), Is.EqualTo(fixture.Oracle.BridgeResults));
				Assert.That(report.RootElement.GetProperty("InterfaceResults").EnumerateArray().Select(v => v.GetInt32()), Is.EqualTo(fixture.Oracle.NodeResults));
				Assert.That(report.RootElement.GetProperty("Bindings").GetArrayLength(), Is.EqualTo(2));
				if (mode == ScriptExportMode.Hybrid)
				{
					var binding = report.RootElement.GetProperty("Bindings").EnumerateArray().Single(b => b.GetProperty("Assembly").GetString() == "AssetRipper.GraphComponents");
					Assert.That(binding.GetProperty("LocalId").GetInt64(), Is.EqualTo(ScriptHashing.CalculateScriptFileID("AssetRipper.AssemblyGraph.Components", "GraphNode")));
					Assert.That(report.RootElement.GetProperty("Plugins").GetArrayLength(), Is.EqualTo(2));
				}
			}
			else Assert.That(report.RootElement.GetProperty("Failures").EnumerateArray().Any(f => f.GetProperty("Code").GetString() == "MissingScript"), Is.True);
		}
		UnityEditorRoundTripRunner.RemoveImportCache(project);
	}
	private static void AssertExportLayout(string assets, ScriptExportMode mode)
	{
		Assert.That(Directory.GetFiles(assets, "Assembly-CSharp.dll", SearchOption.AllDirectories), Is.Empty);
		Assert.That(Directory.GetFiles(assets, "AssemblyBridge.cs", SearchOption.AllDirectories), Has.Length.EqualTo(1));
		if (mode == ScriptExportMode.Hybrid)
		{
			Assert.That(Directory.GetFiles(assets, "*.asmdef", SearchOption.AllDirectories), Is.Empty);
			Assert.That(Directory.GetFiles(assets, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName),
				Is.EquivalentTo(new[] { "AssetRipper.GraphModel.dll", "AssetRipper.GraphComponents.dll" }));
			Assert.That(Directory.GetFiles(assets, "GraphNode.cs", SearchOption.AllDirectories), Is.Empty);
		}
		else
		{
			Assert.That(Directory.GetFiles(assets, "*.dll", SearchOption.AllDirectories), Is.Empty);
			var definitions = Directory.GetFiles(assets, "*.asmdef", SearchOption.AllDirectories); Assert.That(definitions, Has.Length.EqualTo(2));
			using var model = JsonDocument.Parse(File.ReadAllText(definitions.Single(p => Path.GetFileName(p) == "AssetRipper.GraphModel.asmdef")));
			using var components = JsonDocument.Parse(File.ReadAllText(definitions.Single(p => Path.GetFileName(p) == "AssetRipper.GraphComponents.asmdef")));
			Assert.That(model.RootElement.GetProperty("references").GetArrayLength(), Is.Zero);
			Assert.That(components.RootElement.GetProperty("references").EnumerateArray().Select(r => r.GetString()), Is.EqualTo(new[] { "AssetRipper.GraphModel" }));
		}
	}
	private static object? Mutate(string project, string output, string damage)
	{
		if (damage == "None") return null;
		string assets = Path.Combine(project, "Assets"); string file;
		if (damage == "MissingAsmdefReference") file = Directory.GetFiles(assets, "AssetRipper.GraphComponents.asmdef", SearchOption.AllDirectories).Single();
		else if (damage == "MissingDependencyDll") file = Directory.GetFiles(assets, "AssetRipper.GraphModel.dll", SearchOption.AllDirectories).Single();
		else file = Directory.GetFiles(assets, "*.prefab", SearchOption.AllDirectories).Single(p => File.ReadAllText(p).Contains("AssemblyGraphRoot", StringComparison.Ordinal));
		byte[] before = File.ReadAllBytes(file); string backup = Path.Combine(output, "Evidence", "Mutation", Path.GetFileName(file));
		Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.WriteAllBytes(backup, before);
		if (damage == "MissingDependencyDll") File.Delete(file);
		else if (damage == "MissingAsmdefReference")
		{
			var definition = JsonNode.Parse(before)!; definition["references"] = new JsonArray(); File.WriteAllText(file, definition.ToJsonString());
		}
		else
		{
			string metadata = File.ReadAllText(Directory.GetFiles(assets, "AssetRipper.GraphComponents.dll.meta", SearchOption.AllDirectories).Single());
			string guid = Regex.Match(metadata, @"(?m)^guid: ([0-9a-fA-F]{32})").Groups[1].Value; Assert.That(guid, Has.Length.EqualTo(32));
			var pattern = new Regex(@"(m_Script: \{fileID: )(-?\d+)(, guid: " + guid + @", type: 3\})");
			string text = File.ReadAllText(file); Assert.That(pattern.Matches(text), Has.Count.EqualTo(1));
			Assert.That(pattern.Match(text).Groups[2].Value, Is.Not.EqualTo("11500000"));
			File.WriteAllText(file, pattern.Replace(text, match => match.Groups[1].Value + "11500000" + match.Groups[3].Value));
		}
		string? after = File.Exists(file) ? FixtureCompiler.Hash(File.ReadAllBytes(file)) : null;
		Assert.That(after, Is.Not.EqualTo(FixtureCompiler.Hash(before)));
		return new { Path = Path.GetRelativePath(project, file), OriginalSha256 = FixtureCompiler.Hash(before), ModifiedSha256 = after };
	}
}
