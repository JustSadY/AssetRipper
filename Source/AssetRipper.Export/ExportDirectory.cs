namespace AssetRipper.Export;

/// <summary>
/// Reserves an output directory without deleting or reusing an existing export.
/// </summary>
public static class ExportDirectory
{
	private const string OwnershipFileName = ".assetripper-export";

	public static string Create(string requestedPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
		string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
		if (path == Path.GetPathRoot(path))
		{
			throw new ArgumentException("The export directory cannot be a filesystem root.", nameof(requestedPath));
		}

		Directory.CreateDirectory(path);
		if (!Directory.EnumerateFileSystemEntries(path).Any() && TryReserve(path))
		{
			return path;
		}

		while (true)
		{
			string jobPath = Path.Join(path, $"AssetRipper_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
			Directory.CreateDirectory(jobPath);
			if (TryReserve(jobPath))
			{
				return jobPath;
			}
		}
	}

	private static bool TryReserve(string path)
	{
		string ownershipPath = Path.Join(path, OwnershipFileName);
		try
		{
			using FileStream stream = new(ownershipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			return true;
		}
		catch (IOException) when (File.Exists(ownershipPath))
		{
			return false;
		}
	}
}
