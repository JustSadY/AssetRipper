using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Security.Cryptography;
using System.Text;

namespace AssetRipper.Tests.Decompilation;

internal sealed record FixtureProfile(LanguageVersion Language, OptimizationLevel Optimization);
internal sealed record FixtureCompilation(CompilationCheck Check, byte[]? AssemblyBytes);

internal static class FixtureCompiler
{
	// No host TPA, SDK probing, or implicit references: exactly these pinned reference images.
	public static IReadOnlyList<(string File, byte[] Bytes)> ReferenceImages { get; } = Basic.Reference.Assemblies.Net100.ReferenceInfos.AllValues
		.Select(r => (r.FileName, r.ImageBytes)).OrderBy(r => r.FileName, StringComparer.Ordinal).ToArray();
	public static IReadOnlyList<MetadataReference> References { get; } = ReferenceImages
		.Select(r => MetadataReference.CreateFromImage(r.Bytes, filePath: r.File)).ToArray();
	public static ReferenceProfile ReferenceProfile { get; } = new("ControlledFixture.Net100", "Basic.Reference.Assemblies.Net100", "1.8.12",
		ReferenceImages.Select(r => new ReferenceImage(r.File, Hash(r.Bytes))).ToArray());

	public static FixtureCompilation Compile(IReadOnlyDictionary<string, string> sources, FixtureProfile profile, IEnumerable<MetadataReference>? references = null)
	{
		CSharpParseOptions parseOptions = new(profile.Language);
		CSharpCompilationOptions options = new(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: profile.Optimization,
			checkOverflow: true, allowUnsafe: false, deterministic: true, nullableContextOptions: NullableContextOptions.Disable);
		CSharpCompilation compilation = CSharpCompilation.Create("ManagedRoundTrip",
			sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => CSharpSyntaxTree.ParseText(s.Value, parseOptions, s.Key, Encoding.UTF8)),
			references ?? References, options);
		using MemoryStream stream = new();
		var result = compilation.Emit(stream);
		CompilerDiagnostic[] diagnostics = result.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
			.Select(d =>
			{
				var location = d.Location.GetLineSpan();
				return new CompilerDiagnostic(d.Id, d.Severity.ToString(), d.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
					d.Location.IsInSource ? location.Path : null, d.Location.IsInSource ? location.StartLinePosition.Line + 1 : null,
					d.Location.IsInSource ? location.StartLinePosition.Character + 1 : null);
			}).OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
		return new(new(result.Success ? CheckStatus.Passed : CheckStatus.Failed, diagnostics), result.Success ? stream.ToArray() : null);
	}

	public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
