using AssetRipper.IO.Files.SerializedFiles.IO;

namespace AssetRipper.IO.Files.SerializedFiles.Parser;

public abstract partial class SerializedTypeBase
{
	public int TypeID
	{
		get
		{
			return RawTypeID;
		}
		set
		{
			RawTypeID = value;
		}
	}

	/// <summary>
	/// For versions less than 17, it specifies <see cref="TypeID"/> or -<see cref="ScriptTypeIndex"/> -1 for MonoBehaviour
	/// </summary>
	public int OriginalTypeID
	{
		get
		{
			return RawTypeID;
		}
		set
		{
			RawTypeID = value;
		}
	}
	public int RawTypeID { get; set; }
	public bool IsStrippedType { get; set; }
	/// <summary>
	/// For MonoBehaviour, this specifies script type
	/// </summary>
	public short ScriptTypeIndex { get; set; }
	/// <summary>
	/// The type of the class.
	/// </summary>
	public TypeTrees.TypeTree OldType { get; } = new();
	public Hash128 ScriptID { get; set; }
	public Hash128 OldTypeHash { get; set; }
	/// <summary>
	/// Format 23+: the xxHash128 under which the type tree is stored. It is carried, not verified;
	/// the hashed input is not established without a Unity 6000.5 build.
	/// </summary>
	public Hash128 ExtractedTypeTreeHash { get; set; }
	/// <summary>
	/// Format 23+: the size recorded before the type tree. Zero means the type tree is stored outside this file.
	/// </summary>
	public int StoredTypeTreeSize { get; set; }
	/// <summary>
	/// Format 23+: the type tree is not in this file; <see cref="OldType"/> is empty and readers fall back to schemas
	/// that do not need the file's type tree.
	/// </summary>
	public bool IsTypeTreeExtracted { get; set; }
	/// <summary>
	/// Format 23+: bytes the inline type tree occupied when read, for comparison with <see cref="StoredTypeTreeSize"/>,
	/// whose exact extent is not established.
	/// </summary>
	public int? InlineTypeTreeByteCount { get; private set; }

	internal void Read(SerializedReader reader, bool hasTypeTree)
	{
		RawTypeID = reader.ReadInt32();
		int typeIdLocal;
		if (reader.Generation < FormatVersion.RefactoredClassId)
		{
			typeIdLocal = RawTypeID < 0 ? -1 : RawTypeID;
			IsStrippedType = false;
			ScriptTypeIndex = -1;
		}
		else
		{
			typeIdLocal = RawTypeID;
			IsStrippedType = reader.ReadBoolean();
		}

		if (reader.Generation >= FormatVersion.RefactorTypeData)
		{
			ScriptTypeIndex = reader.ReadInt16();
		}

		if (reader.Generation >= FormatVersion.HasTypeTreeHashes)
		{
			bool readScriptID = (typeIdLocal == -1)
				|| (typeIdLocal == 114)
				|| (!IgnoreScriptTypeForHash(reader.Generation, reader.Version) && ScriptTypeIndex >= 0);
			if (readScriptID)
			{
				ScriptID = Hash128.Read(reader);
			}
			OldTypeHash = Hash128.Read(reader);
		}

		if (hasTypeTree)
		{
			if (HasExtractedTypeTrees(reader.Generation))
			{
				ExtractedTypeTreeHash = Hash128.Read(reader);
				StoredTypeTreeSize = reader.ReadInt32();
				if (StoredTypeTreeSize < 0)
				{
					throw new InvalidDataException($"Type tree size cannot be negative: {StoredTypeTreeSize}");
				}
				IsTypeTreeExtracted = StoredTypeTreeSize == 0;
				if (IsTypeTreeExtracted)
				{
					OldType.Clear();
					InlineTypeTreeByteCount = null;
				}
				else
				{
					long start = reader.BaseStream.Position;
					OldType.Read(reader);
					InlineTypeTreeByteCount = checked((int)(reader.BaseStream.Position - start));
				}
			}
			else
			{
				OldType.Read(reader);
			}
			if (reader.Generation < FormatVersion.HasTypeTreeHashes)
			{
				//OldTypeHash gets recalculated here in a complicated way on 2023.
			}
			else if (reader.Generation >= FormatVersion.StoresTypeDependencies)
			{
				ReadTypeDependencies(reader);
			}
		}
	}

	private protected abstract void ReadTypeDependencies(SerializedReader reader);

	protected abstract bool IgnoreScriptTypeForHash(FormatVersion formatVersion, UnityVersion unityVersion);

	private protected abstract void WriteTypeDependencies(SerializedWriter writer);

	internal void Write(SerializedWriter writer, bool hasTypeTree)
	{
		writer.Write(RawTypeID);
		if (HasIsStrippedType(writer.Generation))
		{
			writer.Write(IsStrippedType);
		}
		if (HasScriptTypeIndex(writer.Generation))
		{
			writer.Write(ScriptTypeIndex);
		}
		if (HasHash(writer.Generation))
		{
			bool writeScriptID = (RawTypeID == -1)
				|| (RawTypeID == 114)
				|| (!IgnoreScriptTypeForHash(writer.Generation, writer.Version) && ScriptTypeIndex >= 0);
			if (writeScriptID)
			{
				ScriptID.Write(writer);
			}
			OldTypeHash.Write(writer);
		}

		if (hasTypeTree)
		{
			if (HasExtractedTypeTrees(writer.Generation))
			{
				ExtractedTypeTreeHash.Write(writer);
				if (IsTypeTreeExtracted)
				{
					writer.Write(0);
				}
				else
				{
					// The recorded size covers the type tree as written here, including its "tthm" header.
					using MemoryStream tree = new();
					using (SerializedWriter treeWriter = new(tree, writer.EndianType, writer.Generation, writer.Version))
					{
						OldType.Write(treeWriter);
					}
					byte[] bytes = tree.ToArray();
					writer.Write(bytes.Length);
					writer.Write(bytes, 0, bytes.Length);
				}
			}
			else
			{
				OldType.Write(writer);
			}
			if (HasTypeDependencies(writer.Generation))
			{
				WriteTypeDependencies(writer);
			}
		}
	}

	public override string ToString()
	{
		return TypeID.ToString();
	}

	/// <summary>
	/// 5.5.0a and greater, ie format version 16+
	/// </summary>
	public static bool HasIsStrippedType(FormatVersion generation) => generation >= FormatVersion.RefactoredClassId;
	/// <summary>
	/// 5.5.0 and greater, ie format version 17+
	/// </summary>
	public static bool HasScriptTypeIndex(FormatVersion generation) => generation >= FormatVersion.RefactorTypeData;
	/// <summary>
	/// 5.0.0unk2 and greater, ie format version 13+
	/// </summary>
	public static bool HasHash(FormatVersion generation) => generation >= FormatVersion.HasTypeTreeHashes;

	/// <summary>
	/// 6000.5.0a5 and greater, ie format version 23+
	/// </summary>
	public static bool HasExtractedTypeTrees(FormatVersion generation) => generation >= FormatVersion.ExtractedTypeTreeSupport;
	/// <summary>
	/// 2019.3 and greater, ie format version 21+
	/// </summary>
	public static bool HasTypeDependencies(FormatVersion generation) => generation >= FormatVersion.StoresTypeDependencies;
}
