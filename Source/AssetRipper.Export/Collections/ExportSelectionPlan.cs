using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Files.Exceptions;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_196;

namespace AssetRipper.Export.Collections;

/// <summary>Membership must have been materialized and validated by the exporter exactly once.</summary>
public sealed record ExportSelectionUnit(object Owner, IUnityObjectBase Seed,
	IReadOnlyList<IUnityObjectBase> Members, string Name, bool Exportable);

/// <summary>Promotes roots and dependencies to whole collections. Identities and order are local to this export.</summary>
public sealed class ExportSelectionPlan
{
	public IReadOnlySet<IUnityObjectBase> IncludedAssets { get; }
	public IReadOnlyList<int> CollectionIndices { get; }
	public ExportSelectionReport Report { get; }

	private ExportSelectionPlan(HashSet<IUnityObjectBase> included, int[] indices, ExportSelectionReport report)
	{
		IncludedAssets = included;
		CollectionIndices = indices;
		Report = report;
	}

	public static ExportSelectionPlan Create(IReadOnlyList<IUnityObjectBase> inventory,
		IReadOnlyList<ExportSelectionUnit> units, Predicate<IUnityObjectBase> predicate)
	{
		ExportSelectionReport report = new();
		Dictionary<IUnityObjectBase, SelectionAssetIdentity> identities = new(ReferenceEqualityComparer.Instance);
		SelectionAssetIdentity Identity(IUnityObjectBase asset)
		{
			if (!identities.TryGetValue(asset, out SelectionAssetIdentity? identity))
			{
				identity = new(identities.Count, asset.Collection.Name, asset.Collection.FilePath,
					asset.PathID, asset.ClassID, asset.GetBestName());
				identities.Add(asset, identity);
			}
			return identity;
		}
		foreach (IUnityObjectBase asset in inventory) Identity(asset);

		// Some exporters reuse one owner for several seeds without listing members (notably script export).
		// Empty redirect/unsupported collections are singletons too, but are not one atomic export group.
		Dictionary<object, int> canonicalOwners = new(ReferenceEqualityComparer.Instance);
		Dictionary<IUnityObjectBase, int> owners = new(ReferenceEqualityComparer.Instance);
		Dictionary<int, HashSet<IUnityObjectBase>> members = [];
		for (int i = 0; i < units.Count; i++)
		{
			ExportSelectionUnit unit = units[i];
			int canonical = i;
			if (unit.Exportable)
			{
				if (!canonicalOwners.TryGetValue(unit.Owner, out canonical))
					canonicalOwners.Add(unit.Owner, canonical = i);
			}
			if (!members.TryGetValue(canonical, out HashSet<IUnityObjectBase>? group))
				members.Add(canonical, group = new(ReferenceEqualityComparer.Instance));
			group.Add(unit.Seed);
			group.UnionWith(unit.Members);
			foreach (IUnityObjectBase asset in unit.Members.Prepend(unit.Seed))
			{
				if (owners.TryGetValue(asset, out int existing) && existing != canonical)
					throw new InvalidOperationException("An asset belongs to multiple export owners.");
				owners[asset] = canonical;
			}
		}

		HashSet<IUnityObjectBase> included = new(ReferenceEqualityComparer.Instance);
		Dictionary<IUnityObjectBase, string> reasons = new(ReferenceEqualityComparer.Instance);
		HashSet<int> selected = [];
		Queue<IUnityObjectBase> pending = new();
		void Include(IUnityObjectBase asset, string reason)
		{
			if (!included.Add(asset)) return;
			reasons.Add(asset, reason);
			pending.Enqueue(asset);
		}
		foreach (IUnityObjectBase asset in inventory)
		{
			if (!predicate(asset)) continue;
			report.MatchedAssets.Add(Identity(asset));
			Include(asset, "Requested");
			if (owners.TryGetValue(asset, out int owner) && !units[owner].Exportable)
				report.Issues.Add(new("NonExportableRoot", Identity(asset), null, null, null, "The requested asset has a reference-only or skipped collection; it has no standalone export."));
		}
		if (report.MatchedAssets.Count == 0)
			report.Issues.Add(new("NoMatches", null, null, null, null, "No loaded asset matched the selection."));

		while (pending.TryDequeue(out IUnityObjectBase? asset))
		{
			if (!owners.TryGetValue(asset, out int owner))
			{
				report.Issues.Add(new("MissingExportOwner", Identity(asset), null, null, null, "No prepared export collection owns this asset."));
			}
			else if (selected.Add(owner))
			{
				foreach (IUnityObjectBase member in members[owner]) Include(member, "AtomicCollection");
			}
			try
			{
				if (asset is IMonoBehaviour behaviour)
				{
					behaviour.LoadStructure();
					if (behaviour.Structure is null or UnreadableStructure)
						report.Issues.Add(new("UnknownScriptDependencies", Identity(asset), null, null, null, "Script fields could not be read; dependency coverage is incomplete."));
				}
				if (asset is RawDataObject)
					report.Issues.Add(new("UnknownObjectDependencies", Identity(asset), null, null, null, "Opaque object bytes do not provide dependency coverage."));
				foreach (var (field, pointer) in FetchObjectDependencies(asset))
				{
					if (pointer.IsNull) continue;
					IUnityObjectBase? dependency = asset.Collection.TryGetAsset(pointer);
					if (dependency is null)
						report.Issues.Add(new("UnresolvedReference", Identity(asset), field, pointer.FileID, pointer.PathID, "The referenced object is unavailable."));
					else Include(dependency, "Dependency");
				}
			}
			catch (Exception exception) when (ExceptionPolicy.IsRecoverable(exception))
			{
				report.Issues.Add(new("DependencyReadFailure", Identity(asset), null, null, null, exception.ToString()));
			}
		}
		int[] indices = selected.Order().ToArray();
		foreach (int index in indices)
			report.Collections.Add(new(index, units[index].Name, units[index].Exportable));
		if (report.MatchedAssets.Count > 0 && !indices.Any(i => units[i].Exportable))
			report.Issues.Add(new("NoExportableCollections", null, null, null, null, "The selection has no exportable collections."));
		report.IncludedAssets.AddRange(included.Select(a => new SelectedAsset(Identity(a), reasons[a],
			owners.TryGetValue(a, out int owner) ? owner : null)).OrderBy(a => a.Asset.JobOrdinal));
		return new(included, indices, report);
	}

