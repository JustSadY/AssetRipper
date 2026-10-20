using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.Streams.Smart;
using AssetRipper.Tests.Shared;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace AssetRipper.IO.Files.Tests;

[Category("SerializedFile23")]
public class SerializedFile23Tests
{
	[TestCase(EndianType.LittleEndian, false, false)]
	[TestCase(EndianType.BigEndian, false, false)]
	[TestCase(EndianType.LittleEndian, true, false)]
	[TestCase(EndianType.BigEndian, true, false)]
	[TestCase(EndianType.LittleEndian, false, true)]
	[TestCase(EndianType.BigEndian, false, true)]
	[TestCase(EndianType.LittleEndian, true, true)]
	[TestCase(EndianType.BigEndian, true, true)]
	public void IndependentWirePreservesObjectsHashesDependenciesAndReferenceTypes(EndianType endian, bool extracted, bool refExtracted)
	{
		var fixture = SerializedFile23Fixture.Create(endian, extracted, refExtracted); using var file = Read(fixture.Bytes);
		Assert.That(file.Generation, Is.EqualTo(FormatVersion.ExtractedTypeTreeSupport)); Assert.That(file.Version.ToString(), Is.EqualTo("6000.4.0f1"));
		Assert.That(file.EndianType, Is.EqualTo(endian)); Assert.That(file.Objects[0].FileID, Is.EqualTo(4294967419L));
		Assert.That(file.Objects[0].ObjectData, Is.EqualTo(fixture.Payload)); Assert.That(file.Types[0].TypeDependencies, Is.EqualTo(new[] { 0 }));
		Assert.That(file.RefTypes[0].FullName, Is.EqualTo("Fixture.Node")); Assert.That(file.RefTypes[0].AsmName.String, Is.EqualTo("Assembly-CSharp"));
		Assert.That(file.ExtractedTypeTreeCount, Is.EqualTo((extracted ? 1 : 0) + (refExtracted ? 1 : 0)));
		Assert.That(file.Types[0].IsTypeTreeExtracted, Is.EqualTo(extracted)); Assert.That(file.RefTypes[0].IsTypeTreeExtracted, Is.EqualTo(refExtracted));
		Assert.That(file.Types[0].OldType.Nodes.Count, Is.EqualTo(extracted ? 0 : 1));
		if (extracted) Assert.That(file.Types[0].InlineTypeTreeByteCount, Is.Null);
		else
		{
			Assert.That(file.Types[0].OldType.Nodes[0].Type, Is.EqualTo("TextAsset"));
			Assert.That(file.Types[0].OldType.Nodes[0].RefTypeHash, Is.EqualTo(0x1020304050607080UL));
			Assert.That(file.Types[0].InlineTypeTreeByteCount, Is.EqualTo(63));
		}
		byte[] output = file.ToByteArray();
		string evidence = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults", "serialized-file23", $"{endian}-{extracted}-{refExtracted}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(evidence); File.WriteAllBytes(Path.Combine(evidence, "input.bin"), fixture.Bytes); File.WriteAllBytes(Path.Combine(evidence, "output.bin"), output);
		using var roundtrip = Read(output);
		Assert.That(output.AsSpan(fixture.HashOffset, 16).ToArray(), Is.EqualTo(fixture.Bytes.AsSpan(fixture.HashOffset, 16).ToArray()));
		Assert.That(output.AsSpan(fixture.RefHashOffset, 16).ToArray(), Is.EqualTo(fixture.Bytes.AsSpan(fixture.RefHashOffset, 16).ToArray()));
		Assert.That(roundtrip.Types[0].ExtractedTypeTreeHash, Is.EqualTo(file.Types[0].ExtractedTypeTreeHash));
		Assert.That(roundtrip.RefTypes[0].ExtractedTypeTreeHash, Is.EqualTo(file.RefTypes[0].ExtractedTypeTreeHash));
		Assert.That(roundtrip.Objects[0].ObjectData, Is.EqualTo(fixture.Payload));
		if (!extracted)
		{
			Assert.That(SerializedFile23Fixture.ReadInt(output, fixture.SizeOffset, endian), Is.EqualTo(63));
			Assert.That(SerializedFile23Fixture.ReadInt(output, fixture.MagicOffset, endian), Is.EqualTo(0x7474686D));
			Assert.That(SerializedFile23Fixture.ReadInt(output, fixture.TreeVersionOffset, endian), Is.EqualTo(23));
		}
		using Stream evidenceStream = File.Create(Path.Combine(evidence, "verification.json"));
		using Utf8JsonWriter json = new(evidenceStream);
		json.WriteStartObject(); json.WriteString("Source", "IndependentSyntheticV23"); json.WriteString("RealUnity6000_5Validation", "NotRun");
		json.WriteString("Endian", endian.ToString()); json.WriteBoolean("Extracted", extracted); json.WriteBoolean("RefExtracted", refExtracted);
		json.WriteString("InputSha256", Convert.ToHexStringLower(SHA256.HashData(fixture.Bytes)));
		json.WriteString("OutputSha256", Convert.ToHexStringLower(SHA256.HashData(output)));
		json.WriteString("ObjectSha256", Convert.ToHexStringLower(SHA256.HashData(fixture.Payload))); json.WriteString("Result", "Passed"); json.WriteEndObject();
	}
	[TestCase(EndianType.LittleEndian)]
	[TestCase(EndianType.BigEndian)]
	public void V22NonEmptyTypeDependenciesKeepTheirInt32Width(EndianType endian)
	{
		using var source = Read(SerializedFile23Fixture.Create(endian).Bytes);
		SerializedFileBuilder builder = new()
		{
			Generation = FormatVersion.LargeFilesSupport, Version = source.Version, Platform = source.Platform,
			EndianType = endian, HasTypeTree = true,
		};
		builder.Types.AddRange(source.Types.ToArray()); builder.RefTypes.AddRange(source.RefTypes.ToArray());
		builder.Objects.AddRange(source.Objects.ToArray());
		using var again = Read(builder.Build().ToByteArray());
		Assert.That(again.Generation, Is.EqualTo(FormatVersion.LargeFilesSupport));
		Assert.That(again.Types[0].TypeDependencies, Is.EqualTo(new[] { 0 }));
		Assert.That(again.RefTypes[0].FullName, Is.EqualTo("Fixture.Node"));
		Assert.That(again.Objects[0].FileID, Is.EqualTo(source.Objects[0].FileID));
		Assert.That(again.Objects[0].ObjectData, Is.EqualTo(source.Objects[0].ObjectData));
	}
	[TestCase(EndianType.LittleEndian)]
	[TestCase(EndianType.BigEndian)]
	public void DisabledTypeTreesDoNotConsumeV23Prefixes(EndianType endian)
	{
		var fixture = SerializedFile23Fixture.Create(endian, hasTree: false); using var file = Read(fixture.Bytes);
		Assert.That(file.HasTypeTree, Is.False); Assert.That(file.ExtractedTypeTreeCount, Is.Zero);
		Assert.That(file.Objects[0].ObjectData, Is.EqualTo(fixture.Payload));
		using var again = Read(file.ToByteArray()); Assert.That(again.Objects[0].ObjectData, Is.EqualTo(fixture.Payload));
	}
	[TestCase("magic", EndianType.LittleEndian)]
	[TestCase("magic", EndianType.BigEndian)]
	[TestCase("version", EndianType.LittleEndian)]
	[TestCase("size", EndianType.BigEndian)]
	public void InvalidTreePrefixesFailExplicitly(string field, EndianType endian)
	{
		var fixture = SerializedFile23Fixture.Create(endian);
		int offset = field == "magic" ? fixture.MagicOffset : field == "version" ? fixture.TreeVersionOffset : fixture.SizeOffset;
		SerializedFile23Fixture.WriteInt(fixture.Bytes, offset, field == "version" ? 22 : -1, endian);
		Assert.That(() => Read(fixture.Bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains(field == "size" ? "negative" : field));
	}
	[Test]
	public void StoredInlineSizeIsObservedWithoutPretendingItsScopeIsProven()
	{
		var fixture = SerializedFile23Fixture.Create(EndianType.LittleEndian); SerializedFile23Fixture.WriteInt(fixture.Bytes, fixture.SizeOffset, 999, EndianType.LittleEndian);
		using var file = Read(fixture.Bytes); Assert.That(file.Types[0].StoredTypeTreeSize, Is.EqualTo(999)); Assert.That(file.Types[0].InlineTypeTreeByteCount, Is.EqualTo(63));
		Assert.That(file.Objects[0].ObjectData, Is.EqualTo(fixture.Payload));
	}
	[TestCase(24)]
	[TestCase(64)]
	public void FuturePlausibleHeadersAreRecognizedButNeverReadAsResources(int generation)
	{
		var fixture = SerializedFile23Fixture.Create(EndianType.LittleEndian, version: generation);
		using SmartStream stream = SmartStream.CreateMemory(fixture.Bytes); Assert.That(SerializedFileScheme.Default.CanRead(stream), Is.True); Assert.That(stream.Position, Is.Zero);
		Assert.That(() => SchemeReader.ReadFile(fixture.Bytes, "future.assets", "future.assets"), Throws.TypeOf<UnsupportedSerializedFileVersionException>().With.Message.Contains(generation.ToString()).And.Message.Contains("23"));
	}
	[TestCase(4)]
	[TestCase(65)]
	[TestCase(int.MaxValue)]
	public void OutOfRecognitionRangeIsNotInventedAsSerialized(int generation)
	{
		var fixture = SerializedFile23Fixture.Create(EndianType.LittleEndian, version: generation); using SmartStream stream = SmartStream.CreateMemory(fixture.Bytes);
		Assert.That(SerializedFileScheme.Default.CanRead(stream), Is.False); Assert.That(stream.Position, Is.Zero);
	}
	[TestCase(22, 16)]
	[TestCase(23, 24)]
	[TestCase(24, 31)]
	[TestCase(64, 47)]
	public void TruncatedExtendedHeaderProbeReturnsFalseAndRestoresPosition(int generation, int length)
	{
		byte[] bytes = new byte[length]; BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), generation);
		using SmartStream stream = SmartStream.CreateMemory(bytes); Assert.That(SerializedFileScheme.Default.CanRead(stream), Is.False);
		Assert.That(stream.Position, Is.Zero);
	}
	[Test]
	public void DifferentExternalTreeIdentitiesAreNotEqualTypes()
	{
		var a = SerializedFile23Fixture.Create(EndianType.LittleEndian, true); var b = SerializedFile23Fixture.Create(EndianType.LittleEndian, true); b.Bytes[b.HashOffset] ^= 1; b.Bytes[b.RefHashOffset] ^= 1;
		using var left = Read(a.Bytes); using var right = Read(b.Bytes);
		Assert.That(left.Types[0], Is.Not.EqualTo(right.Types[0])); Assert.That(left.RefTypes[0], Is.Not.EqualTo(right.RefTypes[0]));
	}
	private static SerializedFile Read(byte[] bytes) => (SerializedFile)SchemeReader.ReadFile(bytes, "synthetic.assets", "synthetic.assets");
	[Test]
	public void RandomAndImplausibleFutureHeadersAreNotSerializedFiles()
	{
		Random random = new(23);
		for (int i = 0; i < 32; i++)
		{
			byte[] bytes = new byte[48 + i * 13]; random.NextBytes(bytes);
			using SmartStream stream = SmartStream.CreateMemory(bytes);
			Assert.That(SerializedFileScheme.Default.CanRead(stream), Is.False); Assert.That(stream.Position, Is.Zero);
		}
		var fixture = SerializedFile23Fixture.Create(EndianType.LittleEndian, version: 24);
		BinaryPrimitives.WriteInt64BigEndian(fixture.Bytes.AsSpan(24), fixture.Bytes.Length + 1);
		using SmartStream wrongLength = SmartStream.CreateMemory(fixture.Bytes);
		Assert.That(SerializedFileScheme.Default.CanRead(wrongLength), Is.False);
	}
}
