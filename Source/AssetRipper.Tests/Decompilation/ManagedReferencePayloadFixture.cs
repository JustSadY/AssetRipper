using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.IO.Recovery;
using AssetRipper.Assets.IO.Writing;
using AssetRipper.Import.AssetCreation;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.Tests.Decompilation;

// Controlled shims and hand-written bytes; this is not an Editor-generated corpus.
internal sealed class ManagedReferencePayloadFixture : IDisposable
{
	private const string Source = """
		using System;
		using System.Collections.Generic;
		namespace UnityEngine {
		 public class Object {} public class Component : Object {} public class Behaviour : Component {} public class MonoBehaviour : Behaviour {}
		 [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeReference : Attribute {}
		}
		namespace ManagedGraphFixture {
		 [Serializable] public class Node { public int value; [UnityEngine.SerializeReference] public Node next; public UnityEngine.Object target; }
		 [Serializable] public class Box { [UnityEngine.SerializeReference] public Node child; }
		 public class Host : UnityEngine.MonoBehaviour {
		  [UnityEngine.SerializeReference] public Node root;
		  [UnityEngine.SerializeReference] public List<Node> items;
		  public Box inline;
		 }
		}
		[Serializable] public class GlobalNode : ManagedGraphFixture.Node {}
		""";
	private static readonly Lazy<byte[]> Image = new(() =>
	{
		var result = FixtureCompiler.Compile(new Dictionary<string, string> { ["graph.cs"] = Source }, new(LanguageVersion.CSharp9, OptimizationLevel.Release));
		Assert.That(result.Check.Status, Is.EqualTo(CheckStatus.Passed)); return result.AssemblyBytes!;
	});
	public static readonly UnityVersion Version = UnityVersion.Parse("6000.4.0f1");
	public GameBundle Bundle { get; } = new();
	public ProcessedAssetCollection Assets { get; }
	public MonoBehaviourPayloadFixture.PayloadManager Manager { get; } = new(Image.Value);
	public IMonoBehaviour Seed { get; }
	public ITextAsset Healthy { get; }
	public ManagedReferencePayloadFixture()
	{
		Assets = Bundle.AddNewProcessedCollection("graph.assets", Version);
		Assets.SetLayout(Version, BuildTarget.StandaloneWin64Player, TransferInstructionFlags.SerializeGameRelease);
		Healthy = Assets.CreateAsset(49, TextAsset.Create); Healthy.Name = "GraphHealthy"; Healthy.Script_C49 = "Independent asset";
		IMonoScript script = Assets.CreateAsset(115, MonoScript.Create);
		script.AssemblyName = "ManagedRoundTrip"; script.Namespace = "ManagedGraphFixture"; script.ClassName_R = "Host";
		Seed = Assets.CreateAsset(114, MonoBehaviour.Create); Seed.ScriptP = script;
	}

	public byte[] Fields(string variant = "valid", EndianType endian = EndianType.LittleEndian)
	{
		using MemoryStream bytes = new();
		void Number(long value, int width)
		{
			Span<byte> buffer = stackalloc byte[8];
			if (endian == EndianType.LittleEndian) BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
			else BinaryPrimitives.WriteInt64BigEndian(buffer, value);
			bytes.Write(endian == EndianType.LittleEndian ? buffer[..width] : buffer[(8 - width)..]);
		}
		void Align() { while (bytes.Length % 4 != 0) bytes.WriteByte(0); }
		void Text(string value) { byte[] text = Encoding.UTF8.GetBytes(value); Number(text.Length, 4); bytes.Write(text); Align(); }
		long first = variant == "zero" ? 0 : 101; const long second = 4294967419;
		Number(variant switch { "root" => 999, "negative" => -3, "null" => -2, "unknown" => -1, _ => first }, 8);
		Number(2, 4); Number(variant == "array" ? 999 : second, 8); Number(-2, 8);
		Number(variant == "inline" ? 999 : second, 8);
		Number(2, 4); Number(variant == "host-scope" ? 2 : 3, 4);
		Number(-2, 8); Text(""); Text(""); Text("");
		Node(first, variant == "node" ? 999 : variant == "host-scope" ? first : second);
		if (variant != "host-scope") Node(variant == "duplicate" ? first : second, first);
		Align(); return bytes.ToArray();

		void Node(long rid, long next)
		{
			Number(rid, 8); Text(variant switch { "missing-type" => "AbsentNode", "missing-class" => "", "global-namespace" => "GlobalNode", _ => "Node" });
			Text(variant == "global-namespace" ? "" : "ManagedGraphFixture"); Text(variant == "missing-assembly" ? "" : "ManagedRoundTrip");
			if (variant is "missing-class" or "missing-assembly") return;
			Number(-37, 4); Number(next, 8); Number(0, 4); Number(Healthy.PathID, 8);
		}
	}

	public IMonoBehaviour Read(byte[] fields, IRawObjectSnapshot? snapshot = null, bool newHost = false)
	{
		IMonoBehaviour seed = newHost ? Assets.CreateAsset(114, MonoBehaviour.Create) : Seed;
		seed.ScriptP = Seed.ScriptP;
		using MemoryStream header = new(); using (AssetWriter writer = new(header, Assets)) seed.WriteRelease(writer);
		IMonoBehaviour result = (IMonoBehaviour)new GameAssetFactory(Manager).ReadAsset(seed.AssetInfo, header.ToArray().Concat(fields).ToArray(), null, [], snapshot)!;
		Assets.ReplaceAsset(result); return result;
	}
	public void Dispose() => Manager.Dispose();
}
