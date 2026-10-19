using AsmResolver.DotNet;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace AssetRipper.Import.Structure.Assembly;

public static class AssemblyResolution
{
	public static AssemblyDefinition? Resolve(IAssemblyManager manager, AssemblyIdentity identity, bool allowVersionFallback,
		Action<AssemblyResolutionDiagnostic> report)
	{
		AssemblyDefinition[] named = manager.GetAssemblyCandidates(identity.Name).ToArray();
		AssemblyDefinition[] exact = named.Where(a => AssemblyIdentity.From(a) == identity).ToArray();
		if (exact.Length == 1) return exact[0];
		if (exact.Length > 1)
		{
			report(new("AmbiguousIdentity", identity.ToString(), true, Describe(manager, exact)));
			return null;
		}
		AssemblyDefinition[] compatible = named.Where(a =>
		{
			AssemblyIdentity candidate = AssemblyIdentity.From(a);
			return candidate.Culture == identity.Culture && candidate.PublicKeyToken == identity.PublicKeyToken;
		}).ToArray();
		if (allowVersionFallback && compatible.Length == 1)
		{
			report(new("UniqueVersionFallback", identity.ToString(), false, Describe(manager, compatible)));
			return compatible[0];
		}
		report(new(named.Length == 0 ? "Missing" : compatible.Length > 1 ? "AmbiguousVersion" : "IdentityMismatch",
			identity.ToString(), named.Length > 0, Describe(manager, named)));
		return null;
	}

	public static AssemblyCandidateInfo[] Describe(IAssemblyManager manager, IEnumerable<AssemblyDefinition> assemblies) => assemblies
		.Select(a => new AssemblyCandidateInfo(AssemblyIdentity.From(a).ToString(), manager.GetSourceInfo(a)?.SourcePath,
			manager.GetSourceInfo(a)?.OriginalSha256, a.ManifestModule?.Mvid.ToString()))
		.OrderBy(a => a.Identity, StringComparer.Ordinal).ThenBy(a => a.SourcePath, StringComparer.Ordinal)
		.ThenBy(a => a.OriginalSha256, StringComparer.Ordinal).ThenBy(a => a.ModuleMvid, StringComparer.Ordinal).ToArray();
}
