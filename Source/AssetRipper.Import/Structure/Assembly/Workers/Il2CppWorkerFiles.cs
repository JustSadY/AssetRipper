using System.Security.Cryptography;

namespace AssetRipper.Import.Structure.Assembly.Workers;

public static class Il2CppWorkerFiles
{
	private static readonly string[] RecoveredGates = ["IsilChecked", "StackHeightChecked", "TypedCilChecked"];

	public static Il2CppWorkerArtifact Identify(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return new(Path.GetFullPath(path), stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
	}
	public static void Verify(Il2CppWorkerArtifact artifact)
	{
		if (artifact is null || !Path.IsPathFullyQualified(artifact.Path) || artifact.Length < 1 || artifact.Sha256 is not { Length: 64 })
			throw new InvalidDataException("Invalid worker input identity.");
		if (Identify(artifact.Path) != artifact) throw new InvalidDataException("Worker input hash or length mismatch.");
	}
	public static string ResolveOutput(string directory, string relative) => Resolve(directory, relative, "Assemblies", ".dll");
	public static string ResolveEvidence(string directory, string relative) => Resolve(directory, relative, "Evidence", ".txt");
	private static string Resolve(string directory, string relative, string folder, string extension)
	{
		// Ordinal numeric filenames avoid names from native metadata becoming filesystem paths.
		string prefix = folder + "/";
		if (relative is null || relative.Length != prefix.Length + 5 + extension.Length || !relative.StartsWith(prefix, StringComparison.Ordinal)
			|| !relative.EndsWith(extension, StringComparison.Ordinal) || relative.AsSpan(prefix.Length, 5).IndexOfAnyExceptInRange('0', '9') >= 0)
			throw new InvalidDataException($"Invalid worker {folder} path.");
		string path = Path.Combine(directory, relative);
		if ((File.GetAttributes(Path.Combine(directory, folder)) & FileAttributes.ReparsePoint) != 0
			|| (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new InvalidDataException("Worker output links are not accepted.");
		return path;
	}
	public static void ValidateResponse(Il2CppWorkerRequest request, Il2CppWorkerResponse response, string job, int processId)
	{
		if (response.SchemaVersion != Il2CppWorkerProtocol.Version || response.JobId != request.JobId || response.ProcessId != processId
			|| response.Native != request.Native || response.Metadata != request.Metadata || response.VersionFiles is null || !response.VersionFiles.SequenceEqual(request.VersionFiles))
			throw new InvalidDataException("Worker response does not match this job, process or input identities.");
		if (response.Status != "Completed") throw new InvalidDataException("Worker analysis did not complete: " + response.Error);
		if (!UnityVersion.TryParse(response.UnityVersion, out UnityVersion resolved, out _) || resolved == default
			|| request.UnityVersion is { } expected && UnityVersion.Parse(expected) != resolved)
			throw new InvalidDataException("Invalid or mismatched worker Unity version.");
		if (response.Recovery is not { SchemaVersion: 1, Validation: "NotRun" } recovery || recovery.RequestedContentLevel != request.ContentLevel.ToString()
			|| response.Assemblies is not { Length: > 0 and <= 4096 } || response.Methods is null)
			throw new InvalidDataException("Incomplete worker analysis report.");
		if (recovery.Isolation is not null || recovery.MethodBodies is not { GeneratedStub: >= 0, NoBodyByDesign: >= 0, Unknown: 0, RecoveredNative: >= 0 })
			throw new InvalidDataException("Invalid worker counts or premature protocol validation claim.");
		if (recovery.PairValidation is not { Status: "Passed" or "Partial", Images: > 0, TypeDefinitions: > 0, MethodDefinitions: >= 0 })
			throw new InvalidDataException("The worker did not report a structural binary/metadata pair validation.");

		bool level3 = request.ContentLevel == Configuration.ScriptContentLevel.Level3;
		if (request.Candidates is null)
		{
			if (!recovery.UsesKnownStubOutput || response.Methods.Length != 0 || recovery.MethodBodies.RecoveredNative != 0
				|| recovery.RecoveryCapability != (level3 ? "RecoveryUnavailable" : "NotRequested"))
				throw new InvalidDataException("Metadata-only worker must return known stubs without recovery claims.");
			if (recovery.OutputFormat is not { TypeName: "Cpp2IL.Core.OutputFormats.AsmResolverDllOutputFormatDefault", AssemblyName: "Cpp2IL.Core", Id: "dll_default" })
				throw new InvalidDataException("Unexpected metadata-only worker provider.");
		}
		if (response.Methods.Any(m => m.BehaviorValidation != "NotRun" || m.BodyOrigin is not ("GeneratedStub" or "RecoveredNative" or "NoBodyByDesign")))
			throw new InvalidDataException("A worker cannot claim behavioral validation or an unknown body origin.");
		if (request.Candidates is { } selection)
		{
			if (recovery.UsesKnownStubOutput || recovery.RecoveryCapability != "ExperimentalCandidates" || response.Methods.Length > 4096
				|| recovery.OutputFormat.Id != "experimental_checked_candidates"
				|| response.Methods.Any(m => !selection.TypeNames.Contains(m.Type, StringComparer.Ordinal))
				|| response.Methods.Count(m => m.BodyOrigin == "RecoveredNative") != recovery.MethodBodies.RecoveredNative)
				throw new InvalidDataException("Inconsistent experimental candidate report.");
			// A recovered body must have passed every gate, in order, and must not be a shared generic body.
			if (response.Methods.Any(m => m.BodyOrigin == "RecoveredNative" && (m.StructuralValidation != "TypedCilChecked" || m.IsGeneric
				|| m.SharedAddressMethods > 1 || m.Gates is null || !m.Gates.AsSpan().EndsWith(RecoveredGates))))
				throw new InvalidDataException("A recovered candidate lacks the required structural gates.");
		}
		if (response.Assemblies.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != response.Assemblies.Length
			|| response.Assemblies.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != response.Assemblies.Length)
			throw new InvalidDataException("Duplicate worker output path or assembly name.");
		foreach (Il2CppWorkerAssembly assembly in response.Assemblies)
		{
			string path = ResolveOutput(job, assembly.Path);
			Verify(new(path, assembly.Length, assembly.Sha256));
		}
		Il2CppWorkerEvidence[] sidecars = response.Methods.Select(m => m.Sidecar).OfType<Il2CppWorkerEvidence>().ToArray();
		if (sidecars.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sidecars.Length)
			throw new InvalidDataException("Duplicate worker evidence path.");
		foreach (Il2CppWorkerEvidence sidecar in sidecars)
		{
			Verify(new(ResolveEvidence(job, sidecar.Path), sidecar.Length, sidecar.Sha256));
		}
		Verify(request.Native); Verify(request.Metadata);
		foreach (var file in request.VersionFiles) Verify(file);
	}
}
