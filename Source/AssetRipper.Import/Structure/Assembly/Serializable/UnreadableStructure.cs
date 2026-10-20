using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Metadata;

namespace AssetRipper.Import.Structure.Assembly.Serializable;

/// <summary>A failed read retains bytes, not guessed fields or a complete dependency list.</summary>
public sealed class UnreadableStructure(ReadOnlyArraySegment<byte> data, SerializableStructureReadResult result,
	string schemaSource, int? objectByteOffset, string payloadScope = "ScriptFields") : UnityAssetBase, IDeepCloneable
{
	public ReadOnlyArraySegment<byte> Data { get; } = data;
	public SerializableStructureReadResult ReadResult { get; } = result.Succeeded
		? throw new ArgumentException("An unreadable structure must have a failed read result.", nameof(result)) : result;
	public string SchemaSource { get; } = schemaSource;
	public int? ObjectByteOffset { get; } = objectByteOffset;
	public string PayloadScope { get; } = payloadScope;
	public string DependencyCoverage => "Incomplete";

	// There are no decoded references to convert. Keep the failure evidence when
	// cloning instead of replacing it with an indistinguishable empty structure.
	public IUnityAssetBase DeepClone(PPtrConverter converter) => this;
	public override void Reset() { }
	public override bool? AddToEqualityComparer(IUnityAssetBase other, AssetEqualityComparer comparer)
		=> other is UnreadableStructure failed && ReadResult == failed.ReadResult && SchemaSource == failed.SchemaSource
			&& PayloadScope == failed.PayloadScope && ((ReadOnlySpan<byte>)Data).SequenceEqual(failed.Data);
}

/// <summary>Byte consumption is a layout check, not proof of schema or reference correctness.</summary>
public sealed record SerializableStructureReadResult(int BytesRead, int ByteLength, string? Diagnostic)
{
	public bool Succeeded => Diagnostic is null;
}
