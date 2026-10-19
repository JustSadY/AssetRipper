using AsmResolver.DotNet;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Processing;

namespace AssetRipper.Export.UnityProjects.Scripts;

public class DllPostExporter : IPostExporter
{
	public void DoPostExport(GameData gameData, FullConfiguration settings, FileSystem fileSystem)
	{
		Logger.Info(LogCategory.Export, "Saving game assemblies...");
		IAssemblyManager assemblyManager = gameData.AssemblyManager;
		AssemblyDefinition[] assemblies = assemblyManager.GetAssemblies().ToArray();
		AssemblyExportLayout layout = new(assemblyManager);
		if (assemblies.Length != 0)
		{
			string outputDirectory = fileSystem.Path.Join(settings.AuxiliaryFilesPath, "GameAssemblies");

			fileSystem.Directory.Create(outputDirectory);
			foreach (AssemblyDefinition assembly in assemblies)
			{
				string filepath = fileSystem.Path.Join(outputDirectory, layout.GetStem(assembly) + ".dll");
				assemblyManager.SaveAssembly(assembly, filepath, fileSystem);
			}
		}
	}
}
