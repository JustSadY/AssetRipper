using AssetRipper.Assets.IO.Recovery;
using System.Text.Json.Serialization;

namespace AssetRipper.Import.Recovery;

public sealed record RawRecoveryJournalEntry
{
	public int SchemaVersion => 1;
	public required string Event { get; init; }
	public string? CollectionId { get; init; }
	public RawCollectionInfo? Collection { get; init; }
	public string? ObjectId { get; init; }
	public RawObjectInfo? Object { get; init; }
	public string? PayloadPath { get; init; }
	public string? Sha256 { get; init; }
	public int? ByteLength { get; init; }
	public RawObjectReadStatus? ReadStatus { get; init; }
	public string? Diagnostic { get; init; }
	public string? Phase { get; init; }
	public RawRecoverySummary? Summary { get; init; }
}

public sealed record RawRecoverySummary(
	string DirectoryPath,
	string JournalPath,
	long DiscoveredObjectCount,
	long PreservedObjectCount,
	long FailedObjectCount,
	long PendingObjectCount)
{
	public int SchemaVersion => 1;
	public string Scope => "SerializedObjectBytes";
	public bool ObjectIdsAreJobLocal => true;
	public bool HasIncompleteObjects => DiscoveredObjectCount != PreservedObjectCount || FailedObjectCount > 0 || PendingObjectCount > 0;
}

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RawRecoveryJournalEntry))]
[JsonSerializable(typeof(RawRecoverySummary))]
internal sealed partial class RawRecoveryJsonContext : JsonSerializerContext
{
}
