using AssetRipper.IO.Files;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;

namespace AssetRipper.Export.Scripts;

public partial class ILSpyWholeProjectDecompiler : WholeProjectDecompiler
{
	protected FileSystem FileSystem { get; }
	private readonly object fileSystemSync = new();

	public ILSpyWholeProjectDecompiler(DecompilerSettings settings, ILSpyAssemblyResolver assemblyResolver, IProjectFileWriter projectFileWriter, FileSystem fileSystem)
		: base(settings, Guid.NewGuid(), assemblyResolver, projectFileWriter, NullAssemblyReferenceClassifier.Instance, null)
	{
		FileSystem = fileSystem;
	}

	protected override void CreateDirectory(string path)
	{
		lock (fileSystemSync)
		{
			try
			{
				FileSystem.Directory.Create(path);
			}
			catch (IOException)
			{
				FileSystem.File.Delete(path);
				FileSystem.Directory.Create(path);
			}
		}
	}

	protected override TextWriter CreateFile(string path)
	{
		lock (fileSystemSync)
		{
			Stream stream = FileSystem.File.Create(path);
			return new StreamWriter(stream);
		}
	}
}
