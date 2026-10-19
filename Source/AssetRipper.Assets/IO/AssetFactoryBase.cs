using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.SerializedFiles.Parser;

namespace AssetRipper.Assets.IO;

public abstract class AssetFactoryBase
{
	public IRawObjectSink? RawObjectSink { get; init; }

	public abstract IUnityObjectBase? ReadAsset(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType, IReadOnlyList<SerializedTypeReference> referenceTypes);

	public virtual IUnityObjectBase? ReadAsset(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType, IReadOnlyList<SerializedTypeReference> referenceTypes, IRawObjectSnapshot? snapshot)
	{
		IUnityObjectBase? asset = ReadAsset(assetInfo, assetData, assetType, referenceTypes);
		snapshot?.ReportReadStatus(asset is null ? RawObjectReadStatus.FactoryReturnedNull : RawObjectReadStatus.Parsed);
		return asset;
	}
}
