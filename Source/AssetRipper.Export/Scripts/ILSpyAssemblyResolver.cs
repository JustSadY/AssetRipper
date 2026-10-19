using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using ICSharpCode.Decompiler.Metadata;
using IAssemblyResolver = ICSharpCode.Decompiler.Metadata.IAssemblyResolver;

namespace AssetRipper.Export.Scripts;

public sealed class ILSpyAssemblyResolver(IAssemblyManager manager) : IAssemblyResolver
{
	private readonly Dictionary<AssemblyDefinition, PEFile> peAssemblies = new(ReferenceEqualityComparer.Instance);
	private readonly List<AssemblyResolutionDiagnostic> diagnostics = [];
	public IReadOnlyList<AssemblyResolutionDiagnostic> Diagnostics { get { lock (peAssemblies) return diagnostics.ToArray(); } }

	public MetadataFile? Resolve(IAssemblyReference reference)
	{
		lock (peAssemblies)
		{
			AssemblyIdentity identity = new(reference.Name, reference.Version, reference.Culture, reference.PublicKeyToken);
			// ILSpy 11 injects these AssemblyNameReference probes without a public key token.
			// They are optional, not references read from the PE metadata. Do not invent a
			// publisher match or report a signed candidate as a failed required dependency.
			if (IsImplicitFrameworkProbe(reference) && !manager.GetAssemblyCandidates(identity.Name).Any(a => AssemblyIdentity.From(a) == identity))
			{
				Report(new("ImplicitFrameworkProbeUnresolved", identity.ToString(), false,
					AssemblyResolution.Describe(manager, manager.GetAssemblyCandidates(identity.Name))));
				return null;
			}
			AssemblyDefinition? assembly = AssemblyResolution.Resolve(manager, identity, true, Report);
			return assembly is null ? null : Resolve(assembly);
		}
	}

	/// <summary>Resolve this exact physical candidate, even if its CLR identity is duplicated.</summary>
	public PEFile Resolve(AssemblyDefinition assembly)
	{
		lock (peAssemblies)
		{
			if (!peAssemblies.TryGetValue(assembly, out PEFile? result))
			{
				Stream stream = manager.GetStreamForAssembly(assembly);
				stream.Position = 0;
				result = new PEFile(assembly.Name ?? "", stream);
				peAssemblies.Add(assembly, result);
			}
			return result;
		}
	}

	public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

	private static bool IsImplicitFrameworkProbe(IAssemblyReference reference) => reference is AssemblyNameReference
		&& reference.Name is "System.Runtime.InteropServices" or "System.Runtime.CompilerServices.Unsafe"
		&& string.IsNullOrEmpty(reference.Culture) && (reference.PublicKeyToken is null || reference.PublicKeyToken.Length == 0);

	public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
	{
		lock (peAssemblies)
		{
			AssemblyDefinition? owner = peAssemblies.FirstOrDefault(p => ReferenceEquals(p.Value, mainModule)).Key;
			if (owner?.ManifestModule?.Name == moduleName) return Resolve(owner);
			// Assembly streams contain the manifest module, not arbitrary netmodule payloads.
			AssemblyDefinition[] matches = manager.GetAssemblies().Where(a => a.ManifestModule?.Name == moduleName).ToArray();
			if (owner is null && matches.Length == 1) return Resolve(matches[0]);
			Report(new(owner is not null ? "UnsupportedNetModule" : matches.Length > 1 ? "AmbiguousModule" : "MissingModule",
				moduleName, owner is not null || matches.Length > 1, AssemblyResolution.Describe(manager, owner is null ? matches : [owner])));
			return null;
		}
	}

	public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

	private void Report(AssemblyResolutionDiagnostic diagnostic)
	{
		if (diagnostics.Any(d => d.Kind == diagnostic.Kind && d.RequestedIdentity == diagnostic.RequestedIdentity && d.Candidates.SequenceEqual(diagnostic.Candidates))) return;
		diagnostics.Add(diagnostic);
		Logger.Warning(LogCategory.Export, $"Assembly resolution {diagnostic.Kind}: {diagnostic.RequestedIdentity}");
	}

	IDisposable? IAssemblyResolver.BeginSnapshot() => null;
}
