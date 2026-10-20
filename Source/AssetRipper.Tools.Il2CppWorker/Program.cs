using AssetRipper.Import.Structure.Assembly.Workers;

namespace AssetRipper.Tools.Il2CppWorker;

internal static class Program
{
	private static int Main(string[] args)
	{
		if (args is not ["--request", string path])
		{
			Console.Error.WriteLine("Usage: AssetRipper.Tools.Il2CppWorker --request /absolute/job/request.json"); return 2;
		}
		try { return Il2CppWorkerHost.Run(path); }
		catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
		{
			Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 2;
		}
	}
}
