using AssetRipper.Assets;
using AssetRipper.SerializationLogic;

namespace AssetRipper.Import.Structure.Assembly.Serializable;

/// <summary>Checks stable IDs within one host without following logical graph edges.</summary>
internal static class ManagedReferenceGraphValidator
{
	public static void Validate(SerializableStructure host, SerializableStructure registry)
	{
		// Indexed references in the legacy terminated format have a different
		// null/index contract. Do not apply stable-ID rules to that format.
		if (registry[ManagedReferenceTypes.VersionFieldName].AsInt32 != 2) return;
		IUnityAssetBase[] entries = registry[ManagedReferenceTypes.ReferenceIdsFieldName].AsAssetArray;
		HashSet<long> ids = [];
		foreach (IUnityAssetBase entry in entries)
		{
			long id = ((SerializableStructure)entry)[ManagedReferenceTypes.ReferenceIdFieldName].AsInt64;
			if (id >= 0 && !ids.Add(id)) throw new InvalidDataException($"Duplicate managed reference ID {id} in the host registry.");
		}

		// Build the entire ID set first so forward references and cycles are legal.
		VisitStructure(host, "", ids);
		for (int i = 0; i < entries.Length; i++)
		{
			VisitValue(((SerializableStructure)entries[i])[ManagedReferenceTypes.DataFieldName], $"references.RefIds[{i}].data", ids);
		}
	}

	private static void VisitStructure(SerializableStructure structure, string path, HashSet<long> ids)
	{
		if (structure.Type == ManagedReferenceTypes.Registry) return; // Entries are visited separately with their concrete data layouts.
		if (structure.Type == ManagedReferenceTypes.ManagedReference)
		{
			long id = structure[ManagedReferenceTypes.ReferenceIdFieldName].AsInt64;
			if (id is not (-1 or -2) && !ids.Contains(id))
				throw new InvalidDataException($"Managed reference at {path} uses ID {id}, which is absent from the host registry.");
			return;
		}
		for (int i = 0; i < structure.Fields.Length; i++)
		{
			string field = structure.Type.Fields[i].Name;
			VisitValue(structure.Fields[i], path.Length == 0 ? field : $"{path}.{field}", ids);
		}
	}

	private static void VisitValue(SerializableValue value, string path, HashSet<long> ids)
	{
		switch (value.CValue)
		{
			case SerializableStructure structure:
				VisitStructure(structure, path, ids);
				break;
			case IUnityAssetBase[] assets:
				for (int i = 0; i < assets.Length; i++)
					if (assets[i] is SerializableStructure element) VisitStructure(element, $"{path}[{i}]", ids);
				break;
			case IUnityAssetBase[][] arrays:
				for (int i = 0; i < arrays.Length; i++)
					for (int j = 0; j < arrays[i].Length; j++)
						if (arrays[i][j] is SerializableStructure element) VisitStructure(element, $"{path}[{i}][{j}]", ids);
				break;
			case SerializablePair pair:
				VisitValue(pair.First, $"{path}.{pair.Type.Fields[0].Name}", ids);
				VisitValue(pair.Second, $"{path}.{pair.Type.Fields[1].Name}", ids);
				break;
			case SerializablePair[] pairs:
				for (int i = 0; i < pairs.Length; i++)
				{
					VisitValue(pairs[i].First, $"{path}[{i}].{pairs[i].Type.Fields[0].Name}", ids);
					VisitValue(pairs[i].Second, $"{path}[{i}].{pairs[i].Type.Fields[1].Name}", ids);
				}
				break;
		}
	}
}
