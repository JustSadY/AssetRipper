using AsmResolver.DotNet;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal sealed record EditorFixtureFile(string Path, string Sha256);
internal sealed record EditorFixtureManifest(int SchemaVersion, string UnityVersion, string BuildTarget, EditorFixtureFile[] Files);
internal sealed record EditorPointerOracle(string Field, bool IsNull, string? Name, string? Type, string? SourceGuid, long SourceLocalId, string? Text);
internal sealed record EditorValueOracle(int Inherited, int Hidden, bool Flag, string Label, int[] Numbers, int NestedCount, EditorPointerOracle[] Pointers);
internal sealed record EditorPayloadOracle(int SchemaVersion, string UnityVersion, string BuildTarget, string ScriptAssembly,
	string ScriptNamespace, string ScriptType, bool EditorSceneReadbackVerified, EditorValueOracle Expected);

internal sealed record UnityEditorPayloadFixture(string DirectoryPath, EditorFixtureManifest Manifest, EditorPayloadOracle Oracle)
{
	public const string EnvironmentVariable = "ASSETRIPPER_UNITY_PAYLOAD_FIXTURE";
	public static UnityEditorPayloadFixture Require()
	{
		string? directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (string.IsNullOrWhiteSpace(directory)) Assert.Ignore($"Editor-generated payload check NotRun: set {EnvironmentVariable} to a generated fixture directory.");
		return Load(directory!, LocalFileSystem.Instance);
	}

	public static UnityEditorPayloadFixture Load(string directory, FileSystem fs)
	{
		EditorFixtureManifest manifest = VerifyManifest(directory, fs, ["Sources/EditorPayload.cs", "Sources/PayloadFixtureBuilder.cs", "Sources/PayloadScene.unity", "Sources/Payload.prefab"]);
		EditorPayloadOracle oracle = JsonSerializer.Deserialize<EditorPayloadOracle>(fs.File.ReadAllText(fs.Path.Join(directory, "oracle.json")))!;
		if (oracle.SchemaVersion != 1 || oracle.UnityVersion != manifest.UnityVersion || oracle.BuildTarget != manifest.BuildTarget || !oracle.EditorSceneReadbackVerified)
			throw new InvalidDataException("Editor fixture oracle does not match its manifest.");
		return new(directory, manifest, oracle);
	}

	internal static EditorFixtureManifest VerifyManifest(string directory, FileSystem fs, string[] requiredSources)
	{
		EditorFixtureManifest manifest = JsonSerializer.Deserialize<EditorFixtureManifest>(fs.File.ReadAllText(fs.Path.Join(directory, "fixture-manifest.json")))!;
		if (manifest.SchemaVersion != 1 || manifest.UnityVersion != "6000.4.0f1" || manifest.BuildTarget != "StandaloneLinux64"
			|| manifest.Files.Length == 0 || manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
			throw new InvalidDataException("Unsupported or duplicate Editor fixture manifest.");
		foreach (EditorFixtureFile file in manifest.Files)
		{
			if (Path.IsPathRooted(file.Path) || file.Path.Split('/', '\\').Contains("..")) throw new InvalidDataException("Fixture paths must be relative without traversal.");
			byte[] bytes = fs.File.ReadAllBytes(fs.Path.Join(directory, file.Path));
			if (FixtureCompiler.Hash(bytes) != file.Sha256) throw new InvalidDataException($"Editor fixture hash mismatch: {file.Path}");
		}
		foreach (string required in new[] { "Assembly-CSharp.dll", "oracle.json", "WithTypeTree/payload", "WithTypeTree/external",
			"WithoutTypeTree/payload", "WithoutTypeTree/external" }.Concat(requiredSources))
			if (!manifest.Files.Any(f => f.Path == required)) throw new InvalidDataException($"Missing fixture manifest entry: {required}");
		return manifest;
	}

	public EditorPayloadManager CreateManager()
		=> CreateManager(DirectoryPath);

	internal static EditorPayloadManager CreateManager(string directory)
	{
		string? editorData = Environment.GetEnvironmentVariable(PinnedUnityReferences.EditorDataEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(editorData)) throw new InvalidDataException("A configured Editor payload fixture requires ASSETRIPPER_UNITY_EDITOR_DATA; no host references are substituted.");
		var references = PinnedUnityReferences.Load(PinnedUnityReferences.ReadManifest(), editorData, LocalFileSystem.Instance);
		return new(File.ReadAllBytes(Path.Combine(directory, "Assembly-CSharp.dll")), references);
	}

	internal sealed class EditorPayloadManager : BaseManager
	{
		private readonly AssemblyDefinition fixture;
		public override ScriptingBackend ScriptingBackend => ScriptingBackend.Mono;
		public override IEnumerable<AssemblyDefinition> GetAssemblies() => fixture is null ? [] : [fixture];
		public EditorPayloadManager(byte[] image, PinnedUnityReferences references) : base(_ => { })
		{
			var corlibImage = references.Images.Single(i => Path.GetFileName(i.File) == "mscorlib.dll");
			AssemblyDefinition corlib = AssemblyDefinition.FromBytes(corlibImage.Bytes, createRuntimeContext: false);
			RuntimeContext context = new(DotNetRuntimeInfo.NetCoreApp(10, 0), (bool?)null, corlib);
			foreach (var reference in references.Images)
			{
				AssemblyDefinition assembly = reference.File == corlibImage.File ? corlib : AssemblyDefinition.FromBytes(reference.Bytes, createRuntimeContext: false);
				context.AddAssembly(assembly); Add(assembly);
			}
			fixture = AssemblyDefinition.FromBytes(image, createRuntimeContext: false);
			context.AddAssembly(fixture); Add(fixture);
		}
	}
}
