using AsmResolver.DotNet;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly.Workers;
using AssetRipper.Import.Structure.Platforms;

namespace AssetRipper.Import.Structure.Assembly.Managers;

/// <summary>Built-in metadata/stub import in a new worker process. Custom recovery hooks are not transported.</summary>
public sealed class IsolatedIL2CppManager(Action<string> callback, ScriptContentLevel level, Il2CppWorkerOptions options) : BaseManager(callback)
{
	public override ScriptingBackend ScriptingBackend => ScriptingBackend.IL2Cpp;
	public override Il2CppRecoveryReport? Il2CppRecovery => recovery;
	private Il2CppRecoveryReport? recovery;
	public Il2CppWorkerRun? WorkerRun { get; private set; }
	public override void Initialize(PlatformGameStructure gameStructure) => InitializeAsync(gameStructure).GetAwaiter().GetResult();
	public async Task InitializeAsync(PlatformGameStructure gameStructure, CancellationToken cancellationToken = default)
	{
		if (WorkerRun is not null || GetAssemblies().Any()) throw new InvalidOperationException("An isolated manager can initialize only once.");
		if (level == ScriptContentLevel.Level3 && (IL2CppManager.RecoveryOutputFormat is not null || IL2CppManager.RecoveryProcessingLayers is not null))
			throw new NotSupportedException("The metadata-only worker does not transport custom recovery hooks.");
		string native = gameStructure.Il2CppGameAssemblyPath ?? throw new InvalidDataException("Missing IL2CPP native binary path.");
		string metadata = gameStructure.Il2CppMetaDataPath ?? throw new InvalidDataException("Missing IL2CPP metadata path.");
		WorkerRun = await Il2CppWorkerClient.AnalyzeAsync(options, native, metadata, gameStructure.Version?.ToString(), level,
			versionSource: new(gameStructure.UnityPlayerPath, gameStructure.GameDataPath), cancellationToken: cancellationToken).ConfigureAwait(false);
		if (WorkerRun.Status == "Cancelled") throw new OperationCanceledException($"IL2CPP analysis cancelled. Evidence: {WorkerRun.JobDirectory}", cancellationToken);
		if (!WorkerRun.Completed) throw new Il2CppWorkerException(WorkerRun);
		try
		{
			// Stage every managed image before making any of them visible through this manager.
			List<AssemblyDefinition> staged = [];
			foreach (var file in WorkerRun.Response!.Assemblies)
			{
				string path = Il2CppWorkerFiles.ResolveOutput(WorkerRun.JobDirectory, file.Path);
				byte[] bytes = File.ReadAllBytes(path);
				if (bytes.LongLength != file.Length || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != file.Sha256)
					throw new InvalidDataException("Worker assembly changed before managed metadata loading.");
				AssemblyDefinition assembly = AssemblyDefinition.FromBytes(bytes, createRuntimeContext: false);
				if (assembly.Name?.ToString() != file.Name) throw new InvalidDataException("Worker assembly identity differs from its manifest.");
				staged.Add(assembly);
			}
			Il2CppRecoveryReport report = WorkerRun.Response.Recovery!;
			if (report.WithMethodBodies(staged).MethodBodies != report.MethodBodies) throw new InvalidDataException("Worker method-body counts differ from its managed output.");
			AssemblyDefinition corlib = staged.Single(a => a.Name == "mscorlib");
			RuntimeContext context = new(DotNetRuntimeInfo.NetCoreApp(10, 0), (bool?)null, corlib);
			foreach (AssemblyDefinition assembly in staged) if (!ReferenceEquals(assembly, corlib)) context.AddAssembly(assembly);
			foreach (AssemblyDefinition assembly in staged) Add(assembly);
			recovery = report with { Isolation = new(WorkerRun.Response.JobId, WorkerRun.Response.ProcessId, "Process", "Passed") };
		}
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			WorkerRun = WorkerRun with { Status = "InvalidAssembly", Error = e.GetType().Name + ": " + e.Message };
			Il2CppWorkerProtocol.WriteRun(Path.Combine(WorkerRun.JobDirectory, "manager.json"), WorkerRun);
			throw new Il2CppWorkerException(WorkerRun);
		}
	}
}

public sealed class Il2CppWorkerException(Il2CppWorkerRun run) : IOException($"IL2CPP worker {run.Status}. {run.Error} Evidence: {run.JobDirectory}")
{
	public Il2CppWorkerRun Run { get; } = run;
}
