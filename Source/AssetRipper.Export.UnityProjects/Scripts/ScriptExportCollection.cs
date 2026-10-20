using AsmResolver.DotNet;
using AssetRipper.Assets;
using AssetRipper.Export.Scripts;
using AssetRipper.Export.UnityProjects.Scripts.AssemblyDefinitions;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.SourceGenerated.Classes.ClassID_1050;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.PlatformSettingsData_Plugin;
using System.Diagnostics;

namespace AssetRipper.Export.UnityProjects.Scripts;
public sealed class ScriptExportCollection : ScriptExportCollectionBase
{
	public ScriptExportReport? LastReport { get; private set; }

	public ScriptExportCollection(ScriptExporter assetExporter, IMonoScript firstScript) : base(assetExporter, firstScript)
	{
		Debug.Assert(assetExporter.AssemblyManager.IsSet);

		// find copies in whole project and skip them
		Dictionary<MonoScriptInfo, IMonoScript> uniqueDictionary = new();
		foreach (IMonoScript assetScript in firstScript.Collection.Bundle.FetchAssetsInHierarchy().OfType<IMonoScript>())
		{
			MonoScriptInfo info = MonoScriptInfo.From(assetScript);
			if (uniqueDictionary.TryGetValue(info, out IMonoScript? uniqueScript))
			{
				m_scripts.Add(assetScript, uniqueScript);
			}
			else
			{
				m_scripts.Add(assetScript, assetScript);
				uniqueDictionary.Add(info, assetScript);
				if (ShouldExport(assetScript))
				{
					m_export.Add(assetScript);
				}
			}
		}
	}

	private bool ShouldExport(IMonoScript script)
	{
		return AssetExporter.GetExportType(script) is AssemblyExportType.Decompile;
	}

