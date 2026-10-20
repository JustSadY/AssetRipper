using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using AssetRipper.IO.Files.Exceptions;

namespace AssetRipper.Export.Scripts;

public partial class ILSpyWholeProjectDecompiler
{
	/// <summary>
	/// ILSpy already isolates files and method bodies. Retry only shared C# files which
	/// were replaced entirely by its error comment. Never replace existing partial source.
	/// Each original top-level type is attempted once, with its nested types intact.
	/// </summary>
	internal IReadOnlyList<ScriptFileRecoveryResult> RecoverFailedTypeFiles(MetadataFile module, string outputDirectory)
	{
		if (Errors.Count == 0) return [];
		var metadata = module.Metadata;
		var groups = metadata.GetTopLevelTypeDefinitions().Where(h => IncludeTypeWhenDecompilingProject(module, h))
			.GroupBy(h =>
			{
				var type = metadata.GetTypeDefinition(h);
				return GetFileNameForType(metadata.GetString(type.Namespace), metadata.GetString(type.Name), ".cs");
			}, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal);
		List<ScriptFileRecoveryResult> results = [];
		DecompilerTypeSystem? typeSystem = null;
		foreach (var group in groups)
		{
			string originalPath = FileSystem.Path.Join(outputDirectory, group.Key);
			if (!FileSystem.File.Exists(originalPath)) continue;
			string original = FileSystem.File.ReadAllText(originalPath);
			if (!IsErrorCommentOnly(original)) continue;
			// Resource-backed partial types require the companion-file/partial-member map,
			// which WholeProjectDecompiler does not expose. Preserve that output unchanged.
			if (module.Resources.Any(r => r.Name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase)))
			{
				results.Add(new(group.Key, null, null, [], "Resource-backed partial types require their original companion map."));
				continue;
			}

			List<ScriptTypeRecoveryResult> types = [];
			string? evidenceFile = null, evidenceHash = null;
			bool originalReplaced = false;
			foreach (TypeDefinitionHandle handle in group.OrderBy(h => metadata.GetTypeDefinition(h).GetGenericParameters().Count)
				.ThenBy(h => MetadataTokens.GetToken(h)))
			{
				var definition = metadata.GetTypeDefinition(handle);
				string ns = metadata.GetString(definition.Namespace);
				string name = metadata.GetString(definition.Name);
				string fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
				string token = $"0x{MetadataTokens.GetToken(handle):X8}";
				List<ScriptDecompilationDiagnostic> diagnostics = [];
				string? source = null;
				CSharpDecompiler? singleType = null;
				try
				{
					typeSystem ??= new DecompilerTypeSystem(module, AssemblyResolver, Settings);
					singleType = CreateDecompiler(typeSystem);
					var tree = singleType.DecompileTypes([handle]);
					using StringWriter writer = new();
					tree.AcceptVisitor(new CSharpOutputVisitor(writer, Settings.CSharpFormattingOptions));
					source = writer.ToString();
				}
				catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception))
				{
					diagnostics.Add(ScriptDecompilation.CreateDiagnostic(exception, "TypeRecovery"));
				}
				foreach (DecompilerException error in singleType?.Errors ?? [])
				{
					if (!ExceptionPolicy.IsRecoverable(error)) ExceptionDispatchInfo.Capture(error).Throw();
					diagnostics.Add(ScriptDecompilation.CreateDiagnostic(error, "TypeRecovery"));
				}
				if (source is null || diagnostics.Count != 0)
				{
					types.Add(new(fullName, token, ScriptTypeRecoveryStatus.Failed, null, diagnostics));
					continue;
				}

				// Save the exact failed output before replacing its canonical script path.
				// A .txt sidecar cannot be picked up by the SDK's default Compile glob.
				if (evidenceFile is null)
				{
					byte[] bytes = FileSystem.File.ReadAllBytes(originalPath);
					evidenceHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
					evidenceFile = SaveEvidence(outputDirectory, group.Key, bytes);
				}
				string relativePath = originalReplaced ? GetRecoveryPath(outputDirectory, group.Key, handle) : group.Key;
				try
				{
					using (TextWriter writer = CreateFile(FileSystem.Path.Join(outputDirectory, relativePath))) writer.Write(source);
					types.Add(new(fullName, token, ScriptTypeRecoveryStatus.Recovered, relativePath, []));
					originalReplaced = true;
				}
				catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception))
				{
					types.Add(new(fullName, token, ScriptTypeRecoveryStatus.Failed, null,
						[ScriptDecompilation.CreateDiagnostic(exception, "TypeRecoveryWrite")]));
				}
			}
			results.Add(new(group.Key, evidenceFile, evidenceHash, types));
		}
		return results;
	}

	private static bool IsErrorCommentOnly(string source) => source.AsSpan().TrimStart().StartsWith("// ILSpy could not decompile this.", StringComparison.Ordinal)
		&& source.Split('\n').All(line => string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart().StartsWith("//", StringComparison.Ordinal));

	private string GetRecoveryPath(string outputDirectory, string originalFile, TypeDefinitionHandle type)
	{
		string stem = FileSystem.Path.GetFileNameWithoutExtension(originalFile);
		// Keep space for the token, extension and ordinal under the decompiler's segment limit.
		if (stem.Length > 120) stem = stem[..120];
		string directory = FileSystem.Path.GetDirectoryName(originalFile) ?? "";
		string name = $"{stem}.Recovered-{MetadataTokens.GetToken(type):X8}";
		string path = Path.Join(directory, name + ".cs");
		for (int i = 1; OutputPathExists(FileSystem.Path.Join(outputDirectory, path)); i++)
			path = Path.Join(directory, $"{name}-{i}.cs");
		return path;
	}

	private string SaveEvidence(string outputDirectory, string originalFile, byte[] bytes)
	{
		string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(originalFile)));
		string directory = FileSystem.Path.Join(outputDirectory, "_DecompilationEvidence");
		// The base CreateDirectory override may remove a colliding file. Evidence
		// storage must preserve existing output and propagate this conflict instead.
		if (FileSystem.File.Exists(directory)) throw new IOException($"Evidence directory conflicts with an existing file: {directory}");
		FileSystem.Directory.Create(directory);
		string relative = Path.Join("_DecompilationEvidence", id + ".txt");
		for (int i = 1; OutputPathExists(FileSystem.Path.Join(outputDirectory, relative)); i++)
			relative = Path.Join("_DecompilationEvidence", $"{id}-{i}.txt");
		FileSystem.File.WriteAllBytes(FileSystem.Path.Join(outputDirectory, relative), bytes);
		return relative;
	}

	private bool OutputPathExists(string path) => FileSystem.File.Exists(path) || FileSystem.Directory.Exists(path);
}
