using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.Processing.Scenes;
using AssetRipper.SourceGenerated.Classes.ClassID_142;
using AssetRipper.SourceGenerated.Classes.ClassID_157;

namespace AssetRipper.Tests;

[Category("UnityScene")]
public class SceneNamingTests
{
	private static readonly UnityVersion Version = UnityVersion.Parse("6000.4.0f1");
	[TestCase("BuildPlayer-MixedCase", "MixedCase")]
	[TestCase("BuildPlayer-World.Forest", "World.Forest")]
	[TestCase("level0", "level0")]
	[TestCase("BuildPlayer-", "buildplayer-")]
	[TestCase("buildplayer-MixedCase", "buildplayer-mixedcase")]
	[TestCase("BuildPlayer-../Other", "buildplayer-../other")]
	public void SceneEntrySpellingIsKeptWithoutChangingDependencyIdentity(string original, string expected)
	{
		GameBundle bundle = MakeBundle(original, false);
		AssetCollection source = bundle.FetchAssetCollections().Single();
		Assert.That(source.Name, Is.EqualTo(SpecialFileNames.FixFileIdentifier(original)));
		Assert.That(source.OriginalName, Is.EqualTo(original));
		Assert.That(bundle.ResolveCollection(source.Name), Is.SameAs(source));
		using BaseManager manager = new(_ => { });
		new SceneDefinitionProcessor().Process(new GameData(bundle, Version, manager, null));
		Assert.That(source.Scene?.Name, Is.EqualTo(expected));
		Assert.That(source.Name, Is.EqualTo(SpecialFileNames.FixFileIdentifier(original)));
	}
	[Test]
	public void ExplicitAssetBundleScenePathTakesPrecedenceOverEntryName()
	{
		GameBundle bundle = MakeBundle("BuildPlayer-MixedCase", true); var source = bundle.FetchAssetCollections().Single();
		using BaseManager manager = new(_ => { }); new SceneDefinitionProcessor().Process(new GameData(bundle, Version, manager, null));
		Assert.That(source.Scene?.Path, Is.EqualTo("Assets/Original/MetadataName"));
		Assert.That(source.Scene?.Name, Is.EqualTo("MetadataName"));
	}
	[Test]
	public void GeneratedCollectionHasNoInventedSourceName()
	{
		GameBundle bundle = new(); var collection = bundle.AddNewProcessedCollection("BuildPlayer-Generated", Version);
		collection.CreateAsset(157, LightmapSettings.Create);
		using BaseManager manager = new(_ => { }); new SceneDefinitionProcessor().Process(new GameData(bundle, Version, manager, null));
		Assert.That(collection.OriginalName, Is.Empty); Assert.That(collection.Scene?.Name, Is.EqualTo("BuildPlayer-Generated"));
	}
	private static GameBundle MakeBundle(string name, bool metadata)
	{
		SerializedFileBuilder builder = new() { Generation = FormatVersion.LargeFilesSupport, Version = Version, Platform = BuildTarget.StandaloneLinux64 };
		foreach (int id in metadata ? new[] { 157, 142 } : new[] { 157 })
		{
			SerializedType type = new() { TypeID = id, ScriptTypeIndex = -1 }; int index = builder.Types.Count; builder.Types.Add(type);
			builder.Objects.Add(new ObjectInfo(type) { FileID = index + 1, SerializedTypeIndex = index, ObjectData = [] });
		}
		var file = builder.Build(); file.Name = name;
		FileStreamBundleFile container = new() { Name = "scenes.bundle" }; container.AddSerializedFile(file);
		GameBundle bundle = new(); bundle.AddBundle(SerializedBundle.FromFileContainer(container, new SceneFactory(name))); return bundle;
	}
	private sealed class SceneFactory(string original) : AssetFactoryBase
	{
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> bytes, SerializedType? type, IReadOnlyList<SerializedTypeReference> refs)
		{
			if (info.ClassID == 157) return LightmapSettings.Create(info);
			IAssetBundle bundle = AssetBundle.Create(info); bundle.IsStreamedSceneAssetBundle = true;
			bundle.SceneHashes!.Add("Assets/Original/MetadataName.unity", original); return bundle;
		}
	}
}
