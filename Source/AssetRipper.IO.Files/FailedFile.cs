using AssetRipper.IO.Files.Streams.Smart;

namespace AssetRipper.IO.Files;

public class FailedFile : FileBase
{
	public string Stage { get; set; } = "ReadFile";
	public int? DeclaredObjectCount { get; set; }
	public string StackTrace { get; set; } = "";

	public override void Read(SmartStream stream)
	{
	}

	public override void Write(Stream stream)
	{
		throw new NotSupportedException();
	}
}
