using AsmResolver.DotNet;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_115;

namespace AssetRipper.Tests.Decompilation;

/// <summary>Exports only the trusted fixture; reference metadata stays available to ILSpy without exporting the BCL.</summary>
internal sealed class ScriptExportFixture : BaseManager
{
	private readonly AssemblyDefinition fixture;
	public override ScriptingBackend ScriptingBackend => ScriptingBackend.Mono;
	public override IEnumerable<AssemblyDefinition> GetAssemblies() => fixture is null ? [] : [fixture];

	public ScriptExportFixture(byte[] assembly, IEnumerable<(string File, byte[] Bytes)> references) : base(_ => { })
	{
		foreach (var reference in references) Read(new MemoryStream(reference.Bytes, writable: false), reference.File);
		fixture = Read(new MemoryStream(assembly, writable: false), "ManagedRoundTrip.dll");
	}

	public (VirtualFileSystem FileSystem, string ScriptDirectory, string ReportPath, bool Succeeded) Export(
		bool primary, UnityVersion version, ScriptLanguageVersion language, string typeName, string @namespace)
	{
		GameBundle bundle = new();
		var assets = bundle.AddNewProcessedCollection("LanguageFixture", version);
		IMonoScript script = assets.CreateAsset(115, info => MonoScript.Create(info));
		script.ClassName_R = typeName;
		script.Namespace = @namespace;
		script.AssemblyName = "ManagedRoundTrip";
		GameData data = new(bundle, version, this, null);
		FullConfiguration settings = new() { ExportRootPath = "/output" };
		// Deliberately stale: primary-content must use the GameData version, just as ExportHandler does.
		settings.SetProjectSettings(new(2017, 1));
		settings.ExportSettings.ScriptLanguageVersion = language;
		settings.ExportSettings.ScriptExportMode = ScriptExportMode.Decompiled;
		VirtualFileSystem fs = new();
		if (primary)
		{
			PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(data, settings);
			exporter.Export(bundle, settings, fs);
			return (fs, "/output/Scripts/ManagedRoundTrip", "/output/script-export-report.json", exporter.LastExportSucceeded == true);
		}
		else
		{
			ExportHandler exporter = new(settings);
			exporter.Export(data, "/output", fs);
			return (fs, "/output/ExportedProject/Assets/Scripts/ManagedRoundTrip", "/output/ExportedProject/script-export-report.json", exporter.LastExportSucceeded == true);
		}
	}

	public static Dictionary<string, string> ReadSources(VirtualFileSystem fs, string directory) => fs.Directory
		.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
		.ToDictionary(p => p[(directory.Length + 1)..], fs.File.ReadAllText, StringComparer.Ordinal);
}