	public override bool Export(IExportContainer container, string projectDirectory, FileSystem fileSystem)
	{
		Logger.Info(LogCategory.Export, "Exporting scripts...");
		ScriptExportReport report = new()
		{
			ScriptingBackend = AssetExporter.AssemblyManager.ScriptingBackend,
			Il2CppRecovery = AssetExporter.AssemblyManager.Il2CppRecovery,
			ScriptContentLevel = AssetExporter.Decompiler.ScriptContentLevel,
			LanguageSelection = AssetExporter.Decompiler.LanguageSelection,
		};
		LastReport = report;
		report.ResolutionDiagnostics.AddRange(AssetExporter.AssemblyManager.ResolutionDiagnostics);
		AssemblyExportLayout layout = new(AssetExporter.AssemblyManager);

		string assetsDirectoryPath = fileSystem.Path.Join(projectDirectory, AssetsKeyword);

		Dictionary<string, AssemblyDefinitionDetails> assemblyDefinitionDetailsDictionary = new();
		Dictionary<string, AssemblyExportType> emittedAssemblies = new(StringComparer.Ordinal);

		string pluginsFolder = fileSystem.Path.Join(assetsDirectoryPath, "Plugins");

		foreach (AssemblyDefinition assembly in AssetExporter.AssemblyManager.GetAssemblies())
		{
			string assemblyName = assembly.Name!;
			// Unity cannot bind versionless MonoScript names to multiple candidates safely.
			// All DLL candidates are preserved separately by DllPostExporter.
			if (layout.IsConflicting(assembly)) continue;
			AssemblyExportType exportType = AssetExporter.GetExportType(assemblyName);

			if (exportType is AssemblyExportType.Decompile)
			{
				Logger.Info(LogCategory.Export, $"Decompiling {assemblyName}");
				string outputDirectory = fileSystem.Path.Join(assetsDirectoryPath, GetScriptsFolderName(assemblyName), assemblyName);
				fileSystem.Directory.Create(outputDirectory);
				report.Assemblies.Add(AssetExporter.Decompiler.DecompileWholeProject(assembly, outputDirectory, fileSystem));

				assemblyDefinitionDetailsDictionary.TryAdd(assemblyName, new AssemblyDefinitionDetails(assembly, outputDirectory));
				emittedAssemblies.Add(assemblyName, AssemblyExportType.Decompile);
			}
			else if (exportType is AssemblyExportType.Save)
			{
				Logger.Info(LogCategory.Export, $"Saving {assemblyName}");
				fileSystem.Directory.Create(pluginsFolder);
				string outputPath = fileSystem.Path.Join(pluginsFolder, SpecialFileNames.AddAssemblyFileExtension(assemblyName));
				AssetExporter.AssemblyManager.SaveAssembly(assembly, outputPath, fileSystem);
				OnAssemblyExported(container, outputPath, fileSystem);
				emittedAssemblies.Add(assemblyName, AssemblyExportType.Save);
			}
		}

		foreach (IMonoScript asset in m_export)
		{
			GetExportSubPath(asset, out string subFolderPath, out string fileName);
			string folderPath = fileSystem.Path.Join(assetsDirectoryPath, subFolderPath);
			string filePath = fileSystem.Path.Join(folderPath, fileName);
			if (!fileSystem.File.Exists(filePath))
			{
				fileSystem.Directory.Create(folderPath);
				fileSystem.File.WriteAllText(filePath, EmptyScript.GetContent(asset));
				report.PlaceholderScripts.Add(filePath);
				Logger.Warning(LogCategory.Export, $"Created placeholder script '{filePath}'.");
				string assemblyName = asset.GetAssemblyNameFixed();
				if (!assemblyDefinitionDetailsDictionary.ContainsKey(assemblyName))
				{
					string assemblyDirectoryPath = fileSystem.Path.Join(assetsDirectoryPath, GetScriptsFolderName(assemblyName), assemblyName);
					AssemblyDefinitionDetails details = new AssemblyDefinitionDetails(assemblyName, assemblyDirectoryPath);
					assemblyDefinitionDetailsDictionary.Add(assemblyName, details);
					emittedAssemblies.TryAdd(assemblyName, AssemblyExportType.Decompile);
				}
			}

			if (fileSystem.File.Exists($"{filePath}.meta"))
			{
				Logger.Error(LogCategory.Export, $"Metafile already exists at {filePath}.meta");
				//throw new Exception($"Metafile already exists at {filePath}.meta");
			}
			else
			{
				OnScriptExported(container, asset, filePath, fileSystem);
			}
		}

		// assembly definitions were added in 2017.3
		//     see: https://blog.unity.com/technology/unity-2017-3b-feature-preview-assembly-definition-files-and-transform-tool
		if (assemblyDefinitionDetailsDictionary.Count > 0 && container.ExportVersion.GreaterThanOrEquals(2017, 3))
		{
			foreach (AssemblyDefinitionDetails details in assemblyDefinitionDetailsDictionary.Values)
			{
				// exclude predefined assemblies like Assembly-CSharp.dll
				//    see: https://docs.unity3d.com/2017.3/Documentation/Manual/ScriptCompilationAssemblyDefinitionFiles.html
				if (!ReferenceAssemblies.IsPredefinedAssembly(details.AssemblyName))
				{
					report.UnityAssemblyReferenceFailures.AddRange(AssemblyDefinitionExporter.Export(details, fileSystem,
						AssetExporter.ReferenceAssemblyDictionary, emittedAssemblies));
				}
			}
		}

		report.ResolutionDiagnostics.AddRange(AssetExporter.Decompiler.ResolutionDiagnostics);
		report.Write(projectDirectory, fileSystem);
		return report.Succeeded;
	}

	private void OnAssemblyExported(IExportContainer container, string path, FileSystem fileSystem)
	{
		UnityGuid guid = ScriptHashing.CalculateAssemblyGuid(Path.GetFileName(path));
		IPluginImporter importer = PluginImporter.Create(Assets.First().Collection, container.ExportVersion);
		if (importer.HasPlatformData())
		{
			PlatformSettingsData_Plugin anyPlatformSettings = importer.AddPlatformSettings("Any", Utf8String.Empty);
			anyPlatformSettings.Enabled = true;

			PlatformSettingsData_Plugin editorPlatformSettings = importer.AddPlatformSettings("Editor", "Editor");
			editorPlatformSettings.Enabled = false;
			editorPlatformSettings.Settings.Add("DefaultValueInitialized", "true");
		}

		Meta meta = new Meta(guid, importer);
		ExportMeta(container, meta, path, fileSystem);

	}

	public override string Name => nameof(ScriptExportCollection);

	private readonly List<IMonoScript> m_export = new();
	private readonly Dictionary<IUnityObjectBase, IMonoScript> m_scripts = new();
}
