using AsmResolver.DotNet;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using System.Security.Cryptography;
using System.Text;

namespace AssetRipper.Export.Scripts;

/// <summary>Distinct output paths for physical candidates; CLR identities are never renamed.</summary>
public sealed class AssemblyExportLayout
{
	private readonly Dictionary<AssemblyDefinition, string> stems = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<AssemblyDefinition> conflicts = new(ReferenceEqualityComparer.Instance);
	public bool IsConflicting(AssemblyDefinition assembly) => conflicts.Contains(assembly);
	public string GetStem(AssemblyDefinition assembly) => stems[assembly];

	public AssemblyExportLayout(IAssemblyManager manager)
	{
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		var groups = manager.GetAssemblies().GroupBy(a => SafeName(a.Name ?? "Assembly"), StringComparer.OrdinalIgnoreCase)
			.OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
		foreach (var group in groups.Where(g => g.Count() == 1))
		{
			used.Add(group.Key);
			stems.Add(group.Single(), group.Key);
		}
		foreach (var group in groups.Where(g => g.Count() > 1))
		{
			string baseName = group.Select(a => SafeName(a.Name ?? "Assembly")).Order(StringComparer.Ordinal).First();
			Dictionary<AssemblyDefinition, string> hashes = new(ReferenceEqualityComparer.Instance);
			foreach (AssemblyDefinition assembly in group)
			{
				string? hash = manager.GetSourceInfo(assembly)?.OriginalSha256;
				if (hash is null)
				{
					Stream stream = manager.GetStreamForAssembly(assembly);
					stream.Position = 0;
					hash = Convert.ToHexStringLower(SHA256.HashData(stream));
					stream.Position = 0;
				}
				hashes.Add(assembly, hash);
			}
			foreach (AssemblyDefinition assembly in group.OrderBy(a => AssemblyIdentity.From(a).ToString(), StringComparer.Ordinal)
				.ThenBy(a => hashes[a], StringComparer.Ordinal)
				.ThenBy(a => a.ManifestModule?.Mvid).ThenBy(a => manager.GetSourceInfo(a)?.SourcePath, StringComparer.Ordinal))
			{
				string key = AssemblyIdentity.From(assembly) + "|" + assembly.ManifestModule?.Mvid + "|" + hashes[assembly];
				string stem = baseName + "__" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
				string unique = stem;
				for (int ordinal = 2; !used.Add(unique); ordinal++) unique = stem + "_" + ordinal;
				stems.Add(assembly, unique);
				conflicts.Add(assembly);
			}
		}
	}

	private static string SafeName(string name)
	{
		string safe = FileSystem.FixInvalidFileNameCharacters(name).Trim();
		return safe is "" or "." or ".." ? "Assembly" : safe;
	}
}
