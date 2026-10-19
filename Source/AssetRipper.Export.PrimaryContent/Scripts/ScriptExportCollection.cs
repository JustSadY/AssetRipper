using AsmResolver.DotNet;
using AssetRipper.Assets;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using System.Xml;

namespace AssetRipper.Export.PrimaryContent.Scripts;

public sealed class ScriptExportCollection : ExportCollectionBase
{
	public ScriptExportReport? LastReport { get; private set; }

	public ScriptExportCollection(ScriptContentExtractor contentExtractor)
	{
		ContentExtractor = contentExtractor;
		LanguageSelection = contentExtractor.LanguageSelection;
	}

	public ScriptExportCollection(ScriptContentExtractor contentExtractor, LanguageVersion languageVersion) : this(contentExtractor)
	{
		if (languageVersion != LanguageVersion) LanguageSelection = ScriptLanguageSelection.Explicit(languageVersion);
	}

	public override ScriptContentExtractor ContentExtractor { get; }

	public LanguageVersion LanguageVersion => LanguageSelection.Effective;
	public ScriptLanguageSelection LanguageSelection { get; }

	public override IEnumerable<IUnityObjectBase> Assets => [];

	public override string Name => nameof(ScriptExportCollection);

	public override bool Contains(IUnityObjectBase asset) => asset is IMonoScript;

	public override bool Export(string projectDirectory, FileSystem fileSystem)
	{
		IAssemblyManager assemblyManager = ContentExtractor.AssemblyManager;
		ScriptExportReport report = new()
		{
			ScriptingBackend = assemblyManager.ScriptingBackend,
			LanguageSelection = LanguageSelection,
			ProjectScaffoldFramework = ProjectFileWriter.ScaffoldFramework,
		};
		LastReport = report;
		report.ResolutionDiagnostics.AddRange(assemblyManager.ResolutionDiagnostics);
		ILSpyAssemblyResolver assemblyResolver = new(assemblyManager);
		AssemblyExportLayout layout = new(assemblyManager);

		string assemblyDirectory = fileSystem.Path.Join(projectDirectory, "Assemblies");
		fileSystem.Directory.Create(assemblyDirectory);

		//Export assemblies
		foreach (AssemblyDefinition assembly in assemblyManager.GetAssemblies())
		{
			Stream stream = assemblyManager.GetStreamForAssembly(assembly);
			stream.Position = 0;

			//Write assembly
			{
				string assemblyPath = fileSystem.Path.Join(assemblyDirectory, layout.GetStem(assembly) + ".dll");
				using Stream fileStream = fileSystem.File.Create(assemblyPath);
				stream.CopyTo(fileStream);
				stream.Position = 0;
			}
		}

		//Decompile scripts
		string scriptDirectory = fileSystem.Path.Join(projectDirectory, "Scripts");
		List<string> projectPaths = [];
		foreach (AssemblyDefinition assembly in assemblyManager.GetAssemblies())
		{
			string assemblyName = assembly.Name ?? throw new InvalidOperationException("Assembly name is null");
			Logger.Info(LogCategory.Export, $"Decompiling assembly {assemblyName}...");
			string outputDirectory = fileSystem.Path.Join(scriptDirectory, layout.GetStem(assembly));
			fileSystem.Directory.Create(outputDirectory);

			DecompilerSettings settings = new();

			settings.SetLanguageVersion(LanguageVersion);

			settings.AlwaysShowEnumMemberValues = true;
			settings.ShowXmlDocumentation = true;

			settings.UseNestedDirectoriesForNamespaces = true;

			if (assemblyName is "mscorlib")
			{
				// Disable tuple types (the "(int, string)" syntax) for mscorlib to avoid compilation issues.
				// System.Private.CoreLib doesn't seem to use tuple types, and trying to use them in mscorlib causes errors.
				settings.TupleTypes = false;
			}

			ILSpyWholeProjectDecompiler decompiler = new(settings, assemblyResolver, ProjectFileWriter.Instance, fileSystem) { LanguageVersion = LanguageVersion };
			ScriptDecompilationResult result = ScriptDecompilation.Run(decompiler, assemblyResolver, assembly, outputDirectory);
			report.Assemblies.Add(result);
			if (fileSystem.File.Exists(fileSystem.Path.Join(outputDirectory, assemblyName + ".csproj")))
			{
				projectPaths.Add(fileSystem.Path.Join(layout.GetStem(assembly), assemblyName + ".csproj"));
			}
		}

		// Write solution file
		if (projectPaths.Count > 0)
		{
			projectPaths.Sort(StringComparer.Ordinal);

			string solutionPath = fileSystem.Path.Join(scriptDirectory, "Scripts.slnx");
			using Stream stream = fileSystem.File.Create(solutionPath);
			using StreamWriter streamWriter = new(stream)
			{
				NewLine = "\n",
				AutoFlush = true
			};
			using XmlTextWriter xmlWriter = new(streamWriter)
			{
				Formatting = Formatting.Indented
			};

			xmlWriter.WriteStartElement("Solution");

			foreach (string projectPath in projectPaths)
			{
				xmlWriter.WriteStartElement("Project");
				xmlWriter.WriteAttributeString("Path", projectPath);
				xmlWriter.WriteEndElement();
			}

			xmlWriter.WriteEndElement();
		}

		report.ResolutionDiagnostics.AddRange(assemblyResolver.Diagnostics);
		report.Write(projectDirectory, fileSystem);
		return report.Succeeded;
	}
}
