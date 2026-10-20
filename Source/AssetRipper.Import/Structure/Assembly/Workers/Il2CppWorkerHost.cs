using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using System.Buffers.Binary;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>Call only from the worker executable, once. It never executes generated assemblies.</summary>
public static class Il2CppWorkerHost
{
	public static int Run(string requestPath)
	{
		requestPath = Path.GetFullPath(requestPath);
		Il2CppWorkerRequest request = Il2CppWorkerProtocol.ReadRequest(requestPath);
		string job = Path.GetDirectoryName(requestPath)!;
		string status = "RejectedInput"; string? error = null; string? resolvedVersion = null; Il2CppRecoveryReport? recovery = null;
		List<Il2CppWorkerAssembly> assemblies = []; Il2CppWorkerMethod[] methods = [];
		try
		{
			Validate(request, job);
			status = "AnalysisFailed";
			Il2CppCandidateOutput? output = request.Candidates is null ? null : new(request.Candidates);
			IL2CppManager.RecoveryOutputFormat = output; IL2CppManager.RecoveryProcessingLayers = null;
			using IL2CppManager manager = new(_ => { }, request.ContentLevel) { ValidateNativePair = true };
			UnityVersion version = ResolveVersion(request);
			resolvedVersion = version.ToString();
			manager.Initialize(new WorkerStructure(request, job, version));
			recovery = manager.Il2CppRecovery!;
			if (recovery.PairValidation is null) throw new InvalidDataException("The structural pair validation did not run.");
			if (output is not null)
			{
				methods = output.Methods.OrderBy(m => m.Signature, StringComparer.Ordinal).ThenBy(m => m.OriginalToken).ToArray();
				if (methods.Length == 0 || request.Candidates!.TypeNames.Any(t => !methods.Any(m => m.Type == t)))
					throw new InvalidDataException("Requested candidate type was not found or has no methods.");
				// Every managed body of this provider is either the minimal stub or a gated candidate.
				int accepted = methods.Count(m => m.BodyOrigin == "RecoveredNative");
				recovery = recovery with
				{
					MethodBodies = new(recovery.MethodBodies.Unknown - accepted, recovery.MethodBodies.NoBodyByDesign, 0) { RecoveredNative = accepted },
				};
				methods = WriteSidecars(job, methods, output);
			}
			Il2CppWorkerFiles.Verify(request.Native); Il2CppWorkerFiles.Verify(request.Metadata);
			foreach (var file in request.VersionFiles) Il2CppWorkerFiles.Verify(file);
			Directory.CreateDirectory(Path.Combine(job, "Assemblies"));
			int index = 0;
			foreach (var assembly in manager.GetAssemblies().OrderBy(a => a.Name?.ToString(), StringComparer.Ordinal))
			{
				if (index >= 4096) throw new InvalidDataException("Worker assembly count exceeds 4096.");
				string relative = $"Assemblies/{index++:D5}.dll"; string path = Path.Combine(job, relative);
				using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write)) manager.SaveAssembly(assembly, file);
				var identity = Il2CppWorkerFiles.Identify(path);
				assemblies.Add(new(relative, assembly.Name?.ToString() ?? throw new InvalidDataException("Missing assembly name."), identity.Length, identity.Sha256));
			}
			status = "Completed";
		}
		catch (Il2CppPairMismatchException e)
		{
			status = "RejectedPair"; error = e.GetType().Name + ": " + e.Message; assemblies.Clear(); methods = [];
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			// Provider wrappers such as LibCpp2ILInitializationException hide the actual cause in the inner exception.
			error = e.GetType().Name + ": " + e.Message + (e.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : "");
			assemblies.Clear(); methods = [];
		}
		Il2CppWorkerProtocol.WriteResponse(Path.Combine(job, Il2CppWorkerProtocol.ResponseFile), new(Il2CppWorkerProtocol.Version,
			request.JobId, Environment.ProcessId, status, error, request.Native, request.Metadata, recovery, assemblies.ToArray(), methods)
			{ UnityVersion = resolvedVersion, VersionFiles = request.VersionFiles });
		return status switch { "Completed" => 0, "RejectedInput" => 2, "RejectedPair" => 3, _ => 1 };
	}
	private static Il2CppWorkerMethod[] WriteSidecars(string job, Il2CppWorkerMethod[] methods, Il2CppCandidateOutput output)
	{
		Directory.CreateDirectory(Path.Combine(job, "Evidence"));
		Il2CppWorkerMethod[] written = new Il2CppWorkerMethod[methods.Length];
		int index = 0;
		for (int i = 0; i < methods.Length; i++)
		{
			if (!output.Sidecars.TryGetValue(methods[i], out string? text)) { written[i] = methods[i]; continue; }
			string relative = $"Evidence/{index++:D5}.txt"; string path = Path.Combine(job, relative);
			using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write)) file.Write(System.Text.Encoding.UTF8.GetBytes(text));
			var identity = Il2CppWorkerFiles.Identify(path);
			written[i] = methods[i] with { Sidecar = new(relative, identity.Length, identity.Sha256) };
		}
		return written;
	}
	private static void Validate(Il2CppWorkerRequest request, string job)
	{
		if (request.SchemaVersion != Il2CppWorkerProtocol.Version || !Guid.TryParseExact(request.JobId, "N", out _)
			|| Path.GetFileName(job) != request.JobId || request.ContentLevel is < ScriptContentLevel.Level1 or > ScriptContentLevel.Level3
			|| request.UnityVersion is { } explicitVersion && (!UnityVersion.TryParse(explicitVersion, out UnityVersion version, out _) || version == default))
			throw new InvalidDataException("Unsupported worker request identity, content level or Unity version.");
		if (request.Native.Path != Path.Combine(job, "Input", "native.bin") || request.Metadata.Path != Path.Combine(job, "Input", "global-metadata.dat"))
			throw new InvalidDataException("Worker inputs must be the job's immutable snapshots.");
		Il2CppWorkerFiles.Verify(request.Native); Il2CppWorkerFiles.Verify(request.Metadata);
		if (request.VersionFiles is null || request.VersionFiles.Length != (request.UnityVersion is null ? 1 : 0))
			throw new InvalidDataException("Invalid version-evidence selection.");
		foreach (var file in request.VersionFiles)
		{
			string name = Path.GetFileName(file.Path);
			if (name is not ("globalgamemanagers" or "data.unity3d" or "UnityPlayer.bin") || file.Path != Path.Combine(job, "Input", "Version", name))
				throw new InvalidDataException("Invalid worker version-evidence path.");
			Il2CppWorkerFiles.Verify(file);
		}
		using (FileStream stream = File.OpenRead(request.Metadata.Path))
		{
			Span<byte> header = stackalloc byte[8]; stream.ReadExactly(header);
			if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xFAB11BAF || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) < 16)
				throw new InvalidDataException("Unsupported or encrypted IL2CPP metadata header.");
		}
		if (request.Candidates is { } selection && (request.ContentLevel != ScriptContentLevel.Level3
			|| string.IsNullOrWhiteSpace(selection.AssemblyName) || selection.TypeNames is not { Length: > 0 and <= 64 }
			|| selection.TypeNames.Any(string.IsNullOrWhiteSpace) || selection.TypeNames.Distinct(StringComparer.Ordinal).Count() != selection.TypeNames.Length))
			throw new InvalidDataException("Experimental candidates require Level3, one assembly and 1–64 distinct explicit types.");
	}
	private static UnityVersion ResolveVersion(Il2CppWorkerRequest request)
	{
		if (request.UnityVersion is not null) return UnityVersion.Parse(request.UnityVersion);
		string path = request.VersionFiles.Single().Path;
		UnityVersion version;
		if (Path.GetFileName(path) == "globalgamemanagers") version = Cpp2IL.Core.Cpp2IlApi.GetVersionFromGlobalGameManagers(File.ReadAllBytes(path));
		else if (Path.GetFileName(path) == "data.unity3d")
		{
			using FileStream stream = File.OpenRead(path); version = Cpp2IL.Core.Cpp2IlApi.GetVersionFromDataUnity3D(stream);
		}
		else version = Cpp2IL.Core.Cpp2IlApi.DetermineUnityVersion(path, null);
		if (version == default) throw new InvalidDataException("The worker could not determine a Unity version from the captured evidence.");
		return version;
	}
	private sealed class WorkerStructure : PlatformGameStructure
	{
		public WorkerStructure(Il2CppWorkerRequest request, string job, UnityVersion version) : base(job, LocalFileSystem.Instance)
		{
			GameDataPath = job; Il2CppGameAssemblyPath = request.Native.Path; Il2CppMetaDataPath = request.Metadata.Path; Version = version;
		}
	}
}
