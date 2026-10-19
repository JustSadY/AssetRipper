using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using System.Buffers.Binary;

namespace AssetRipper.IO.Files.Tests;

public class FileContainerIsolationTests
{
	[Test]
	public void MalformedSerializedEntryDoesNotDiscardSiblingOrNestedEntries()
	{
		SerializedFileBuilder builder = new()
		{
			Generation = FormatVersion.LargeFilesSupport, Version = new(2022, 3), EndianType = EndianType.LittleEndian, HasTypeTree = false,
		};
		byte[] good = builder.Build().ToByteArray();
		byte[] malformed = (byte[])good.Clone();
		// Keep the header valid, but use an invalid negative type-table length.
		int versionEnd = Array.IndexOf(malformed, (byte)0, 48);
		BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(versionEnd + 1 + sizeof(int) + sizeof(bool)), -1);
		FileStreamBundleFile parent = new();
		parent.AddResourceFile(new(malformed, "/source.bundle", "broken.assets"));
		parent.AddResourceFile(new(good, "/source.bundle", "healthy.assets"));
		FileStreamBundleFile nested = new();
		nested.AddResourceFile(new(good, "/source.bundle", "nested.assets"));
		parent.AddFileContainer(nested);
		parent.ReadContentsRecursively();
		Assert.That(parent.FailedFiles, Has.Count.EqualTo(1));
		Assert.That(parent.FailedFiles[0].Name, Is.EqualTo("broken.assets"));
		Assert.That(parent.FailedFiles[0].StackTrace, Does.Contain(nameof(OverflowException)));
		Assert.That(parent.FetchSerializedFiles().Select(f => f.Name), Is.EqualTo(new[] { "healthy.assets", "nested.assets" }));
	}
}
