using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using System.Runtime.ExceptionServices;

namespace AssetRipper.Assets.Collections;

/// <summary>
/// A collection of assets read from a <see cref="SerializedFile"/>.
/// </summary>
public sealed class SerializedAssetCollection : AssetCollection
{
	private FileIdentifier[]? DependencyIdentifiers { get; set; }

	private SerializedAssetCollection(Bundle bundle) : base(bundle)
	{
	}

	internal void InitializeDependencyList(IDependencyProvider? dependencyProvider)
	{
		if (Dependencies.Count > 1)
		{
			throw new Exception("Dependency list has already been initialized.");
		}
		if (DependencyIdentifiers is not null)
		{
			for (int i = 0; i < DependencyIdentifiers.Length; i++)
			{
				FileIdentifier identifier = DependencyIdentifiers[i];
				AssetCollection? dependency = Bundle.ResolveCollection(identifier);
				if (dependency is null)
				{
					dependencyProvider?.ReportMissingDependency(identifier);
				}
				SetDependency(i + 1, dependency);
			}
			DependencyIdentifiers = null;
		}
	}

	/// <summary>
	/// Creates a <see cref="SerializedAssetCollection"/> from a <see cref="SerializedFile"/>.
	/// </summary>
	/// <remarks>
	/// The new <see cref="SerializedAssetCollection"/> is automatically added to the <paramref name="bundle"/>.
	/// </remarks>
	/// <param name="bundle">The <see cref="Bundle"/> to add this collection to.</param>
	/// <param name="file">The <see cref="SerializedFile"/> from which to make this collection.</param>
	/// <param name="factory">A factory for creating assets.</param>
	/// <param name="defaultVersion">The default version to use if the file does not have a version, ie the version has been stripped.</param>
	/// <returns>The new collection.</returns>
	internal static SerializedAssetCollection FromSerializedFile(Bundle bundle, SerializedFile file, AssetFactoryBase factory, UnityVersion defaultVersion = default)
	{
		UnityVersion version = file.Version.Equals(0, 0, 0) ? defaultVersion : file.Version;
		SerializedAssetCollection collection = new SerializedAssetCollection(bundle)
		{
			Name = file.NameFixed,
			OriginalName = file.Name,
			FilePath = file.FilePath,
			Version = version,
			OriginalVersion = version,
			Platform = file.Platform,
			Flags = file.Flags,
			EndianType = file.EndianType,
		};
		try
		{
			ReadOnlySpan<FileIdentifier> fileDependencies = file.Dependencies;
			if (fileDependencies.Length > 0)
			{
				collection.DependencyIdentifiers = fileDependencies.ToArray();
			}
			ReadData(collection, file, factory);
		}
		catch
		{
			// Dependency binding has not started. Do not expose a half-populated collection.
			bundle.RemoveFailedCollection(collection);
			throw;
		}
		return collection;
	}

	private static void ReadData(SerializedAssetCollection collection, SerializedFile file, AssetFactoryBase factory)
	{
		SerializedTypeReference[] referenceTypes = file.RefTypes.ToArray();
		IRawObjectCollection? rawCollection = factory.RawObjectSink?.BeginCollection(new(
			file.FilePath, file.Name, file.NameFixed, collection.Bundle.Name, collection.OriginalVersion.ToString(),
			(int)file.Generation, (int)file.Platform, file.EndianType.ToString(), file.HasTypeTree));
		Exception? readFailure = null;
		foreach (ObjectInfo objectInfo in file.Objects)
		{
			int classID = objectInfo.TypeID < 0 ? 114 : objectInfo.TypeID;
			AssetInfo assetInfo = new AssetInfo(collection, objectInfo.FileID, classID);
			IRawObjectSnapshot? snapshot = rawCollection?.Preserve(new(
				objectInfo.FileID, classID, objectInfo.TypeID, objectInfo.SerializedTypeIndex, objectInfo.ScriptTypeIndex,
				objectInfo.ByteOffset, objectInfo.DeclaredByteSize), objectInfo.ObjectData);
			if (readFailure is not null)
			{
				snapshot?.ReportReadStatus(RawObjectReadStatus.CollectionAborted, "Factory parsing stopped after an earlier object failed in this collection.");
				continue;
			}
			try
			{
				IUnityObjectBase? asset = factory.ReadAsset(assetInfo, objectInfo.ObjectData, objectInfo.Type, referenceTypes, snapshot);
				if (asset is not null)
				{
					collection.AddAsset(asset);
				}
			}
			catch (Exception exception) when (ExceptionPolicy.IsRecoverableInput(exception))
			{
				snapshot?.ReportReadStatus(RawObjectReadStatus.FactoryException, exception.ToString());
				readFailure = exception;
			}
		}
		if (readFailure is not null)
		{
			ExceptionDispatchInfo.Capture(readFailure).Throw();
		}
	}
}
