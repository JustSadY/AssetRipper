using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;

namespace AssetRipper.IO.Files.Tests;

public class ObjectInfoSourceTests
{
	[TestCase(EndianType.LittleEndian)]
	[TestCase(EndianType.BigEndian)]
	public void ObjectOffsetsAddressTheirOriginalSerializedPayloads(EndianType endian)
	{
		SerializedType type = new() { TypeID = 49, ScriptTypeIndex = -1 };
		SerializedFileBuilder builder = new()
		{
			Generation = FormatVersion.LargeFilesSupport, Version = new(2022, 3), EndianType = endian,
		};
		builder.Types.Add(type);
		builder.Objects.Add(new ObjectInfo(type) { FileID = 1, SerializedTypeIndex = 0, ObjectData = [1, 2, 3] });
		builder.Objects.Add(new ObjectInfo(type) { FileID = 2, SerializedTypeIndex = 0, ObjectData = [4, 5, 6, 7, 8] });
		byte[] bytes = builder.Build().ToByteArray();
		using SmartStream stream = SmartStream.CreateMemory(bytes);
		using SerializedFile file = SerializedFileScheme.Default.Read(stream, "fixture.assets", "fixture.assets");
		for (int i = 0; i < builder.Objects.Count; i++)
		{
			ObjectInfo obj = file.Objects[i];
			Assert.That(obj.ByteOffset, Is.Not.Null);
			Assert.That(obj.DeclaredByteSize, Is.EqualTo(builder.Objects[i].ObjectData.Length));
			Assert.That(bytes.AsSpan((int)obj.ByteOffset!.Value, obj.ObjectData.Length).ToArray(), Is.EqualTo(builder.Objects[i].ObjectData));
			Assert.That(obj, Is.EqualTo(builder.Objects[i]));
		}
	}
}