	private static IEnumerable<(string, PPtr)> FetchObjectDependencies(IUnityObjectBase asset)
	{
		if (asset is INavMeshSettings settings)
		{
			// SourceGenerated 1.4.0 repeats the NavMeshSettings pointer indefinitely. These are its
			// only object-reference fields: the pre-5 NavMesh and the 5+ NavMeshData replacement.
			if (settings.Has_NavMesh()) yield return ("m_NavMesh", new PPtr(settings.NavMesh));
			if (settings.Has_NavMeshData()) yield return ("m_NavMeshData", new PPtr(settings.NavMeshData));
		}
		else
		{
			foreach (var dependency in asset.FetchDependencies()) yield return dependency;
		}
	}
}

public sealed class ExportSelectionReport
{
	public string Scope => "AtomicCollectionsAndForwardObjectReferences";
	public string DependencyCoverage => Issues.Count == 0 ? "KnownObjectReferencesOnly" : "Incomplete";
	public bool Succeeded => Issues.Count == 0;
	public List<SelectionAssetIdentity> MatchedAssets { get; } = [];
	public List<SelectedAsset> IncludedAssets { get; } = [];
	public List<SelectedCollection> Collections { get; } = [];
	public List<SelectionIssue> Issues { get; } = [];
}

public sealed record SelectionAssetIdentity(int JobOrdinal, string Collection, string SourcePath, long PathId, int ClassId, string Name);
public sealed record SelectedAsset(SelectionAssetIdentity Asset, string Reason, int? CollectionIndex);
public sealed record SelectedCollection(int PreparationIndex, string Name, bool Exportable);
public sealed record SelectionIssue(string Kind, SelectionAssetIdentity? Source, string? Field, int? FileId, long? PathId, string Diagnostic);
