using AsmResolver.DotNet;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser.TypeTrees;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.Tests.Decompilation;

/// <summary>Hand-written bytes and two independent schemas. The Unity names below are controlled shims, not an Editor fixture.</summary>
internal sealed class MonoBehaviourPayloadFixture : IDisposable
{
	public const string Source = """
		using System;
		namespace UnityEngine {
		 public class Object {} public class Component : Object {} public class Behaviour : Component {} public class MonoBehaviour : Behaviour {}
		 [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute {}
		}
		namespace PayloadFixture {
		 [Serializable] public class Nested { public int count; public UnityEngine.Object target; }
		 public class Parent : UnityEngine.MonoBehaviour { public int inherited; [UnityEngine.SerializeField] private short hidden; }
		 public class Example : Parent {
		  public bool enabled; public string label; public int[] values; public UnityEngine.Object direct;
		  public UnityEngine.Object[] links; public Nested nested;
		  [NonSerialized] public int ignored; public static int global; public readonly int readOnly;
		 }
		}
		""";
	private static readonly Lazy<byte[]> Image = new(() =>
	{
		var result = FixtureCompiler.Compile(new Dictionary<string, string> { ["payload.cs"] = Source }, new(LanguageVersion.CSharp9, OptimizationLevel.Release));
		Assert.That(result.Check.Status, Is.EqualTo(CheckStatus.Passed));
		return result.AssemblyBytes!;
	});
	public static readonly UnityVersion Version = UnityVersion.Parse("2022.3.0f1");
	public GameBundle Bundle { get; } = new();
	public ProcessedAssetCollection Assets { get; }
	public PayloadManager Manager { get; } = new(Image.Value);
	public IMonoBehaviour Seed { get; }
	public ITextAsset Local { get; }
	public ITextAsset External { get; }
	public byte[] AssemblyBytes => Image.Value;

	public MonoBehaviourPayloadFixture()
	{
		Assets = Bundle.AddNewProcessedCollection("payload.assets", Version);
		Assets.SetLayout(Version, BuildTarget.StandaloneWin64Player, TransferInstructionFlags.SerializeGameRelease);
		Assets.FilePath = "/fixture/payload.assets";
		Local = Assets.CreateAsset(49, TextAsset.Create); Local.Name = "Local";
		var external = Bundle.AddNewProcessedCollection("external.assets", Version);
		External = external.CreateAsset(49, TextAsset.Create); External.Name = "External";
		Assert.That(Assets.AddDependency(external), Is.EqualTo(1));
		IMonoScript script = Assets.CreateAsset(115, MonoScript.Create);
		script.AssemblyName = "ManagedRoundTrip"; script.Namespace = "PayloadFixture"; script.ClassName_R = "Example";
		Seed = Assets.CreateAsset(114, MonoBehaviour.Create);
		Seed.ScriptP = script;
	}

	public byte[] Fields(EndianType endian = EndianType.LittleEndian)
	{
		// Independent oracle writer: no SerializableStructure/SerializableValue.Write calls.
		using MemoryStream bytes = new();
		void Number(long value, int width)
		{
			Span<byte> buffer = stackalloc byte[8];
			if (endian == EndianType.LittleEndian) BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
			else BinaryPrimitives.WriteInt64BigEndian(buffer, value);
			bytes.Write(endian == EndianType.LittleEndian ? buffer[..width] : buffer[(8 - width)..]);
		}
		void Align() { while (bytes.Length % 4 != 0) bytes.WriteByte(0); }
		void Pointer(int file, long path) { Number(file, 4); Number(path, 8); }
		Number(-17, 4); Number(-1234, 2); Align(); bytes.WriteByte(1); Align();
		byte[] label = Encoding.UTF8.GetBytes("Merhaba ☃"); Number(label.Length, 4); bytes.Write(label); Align();
		Number(3, 4); Number(7, 4); Number(-9, 4); Number(42, 4);
		Pointer(0, Local.PathID);
		Number(4, 4); Pointer(0, Local.PathID); Pointer(1, External.PathID); Pointer(0, 0); Pointer(2, 999);
		Number(91, 4); Pointer(0, 0x100000001L);
		return bytes.ToArray();
	}

	public IMonoBehaviour Read(byte[] fields, bool typeTree, AssetRipper.Assets.IO.Recovery.IRawObjectSnapshot? snapshot = null)
	{
		using MemoryStream header = new();
		using (AssetWriter writer = new(header, Assets)) Seed.WriteRelease(writer);
		byte[] data = header.ToArray().Concat(fields).ToArray();
		IMonoBehaviour result = (IMonoBehaviour)new GameAssetFactory(Manager).ReadAsset(Seed.AssetInfo, data, typeTree ? Tree() : null, [], snapshot)!;
		Assets.ReplaceAsset(result);
		return result;
	}

	public static SerializedType Tree()
	{
		SerializedType type = new() { TypeID = 114, ScriptTypeIndex = -1 };
		var nodes = type.OldType.Nodes;
		void Node(string kind, string name, int depth = 1, bool align = false) => nodes.Add(new TypeTreeNode(kind, name, depth, align) { Version = 1 });
		void Pointer(string name, int depth = 1) { Node("PPtr<Object>", name, depth); Node("int", "m_FileID", depth + 1); Node("SInt64", "m_PathID", depth + 1); }
		void Array(string name, string element)
		{
			Node("vector", name); nodes.Add(new TypeTreeNode("Array", "Array", 2, true) { TypeFlags = 1, Version = 1 });
			Node("int", "size", 3);
			if (element == "PPtr") Pointer("data", 3); else Node(element, "data", 3);
		}
		Node("MonoBehaviour", "Base", 0); Node("string", "m_Name");
		Node("int", "inherited"); Node("short", "hidden", align: true); Node("bool", "enabled", align: true);
		Node("string", "label"); nodes.Add(new TypeTreeNode("Array", "Array", 2, true) { TypeFlags = 1, Version = 1 }); Node("int", "size", 3); Node("char", "data", 3);
		Array("values", "int"); Pointer("direct"); Array("links", "PPtr");
		Node("Nested", "nested"); Node("int", "count", 2); Pointer("target", 2);
		return type;
	}

	public void Dispose() => Manager.Dispose();

	internal sealed class PayloadManager : BaseManager
	{
		public override ScriptingBackend ScriptingBackend => ScriptingBackend.Mono;
		public PayloadManager(byte[] image) : base(_ => { })
		{
			var images = FixtureCompiler.ReferenceImages;
			AssemblyDefinition corlib = AssemblyDefinition.FromBytes(images.Single(i => i.File == "System.Runtime.dll").Bytes, createRuntimeContext: false);
			RuntimeContext context = new(DotNetRuntimeInfo.NetCoreApp(10, 0), (bool?)null, corlib);
			context.AddAssembly(corlib); Add(corlib);
			foreach (var reference in images.Where(i => i.File != "System.Runtime.dll")) Read(new MemoryStream(reference.Bytes, false), reference.File);
			Read(new MemoryStream(image, false), "ManagedRoundTrip.dll");
		}
	}
}
