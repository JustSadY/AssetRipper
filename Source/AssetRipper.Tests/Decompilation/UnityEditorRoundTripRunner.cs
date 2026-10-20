using System.Diagnostics;
using System.Text.Json;

namespace AssetRipper.Tests.Decompilation;

internal static class UnityEditorRoundTripRunner
{
	internal static async Task<(int ExitCode, JsonDocument Report)> Run(string executable, string project, string output, string validator)
	{
		var run = await RunProcess(executable, project, output, validator);
		Assert.That(run.CompilerErrors, Is.Empty, string.Join('\n', run.CompilerErrors));
		string reportPath = Path.Combine(output, "editor-import-report.json");
		Assert.That(File.Exists(reportPath), Is.True, $"Editor validation report missing. Exit {run.ExitCode}. Logs: {run.Logs}. Export: {output}");
		TestContext.AddTestAttachment(reportPath, "Editor reimport of actual AssetRipper output; source-owned fixture");
		return (run.ExitCode, JsonDocument.Parse(File.ReadAllText(reportPath)));
	}

	internal static async Task ExpectCompilerFailure(string executable, string project, string output, string validator, string sourceFile)
	{
		var run = await RunProcess(executable, project, output, validator);
		Assert.That(run.ExitCode, Is.Not.Zero, "The deliberately broken assembly graph must fail compilation.");
		Assert.That(run.CompilerErrors.Any(e => e.Contains("error CS", StringComparison.Ordinal) && e.Contains(sourceFile, StringComparison.Ordinal)), Is.True,
			string.Join('\n', run.CompilerErrors));
		Assert.That(File.Exists(Path.Combine(output, "editor-import-report.json")), Is.False, "A compiler failure must not masquerade as an executed semantic validator.");
		File.WriteAllText(Path.Combine(output, "expected-compiler-failure.json"), JsonSerializer.Serialize(new
		{
			Validation = "CompilationRejected", SemanticValidation = "NotRun", run.ExitCode, run.CompilerErrors, SourceFile = sourceFile,
		}, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static async Task<(int ExitCode, string[] CompilerErrors, string Logs)> RunProcess(string executable, string project, string output, string validator)
	{
		string reportPath = Path.Combine(output, "editor-import-report.json");
		string logs = Path.Combine(Path.GetTempPath(), "assetripper-editor-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(logs);
		ProcessStartInfo start = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (string argument in new[] { "-batchmode", "-nographics", "-projectPath", project, "-executeMethod", validator + ".Validate", "-quit", "-logFile", Path.Combine(logs, "editor.log") }) start.ArgumentList.Add(argument);
		start.Environment["ASSETRIPPER_ROUNDTRIP_ORACLE"] = Path.Combine(output, "oracle.json");
		start.Environment["ASSETRIPPER_ROUNDTRIP_REPORT"] = reportPath;
		using Process process = Process.Start(start)!;
		Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
		try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
		catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
		File.WriteAllText(Path.Combine(logs, "stdout.log"), await stdout); File.WriteAllText(Path.Combine(logs, "stderr.log"), await stderr);
		string[] compilerErrors = File.Exists(Path.Combine(logs, "editor.log"))
			? File.ReadLines(Path.Combine(logs, "editor.log")).Where(line => line.Contains("error CS", StringComparison.Ordinal) || line.Contains("Scripts have compiler errors", StringComparison.Ordinal)).ToArray() : [];
		File.WriteAllText(Path.Combine(output, "editor-run.json"), JsonSerializer.Serialize(new { process.ExitCode, CompilerErrors = compilerErrors, FullLogs = logs }, new JsonSerializerOptions { WriteIndented = true }));
		return (process.ExitCode, compilerErrors, logs);
	}

	internal static object[] Snapshot(string project, string output)
	{
		string evidence = Path.Combine(output, "Evidence");
		foreach (string directory in new[] { "Assets", "ProjectSettings", "Packages" })
		{
			foreach (string file in Directory.EnumerateFiles(Path.Combine(project, directory), "*", SearchOption.AllDirectories))
			{
				string target = Path.Combine(evidence, "BeforeImport", Path.GetRelativePath(project, file));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
			}
		}
		return Directory.EnumerateFiles(Path.Combine(evidence, "BeforeImport"), "*", SearchOption.AllDirectories)
			.Order(StringComparer.Ordinal).Select(p => (object)new { Path = Path.GetRelativePath(evidence, p), Sha256 = FixtureCompiler.Hash(File.ReadAllBytes(p)) }).ToArray();
	}

	internal static void RemoveImportCache(string project)
	{
		string library = Path.Combine(project, "Library");
		if (Directory.Exists(library)) Directory.Delete(library, recursive: true);
	}
}
