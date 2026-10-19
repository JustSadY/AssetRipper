using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.PE.Builder;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.Import.Logging;
using AssetRipper.SerializationLogic;
using System.Security.Cryptography;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public class BaseManager : IAssemblyManager
{
	public bool IsSet => ScriptingBackend != ScriptingBackend.Unknown;
	public virtual ScriptingBackend ScriptingBackend => ScriptingBackend.Unknown;

	private readonly List<AssemblyDefinition> assemblies = [];
	private readonly Dictionary<string, List<AssemblyDefinition>> names = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, List<AssemblyDefinition>> aliases = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> requestedNames = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<AssemblyDefinition, AssemblySourceInfo> sources = new(ReferenceEqualityComparer.Instance);
	protected readonly Dictionary<AssemblyDefinition, Stream> m_assemblyStreams = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<Stream> sourceStreams = new(ReferenceEqualityComparer.Instance);
	private readonly List<AssemblyResolutionDiagnostic> diagnostics = [];
	public IReadOnlyList<AssemblyResolutionDiagnostic> ResolutionDiagnostics => diagnostics;
	public bool HasAssemblyNameCollisions => names.Values.Any(v => v.Count > 1);
	protected readonly Dictionary<string, bool> m_validTypes = new();
	private readonly Dictionary<FieldSerializer, Dictionary<ITypeDefOrRef, SerializableType>> monoTypeCache = new();

	private event Action<string> m_requestAssemblyCallback;
	private readonly Dictionary<string, SerializableType> m_serializableTypes = new();

	public RuntimeContext? RuntimeContext
	{
		get
		{
			field ??= GetAssemblies().FirstOrDefault(a => a?.RuntimeContext is not null)?.RuntimeContext;
			return field;
		}
	}

	public BaseManager(Action<string> requestAssemblyCallback)
	{
		m_requestAssemblyCallback = requestAssemblyCallback ?? throw new ArgumentNullException(nameof(requestAssemblyCallback));
	}

	public virtual void Initialize(PlatformGameStructure gameStructure)
	{
	}

	protected static string GetUniqueName(ITypeDefOrRef type)
	{
		string assembly = SpecialFileNames.RemoveAssemblyFileExtension(type.Scope?.Name ?? "");
		return ScriptIdentifier.ToUniqueName(assembly, type.FullName);
	}

	public AssemblyDefinition Load(string filePath, FileSystem fileSystem)
	{
		filePath = fileSystem.Path.GetFullPath(filePath);
		Stream stream = fileSystem.File.OpenRead(filePath);
		try
		{
			return Read(stream, filePath);
		}
		catch (BadImageFormatException badImageFormatException)
		{
			stream.Dispose();
			throw new BadImageFormatException($"Could not read {filePath}", badImageFormatException);
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	public void Add(AssemblyDefinition assembly)
	{
		if (assemblies.Any(a => ReferenceEquals(a, assembly))) return;
		string assemblyName = ToAssemblyName(assembly);
		RuntimeContext? context = RuntimeContext;
		if (!names.TryGetValue(assemblyName, out List<AssemblyDefinition>? candidates))
		{
			candidates = [];
			names.Add(assemblyName, candidates);
		}
		if (candidates.Count == 0 && context is not null)
		{
			if (assembly.RuntimeContext is null) context.AddAssembly(assembly);
			else if (assembly.RuntimeContext != context) throw new InvalidOperationException($"Assembly '{assemblyName}' belongs to a different runtime context");
		}
		assemblies.Add(assembly);
		candidates.Add(assembly);
		requestedNames.Clear();
		monoTypeCache.Clear();
		if (candidates.Count > 1)
		{
			ReportResolution(new("SimpleNameCollision", assemblyName, true, AssemblyResolution.Describe(this, candidates)));
		}
	}

	public AssemblySourceInfo? GetSourceInfo(AssemblyDefinition assembly) => sources.GetValueOrDefault(assembly);

	private void ReportResolution(AssemblyResolutionDiagnostic diagnostic)
	{
		if (diagnostics.Any(d => d.Kind == diagnostic.Kind && d.RequestedIdentity == diagnostic.RequestedIdentity
			&& d.Candidates.SequenceEqual(diagnostic.Candidates))) return;
		diagnostics.Add(diagnostic);
		Logger.Warning(LogCategory.Import, $"Assembly resolution {diagnostic.Kind}: {diagnostic.RequestedIdentity}");
	}

	public Stream GetStreamForAssembly(AssemblyDefinition assembly)
	{
		if (m_assemblyStreams.TryGetValue(assembly, out Stream? result))
		{
			return result;
		}
		else
		{
			MemoryStream memoryStream = new();
			assembly.ManifestModule?.ToPEImage(new ManagedPEImageBuilder(), false).ToPEFile(new ManagedPEFileBuilder()).Write(memoryStream);
			m_assemblyStreams.Add(assembly, memoryStream);
			return memoryStream;
		}
	}

	public void ClearStreamCache()
	{
		foreach (Stream stream in m_assemblyStreams.Values)
		{
			if (!sourceStreams.Contains(stream)) stream.Dispose();
		}
		m_assemblyStreams.Clear();
	}

	private static string ToAssemblyName(AssemblyDefinition assembly)
	{
		return assembly.Name?.ToString() ?? "";
	}

	public IReadOnlyList<AssemblyDefinition> GetAssemblyCandidates(string simpleName) => names.TryGetValue(simpleName, out List<AssemblyDefinition>? bucket) ? bucket.ToArray() : [];

	public AssemblyDefinition Read(Stream stream, string fileNameOrPath)
	{
		string? hash = null;
		if (stream.CanSeek)
		{
			stream.Position = 0;
			hash = Convert.ToHexStringLower(SHA256.HashData(stream));
			stream.Position = 0;
		}
		AssemblyDefinition assembly = AssemblyDefinition.FromStream(stream, createRuntimeContext: false);
		string name = Path.GetFileNameWithoutExtension(fileNameOrPath);
		sources.Add(assembly, new(fileNameOrPath, hash));
		Add(assembly);
		if (!aliases.TryGetValue(name, out List<AssemblyDefinition>? bucket)) aliases.Add(name, bucket = []);
		bucket.Add(assembly);
		m_assemblyStreams.Add(assembly, stream);
		sourceStreams.Add(stream);
		return assembly;
	}

	public bool IsAssemblyLoaded(string assembly)
	{
		return Candidates(assembly).Count > 0;
	}

	public bool IsPresent(ScriptIdentifier scriptID)
	{
		if (!IsSet)
		{
			return false;
		}

		if (scriptID.IsDefault)
		{
			return false;
		}
		else
		{
			return FindType(scriptID.Assembly, scriptID.Namespace, scriptID.Name) != null;
		}
	}

	public bool IsValid(ScriptIdentifier scriptID)
	{
		if (!IsSet)
		{
			return false;
		}

		if (scriptID.IsDefault)
		{
			return false;
		}

		TypeDefinition? type = FindType(scriptID);
		if (type == null)
		{
			return false;
		}

		if (type.IsAbstract)
		{
			return false;
		}

		return true;
	}

	public TypeDefinition GetTypeDefinition(ScriptIdentifier scriptID)
	{
		return FindType(scriptID) ?? throw new ArgumentException($"Can't find type {scriptID.UniqueName}");
	}

	public ScriptIdentifier GetScriptID(string assembly, string @namespace, string name)
	{
		if (!IsSet)
		{
			return default;
		}

		TypeDefinition? type = FindType(assembly, @namespace, name);
		if (type == null)
		{
			return default;
		}
		return new ScriptIdentifier(assembly, type.Namespace ?? "", type.Name ?? "");
	}

	public bool TryGetSerializableType(
		ScriptIdentifier scriptID,
		UnityVersion version,
		[NotNullWhen(true)] out SerializableType? scriptType,
		[NotNullWhen(false)] out string? failureReason)
	{
		if (HasAssemblyNameCollisions)
		{
			scriptType = null;
			failureReason = "Assembly simple-name collisions prevent safe shared-context field recovery. See assembly-resolution-report.json.";
			return false;
		}
		if (m_serializableTypes.TryGetValue(scriptID.UniqueName, out scriptType))
		{
			failureReason = null;
			return true;
		}
		TypeDefinition? type = FindType(scriptID);
		if (type is null)
		{
			scriptType = null;
			failureReason = $"Can't find type: {scriptID.UniqueName}";
			return false;
		}
		else
		{
			FieldSerializer fieldSerializer = new(version, RuntimeContext);
			if (!monoTypeCache.TryGetValue(fieldSerializer, out Dictionary<ITypeDefOrRef, SerializableType>? typeCache))
			{
				typeCache = new(ReferenceEqualityComparer.Instance);
				monoTypeCache[fieldSerializer] = typeCache;
			}

			if (typeCache.TryGetValue(type, out SerializableType? monoType)
				|| fieldSerializer.TryCreateSerializableType(type, typeCache, out monoType, out failureReason))
			{
				scriptType = monoType;
				failureReason = null;
				return true;
			}
			else
			{
				scriptType = null;
				return false;
			}
		}
	}

	internal void AddSerializableType(ITypeDefOrRef type, SerializableType scriptType)
	{
		string uniqueName = GetUniqueName(type);
		AddSerializableType(uniqueName, scriptType);
	}

	internal void InvokeRequestAssemblyCallback(string assemblyName) => m_requestAssemblyCallback.Invoke(assemblyName);

	internal void AddSerializableType(string uniqueName, SerializableType scriptType) => m_serializableTypes.Add(uniqueName, scriptType);

	protected AssemblyDefinition? FindAssembly(string name)
	{
		IReadOnlyList<AssemblyDefinition> candidates = Candidates(name);
		if (candidates.Count == 0 && requestedNames.Add(name))
		{
			InvokeRequestAssemblyCallback(name);
			candidates = Candidates(name);
		}
		if (candidates.Count > 1) ReportResolution(new("AmbiguousSimpleName", name, true, AssemblyResolution.Describe(this, candidates)));
		return candidates.Count == 1 ? candidates[0] : null;
	}

	private IReadOnlyList<AssemblyDefinition> Candidates(string name)
	{
		if (names.TryGetValue(name, out List<AssemblyDefinition>? exact)) return exact;
		name = SpecialFileNames.RemoveAssemblyFileExtension(name);
		return names.TryGetValue(name, out List<AssemblyDefinition>? named) ? named
			: aliases.TryGetValue(name, out List<AssemblyDefinition>? alias) ? alias : [];
	}

	protected TypeDefinition? FindType(string assembly, string @namespace, string name)
	{
		AssemblyDefinition? definition = FindAssembly(assembly);
		if (definition == null)
		{
			return null;
		}

		foreach (ModuleDefinition module in definition.Modules)
		{
			TypeDefinition? type = module.GetType(@namespace, name);
			if (type != null)
			{
				return type;
			}
		}
		return null;
	}

	protected TypeDefinition? FindType(ScriptIdentifier scriptID)
	{
		return FindType(scriptID.Assembly, scriptID.Namespace, scriptID.Name);
	}

	public virtual IEnumerable<AssemblyDefinition> GetAssemblies()
	{
		return assemblies;
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected void Dispose(bool disposing)
	{
		if (disposing)
		{
			ClearStreamCache();
			foreach (Stream stream in sourceStreams) stream.Dispose();
			sourceStreams.Clear();
		}
	}

	~BaseManager() => Dispose(false);
}
