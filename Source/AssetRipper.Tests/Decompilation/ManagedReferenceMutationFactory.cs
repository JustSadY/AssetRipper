using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.Tests.Decompilation;

// Mutates only a copied object handed to the factory. Corpus bundles stay intact.
// Offsets are decoded from the known fixture layout, not found by byte search.
internal sealed class ManagedReferenceMutationFactory(UnityEditorPayloadFixture.EditorPayloadManager manager, string damage) : AssetFactoryBase
{
	private readonly GameAssetFactory factory = new(manager);
	public byte[] Original { get; private set; } = [];
	public byte[] Modified { get; private set; } = [];
	public int MutationOffset { get; private set; }
	public int MutationLength { get; private set; }
	public int StructureStart { get; private set; }
	public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references)
		=> ReadAsset(info, data, type, references, null);
	public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data, SerializedType? type, IReadOnlyList<SerializedTypeReference> references, IRawObjectSnapshot? snapshot)
	{
		if (info.ClassID != 114) return factory.ReadAsset(info, data, type, references, snapshot);
		Assert.That(Original, Is.Empty, "Fixture must contain exactly one MonoBehaviour.");
		Original = data.ToArray(); Modified = (byte[])Original.Clone();
		EndianSpanReader reader = new(Original, info.Collection.EndianType);
		MonoBehaviour.Create(info).Read(ref reader); StructureStart = reader.Position;
		Dictionary<string, int> offsets = [];
		offsets["root"] = reader.Position; Assert.That(reader.ReadInt64(), Is.EqualTo(101));
		Assert.That(reader.ReadInt64(), Is.EqualTo(101)); Assert.That(reader.ReadInt64(), Is.EqualTo(-2));
		Assert.That(reader.ReadInt32(), Is.EqualTo(3));
		Assert.That(reader.ReadInt64(), Is.EqualTo(4294967419)); Assert.That(reader.ReadInt64(), Is.EqualTo(-2)); Assert.That(reader.ReadInt64(), Is.EqualTo(101));
		Assert.That(reader.ReadInt64(), Is.EqualTo(4294967419));
		Assert.That(reader.ReadInt32(), Is.EqualTo(2)); Assert.That(reader.ReadInt32(), Is.EqualTo(3));
		for (int i = 0; i < 3; i++)
		{
			int ridOffset = reader.Position;
			Assert.That(reader.ReadInt64(), Is.EqualTo(i == 0 ? -2 : i == 1 ? 101 : 4294967419));
			int classOffset = reader.Position + sizeof(int);
			string className = ReadText(ref reader);
			Assert.That(className, Is.EqualTo(i == 0 ? "" : i == 1 ? "NumberNode" : "TextNode"));
			Assert.That(ReadText(ref reader), Is.EqualTo(i == 0 ? "" : "AssetRipper.EditorGraphFixture"));
			Assert.That(ReadText(ref reader), Is.EqualTo(i == 0 ? "" : "Assembly-CSharp"));
			if (i == 0) continue;
			Assert.That(ReadText(ref reader), Is.EqualTo(i == 1 ? "first ☃" : "second"));
			if (i == 1) { offsets["node"] = reader.Position; offsets["missing-type"] = classOffset; }
			else offsets["duplicate"] = ridOffset;
			Assert.That(reader.ReadInt64(), Is.EqualTo(i == 1 ? 4294967419 : 101));
			_ = reader.ReadInt32(); _ = reader.ReadInt64(); // Unity PPtr
			if (i == 1) Assert.That(reader.ReadInt32(), Is.EqualTo(-37));
			else Assert.That(ReadText(ref reader), Is.EqualTo("polymorphic ☃"));
		}
		reader.Align(); Assert.That(reader.Position, Is.EqualTo(Original.Length));
		MutationOffset = offsets[damage]; MutationLength = damage == "missing-type" ? 10 : sizeof(long);
		if (damage == "missing-type") Encoding.UTF8.GetBytes("AbsentNode").CopyTo(Modified, MutationOffset);
		else
		{
			long rid = damage == "duplicate" ? 101 : 999;
			if (info.Collection.EndianType == EndianType.LittleEndian) BinaryPrimitives.WriteInt64LittleEndian(Modified.AsSpan(MutationOffset), rid);
			else BinaryPrimitives.WriteInt64BigEndian(Modified.AsSpan(MutationOffset), rid);
		}
		return factory.ReadAsset(info, Modified, type, references, snapshot);
	}
	private static string ReadText(ref EndianSpanReader reader)
	{
		int length = reader.ReadInt32(); Assert.That(length, Is.InRange(0, reader.Length - reader.Position));
		byte[] bytes = new byte[length]; for (int i = 0; i < length; i++) bytes[i] = reader.ReadByte();
		reader.Align(); return Encoding.UTF8.GetString(bytes);
	}
}
