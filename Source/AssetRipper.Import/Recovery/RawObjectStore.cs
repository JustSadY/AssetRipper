using AssetRipper.Assets.IO.Recovery;
using AssetRipper.IO.Files;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssetRipper.Import.Recovery;

/// <summary>
/// Preserves serialized objects before parsing, without keeping a second in-memory asset inventory.
/// The append-only journal remains usable when import or later processing fails.
/// </summary>
public sealed class RawObjectStore : IRawObjectSink
{
	private readonly FileSystem fileSystem;
	private readonly object sync = new();
	private long collectionCount;
	private long discoveredCount;
	private long preservedCount;
	private long failedCount;
	private long pendingCount;

	public string DirectoryPath { get; }
	public string JournalPath { get; }

	public RawObjectStore(FileSystem fileSystem, string rootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
		this.fileSystem = fileSystem;
		DirectoryPath = fileSystem.Path.Join(fileSystem.Path.GetFullPath(rootDirectory), $"Recovery_{Guid.NewGuid():N}");
		JournalPath = fileSystem.Path.Join(DirectoryPath, "objects.jsonl");
		fileSystem.Directory.Create(fileSystem.Path.Join(DirectoryPath, "Objects"));
		using (Stream stream = fileSystem.File.Create(JournalPath))
		{
			Flush(stream);
		}
		Append(new() { Event = "JobStarted", Phase = "Importing" });
	}

	public IRawObjectCollection BeginCollection(RawCollectionInfo source)
	{
		lock (sync)
		{
			string id = $"c{++collectionCount:D8}";
			Append(new() { Event = "CollectionDiscovered", CollectionId = id, Collection = source });
			return new Collection(this, id);
		}
	}

	public RawRecoverySummary GetSummary()
	{
		lock (sync)
		{
			return new(DirectoryPath, JournalPath, discoveredCount, preservedCount, failedCount, pendingCount);
		}
	}

	public void WriteSummary(string phase)
	{
		lock (sync)
		{
			RawRecoverySummary summary = GetSummary();
			Append(new() { Event = "Checkpoint", Phase = phase, Summary = summary });
			WriteSummaryFile(fileSystem.Path.Join(DirectoryPath, "summary.json"), fileSystem, summary);
		}
	}

	/// <summary>
	/// Writes a reference to the persistent recovery job. Payloads stay in DirectoryPath.
	/// </summary>
	public void WriteReference(string exportRoot, FileSystem targetFileSystem)
	{
		targetFileSystem.Directory.Create(exportRoot);
		WriteSummaryFile(targetFileSystem.Path.Join(exportRoot, "raw-recovery.json"), targetFileSystem, GetSummary());
	}

	private static void WriteSummaryFile(string path, FileSystem fileSystem, RawRecoverySummary summary)
	{
		using Stream stream = fileSystem.File.Create(path);
		JsonSerializer.Serialize(stream, summary, RawRecoveryJsonContext.Default.RawRecoverySummary);
		Flush(stream);
	}

	private IRawObjectSnapshot Preserve(string collectionId, long ordinal, RawObjectInfo source, ReadOnlySpan<byte> data)
	{
		lock (sync)
		{
			string id = $"{collectionId}:o{ordinal:D8}";
			string hash = Convert.ToHexStringLower(SHA256.HashData(data));
			string relativePath = fileSystem.Path.Join("Objects", hash + ".bin");
			Append(new()
			{
				Event = "ObjectDiscovered", CollectionId = collectionId, ObjectId = id, Object = source,
				Sha256 = hash, ByteLength = data.Length,
			});
			discoveredCount++;
			string path = fileSystem.Path.Join(DirectoryPath, relativePath);
			if (fileSystem.File.Exists(path))
			{
				using Stream existing = fileSystem.File.OpenRead(path);
				existing.Position = 0;
				if (existing.Length != data.Length || Convert.ToHexStringLower(SHA256.HashData(existing)) != hash)
				{
					throw new InvalidDataException($"Raw payload failed integrity verification: {path}");
				}
			}
			else
			{
				using Stream stream = fileSystem.File.Create(path);
				stream.Write(data);
				Flush(stream);
			}
			Append(new()
			{
				Event = "ObjectPreserved", ObjectId = id, PayloadPath = relativePath,
				Sha256 = hash, ByteLength = data.Length, ReadStatus = RawObjectReadStatus.Captured,
			});
			preservedCount++;
			pendingCount++;
			return new Snapshot(this, id);
		}
	}

	private void Append(RawRecoveryJournalEntry entry)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, RawRecoveryJsonContext.Default.RawRecoveryJournalEntry);
		using Stream stream = fileSystem.File.OpenWrite(JournalPath);
		stream.Position = stream.Length;
		stream.Write(bytes);
		stream.WriteByte((byte)'\n');
		Flush(stream);
	}

	private static void Flush(Stream stream)
	{
		if (stream is FileStream file)
		{
			file.Flush(flushToDisk: true);
		}
		else
		{
			stream.Flush();
		}
	}

	private sealed class Collection(RawObjectStore owner, string id) : IRawObjectCollection
	{
		private long ordinal;
		public IRawObjectSnapshot Preserve(RawObjectInfo source, ReadOnlySpan<byte> data) => owner.Preserve(id, Interlocked.Increment(ref ordinal), source, data);
	}

	private sealed class Snapshot(RawObjectStore owner, string id) : IRawObjectSnapshot
	{
		private RawObjectReadStatus current = RawObjectReadStatus.Captured;

		public void ReportReadStatus(RawObjectReadStatus status, string? diagnostic = null)
		{
			lock (owner.sync)
			{
				owner.Append(new() { Event = "ObjectRead", ObjectId = id, ReadStatus = status, Diagnostic = diagnostic });
				owner.failedCount += (IsFailure(status) ? 1 : 0) - (IsFailure(current) ? 1 : 0);
				owner.pendingCount += (IsPending(status) ? 1 : 0) - (IsPending(current) ? 1 : 0);
				current = status;
			}
		}

		private static bool IsPending(RawObjectReadStatus status) => status is RawObjectReadStatus.Captured or RawObjectReadStatus.Deferred;
		private static bool IsFailure(RawObjectReadStatus status) => status is not (RawObjectReadStatus.Captured or RawObjectReadStatus.Deferred or RawObjectReadStatus.Parsed);
	}
}
