using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_27;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using System.Text.Json;

namespace AssetRipper.Export.UnityProjects.PathIdMapping;

public sealed class PathIdMapExporter(IReadOnlySet<IUnityObjectBase>? selectedAssets = null) : IPostExporter
{
	public void DoPostExport(GameData gameData, FullConfiguration settings, FileSystem fileSystem)
	{
		SerializedGameInfo gameInfo = new();
		foreach (SerializedAssetCollection collection in gameData.GameBundle.FetchAssetCollections().OfType<SerializedAssetCollection>())
		{
			SerializedFileInfo fileInfo = new()
			{
				Name = collection.Name,
			};
			foreach (IUnityObjectBase asset in collection)
			{
				if (selectedAssets is not null && !selectedAssets.Contains(asset)) continue;
				if (asset is IMesh or ITexture or IAudioClip or ITextAsset)//Commonly useful asset types
				{
					fileInfo.Assets.Add(new()
					{
						Name = (asset as INamed)?.Name,
						Type = asset.ClassName,
						PathID = asset.PathID,
					});
				}
			}
			if (selectedAssets is null || fileInfo.Assets.Count > 0) gameInfo.Files.Add(fileInfo);
		}

		string outputDirectory = settings.AuxiliaryFilesPath;
		fileSystem.Directory.Create(outputDirectory);
		using Stream stream = fileSystem.File.Create(fileSystem.Path.Join(outputDirectory, "path_id_map.json"));
		JsonSerializer.Serialize(stream, gameInfo, SerializedGameInfoSerializerContext.Default.SerializedGameInfo);
		stream.Flush();
	}
}
