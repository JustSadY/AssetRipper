using AssetRipper.Import.Configuration;
using System.Diagnostics;
using System.Text;

namespace AssetRipper.Import.Structure.Assembly.Workers;

/// <summary>One fresh process per analysis. This is crash isolation, not an OS security sandbox.</summary>
public static class Il2CppWorkerClient
{
	public static async Task<Il2CppWorkerRun> AnalyzeAsync(Il2CppWorkerOptions options, string nativePath, string metadataPath,
		string? unityVersion, ScriptContentLevel level, Il2CppCandidateSelection? candidates = null, Il2CppVersionSource? versionSource = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (!Path.IsPathFullyQualified(options.ExecutablePath) || options.TimeoutSeconds is < 1 or > 86400)
			throw new ArgumentException("Worker requires an absolute executable path and timeout of 1–86400 seconds.");
		if (options.MaxHeapMegabytes is < 64 or > 1048576)
			throw new ArgumentException("Worker heap limit must be 64–1048576 MiB.");
		string root = Path.GetFullPath(options.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "AssetRipper", "Il2CppJobs"));
		string id = Guid.NewGuid().ToString("N"), job = Path.Combine(root, id); Directory.CreateDirectory(job);
		Stopwatch elapsed = Stopwatch.StartNew(); int? pid = null, exitCode = null; string status = "Preparing"; string? error = null;
		Il2CppWorkerResponse? response = null; Capture stdout = new(), stderr = new();
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			Directory.CreateDirectory(Path.Combine(job, "Input"));
			var native = await Snapshot(nativePath, Path.Combine(job, "Input", "native.bin"), cancellationToken).ConfigureAwait(false);
			var metadata = await Snapshot(metadataPath, Path.Combine(job, "Input", "global-metadata.dat"), cancellationToken).ConfigureAwait(false);
			List<Il2CppWorkerArtifact> versionFiles = [];
			if (unityVersion is null)
			{
				string? versionFile = null; string? targetName = null;
				foreach (string name in new[] { "globalgamemanagers", "data.unity3d" })
				{
					string? path = versionSource?.GameDataPath is { } data ? Path.Combine(data, name) : null;
					if (File.Exists(path)) { versionFile = path; targetName = name; break; }
				}
				if (versionFile is null && File.Exists(versionSource?.UnityPlayerPath)) { versionFile = versionSource.UnityPlayerPath; targetName = "UnityPlayer.bin"; }
				if (versionFile is null) throw new InvalidDataException("No explicit Unity version or version-evidence file is available.");
				Directory.CreateDirectory(Path.Combine(job, "Input", "Version"));
				versionFiles.Add(await Snapshot(versionFile, Path.Combine(job, "Input", "Version", targetName!), cancellationToken).ConfigureAwait(false));
			}
			var request = new Il2CppWorkerRequest(Il2CppWorkerProtocol.Version, id, unityVersion, level, native, metadata, candidates) { VersionFiles = versionFiles.ToArray() };
			string requestPath = Path.Combine(job, Il2CppWorkerProtocol.RequestFile); Il2CppWorkerProtocol.WriteRequest(requestPath, request);
			bool managed = options.ExecutablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
			ProcessStartInfo start = new(managed ? "dotnet" : options.ExecutablePath) { UseShellExecute = false, WorkingDirectory = job, RedirectStandardOutput = true, RedirectStandardError = true };
			if (managed) start.ArgumentList.Add(options.ExecutablePath);
			// Hexadecimal bytes, as the .NET GC reads it. A native worker ignores the variable.
			if (options.MaxHeapMegabytes is { } heap) start.Environment["DOTNET_GCHeapHardLimit"] = "0x" + (heap * 1024L * 1024L).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
			start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
			using Process process = new() { StartInfo = start };
			status = "StartFailed";
			if (!process.Start()) throw new IOException("Worker process could not be started.");
			pid = process.Id;
			Task outTask = stdout.Drain(process.StandardOutput), errTask = stderr.Drain(process.StandardError);
			using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(options.TimeoutSeconds));
			using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
			try
			{
				await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
				status = process.ExitCode == 0 ? "ValidatingResponse" : "ExitedWithError";
			}
			catch (OperationCanceledException)
			{
				status = cancellationToken.IsCancellationRequested ? "Cancelled" : "TimedOut";
				try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
				await process.WaitForExitAsync().ConfigureAwait(false);
			}
			catch
			{
				try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
				await process.WaitForExitAsync().ConfigureAwait(false); throw;
			}
			exitCode = process.ExitCode;
			// Bound pipe draining as well: a surviving child holding inherited pipes must not hang the parent.
			try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
			catch (TimeoutException) { stdout.Truncated = stderr.Truncated = true; process.StandardOutput.Dispose(); process.StandardError.Dispose(); }
			if (status == "ValidatingResponse")
			{
				status = "InvalidResponse";
				response = Il2CppWorkerProtocol.ReadResponse(Path.Combine(job, Il2CppWorkerProtocol.ResponseFile));
				Il2CppWorkerFiles.ValidateResponse(request, response, job, process.Id);
				status = "Completed";
			}
			else
			{
				error = $"IL2CPP worker {status}; exit code {exitCode}.";
				if (status == "ExitedWithError" && File.Exists(Path.Combine(job, Il2CppWorkerProtocol.ResponseFile)))
				{
					response = Il2CppWorkerProtocol.ReadResponse(Path.Combine(job, Il2CppWorkerProtocol.ResponseFile));
					if (response.JobId != request.JobId || response.ProcessId != process.Id || response.Native != request.Native || response.Metadata != request.Metadata)
					{ status = "InvalidResponse"; throw new InvalidDataException("Failure response does not match this worker job."); }
					error += " " + response.Status + ": " + response.Error;
				}
			}
		}
		catch (OperationCanceledException) { status = "Cancelled"; error = "IL2CPP worker job was cancelled."; }
		catch (Exception e) when (e is not OutOfMemoryException) { error = e.GetType().Name + ": " + e.Message; if (status == "Preparing") status = "InputFailed"; }
		File.WriteAllText(Path.Combine(job, "stdout.log"), stdout.Value); File.WriteAllText(Path.Combine(job, "stderr.log"), stderr.Value);
		bool retained = true;
		if (status == "Completed" && !options.RetainInputSnapshots)
		{
			// The response was validated against these copies above; failed jobs always keep them.
			try { Directory.Delete(Path.Combine(job, "Input"), recursive: true); retained = false; }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
		var result = new Il2CppWorkerRun(job, status, pid, exitCode, elapsed.Elapsed.TotalSeconds, error, stdout.Truncated, stderr.Truncated, response)
		{ InputSnapshotsRetained = retained };
		Il2CppWorkerProtocol.WriteRun(Path.Combine(job, "run.json"), result);
		return result;
	}
	private static async Task<Il2CppWorkerArtifact> Snapshot(string source, string target, CancellationToken token)
	{
		await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
		await using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
			await input.CopyToAsync(output, token).ConfigureAwait(false);
		return Il2CppWorkerFiles.Identify(target);
	}
	private sealed class Capture
	{
		private readonly StringBuilder text = new();
		private readonly object sync = new();
		private bool truncated;
		public bool Truncated { get { lock (sync) return truncated; } set { lock (sync) truncated = value; } }
		public string Value { get { lock (sync) return text.ToString(); } }
		public async Task Drain(StreamReader reader)
		{
			char[] buffer = new char[4096]; int count;
			try
			{
				while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
				{
					lock (sync)
					{
						int take = Math.Min(count, 64 * 1024 - text.Length);
						text.Append(buffer, 0, take); if (take < count) truncated = true;
					}
				}
			}
			catch (ObjectDisposedException) { }
			catch (IOException) { }
		}
	}
}
