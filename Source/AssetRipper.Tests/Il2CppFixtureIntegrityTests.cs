using AssetRipper.IO.Files;
using AssetRipper.Tests.Decompilation;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("Il2CppCapability")]
public class Il2CppFixtureIntegrityTests
{
	[Test]
	public void ControlledManifestAcceptsExactRuntimeObservations()
	{
		var fs = Fixture(); Assert.That(UnityIl2CppFixture.Load("/Fixture", fs).Oracle.Cases, Is.EqualTo(UnityIl2CppFixture.ExpectedCases));
	}
	[Test]
	public void ChangedNativeInputIsRejectedBeforeAnalysis()
	{
		var fs = Fixture(); fs.File.WriteAllText("/Fixture/" + UnityIl2CppFixture.NativePath, "changed");
		Assert.That(() => UnityIl2CppFixture.Load("/Fixture", fs), Throws.TypeOf<InvalidDataException>().With.Message.Contains("hash mismatch"));
	}
	[TestCase("backend")]
	[TestCase("value")]
	[TestCase("input")]
	[TestCase("missing")]
	public void SelfConsistentHashesCannotHideInvalidRuntimeOracle(string mutation)
	{
		var fs = Fixture(); string path = "/Fixture/il2cpp-runtime.json";
		var actual = JsonSerializer.Deserialize<MethodRuntimeReport>(fs.File.ReadAllText(path))!;
		var cases = actual.Cases.ToArray();
		if (mutation == "backend") actual = actual with { Backend = "Mono" };
		if (mutation == "value") cases[0] = cases[0] with { Value = 0 };
		if (mutation == "input") cases[0] = cases[0] with { A = 0 };
		actual = actual with { Cases = mutation == "missing" ? cases[..^1] : cases };
		fs.File.WriteAllText(path, JsonSerializer.Serialize(actual)); WriteManifest(fs);
		Assert.That(() => UnityIl2CppFixture.Load("/Fixture", fs), Throws.TypeOf<InvalidDataException>().With.Message.Contains("runtime observations"));
	}
	[Test]
	public void ManifestCannotEscapeFixtureDirectory()
	{
		var fs = Fixture(); string path = "/Fixture/fixture-manifest.json";
		var manifest = JsonSerializer.Deserialize<EditorFixtureManifest>(fs.File.ReadAllText(path))!;
		manifest.Files[0] = manifest.Files[0] with { Path = "../outside" };
		fs.File.WriteAllText(path, JsonSerializer.Serialize(manifest));
		Assert.That(() => UnityIl2CppFixture.Load("/Fixture", fs), Throws.TypeOf<InvalidDataException>().With.Message.Contains("traversal"));
	}
	private static VirtualFileSystem Fixture()
	{
		VirtualFileSystem fs = new();
		foreach (string path in new[] { UnityIl2CppFixture.NativePath, UnityIl2CppFixture.MetadataPath, UnityIl2CppFixture.MonoAssemblyPath,
			"Sources/Corpus.cs", "Sources/Il2CppFixtureBuilder.cs", "Sources/link.xml", "Sources/Methods.unity" })
		{
			string full = "/Fixture/" + path; fs.Directory.Create(fs.Path.GetDirectoryName(full)!); fs.File.WriteAllText(full, "Controlled integrity input, not a runnable image");
		}
		foreach (var pair in new[] { ("oracle.json", "EditorManaged"), ("mono-runtime.json", "Mono"), ("il2cpp-runtime.json", "IL2CPP") })
			fs.File.WriteAllText("/Fixture/" + pair.Item1, JsonSerializer.Serialize(new MethodRuntimeReport(1, "6000.4.0f1", pair.Item2, UnityIl2CppFixture.ExpectedCases)));
		WriteManifest(fs); return fs;
	}
	private static void WriteManifest(VirtualFileSystem fs)
	{
		var files = fs.Directory.EnumerateFiles("/Fixture", "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("fixture-manifest.json", StringComparison.Ordinal))
			.Select(p => new EditorFixtureFile(p["/Fixture/".Length..], FixtureCompiler.Hash(fs.File.ReadAllBytes(p)))).ToArray();
		fs.File.WriteAllText("/Fixture/fixture-manifest.json", JsonSerializer.Serialize(new EditorFixtureManifest(1, "6000.4.0f1", "StandaloneLinux64", files)));
	}
}
