using AssetRipper.Assets;

namespace AssetRipper.Export.UnityProjects;

/// <summary>Validated membership, committed only after preparation has completed.</summary>
internal sealed record PreparedExportCollection(IExportCollection Collection, IUnityObjectBase Seed,
	IUnityObjectBase[] Assets, string Name, bool Exportable);
