using AssetRipper.Export;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Scripts;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Recovery;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Workers;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using System.Diagnostics;
using System.Text.Json;

namespace AssetRipper.Tools.SystemTester;

internal static class Program
{
	private const string TestsDirectory = "../../Tests";

	private static int Main(string[] args)
	{
		Logger.Add(new ConsoleLogger(true));
		Logger.Add(new FileLogger("AssetRipper.Tools.SystemTester.log"));
		Logger.LogSystemInformation("System Tester");
		try
		{
			Options options = Options.Parse(args);
			SystemTestReport report = new();
			if (options.InputPaths.Count > 0)
			{
				report.Results.Add(RunCase("Input", options.InputPaths, options.OutputPath ?? Path.Join(AppContext.BaseDirectory, "Ripped"), options.RawOutputPath, options.Worker, options.Selection));
			}
			else
			{
				RunCorpus(options, report);
			}
			string reportPath = Path.GetFullPath(options.ReportPath);
			Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
			using (FileStream stream = File.Create(reportPath))
			{
				JsonSerializer.Serialize(stream, report, SystemTestReportJsonContext.Default.SystemTestReport);
			}
			Logger.Info(LogCategory.General, $"{report.Results.Count(r => r.Succeeded)}/{report.Results.Count} tests successfully completed. Report: {reportPath}");
			return report.Succeeded ? 0 : 1;
		}
		catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
		{
			Logger.Error(exception);
			Console.Error.WriteLine("Usage: AssetRipper.Tools.SystemTester [--tests DIR | INPUT...] [--output DIR] [--raw-output DIR] [--report FILE] [--select-class-id INT] [--select-name NAME] [--select-path-id INT64] [--select-collection NAME] [--il2cpp-worker FILE] [--il2cpp-worker-dir DIR] [--il2cpp-worker-timeout SECONDS] [--il2cpp-worker-max-heap MIB] [--il2cpp-worker-discard-inputs]");
			return 2;
		}
	}

	private static void RunCorpus(Options options, SystemTestReport report)
	{
		string root = Path.GetFullPath(options.TestsPath ?? TestsDirectory);
		if (!Directory.Exists(root))
		{
			report.Errors.Add($"Corpus directory not found: {root}");
			Logger.Error(report.Errors[^1]);
			return;
		}
		foreach (string versionPath in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
		{
			string versionName = Path.GetFileName(versionPath);
			foreach (string testPath in Directory.GetDirectories(versionPath).OrderBy(p => p, StringComparer.Ordinal))
			{
				string testName = Path.GetFileName(testPath);
				string inputPath = Path.Join(testPath, "Input");
				string outputPath = options.OutputPath is null
					? Path.Join(testPath, "Output")
					: Path.Join(options.OutputPath, versionName, testName);
				report.Results.Add(RunCase($"{versionName}/{testName}", [inputPath], outputPath, options.RawOutputPath, options.Worker, options.Selection));
			}
		}
		if (report.Results.Count == 0)
		{
			report.Errors.Add($"No test cases found in: {root}");
			Logger.Error(report.Errors[^1]);
		}
	}

	private static SystemTestResult RunCase(string name, IReadOnlyList<string> inputs, string requestedOutput, string? rawOutput, Il2CppWorkerOptions? worker, ExportSelectionSettings selection)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		CountingLogger counter = new();
		Logger.Add(counter);
		string? output = null;
		string? version = null;
		int objectCount = 0;
		int processedObjectCount = 0;
		int assemblyCount = 0;
		string? error = null;
		RawObjectStore? rawRecovery = null;
		CollectionExportReport? collectionExport = null;
		AssemblyResolutionReport? assemblyResolution = null;
		Il2CppWorkerRun? workerRun = null;
		bool succeeded = false;
		try
		{
			foreach (string input in inputs)
			{
				if (!Directory.Exists(input) && !File.Exists(input))
				{
					throw new FileNotFoundException("Test input does not exist.", input);
				}
			}
			FullConfiguration settings = new();
			settings.ImportSettings.RawRecoveryDirectory = rawOutput;
			settings.ImportSettings.Il2CppWorker = worker;
			settings.ExportSettings.Selection = selection;
			ExportHandler handler = new(settings);
			GameData data = handler.Load(inputs, LocalFileSystem.Instance);
			rawRecovery = data.RawRecovery;
			workerRun = (data.AssemblyManager as IsolatedIL2CppManager)?.WorkerRun;
			using (data.AssemblyManager)
			{
				version = data.ProjectVersion.ToString();
				objectCount = data.GameBundle.FetchAssets().Count();
				assemblyCount = data.AssemblyManager.GetAssemblies().Count();
				if (objectCount == 0)
				{
					throw new InvalidDataException("No assets were loaded from the test input.");
				}
				handler.Process(data);
				processedObjectCount = data.GameBundle.FetchAssets().Count();
				output = ExportDirectory.Create(requestedOutput);
				try
				{
					handler.Export(data, output, LocalFileSystem.Instance);
				}
				finally
				{
					collectionExport = handler.LastCollectionReport;
					assemblyResolution = AssemblyResolutionReport.Create(data.AssemblyManager, "AuxiliaryFiles/GameAssemblies");
				}
				succeeded = handler.LastExportSucceeded == true && counter.ErrorCount == 0;
				if (!succeeded)
				{
					error = "Import or export reported incomplete results; inspect the log, collection-export-report.json, assembly-resolution-report.json and script/raw reports when present.";
				}
			}
		}
		catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
		{
			if (exception is Il2CppWorkerException workerException) workerRun = workerException.Run;
			error = exception.ToString();
			Logger.Error(LogCategory.General, $"Test '{name}' failed.", exception);
		}
		finally
		{
			Logger.Remove(counter);
		}
		return new(name, inputs, output, version, objectCount, processedObjectCount, assemblyCount, stopwatch.Elapsed.TotalSeconds,
			counter.WarningCount, counter.ErrorCount, succeeded, error, rawRecovery?.GetSummary(), collectionExport, assemblyResolution, workerRun);
	}

