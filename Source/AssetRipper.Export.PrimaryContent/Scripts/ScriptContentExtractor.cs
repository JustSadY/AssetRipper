using AssetRipper.Assets;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Export.Scripts;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using ICSharpCode.Decompiler.CSharp;

namespace AssetRipper.Export.PrimaryContent.Scripts;

public sealed class ScriptContentExtractor : IContentExtractor
{
	public IAssemblyManager AssemblyManager { get; }
	public LanguageVersion LanguageVersion => LanguageSelection.Effective;
	public ScriptLanguageSelection LanguageSelection { get; }
	private ScriptExportCollection? collection;
	public ScriptContentExtractor(IAssemblyManager assemblyManager, LanguageVersion languageVersion = LanguageVersion.Latest)
		: this(assemblyManager, ScriptLanguageSelection.Explicit(languageVersion))
	{
	}

	public ScriptContentExtractor(IAssemblyManager assemblyManager, ScriptLanguageSelection languageSelection)
	{
		AssemblyManager = assemblyManager;
		LanguageSelection = languageSelection;
	}

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out ExportCollectionBase? exportCollection)
	{
		if (asset is IMonoScript)
		{
			// All scripts share one assembly export, even when selection starts at a later MonoScript.
			exportCollection = collection ??= new ScriptExportCollection(this, (IMonoScript)asset);
			return true;
		}
		else
		{
			exportCollection = null;
			return false;
		}
	}
}
