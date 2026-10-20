using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly.Managers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>
/// Explicit opt-in. The executable is a trusted local tool, not a game artifact. <paramref name="MaxHeapMegabytes"/>
/// caps the managed heap of a .NET worker through DOTNET_GCHeapHardLimit; it is not an OS memory limit.
/// Without <paramref name="RetainInputSnapshots"/>, the hashed input copies of a completed job are deleted;
/// failed jobs always keep them as evidence.
/// </summary>
public sealed record Il2CppWorkerOptions(string ExecutablePath, string? WorkDirectory = null, int TimeoutSeconds = 300,
	int? MaxHeapMegabytes = null, bool RetainInputSnapshots = true);
public sealed record Il2CppWorkerArtifact(string Path, long Length, string Sha256);
public sealed record Il2CppCandidateSelection(string AssemblyName, string[] TypeNames);
public sealed record Il2CppVersionSource(string? UnityPlayerPath, string? GameDataPath);
public sealed record Il2CppWorkerRequest(int SchemaVersion, string JobId, string? UnityVersion, ScriptContentLevel ContentLevel,
	Il2CppWorkerArtifact Native, Il2CppWorkerArtifact Metadata, Il2CppCandidateSelection? Candidates)
{
	[JsonRequired] public Il2CppWorkerArtifact[] VersionFiles { get; init; } = [];
}
public sealed record Il2CppWorkerAssembly(string Path, string Name, long Length, string Sha256);
public sealed record Il2CppGenericInstantiation(string[] TypeArguments, string[] MethodArguments, ulong Pointer, ulong Rva, int MethodsAtAddress);
/// <summary>A job-relative evidence file (<c>Evidence/NNNNN.txt</c>) that the parent verifies but never loads.</summary>
public sealed record Il2CppWorkerEvidence(string Path, long Length, string Sha256);
public sealed record Il2CppWorkerMethod(uint OriginalToken, string Type, string Name, string Signature, ulong Pointer, ulong Rva,
	long? RawOffset, int SharedAddressMethods, string BodyOrigin, string StructuralValidation, string BehaviorValidation,
	string? Diagnostic, int IsilInstructionCount, int CilInstructionCount, string[] Cil, bool EvidenceTruncated)
{
	/// <summary>Every gate status in evaluation order; the last one is <see cref="StructuralValidation"/>.</summary>
	public string[] Gates { get; init; } = [];
	/// <summary>The method or its declaring type has generic parameters.</summary>
	public bool IsGeneric { get; init; }
	public Il2CppAddressLowering? AddressLowering { get; init; }
	/// <summary>Untyped ISIL locals that received an integer or Boolean type from their definitions.</summary>
	public int InferredLocalTypes { get; init; }
	/// <summary>The instruction set that lifted this method's native code.</summary>
	public string? Lifter { get; init; }
	/// <summary>For a generic method: every concrete instantiation the binary carries, and how many methods share each body.</summary>
	public Il2CppGenericInstantiation[]? GenericInstantiations { get; init; }
	/// <summary>Native disassembly, ISIL and CIL text for this method.</summary>
	public Il2CppWorkerEvidence? Sidecar { get; init; }
}
public sealed record Il2CppWorkerResponse(int SchemaVersion, string JobId, int ProcessId, string Status, string? Error,
	Il2CppWorkerArtifact Native, Il2CppWorkerArtifact Metadata, Il2CppRecoveryReport? Recovery,
	Il2CppWorkerAssembly[] Assemblies, Il2CppWorkerMethod[] Methods)
{
	public string? UnityVersion { get; init; }
	[JsonRequired] public Il2CppWorkerArtifact[] VersionFiles { get; init; } = [];
}
public sealed record Il2CppWorkerRun(string JobDirectory, string Status, int? ProcessId, int? ExitCode, double ElapsedSeconds,
	string? Error, bool StdoutTruncated, bool StderrTruncated, Il2CppWorkerResponse? Response)
{
	public bool Completed => Status == "Completed";
	/// <summary>False only after a completed job's input copies were deleted on request.</summary>
	public bool InputSnapshotsRetained { get; init; } = true;
}

/// <summary>Wire serialization is shared by the parent and the separate worker; no reflection fallback.</summary>
public static class Il2CppWorkerProtocol
{
	public const int Version = 2;
	public const string RequestFile = "request.json";
	public const string ResponseFile = "response.json";
	public const int MaxResponseBytes = 16 * 1024 * 1024;
	public static void WriteRequest(string path, Il2CppWorkerRequest request) => Write(path, request, Il2CppWorkerJsonContext.Default.Il2CppWorkerRequest);
	public static void WriteResponse(string path, Il2CppWorkerResponse response) => Write(path, response, Il2CppWorkerJsonContext.Default.Il2CppWorkerResponse);
	public static void WriteRun(string path, Il2CppWorkerRun run) => Write(path, run, Il2CppWorkerJsonContext.Default.Il2CppWorkerRun);
	public static Il2CppWorkerRequest ReadRequest(string path) => Read(path, 64 * 1024, Il2CppWorkerJsonContext.Default.Il2CppWorkerRequest);
	public static Il2CppWorkerResponse ReadResponse(string path) => Read(path, MaxResponseBytes, Il2CppWorkerJsonContext.Default.Il2CppWorkerResponse);
	private static T Read<T>(string path, int limit, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
	{
		using FileStream stream = File.OpenRead(path);
		if (stream.Length > limit) throw new InvalidDataException("Worker protocol document exceeds its size limit.");
		return JsonSerializer.Deserialize(stream, type) ?? throw new InvalidDataException("Empty worker protocol document.");
	}
	private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
	{
		string temporary = path + ".tmp";
		using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(stream, value, type);
		File.Move(temporary, path, overwrite: false);
	}
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Il2CppWorkerRequest))]
[JsonSerializable(typeof(Il2CppWorkerResponse))]
[JsonSerializable(typeof(Il2CppWorkerRun))]
internal sealed partial class Il2CppWorkerJsonContext : JsonSerializerContext;
