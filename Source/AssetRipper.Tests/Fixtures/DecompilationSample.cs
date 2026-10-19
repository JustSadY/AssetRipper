namespace AssetRipper.Tests.Fixtures;

/// <summary>
/// Source-owned fixture for state changes, branches and loops in managed decompilation.
/// </summary>
public class DecompilationSample
{
	public int Value;

	public int Add(int amount)
	{
		Value += amount;
		return Value;
	}

	public static int Clamp(int value) => value < 0 ? 0 : value;

	public static int Sum(int count)
	{
		int result = 0;
		for (int i = 0; i < count; i++)
		{
			result += i;
		}
		return result;
	}
}
