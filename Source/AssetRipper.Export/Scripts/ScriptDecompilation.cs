using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using System.Runtime.ExceptionServices;

namespace AssetRipper.Export.Scripts;

public static class ScriptDecompilation
{
	public static ScriptDecompilationResult Run(
		WholeProjectDecompiler decompiler,
		ILSpyAssemblyResolver resolver,
		AssemblyDefinition assembly,
		string outputDirectory,
		TextWriter? projectFileWriter = null)
	{
		List<ScriptDecompilationDiagnostic> diagnostics = [];
		int resolutionStart = resolver.Diagnostics.Count;
		try
		{
			if (projectFileWriter is null)
			{
				decompiler.DecompileProject(resolver.Resolve(assembly), outputDirectory);
			}
			else
			{
				decompiler.DecompileProject(resolver.Resolve(assembly), outputDirectory, projectFileWriter);
			}
		}
		catch (Exception exception) when (IsRecoverable(exception))
		{
			AddDiagnostic(exception);
		}

		foreach (DecompilerException exception in decompiler.Errors)
		{
			if (!IsRecoverable(exception))
			{
				ExceptionDispatchInfo.Capture(exception).Throw();
			}
			AddDiagnostic(exception);
		}

		foreach (AssemblyResolutionDiagnostic diagnostic in resolver.Diagnostics.Skip(resolutionStart).Where(d => d.IsFailure))
		{
			diagnostics.Add(new(null, diagnostic.RequestedIdentity, $"Assembly resolution failed: {diagnostic.Kind}"));
		}
		return new(assembly.Name ?? string.Empty, outputDirectory, diagnostics) { Identity = AssemblyIdentity.From(assembly).ToString() };

		void AddDiagnostic(Exception exception)
		{
			DecompilerException? decompilerException = exception as DecompilerException;
			diagnostics.Add(new(decompilerException?.FileName, decompilerException?.DecompiledEntity?.FullName, exception.ToString()));
			Logger.Error(LogCategory.Export, $"Failed to decompile '{assembly.Name}'.", exception);
		}
	}

	private static bool IsRecoverable(Exception exception)
	{
		return exception switch
		{
			OperationCanceledException or OutOfMemoryException or IOException or UnauthorizedAccessException => false,
			AggregateException aggregate => aggregate.InnerExceptions.All(IsRecoverable),
			_ => exception.InnerException is null || IsRecoverable(exception.InnerException),
		};
	}
}
