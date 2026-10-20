using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.SourceGenerated.Classes.ClassID_114;

namespace AssetRipper.Import.Structure.Assembly.Serializable;

/// <summary>
/// This is a placeholder asset that lazily reads the actual structure sometime after all the assets have been loaded.
/// This allows MonoBehaviours to be loaded before their referenced MonoScript.
/// </summary>
public sealed class UnloadedStructure : UnityAssetBase, IDeepCloneable
{
	private sealed class StatelessAsset : UnityAssetBase, IDeepCloneable
	{
		public static StatelessAsset Instance { get; } = new();
		private StatelessAsset()
		{
		}

		public override void Reset()
		{
		}

		public override bool? AddToEqualityComparer(IUnityAssetBase other, AssetEqualityComparer comparer)
		{
			return other is StatelessAsset;
		}

		IUnityAssetBase IDeepCloneable.DeepClone(PPtrConverter converter) => this;
	}

	/// <summary>
	/// The <see cref="IMonoBehaviour"/> that <see langword="this"/> is the <see cref="IMonoBehaviour.Structure"/> for.
	/// </summary>
	public IMonoBehaviour MonoBehaviour { get; }

	public IAssemblyManager AssemblyManager { get; }

	/// <summary>
	/// The segment of data for this structure.
	/// </summary>
	public ReadOnlyArraySegment<byte> StructureData { get; }
	public int? ObjectByteOffset { get; }
	private readonly IRawObjectSnapshot? rawSnapshot;

	public UnloadedStructure(IMonoBehaviour monoBehaviour, IAssemblyManager assemblyManager, ReadOnlyArraySegment<byte> structureData,
		IRawObjectSnapshot? rawSnapshot = null, int? objectByteOffset = null)
	{
		MonoBehaviour = monoBehaviour;
		AssemblyManager = assemblyManager;
		StructureData = structureData;
		this.rawSnapshot = rawSnapshot;
		ObjectByteOffset = objectByteOffset;
	}

	internal UnloadedStructure Rebind(IMonoBehaviour target) => new(target, AssemblyManager, StructureData, rawSnapshot, ObjectByteOffset);

	private void ThrowIfNotStructure()
	{
		if (!ReferenceEquals(MonoBehaviour.Structure, this))
		{
			throw new InvalidOperationException("The MonoBehaviour structure has already been loaded.");
		}
	}

	public SerializableStructure? LoadStructure()
	{
		ThrowIfNotStructure();
		string? failureReason = null;
		SerializableStructureReadResult? result = null;
		try
		{
			SerializableStructure? structure = MonoBehaviour.ScriptP?.GetBehaviourType(AssemblyManager, out failureReason)?.CreateSerializableStructure();
			if (structure is not null)
			{
				EndianSpanReader reader = new EndianSpanReader(StructureData, MonoBehaviour.Collection.EndianType);
				if (structure.TryRead(ref reader, MonoBehaviour, AssemblyManager, out result))
				{
					MonoBehaviour.Structure = structure;
				}
			}
		}
		catch (Exception exception) when (ExceptionPolicy.IsRecoverableInput(exception))
		{
			failureReason = exception.ToString();
		}
		// Journal IO is outside the recoverable source-read boundary.
		if (MonoBehaviour.Structure is SerializableStructure loaded)
		{
			rawSnapshot?.ReportReadStatus(RawObjectReadStatus.Parsed);
			return loaded;
		}
		if (failureReason is not null)
		{
			Logger.Warning(LogCategory.Import, $"Could not read MonoBehaviour structure for `{MonoBehaviour.ScriptP?.GetFullName()}`. Reason: {failureReason}");
		}

		result ??= new(0, StructureData.Count, failureReason ?? "MonoBehaviour script is missing or its schema could not be resolved.");
		MonoBehaviour.Structure = new UnreadableStructure(StructureData, result, "Assembly", ObjectByteOffset);
		rawSnapshot?.ReportReadStatus(RawObjectReadStatus.Partial, result.Diagnostic);
		return null;
	}

	private UnityAssetBase LoadStructureOrStatelessAsset()
	{
		return (UnityAssetBase?)LoadStructure() ?? MonoBehaviour.Structure as UnityAssetBase ?? StatelessAsset.Instance;
	}

	public IUnityAssetBase DeepClone(PPtrConverter converter)
	{
		return LoadStructure()?.DeepClone(converter) ?? MonoBehaviour.Structure as UnreadableStructure ?? (IUnityAssetBase)StatelessAsset.Instance;
	}

	#region UnityAssetBase Overrides
	public override bool FlowMappedInYaml => LoadStructure()?.FlowMappedInYaml ?? base.FlowMappedInYaml;

	public override int SerializedVersion => LoadStructure()?.SerializedVersion ?? base.SerializedVersion;

	public override void WalkEditor(AssetWalker walker)
	{
		LoadStructureOrStatelessAsset().WalkEditor(walker);
	}

	public override void WalkRelease(AssetWalker walker)
	{
		LoadStructureOrStatelessAsset().WalkRelease(walker);
	}

	public override void WalkStandard(AssetWalker walker)
	{
		LoadStructureOrStatelessAsset().WalkStandard(walker);
	}

	public override IEnumerable<(string, PPtr)> FetchDependencies()
	{
		return LoadStructure()?.FetchDependencies() ?? [];
	}

	public override void WriteEditor(AssetWriter writer) => LoadStructure()?.WriteEditor(writer);

	public override void WriteRelease(AssetWriter writer) => LoadStructure()?.WriteRelease(writer);

	public override void CopyValues(IUnityAssetBase? source, PPtrConverter converter)
	{
		SerializableStructure? loadedStructure = LoadStructure();
		if (source is not UnloadedStructure unloadedSource)
		{
			loadedStructure?.CopyValues(source, converter);
		}
		else if (ReferenceEquals(this, unloadedSource))
		{
			loadedStructure?.CopyValues(loadedStructure, converter);
		}
		else
		{
			loadedStructure?.CopyValues(unloadedSource.LoadStructure(), converter);
		}
	}

	public override void Reset() => LoadStructure()?.Reset();

	public override bool? AddToEqualityComparer(IUnityAssetBase other, AssetEqualityComparer comparer)
	{
		return LoadStructureOrStatelessAsset().AddToEqualityComparer(other, comparer);
	}
	#endregion
}
