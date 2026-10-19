using AsmResolver.DotNet;

namespace AssetRipper.Import.Structure.Assembly;

/// <summary>Normalized CLR identity; physical candidates with the same identity remain distinct.</summary>
public sealed record AssemblyIdentity
{
	public string Name { get; }
	public Version Version { get; }
	public string Culture { get; }
	public string PublicKeyToken { get; }

	public AssemblyIdentity(string name, Version? version, string? culture, byte[]? publicKeyToken)
	{
		Name = name.ToLowerInvariant();
		Version = version ?? new(0, 0, 0, 0);
		Culture = string.IsNullOrEmpty(culture) || culture.Equals("neutral", StringComparison.OrdinalIgnoreCase) ? "" : culture.ToLowerInvariant();
		PublicKeyToken = publicKeyToken is null ? "" : Convert.ToHexStringLower(publicKeyToken);
	}

	public static AssemblyIdentity From(AssemblyDescriptor assembly) => new(assembly.Name ?? "", assembly.Version, assembly.Culture, assembly.GetPublicKeyToken());
	public override string ToString() => $"{Name}, Version={Version}, Culture={(Culture.Length == 0 ? "neutral" : Culture)}, PublicKeyToken={(PublicKeyToken.Length == 0 ? "null" : PublicKeyToken)}";
}

public sealed record AssemblySourceInfo(string? SourcePath, string? OriginalSha256);

public sealed record AssemblyCandidateInfo(string Identity, string? SourcePath, string? OriginalSha256, string? ModuleMvid);

public sealed record AssemblyResolutionDiagnostic(string Kind, string RequestedIdentity, bool IsFailure, IReadOnlyList<AssemblyCandidateInfo> Candidates);