	private sealed class CountingLogger : ILogger
	{
		public int WarningCount { get; private set; }
		public int ErrorCount { get; private set; }

		public void Log(LogType type, LogCategory category, string message)
		{
			if (type == LogType.Warning)
			{
				WarningCount++;
			}
			else if (type == LogType.Error)
			{
				ErrorCount++;
			}
		}

		public void BlankLine(int numLines) { }
	}

	private sealed class Options
	{
		public string? TestsPath { get; private set; }
		public string? OutputPath { get; private set; }
		public string? RawOutputPath { get; private set; }
		public string ReportPath { get; private set; } = "SystemTester-results.json";
		public List<string> InputPaths { get; } = [];
		public ExportSelectionSettings Selection { get; } = new();
		private string? workerPath, workerDirectory;
		private int workerTimeout = 300;
		private int? workerHeap;
		private bool discardWorkerInputs;
		public Il2CppWorkerOptions? Worker => workerPath is null ? null : new(workerPath, workerDirectory, workerTimeout, workerHeap, !discardWorkerInputs);

		public static Options Parse(string[] args)
		{
			Options options = new();
			for (int i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--tests": options.TestsPath = ReadValue(); break;
					case "--output": options.OutputPath = ReadValue(); break;
					case "--raw-output": options.RawOutputPath = ReadValue(); break;
					case "--report": options.ReportPath = ReadValue(); break;
					case "--select-class-id": options.Selection.ClassIds = [.. options.Selection.ClassIds, int.Parse(ReadValue(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture)]; break;
					case "--select-name": options.Selection.Names = [.. options.Selection.Names, ReadValue()]; break;
					case "--select-path-id": options.Selection.PathIds = [.. options.Selection.PathIds, long.Parse(ReadValue(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture)]; break;
					case "--select-collection": options.Selection.Collections = [.. options.Selection.Collections, ReadValue()]; break;
					case "--il2cpp-worker": options.workerPath = Path.GetFullPath(ReadValue()); break;
					case "--il2cpp-worker-dir": options.workerDirectory = Path.GetFullPath(ReadValue()); break;
					case "--il2cpp-worker-timeout":
						if (!int.TryParse(ReadValue(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out options.workerTimeout)
							|| options.workerTimeout is < 1 or > 86400) throw new ArgumentException("Worker timeout must be 1–86400 seconds.");
						break;
					case "--il2cpp-worker-max-heap":
						if (!int.TryParse(ReadValue(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int heap)
							|| heap is < 64 or > 1048576) throw new ArgumentException("Worker heap limit must be 64–1048576 MiB.");
						options.workerHeap = heap;
						break;
					case "--il2cpp-worker-discard-inputs": options.discardWorkerInputs = true; break;
					case "--": options.InputPaths.AddRange(args[(i + 1)..]); i = args.Length; break;
					default:
						if (args[i].StartsWith("--", StringComparison.Ordinal))
						{
							throw new ArgumentException($"Unknown option: {args[i]}");
						}
						options.InputPaths.Add(args[i]);
						break;
				}

				string ReadValue()
				{
					if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1]))
					{
						throw new ArgumentException($"Missing value for {args[i]}");
					}
					return args[++i];
				}
			}
			if (options.TestsPath is not null && options.InputPaths.Count > 0)
			{
				throw new ArgumentException("--tests cannot be combined with input paths.");
			}
			if (options.workerPath is null && (options.workerDirectory is not null || options.workerTimeout != 300 || options.workerHeap is not null || options.discardWorkerInputs))
				throw new ArgumentException("Worker directory, timeout, heap and input options require --il2cpp-worker.");
			return options;
		}
	}
}
