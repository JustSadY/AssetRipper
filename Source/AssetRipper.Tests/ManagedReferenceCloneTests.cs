using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;
using AssetRipper.SourceGenerated.Classes.ClassID_0;
using AssetRipper.SourceGenerated.Classes.ClassID_49;

namespace AssetRipper.Tests;

// Always runs in CI, without Unity or an externally generated corpus.
[Category("UnityManagedReference")]
public class ManagedReferenceCloneTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void CloneKeepsDynamicDataAndConvertsPointersToDestinationCollection(bool differentCollection)
	{
		GameBundle bundle = new();
		var version = UnityVersion.Parse("6000.4.0f1");
		var source = bundle.AddNewProcessedCollection("source", version);
		var destination = differentCollection ? bundle.AddNewProcessedCollection("destination", version) : source;
		ITextAsset target = source.CreateAsset(49, TextAsset.Create);
		var nodeType = new TestType("ConcreteNode", [new(SerializablePrimitiveType.GetOrCreate(PrimitiveType.String), 0, "label", false),
			new(ManagedReferenceTypes.ManagedReference, 0, "next", false), new(SerializablePointerType.Shared, 0, "target", false)]);
		var hostType = new TestType("Host", [new(ManagedReferenceTypes.ManagedReference, 0, "root", false), ManagedReferenceTypes.RegistryField]);
		SerializableStructure host = hostType.CreateSerializableStructure(); host.InitializeFields(version);
		host["root"]["rid"].AsInt64 = 101;
		host["references"]["version"].AsInt32 = 2;
		var first = Entry(101, 4294967419); var second = Entry(4294967419, 101);
		host["references"]["RefIds"].AsAssetArray = [first, second];
		SerializableStructure clone = host.DeepClone(new(source, destination));
		Assert.That(clone["root"]["rid"].AsInt64, Is.EqualTo(101));
		var entries = clone["references"]["RefIds"].AsAssetArray.Cast<SerializableStructure>().ToArray();
		Assert.That(entries.Select(e => e["rid"].AsInt64), Is.EqualTo(new long[] { 101, 4294967419 }));
		for (int i = 0; i < entries.Length; i++)
		{
			var data = entries[i]["data"].AsStructure;
			var originalData = (i == 0 ? first : second)["data"].AsStructure;
			Assert.That(data.Type, Is.SameAs(nodeType));
			Assert.That(data, Is.Not.SameAs(originalData));
			Assert.That(data["label"].AsString, Is.EqualTo("kept ☃"));
			Assert.That(data["next"]["rid"].AsInt64, Is.EqualTo(i == 0 ? 4294967419 : 101));
			PPtr pointer = new(data["target"].AsPPtr);
			Assert.That(destination.TryGetAsset(pointer), Is.SameAs(target));
			Assert.That(pointer.FileID, Is.EqualTo(differentCollection ? 1 : 0));
			data["label"].AsString = "modified clone";
			Assert.That(originalData["label"].AsString, Is.EqualTo("kept ☃"));
		}
		Assert.That(clone.FetchDependencies().Count(), Is.EqualTo(2));

		SerializableStructure Entry(long rid, long next)
		{
			var entry = (SerializableStructure)ManagedReferenceTypes.ReferencedObject.CreateInstance(2, version);
			entry["rid"].AsInt64 = rid; entry["type"]["class"].AsString = nodeType.Name;
			entry["type"]["asm"].AsString = "Fixture";
			var data = (SerializableStructure)nodeType.CreateInstance(3, version);
			data["label"].AsString = "kept ☃"; data["next"]["rid"].AsInt64 = next;
			((IPPtr<IObject>)data["target"].AsPPtr).SetAsset(source, target);
			entry["data"].AsAsset = data;
			return entry;
		}
	}

	[Test]
	public void EmptyNullRegistryDataRemainsEmptyAndIndependent()
	{
		GameBundle bundle = new(); var collection = bundle.AddNewProcessedCollection("null", UnityVersion.Parse("6000.4.0f1"));
		SerializableStructure entry = ManagedReferenceTypes.ReferencedObject.CreateSerializableStructure(); entry.InitializeFields(collection.Version);
		entry["rid"].AsInt64 = -2;
		SerializableStructure clone = entry.DeepClone(new(collection, collection));
		Assert.That(clone["rid"].AsInt64, Is.EqualTo(-2));
		Assert.That(clone["data"].AsStructure.Type, Is.SameAs(ManagedReferenceTypes.ReferencedObjectData));
		Assert.That(clone["data"].AsStructure.Fields, Is.Empty);
		Assert.That(clone["data"].AsAsset, Is.Not.SameAs(entry["data"].AsAsset));
		Assert.That(clone.FetchDependencies(), Is.Empty);
	}

	private sealed class TestType : SerializableType
	{
		public TestType(string name, Field[] fields) : base("Fixture", PrimitiveType.Complex, name) { Fields = fields; MaxDepth = 5; }
	}
}
