using AssetRipper.Export;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Collections;
using AssetRipper.Export.Scripts;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Recovery;
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
				report.Results.Add(RunCase("Input", options.InputPaths, options.OutputPath ?? Path.Join(AppContext.BaseDirectory, "Ripped"), options.RawOutputPath));
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
			Console.Error.WriteLine("Usage: AssetRipper.Tools.SystemTester [--tests DIR | INPUT...] [--output DIR] [--raw-output DIR] [--report FILE]");
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
				report.Results.Add(RunCase($"{versionName}/{testName}", [inputPath], outputPath, options.RawOutputPath));
			}
		}
		if (report.Results.Count == 0)
		{
			report.Errors.Add($"No test cases found in: {root}");
			Logger.Error(report.Errors[^1]);
		}
	}

	private static SystemTestResult RunCase(string name, IReadOnlyList<string> inputs, string requestedOutput, string? rawOutput)
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
			ExportHandler handler = new(settings);
			GameData data = handler.Load(inputs, LocalFileSystem.Instance);
			rawRecovery = data.RawRecovery;
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
			error = exception.ToString();
			Logger.Error(LogCategory.General, $"Test '{name}' failed.", exception);
		}
		finally
		{
			Logger.Remove(counter);
		}
		return new(name, inputs, output, version, objectCount, processedObjectCount, assemblyCount, stopwatch.Elapsed.TotalSeconds,
			counter.WarningCount, counter.ErrorCount, succeeded, error, rawRecovery?.GetSummary(), collectionExport, assemblyResolution);
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
			return options;
		}
	}
}
