using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Tests.Decompilation;

internal sealed record PinnedUnityReferenceManifest(int SchemaVersion, string Name, string UnityVersion, string ApiCompatibilityLevel,
	LanguageVersion LanguageVersion, IReadOnlyList<ReferenceImage> References);

/// <summary>Compile-only reference images. Exact manifest hashes are required; no host or SDK fallback.</summary>
internal sealed record PinnedUnityReferences(PinnedUnityReferenceManifest Manifest, IReadOnlyList<(string File, byte[] Bytes)> Images,
	IReadOnlyList<MetadataReference> MetadataReferences)
{
	public const string EditorDataEnvironmentVariable = "ASSETRIPPER_UNITY_EDITOR_DATA";

	public static PinnedUnityReferenceManifest ReadManifest()
	{
		using Stream stream = typeof(PinnedUnityReferences).Assembly.GetManifestResourceStream("AssetRipper.Tests.Fixtures.Unity6000.4.NetStandard21.json")!;
		JsonSerializerOptions options = new();
		options.Converters.Add(new JsonStringEnumConverter<LanguageVersion>());
		return JsonSerializer.Deserialize<PinnedUnityReferenceManifest>(stream, options)!;
	}

	public static PinnedUnityReferences Load(PinnedUnityReferenceManifest manifest, string editorDataDirectory, FileSystem fs)
	{
		if (manifest.SchemaVersion != 1 || manifest.References.Count == 0
			|| manifest.References.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.References.Count)
			throw new InvalidDataException("Invalid or duplicate pinned Unity reference manifest.");
		List<(string File, byte[] Bytes)> images = [];
		foreach (ReferenceImage reference in manifest.References)
		{
			if (Path.IsPathRooted(reference.File) || reference.File.Split('/', '\\').Contains(".."))
				throw new InvalidDataException($"Reference must be relative to Editor/Data: {reference.File}");
			string path = fs.Path.Join(editorDataDirectory, reference.File);
			byte[] bytes = fs.File.ReadAllBytes(path);
			string actual = FixtureCompiler.Hash(bytes);
			if (!string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException($"Unity reference hash mismatch: {reference.File}; expected {reference.Sha256}, actual {actual}.");
			images.Add((reference.File, bytes));
		}
		return new(manifest, images, images.Select(i => MetadataReference.CreateFromImage(i.Bytes, filePath: i.File)).ToArray());
	}

	public static PinnedUnityReferences RequireInstalledProfile()
	{
		string? directory = Environment.GetEnvironmentVariable(EditorDataEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Unity reference compile check NotRun: set {EditorDataEnvironmentVariable} to the pinned Editor/Data directory.");
		return Load(ReadManifest(), directory!, LocalFileSystem.Instance);
	}
}
