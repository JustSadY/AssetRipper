namespace AssetRipper.IO.Files.Exceptions;

/// <summary>Filters local data/decoder failures without hiding job-wide failures.</summary>
public static class ExceptionPolicy
{
	public static bool IsRecoverable(Exception exception) => IsRecoverable(exception, false);

	/// <summary>An unexpected end of input can be isolated while reading a source file.</summary>
	public static bool IsRecoverableInput(Exception exception) => IsRecoverable(exception, true);

	private static bool IsRecoverable(Exception exception, bool readingInput) => exception switch
	{
		OperationCanceledException or OutOfMemoryException or UnauthorizedAccessException => false,
		EndOfStreamException when readingInput => exception.InnerException is null || IsRecoverable(exception.InnerException, readingInput),
		IOException => false,
		AggregateException aggregate => aggregate.InnerExceptions.All(e => IsRecoverable(e, readingInput)),
		_ => exception.InnerException is null || IsRecoverable(exception.InnerException, readingInput),
	};
}
