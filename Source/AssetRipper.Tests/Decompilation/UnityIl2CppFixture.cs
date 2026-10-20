using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record MethodObservation(string Id, string Method, int A, int B, int Value);
internal sealed record MethodRuntimeReport(int SchemaVersion, string UnityVersion, string Backend, MethodObservation[] Cases);

internal sealed record UnityIl2CppFixture(string DirectoryPath, EditorFixtureManifest Manifest, MethodRuntimeReport Oracle)
{
	public const string EnvironmentVariable = "ASSETRIPPER_UNITY_IL2CPP_FIXTURE";
	public const string NativePath = "IL2CPP/GameAssembly.so";
	public const string MetadataPath = "IL2CPP/Methods_Data/il2cpp_data/Metadata/global-metadata.dat";
	public const string MonoAssemblyPath = "Mono/Methods_Data/Managed/Assembly-CSharp.dll";
	public const string AndroidNativePath = "IL2CPP/lib/arm64-v8a/libil2cpp.so";
	public const string AndroidMetadataPath = "IL2CPP/assets/bin/Data/Managed/Metadata/global-metadata.dat";
	/// <summary>A build from <c>build_il2cpp_fixture.py --stripped</c>: no Preserve attributes, High managed stripping.</summary>
	public const string StrippedEnvironmentVariable = "ASSETRIPPER_UNITY_IL2CPP_STRIPPED_FIXTURE";
	/// <summary>A build from <c>build_il2cpp_fixture.py --target android-arm64</c>, executed on an arm64-v8a emulator.</summary>
	public const string Arm64EnvironmentVariable = "ASSETRIPPER_UNITY_IL2CPP_ARM64_FIXTURE";
	public bool IsAndroid => Manifest.BuildTarget == "Android";
	public string NativeFile => Path.Combine(DirectoryPath, IsAndroid ? AndroidNativePath : NativePath);
	public string MetadataFile => Path.Combine(DirectoryPath, IsAndroid ? AndroidMetadataPath : MetadataPath);
	internal static MethodObservation[] ExpectedCases { get; } =
	[
		new("Affine/-9", "Affine", -9, 0, -74), new("Affine/0", "Affine", 0, 0, -11), new("Affine/2", "Affine", 2, 0, 3),
		new("Affine/100", "Affine", 100, 0, 689), new("Affine/2147483647", "Affine", int.MaxValue, 0, 2147483630),
		new("Branch/-4", "Branch", -4, 0, 7), new("Branch/0", "Branch", 0, 0, 17), new("Branch/8", "Branch", 8, 0, 4), new("Branch/5", "Branch", 5, 0, 16),
		new("Sum/-2", "Sum", -2, 0, 0), new("Sum/0", "Sum", 0, 0, 0), new("Sum/6", "Sum", 6, 0, 21), new("Sum/12", "Sum", 12, 0, 78),
		new("Divide/17/3", "Divide", 17, 3, 5), new("Divide/-17/3", "Divide", -17, 3, -5), new("Divide/7/0", "Divide", 7, 0, -999),
		new("Counter/3", "Step", 5, 3, 8), new("Counter/-8", "Step", 8, -8, 0), new("Counter/14", "Step", 0, 14, 14), new("Echo/int", "Echo", 42, 0, 42),
	];
	/// <summary>A build from <c>build_il2cpp_fixture.py --pair-variant</c>: one extra method, same types and images.</summary>
	public const string PairVariantEnvironmentVariable = "ASSETRIPPER_UNITY_IL2CPP_PAIR_VARIANT_FIXTURE";
	public static UnityIl2CppFixture RequirePairVariant()
	{
		string? directory = Environment.GetEnvironmentVariable(PairVariantEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Owned IL2CPP pair-variant trial NotRun: set {PairVariantEnvironmentVariable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}
	public static UnityIl2CppFixture RequireStripped() => RequireFrom(StrippedEnvironmentVariable, "stripped");
	public static UnityIl2CppFixture RequireArm64() => RequireFrom(Arm64EnvironmentVariable, "ARM64");
	private static UnityIl2CppFixture RequireFrom(string variable, string kind)
	{
		string? directory = Environment.GetEnvironmentVariable(variable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Owned IL2CPP {kind} trial NotRun: set {variable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}
	public static UnityIl2CppFixture Require()
	{
		string? directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Owned IL2CPP capability trial NotRun: set {EnvironmentVariable}.");
		return Load(directory!, LocalFileSystem.Instance);
	}
	internal static UnityIl2CppFixture Load(string directory, FileSystem fs)
	{
		var manifest = JsonSerializer.Deserialize<EditorFixtureManifest>(fs.File.ReadAllText(fs.Path.Join(directory, "fixture-manifest.json")))!;
		bool android = manifest.BuildTarget == "Android";
		if (manifest.SchemaVersion != 1 || manifest.UnityVersion != "6000.4.0f1" || manifest.BuildTarget is not ("StandaloneLinux64" or "Android")
			|| manifest.Files.Length == 0 || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
			throw new InvalidDataException("Unsupported or duplicate IL2CPP fixture manifest.");
		foreach (var file in manifest.Files)
		{
			if (Path.IsPathRooted(file.Path) || file.Path.Split('/', '\\').Contains("..")) throw new InvalidDataException("Fixture path traversal.");
			using Stream stream = fs.File.OpenRead(fs.Path.Join(directory, file.Path));
			stream.Position = 0;
			if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != file.Sha256)
				throw new InvalidDataException($"IL2CPP fixture hash mismatch: {file.Path}");
		}
		string[] binaries = android ? [AndroidNativePath, AndroidMetadataPath] : [NativePath, MetadataPath, MonoAssemblyPath, "mono-runtime.json"];
		foreach (string required in binaries.Concat(["Sources/Corpus.cs", "Sources/Il2CppFixtureBuilder.cs", "Sources/link.xml", "Sources/Methods.unity", "oracle.json", "il2cpp-runtime.json"]))
			if (!manifest.Files.Any(f => f.Path == required)) throw new InvalidDataException($"Missing IL2CPP fixture entry: {required}");
		MethodRuntimeReport Read(string file) => JsonSerializer.Deserialize<MethodRuntimeReport>(fs.File.ReadAllText(fs.Path.Join(directory, file)))!;
		var oracle = Read("oracle.json");
		if (oracle.SchemaVersion != 1 || oracle.UnityVersion != manifest.UnityVersion || oracle.Backend != "EditorManaged"
			|| !oracle.Cases.SequenceEqual(ExpectedCases))
			throw new InvalidDataException("The owned IL2CPP fixture requires the 20 independently checked observations.");
		// Unity has no ARM64 Mono player, so the Android fixture is checked against the Editor oracle only.
		foreach (string backend in android ? new[] { "IL2CPP" } : new[] { "Mono", "IL2CPP" })
		{
			var runtime = Read(backend.ToLowerInvariant() + "-runtime.json");
			if (runtime.SchemaVersion != 1 || runtime.UnityVersion != oracle.UnityVersion || runtime.Backend != backend || !runtime.Cases.SequenceEqual(oracle.Cases))
				throw new InvalidDataException($"{backend} runtime observations differ from the oracle.");
		}
		return new(directory, manifest, oracle);
	}
	public PlatformGameStructure CreateStructure() => new OwnedStructure(this);
	private sealed class OwnedStructure : PlatformGameStructure
	{
		public OwnedStructure(UnityIl2CppFixture fixture) : base(fixture.DirectoryPath, LocalFileSystem.Instance)
		{
			GameDataPath = Path.Combine(fixture.DirectoryPath, "IL2CPP/Methods_Data");
			Il2CppGameAssemblyPath = Path.Combine(fixture.DirectoryPath, NativePath);
			Il2CppMetaDataPath = Path.Combine(fixture.DirectoryPath, MetadataPath);
			UnityPlayerPath = Path.Combine(fixture.DirectoryPath, "IL2CPP/UnityPlayer.so");
			Version = UnityVersion.Parse(fixture.Manifest.UnityVersion);
		}
	}
}
