using AssetRipper.Assets;
using System.Text.Json.Serialization;

namespace AssetRipper.Export.Configuration;

/// <summary>Exact, ordinal matches. Alternatives within a field are OR; different fields are AND.</summary>
public sealed record ExportSelectionSettings
{
	public int[] ClassIds { get; set; } = [];
	public string[] Names { get; set; } = [];
	public long[] PathIds { get; set; } = [];
	public string[] Collections { get; set; } = [];

	[JsonIgnore]
	public bool HasFilters => ClassIds.Length + Names.Length + PathIds.Length + Collections.Length > 0;

	/// <summary>Snapshots the filters; null means the existing full-export path.</summary>
	public Predicate<IUnityObjectBase>? CreatePredicate()
	{
		if (!HasFilters) return null;
		HashSet<int> classes = [.. ClassIds];
		HashSet<string> names = new(Names, StringComparer.Ordinal);
		HashSet<long> paths = [.. PathIds];
		HashSet<string> collections = new(Collections, StringComparer.Ordinal);
		return asset => (classes.Count == 0 || classes.Contains(asset.ClassID))
			&& (names.Count == 0 || names.Contains(asset.GetBestName()))
			&& (paths.Count == 0 || paths.Contains(asset.PathID))
			&& (collections.Count == 0 || collections.Contains(asset.Collection.Name));
	}
}
