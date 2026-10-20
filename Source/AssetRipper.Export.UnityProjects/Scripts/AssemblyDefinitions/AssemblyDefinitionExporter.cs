using AsmResolver.DotNet;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Structure.Assembly;
using System.Text.Json;

namespace AssetRipper.Export.UnityProjects.Scripts.AssemblyDefinitions;

public static class AssemblyDefinitionExporter
{
	public static void Export(AssemblyDefinitionDetails details, FileSystem fileSystem, Dictionary<string, UnityGuid> referenceAssemblies)
		=> ExportCore(details, fileSystem, referenceAssemblies, null);

	public static IReadOnlyList<UnityAssemblyReferenceFailure> Export(AssemblyDefinitionDetails details, FileSystem fileSystem,
		Dictionary<string, UnityGuid> referenceAssemblies, IReadOnlyDictionary<string, AssemblyExportType> emittedAssemblies)
		=> ExportCore(details, fileSystem, referenceAssemblies, emittedAssemblies);

	private static IReadOnlyList<UnityAssemblyReferenceFailure> ExportCore(AssemblyDefinitionDetails details, FileSystem fileSystem,
		Dictionary<string, UnityGuid> referenceAssemblies, IReadOnlyDictionary<string, AssemblyExportType>? emittedAssemblies)
	{
		List<UnityAssemblyReferenceFailure> failures = [];
		string assetPath = fileSystem.Path.Join(details.OutputFolder, $"{details.AssemblyName}.asmdef");

		AssemblyDefinitionAsset asset = new AssemblyDefinitionAsset(details.AssemblyName);
		ModuleDefinition? module = details.Assembly?.ManifestModule;
		if (module is not null)
		{
			foreach (AssemblyReference reference in module.AssemblyReferences)
			{
				if (reference.Name is null || referenceAssemblies.ContainsKey(reference.Name))
				{
					continue;
				}

				string name = reference.Name;
				if (emittedAssemblies is null)
				{
					asset.References.Add(name);
				}
				else if (emittedAssemblies.TryGetValue(name, out var kind) && kind == AssemblyExportType.Save)
				{
					asset.OverrideReferences = true;
					asset.PrecompiledReferences ??= [];
					asset.PrecompiledReferences.Add(SpecialFileNames.AddAssemblyFileExtension(name));
				}
				else if (ReferenceAssemblies.IsPredefinedAssembly(name))
				{
					failures.Add(new(details.AssemblyName, AssemblyIdentity.From(reference).ToString(), "PredefinedAssemblyCannotBeReferenced"));
				}
				else
				{
					// Preserve the unresolved relation in the artifact and report it. Do not invent a package or a DLL.
					asset.References.Add(name);
					if (!emittedAssemblies.TryGetValue(name, out kind) || kind != AssemblyExportType.Decompile)
						failures.Add(new(details.AssemblyName, AssemblyIdentity.From(reference).ToString(), "MissingAssemblyArtifact"));
				}
			}
		}

		string assetData = JsonSerializer.Serialize(asset, AssemblyDefinitionSerializerContext.Default.AssemblyDefinitionAsset);
		fileSystem.File.WriteAllText(assetPath, assetData);
		return failures;
	}
}
