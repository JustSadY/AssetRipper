using AssetRipper.Export;

namespace AssetRipper.Tests;

public class ExportDirectoryTests
{
	private string root = null!;

	[SetUp]
	public void SetUp() => root = Directory.CreateTempSubdirectory("AssetRipper-ExportTests-").FullName;

	[TearDown]
	public void TearDown() => Directory.Delete(root, true);

	[Test]
	public void EmptyDirectoryIsReservedAndCannotBeReused()
	{
		string first = ExportDirectory.Create(root);
		string second = ExportDirectory.Create(root);
		Assert.That(first, Is.EqualTo(root));
		Assert.That(second, Is.Not.EqualTo(first));
		Assert.That(Path.GetDirectoryName(second), Is.EqualTo(root));
	}

	[Test]
	public void NonEmptyDirectoryPreservesExistingFiles()
	{
		string existing = Path.Join(root, "keep.txt");
		File.WriteAllText(existing, "existing user content");
		string output = ExportDirectory.Create(root);
		Assert.That(output, Is.Not.EqualTo(root));
		Assert.That(File.ReadAllText(existing), Is.EqualTo("existing user content"));
		Assert.That(Directory.Exists(output));
	}

	[Test]
	public void ConcurrentExportsReserveDifferentDirectories()
	{
		string[] outputs = new string[8];
		Parallel.For(0, outputs.Length, i => outputs[i] = ExportDirectory.Create(root));
		Assert.That(outputs.Distinct().Count(), Is.EqualTo(outputs.Length));
	}

	[Test]
	public void FilesystemRootIsRejected()
	{
		Assert.Throws<ArgumentException>(() => ExportDirectory.Create(Path.GetPathRoot(root)!));
	}

	[Test]
	public void ExistingFileIsNotOverwritten()
	{
		string existing = Path.Join(root, "keep.txt");
		File.WriteAllText(existing, "existing user content");
		Assert.Throws<IOException>(() => ExportDirectory.Create(existing));
		Assert.That(File.ReadAllText(existing), Is.EqualTo("existing user content"));
	}
}
