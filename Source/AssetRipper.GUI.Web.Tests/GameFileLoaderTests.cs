using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using System.Text.Json;

namespace AssetRipper.GUI.Web.Tests;

[NonParallelizable]
public class GameFileLoaderTests
{
	[TestCase(false, false)]
	[TestCase(false, true)]
	[TestCase(true, false)]
	[TestCase(true, true)]
	public async Task ExistingFilesSurviveBothExportModes(bool headless, bool primaryContent)
	{
		string root = Directory.CreateTempSubdirectory("AssetRipper-LoaderTests-").FullName;
		bool previousHeadless = GameFileLoader.Headless;
		string previousOutput = GameFileLoader.Settings.ExportRootPath;
		string? previousRecovery = GameFileLoader.Settings.ImportSettings.RawRecoveryDirectory;
		try
		{
			string input = Path.Join(root, "sample.assets");
			CreateInput(input);
			string output = Path.Join(root, "Output");
			Directory.CreateDirectory(output);
			string existing = Path.Join(output, "keep.txt");
			File.WriteAllText(existing, "existing user content");
			GameFileLoader.Headless = headless;
			GameFileLoader.Settings.ImportSettings.RawRecoveryDirectory = Path.Join(root, "Recovery");
			GameFileLoader.LoadAndProcess([input]);
			if (primaryContent)
			{
				await GameFileLoader.ExportPrimaryContent(output);
			}
			else
			{
				await GameFileLoader.ExportUnityProject(output);
			}
			Assert.That(File.ReadAllText(existing), Is.EqualTo("existing user content"));
			string actualOutput = GameFileLoader.Settings.ExportRootPath;
			Assert.That(Path.GetDirectoryName(actualOutput), Is.EqualTo(output));
			Assert.That(Directory.EnumerateFiles(actualOutput, "*", SearchOption.AllDirectories).Count(), Is.GreaterThan(1));
			using JsonDocument recovery = JsonDocument.Parse(File.ReadAllText(Path.Join(actualOutput, "raw-recovery.json")));
			Assert.That(recovery.RootElement.GetProperty("PreservedObjectCount").GetInt64(), Is.EqualTo(1));
			Assert.That(recovery.RootElement.GetProperty("HasIncompleteObjects").GetBoolean(), Is.False);
			Assert.That(File.Exists(recovery.RootElement.GetProperty("JournalPath").GetString()), Is.True);
		}
		finally
		{
			GameFileLoader.Reset();
			GameFileLoader.Headless = previousHeadless;
			GameFileLoader.Settings.ExportRootPath = previousOutput;
			GameFileLoader.Settings.ImportSettings.RawRecoveryDirectory = previousRecovery;
			Directory.Delete(root, true);
		}
	}

	private static void CreateInput(string path)
	{
		GameBundle bundle = new();
		UnityVersion version = UnityVersion.Parse("2022.3.0f1");
		ProcessedAssetCollection collection = bundle.AddNewProcessedCollection("Fixture", version);
		ITextAsset asset = collection.CreateAsset(49, info => TextAsset.Create(info));
		asset.Name = "CorpusSample";
		asset.Script_C49 = "AssetRipper regression fixture";
		using MemoryStream payload = new();
		using (AssetWriter writer = new(payload, collection))
		{
			asset.WriteRelease(writer);
		}
		SerializedType type = new() { TypeID = 49, ScriptTypeIndex = -1 };
		SerializedFileBuilder builder = new()
		{
			Generation = FormatVersion.LargeFilesSupport,
			Version = version,
			Platform = BuildTarget.StandaloneWin64Player,
			EndianType = EndianType.LittleEndian,
			HasTypeTree = false,
		};
		builder.Types.Add(type);
		builder.Objects.Add(new ObjectInfo(type) { FileID = 1, SerializedTypeIndex = 0, ObjectData = payload.ToArray() });
		using FileStream stream = File.Create(path);
		builder.Build().Write(stream);
	}
}
