using AssetRipper.Assets.Bundles;
using AssetRipper.Export;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

namespace AssetRipper.GUI.Web;

public static class GameFileLoader
{
	private static GameData? GameData { get; set; }
	[MemberNotNullWhen(true, nameof(GameData))]
	public static bool IsLoaded => GameData is not null;
	public static GameBundle GameBundle => GameData!.GameBundle;
	public static IAssemblyManager AssemblyManager => GameData!.AssemblyManager;
	public static FullConfiguration Settings { get; } = LoadSettings();
	public static bool Headless { get; set; }

	public static ExportHandler ExportHandler
	{
		private get;
		set
		{
			ArgumentNullException.ThrowIfNull(value);
			value.ThrowIfSettingsDontMatch(Settings);
			field = value;
		}
	} = new(Settings);

	/// <summary>
	/// Is this the premium edition?
	/// </summary>
	/// <remarks>
	/// This is purely for UI functionality and has no direct effect on the presense of features.
	/// </remarks>
	public static bool Premium => ExportHandler.GetType() != typeof(ExportHandler);

	public static void Reset()
	{
		if (GameData is not null)
		{
			GameData = null;
			GC.Collect();
			Logger.Info(LogCategory.General, "Data was reset.");
		}
	}

	public static void LoadAndProcess(IReadOnlyList<string> paths)
	{
		Reset();
		Settings.LogConfigurationValues();
		GameData = ExportHandler.LoadAndProcess(paths, LocalFileSystem.Instance);
	}

	public static Task ExportUnityProject(string path)
	{
		if (IsLoaded && IsValidExportDirectory(path))
		{
			path = ExportDirectory.Create(path);
			ExportHandler.Export(GameData, path, LocalFileSystem.Instance);
		}
		return Task.CompletedTask;
	}

	public static Task ExportPrimaryContent(string path)
	{
		if (IsLoaded && IsValidExportDirectory(path))
		{
			path = ExportDirectory.Create(path);
			Logger.Info(LogCategory.Export, "Starting primary content export");
			Logger.Info(LogCategory.Export, $"Attempting to export assets to {path}...");
			Settings.ExportRootPath = path;
			PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(GameData, Settings);
			exporter.Export(GameBundle, Settings, LocalFileSystem.Instance);
			if (exporter.LastExportSucceeded == true)
			{
				Logger.Info(LogCategory.Export, "Finished exporting primary content.");
			}
			else
			{
				Logger.Warning(LogCategory.Export, "Finished exporting primary content with errors.");
			}
		}
		return Task.CompletedTask;
	}

	private static FullConfiguration LoadSettings()
	{
		FullConfiguration settings = new();
		settings.LoadFromDefaultPath();
		return settings;
	}

	private static bool IsValidExportDirectory(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			Logger.Error(LogCategory.Export, "Export path is empty");
			return false;
		}
		string directoryName = Path.GetFileName(path);
		if (directoryName is "Desktop" or "Documents" or "Downloads")
		{
			Logger.Error(LogCategory.Export, $"Export path '{path}' is a system directory");
			return false;
		}
		return true;
	}
}
