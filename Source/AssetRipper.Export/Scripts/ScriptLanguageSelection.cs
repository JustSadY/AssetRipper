using AssetRipper.Export.Configuration;
using AssetRipper.Primitives;
using ICSharpCode.Decompiler.CSharp;

namespace AssetRipper.Export.Scripts;

/// <summary>Records language selection only; it does not attest to API references or Unity runtime compatibility.</summary>
public sealed record ScriptLanguageSelection(string? UnityVersion, ScriptLanguageVersion Requested, LanguageVersion Effective)
{
	public bool IsAutomatic => Requested is ScriptLanguageVersion.AutoSafe or ScriptLanguageVersion.AutoExperimental;
	public bool UnityVersionUnknown => UnityVersion is null;

	public static ScriptLanguageSelection ForUnity(ScriptLanguageVersion requested, UnityVersion version) =>
		new(version == default ? null : version.ToString(), requested, requested.ToCSharpLanguageVersion(version));

	public static ScriptLanguageSelection Explicit(LanguageVersion version) => new(null, (ScriptLanguageVersion)version, version);
}
