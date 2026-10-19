namespace AssetRipper.Assets.IO.Recovery;

public interface IRawObjectSink
{
	IRawObjectCollection BeginCollection(RawCollectionInfo source);
}

public interface IRawObjectCollection
{
	IRawObjectSnapshot Preserve(RawObjectInfo source, ReadOnlySpan<byte> data);
}

public interface IRawObjectSnapshot
{
	void ReportReadStatus(RawObjectReadStatus status, string? diagnostic = null);
}

public enum RawObjectReadStatus
{
	Captured,
	Deferred,
	Parsed,
	FactoryReturnedNull,
	UnknownType,
	Unreadable,
	Partial,
	FactoryException,
	CollectionAborted,
}

public sealed record RawCollectionInfo(
	string SourcePath,
	string OriginalName,
	string CollectionName,
	string ContainerName,
	string UnityVersion,
	int SerializedFormat,
	int Platform,
	string Endian,
	bool HasTypeTree);

public sealed record RawObjectInfo(
	long PathId,
	int ClassId,
	int TypeId,
	int SerializedTypeIndex,
	short ScriptTypeIndex,
	long? ByteOffset,
	int? DeclaredByteSize)
{
	public string OffsetSpace => "SerializedFile";
}
