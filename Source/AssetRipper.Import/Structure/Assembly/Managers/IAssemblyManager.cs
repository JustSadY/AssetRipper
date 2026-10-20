using AsmResolver.DotNet;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.SerializationLogic;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public interface IAssemblyManager : IDisposable, ITypeResolver
{
	void Initialize(PlatformGameStructure gameStructure);
	AssemblyDefinition Load(string filePath, FileSystem fileSystem);
	void Add(AssemblyDefinition assembly);
	AssemblyDefinition Read(Stream stream, string fileName);

	bool IsAssemblyLoaded(string assembly);
	bool IsPresent(ScriptIdentifier scriptID);
	bool IsValid(ScriptIdentifier scriptID);
	TypeDefinition GetTypeDefinition(ScriptIdentifier scriptID);
	IEnumerable<AssemblyDefinition> GetAssemblies();
	IReadOnlyList<AssemblyDefinition> GetAssemblyCandidates(string simpleName) => GetAssemblies()
		.Where(a => string.Equals(a.Name, simpleName, StringComparison.OrdinalIgnoreCase)).ToArray();
	ScriptIdentifier GetScriptID(string assembly, string @namespace, string name);
	Stream GetStreamForAssembly(AssemblyDefinition assembly);
	void ClearStreamCache();
	AssemblySourceInfo? GetSourceInfo(AssemblyDefinition assembly) => null;
	Il2CppRecoveryReport? Il2CppRecovery => null;
	IReadOnlyList<AssemblyResolutionDiagnostic> ResolutionDiagnostics => [];
	bool HasAssemblyNameCollisions => GetAssemblies().GroupBy(a => a.Name?.ToString() ?? "", StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);

	RuntimeContext? RuntimeContext { get; }
	bool IsSet { get; }
	ScriptingBackend ScriptingBackend { get; }

	public sealed AssemblyDefinition? Mscorlib
	{
		get
		{
			AssemblyDefinition[] matches = GetAssemblies().Where(a => string.Equals(a.Name, "mscorlib", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
			return matches.Length == 1 ? matches[0] : null;
		}
	}
	public sealed bool HasMscorlib2 => Mscorlib?.Version.Major == 2;
}
public static class AssemblyManagerExtensions
{
	public static void SaveAssembly(this IAssemblyManager manager, AssemblyDefinition assembly, string path, FileSystem fileSystem)
	{
		using Stream writeStream = fileSystem.File.Create(path);
		manager.SaveAssembly(assembly, writeStream);
	}
	public static void SaveAssembly(this IAssemblyManager manager, AssemblyDefinition assembly, Stream writeStream)
	{
		Stream readStream = manager.GetStreamForAssembly(assembly);
		readStream.Position = 0;
		readStream.CopyTo(writeStream);
	}
}
