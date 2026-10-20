using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace AssetRipper.Processing.Assemblies;

/// <summary>
/// Returning a null by reference causes an error before C# 11. This replaces the method body with a throw statement.
/// </summary>
public sealed class NullRefReturnProcessor(ScriptContentLevel scriptContentLevel) : IAssemblyProcessor
{
	public void Process(GameData gameData) => Process(gameData.AssemblyManager);

	private void Process(IAssemblyManager manager)
	{
		if (manager.ScriptingBackend != ScriptingBackend.IL2Cpp && scriptContentLevel > ScriptContentLevel.Level1)
		{
			return; // Mono doesn't have method stubs, so this isn't necessary.
		}
		else if (scriptContentLevel > ScriptContentLevel.Level2 && manager.Il2CppRecovery?.UsesKnownStubOutput != true)
		{
			return; // Preserve custom or unknown recovery output; Level 3 can still select known stubs.
		}

		manager.ClearStreamCache();

		foreach (MethodDefinition method in manager.GetAllMethods())
		{
			if (method.Signature?.ReturnType is ByReferenceTypeSignature)
			{
				method.CilMethodBody = new CilMethodBody();
				CilInstructionCollection instructions = method.CilMethodBody.Instructions;
				instructions.Add(CilOpCodes.Ldnull);
				instructions.Add(CilOpCodes.Throw);
			}
		}
	}
}
