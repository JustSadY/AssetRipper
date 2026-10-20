using AssetRipper.IO.Endian;
using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.Tests.Shared;

// Independent synthetic wire fixture. No SerializedFile/TypeTree production writer builds these bytes.
internal sealed record SerializedFile23Fixture(byte[] Bytes, int HashOffset, int SizeOffset, int MagicOffset, int TreeVersionOffset,
	int RefHashOffset, byte[] Payload)
{
	public static SerializedFile23Fixture Create(EndianType endian, bool extracted = false, bool refExtracted = false,
		bool hasTree = true, int version = 23, byte[]? objectData = null)
	{
		using MemoryStream stream = new(); stream.SetLength(48); stream.Position = 48;
		using EndianWriter writer = new(stream, endian);
		Zero("6000.4.0f1"); writer.Write(24); writer.Write(hasTree); writer.Write(1);
		int hash = 0, size = 0, magic = 0, treeVersion = 0, refHash = 0;
		Type(false, extracted);
		writer.Write(1); writer.AlignStream(); writer.Write(4294967419L); writer.Write(0L);
		byte[] payload = objectData ?? [0x12, 0x34, 0x56, 0x78]; writer.Write(payload.Length); writer.Write(0);
		writer.Write(0); writer.Write(0); writer.Write(1); Type(true, refExtracted); Zero("synthetic v23");
		long metadataEnd = stream.Position; while (stream.Position % 16 != 0) writer.Write((byte)0);
		long dataOffset = stream.Position; writer.Write(payload); while (stream.Position % 8 != 0) writer.Write((byte)0);
		byte[] bytes = stream.ToArray();
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), version); bytes[16] = endian == EndianType.BigEndian ? (byte)1 : (byte)0;
		BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), checked((int)(metadataEnd - 48)));
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), bytes.LongLength); BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), dataOffset);
		return new(bytes, hash, size, magic, treeVersion, refHash, payload);

		void Type(bool reference, bool external)
		{
			writer.Write(reference ? -1 : 49); writer.Write(false); writer.Write((short)-1);
			if (reference) writer.Write(new byte[16]); // ScriptID for RawTypeID -1.
			writer.Write(new byte[16]); // OldTypeHash.
			if (hasTree)
			{
				if (reference) refHash = checked((int)stream.Position); else hash = checked((int)stream.Position);
				writer.Write(Enumerable.Range(reference ? 17 : 1, 16).Select(i => (byte)i).ToArray());
				long sizePosition = stream.Position; writer.Write(0);
				if (!reference) size = checked((int)sizePosition);
				if (!external)
				{
					long start = stream.Position;
					if (!reference) { magic = checked((int)start); treeVersion = magic + 4; }
					writer.Write(0x7474686Du); writer.Write(23); writer.Write(1);
					byte[] strings = Encoding.UTF8.GetBytes(reference ? "Node\0Base\0" : "TextAsset\0Base\0"); writer.Write(strings.Length);
					writer.Write((ushort)1); writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u); writer.Write(reference ? 5u : 10u);
					writer.Write(-1); writer.Write(0); writer.Write(0u); writer.Write(0x1020304050607080UL); writer.Write(strings);
					long end = stream.Position; stream.Position = sizePosition; writer.Write(checked((int)(end - start))); stream.Position = end;
				}
			}
			if (hasTree)
			{
				if (reference) { Zero("Node"); Zero("Fixture"); Zero("Assembly-CSharp"); }
				else { writer.Write(1); writer.Write(0); }
			}
		}
		void Zero(string text) { writer.Write(Encoding.UTF8.GetBytes(text)); writer.Write((byte)0); }
	}
	public static int ReadInt(byte[] bytes, int offset, EndianType endian) => endian == EndianType.BigEndian
		? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
	public static void WriteInt(byte[] bytes, int offset, int value, EndianType endian)
	{
		if (endian == EndianType.BigEndian) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), value);
		else BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
	}
}
