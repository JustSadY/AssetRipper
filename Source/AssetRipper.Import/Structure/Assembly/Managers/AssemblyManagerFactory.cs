using AssetRipper.Import.Configuration;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public static class AssemblyManagerFactory
{
	public static IAssemblyManager Create(ScriptingBackend backend, Action<string> callback, ImportSettings settings) => backend switch
	{
		ScriptingBackend.Mono => new MonoManager(callback),
		ScriptingBackend.IL2Cpp when settings.Il2CppWorker is { } worker => new IsolatedIL2CppManager(callback, settings.ScriptContentLevel, worker),
		ScriptingBackend.IL2Cpp => new IL2CppManager(callback, settings.ScriptContentLevel),
		_ => new BaseManager(callback),
	};
}
