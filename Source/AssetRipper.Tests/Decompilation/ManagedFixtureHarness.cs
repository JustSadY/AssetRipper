using AssetRipper.Export.Scripts;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using ICSharpCode.Decompiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal enum FixtureMutation { None, StubClamp, RemoveSerializedField, InvalidReturn }

/// <summary>
/// Executes only the embedded source-owned fixture. Not an API for executing recovered game assemblies.
/// Collectible contexts separate assembly identity; they are not a security sandbox.
/// </summary>
internal static class ManagedFixtureHarness
{
	private const string ResourceName = "AssetRipper.Tests.Fixtures.ManagedRoundTrip.cs";

	public static ManagedFixtureReport Run(FixtureProfile profile, FixtureMutation mutation = FixtureMutation.None)
	{
		using Stream resource = typeof(ManagedFixtureHarness).Assembly.GetManifestResourceStream(ResourceName)!;
		using StreamReader reader = new(resource);
		string source = reader.ReadToEnd();
		ManagedFixtureReport report = new()
		{
			Fixture = ResourceName,
			FixtureSha256 = FixtureCompiler.Hash(Encoding.UTF8.GetBytes(source)),
			References = FixtureCompiler.ReferenceProfile,
			LanguageVersion = profile.Language.ToString(),
			Optimization = profile.Optimization.ToString(),
			CompilerVersion = typeof(CSharpCompilation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
			DecompilerVersion = typeof(DecompilerSettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
			Runtime = RuntimeInformation.FrameworkDescription,
			OperatingSystem = RuntimeInformation.OSDescription,
			Mutation = mutation.ToString(),
			ExpectedBehaviorCount = ManagedFixtureOracle.Cases.Count,
		};
		string artifacts = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "managed-roundtrip",
			$"{profile.Language}-{profile.Optimization}-{mutation}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(artifacts);
		File.WriteAllText(Path.Combine(artifacts, "original.cs"), source);
		try
		{
			FixtureCompilation original = FixtureCompiler.Compile(new Dictionary<string, string> { ["original.cs"] = source }, profile);
			report.OriginalCompilation = original.Check;
			if (original.AssemblyBytes is null) return report;
			report.OriginalAssemblySha256 = FixtureCompiler.Hash(original.AssemblyBytes);
			File.WriteAllBytes(Path.Combine(artifacts, "original.dll"), original.AssemblyBytes);

			using BaseManager manager = new(_ => { });
			// Resolve exclusively against the same reference pack used by the fixture compiler.
			foreach (var image in FixtureCompiler.ReferenceImages) manager.Read(new MemoryStream(image.Bytes, writable: false), image.File);
			var assembly = manager.Read(new MemoryStream(original.AssemblyBytes, writable: false), "ManagedRoundTrip.dll");
			ILSpyAssemblyResolver resolver = new(manager);
			DecompilerSettings settings = new() { UseNestedDirectoriesForNamespaces = true };
			settings.SetLanguageVersion(profile.Language switch
			{
				LanguageVersion.CSharp7_3 => ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp7_3,
				LanguageVersion.CSharp9 => ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp9_0,
				_ => throw new ArgumentOutOfRangeException(nameof(profile)),
			});
			VirtualFileSystem fs = new();
			fs.Directory.Create("/recovered");
			ILSpyWholeProjectDecompiler decompiler = new(settings, resolver, NullProjectFileWriter.Instance, fs);
			report.Decompilation = ScriptDecompilation.Run(decompiler, resolver, assembly, "/recovered", TextWriter.Null);
			report.ResolutionDiagnostics = resolver.Diagnostics;
			Dictionary<string, string> generated = fs.Directory.EnumerateFiles("/recovered", "*.cs", SearchOption.AllDirectories)
				.Order(StringComparer.Ordinal).ToDictionary(p => p["/recovered/".Length..], fs.File.ReadAllText, StringComparer.Ordinal);
			ApplyMutation(generated, profile, mutation);
			foreach (var file in generated)
			{
				report.GeneratedSources.Add(new(file.Key, FixtureCompiler.Hash(Encoding.UTF8.GetBytes(file.Value))));
				string path = Path.Combine(artifacts, "recovered", file.Key);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, file.Value);
			}
			if (!report.Decompilation.Succeeded) return report;
			FixtureCompilation candidate = FixtureCompiler.Compile(generated, profile);
			report.Recompilation = candidate.Check;
			if (candidate.AssemblyBytes is null) return report;
			report.RecompiledAssemblySha256 = FixtureCompiler.Hash(candidate.AssemblyBytes);
			File.WriteAllBytes(Path.Combine(artifacts, "recompiled.dll"), candidate.AssemblyBytes);

			AssemblyLoadContext originalContext = new("fixture-original", isCollectible: true);
			AssemblyLoadContext recoveredContext = new("fixture-recompiled", isCollectible: true);
			try
			{
				using MemoryStream originalStream = new(original.AssemblyBytes, writable: false);
				using MemoryStream candidateStream = new(candidate.AssemblyBytes, writable: false);
				Assembly before = originalContext.LoadFromStream(originalStream);
				Assembly after = recoveredContext.LoadFromStream(candidateStream);
				report.Contract = ManagedFixtureOracle.CompareContract(before, after);
				foreach (FixtureCase test in ManagedFixtureOracle.Cases) report.Behaviors.Add(ManagedFixtureOracle.Compare(before, after, test));
			}
			finally
			{
				originalContext.Unload();
				recoveredContext.Unload();
			}
			return report;
		}
		catch (Exception exception) when (AssetRipper.IO.Files.Exceptions.ExceptionPolicy.IsRecoverable(exception))
		{
			report.HarnessError = exception.ToString();
			return report;
		}
		finally
		{
			string reportPath = Path.Combine(artifacts, "managed-fixture-report.json");
			File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
			TestContext.AddTestAttachment(reportPath, "Managed fixture compile, contract and behavior evidence");
			TestContext.Out.WriteLine($"Managed fixture artifacts: {artifacts}");
		}
	}

	private static void ApplyMutation(Dictionary<string, string> sources, FixtureProfile profile, FixtureMutation mutation)
	{
		if (mutation == FixtureMutation.None) return;
		int changed = 0;
		foreach (string file in sources.Keys.ToArray())
		{
			SyntaxNode root = CSharpSyntaxTree.ParseText(sources[file], new CSharpParseOptions(profile.Language)).GetRoot();
			if (mutation == FixtureMutation.RemoveSerializedField)
			{
				FieldDeclarationSyntax? field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
					.SingleOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "SerializedField"));
				if (field is null) continue;
				root = root.RemoveNode(field, SyntaxRemoveOptions.KeepExteriorTrivia)!;
			}
			else
			{
				MethodDeclarationSyntax? method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.ValueText == "Clamp");
				if (method is null) continue;
				root = root.ReplaceNode(method, method.WithExpressionBody(null).WithSemicolonToken(default)
					.WithBody((BlockSyntax)SyntaxFactory.ParseStatement(mutation == FixtureMutation.StubClamp ? "{ return 0; }" : "{ return missingName; }")));
			}
			sources[file] = root.ToFullString();
			changed++;
		}
		if (changed != 1) throw new InvalidOperationException($"Expected exactly one fixture mutation, changed {changed} files.");
	}
}
