using AsmResolver.DotNet;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Workers;
using AssetRipper.Import.Structure.Platforms;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using LibCpp2IL;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public sealed class IL2CppManager : BaseManager
{
	static IL2CppManager()
	{
		InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
		InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
		InstructionSetRegistry.RegisterInstructionSet<WasmInstructionSet>(DefaultInstructionSets.WASM);
		InstructionSetRegistry.RegisterInstructionSet<ArmV7InstructionSet>(DefaultInstructionSets.ARM_V7);
		bool useNewArm64 = false;
		if (useNewArm64)
		{
			InstructionSetRegistry.RegisterInstructionSet<NewArmV8InstructionSet>(DefaultInstructionSets.ARM_V8);
		}
		else
		{
			InstructionSetRegistry.RegisterInstructionSet<Arm64InstructionSet>(DefaultInstructionSets.ARM_V8);
		}

		LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
	}

	public static List<Cpp2IlProcessingLayer> DefaultProcessingLayers { get; } =
	[
		new AttributeAnalysisProcessingLayer(),
		new MethodOverrideNameFixer(),
	];

	public static AsmResolverDllOutputFormatDefault DefaultOutputFormat { get; } = new();

	public static List<Cpp2IlProcessingLayer>? RecoveryProcessingLayers { get; set; }

	public static AsmResolverDllOutputFormat? RecoveryOutputFormat { get; set; }

	public static event Action? ClearStaticState;

	public string? GameAssemblyPath { get; private set; }
	public string? UnityPlayerPath { get; private set; }
	public string? GameDataPath { get; private set; }
	public string? MetaDataPath { get; private set; }
	public UnityVersion UnityVersion { get; private set; }
	/// <summary>
	/// The requested level; the selected output provider determines whether bodies are stubbed.
	/// </summary>
	private readonly ScriptContentLevel contentLevel;

	public IL2CppManager(Action<string> requestAssemblyCallback, ScriptContentLevel level) : base(requestAssemblyCallback)
	{
		contentLevel = level;
	}

	/// <summary>
	/// Rejects a structurally foreign binary/metadata pair before processing layers and output run.
	/// Off by default so the in-process import keeps its existing behavior; the isolated worker always enables it.
	/// </summary>
	public bool ValidateNativePair { get; init; }

	public override ScriptingBackend ScriptingBackend => ScriptingBackend.IL2Cpp;
	private Il2CppRecoveryReport? recoveryReport;
	public override Il2CppRecoveryReport? Il2CppRecovery => recoveryReport;

	/// <summary>
	/// Describes the current selection without initializing or analyzing a native binary.
	/// The actual import captures its own selection when <see cref="Initialize"/> starts.
	/// </summary>
	public Il2CppRecoveryReport PreviewRecoverySelection() => CaptureSelection().Report;

	private PipelineSelection CaptureSelection()
	{
		// Capture both hooks before invoking provider properties or processing layers. Their callbacks
		// can change the static hooks, but those changes apply to the next initialization only.
		List<Cpp2IlProcessingLayer>? recoveryLayers = RecoveryProcessingLayers;
		AsmResolverDllOutputFormat? recoveryOutput = RecoveryOutputFormat;
		List<Cpp2IlProcessingLayer> layers = (contentLevel == ScriptContentLevel.Level3
			? recoveryLayers ?? DefaultProcessingLayers
			: DefaultProcessingLayers).ToList();
		AsmResolverDllOutputFormat output = contentLevel == ScriptContentLevel.Level3
			? recoveryOutput ?? DefaultOutputFormat
			: DefaultOutputFormat;
		bool knownStub = output.GetType() == typeof(AsmResolverDllOutputFormatDefault);
		string capability = contentLevel != ScriptContentLevel.Level3 ? "NotRequested"
			: knownStub ? "RecoveryUnavailable"
			: output is Il2CppCandidateOutput ? "ExperimentalCandidates"
			: "UnverifiedCustomProvider";
		Il2CppRecoveryReport report = new(1, contentLevel.ToString(), DescribeProvider(output.GetType(), output.OutputFormatId),
			Array.AsReadOnly(layers.Select(layer => DescribeProvider(layer.GetType(), layer.Id)).ToArray()),
			knownStub, new(0, 0, 0), "NotRun")
		{ RecoveryCapability = capability };
		return new(output, layers, report);
	}

	private static Il2CppProviderInfo DescribeProvider(Type type, string id)
	{
		System.Reflection.AssemblyName assembly = type.Assembly.GetName();
		return new(type.FullName ?? type.Name, assembly.Name ?? "", assembly.Version?.ToString() ?? "", id);
	}

	private sealed record PipelineSelection(AsmResolverDllOutputFormat Output, List<Cpp2IlProcessingLayer> Layers, Il2CppRecoveryReport Report);

	public override void Initialize(PlatformGameStructure gameStructure)
	{
		PipelineSelection selection = CaptureSelection();
		recoveryReport = selection.Report;
		string? gameDataPath = gameStructure.GameDataPath;
		if (string.IsNullOrWhiteSpace(gameDataPath))
		{
			throw new ArgumentException($"{nameof(gameStructure.GameDataPath)} cannot be null or whitespace.", nameof(gameStructure));
		}

		GameDataPath = gameDataPath;
		GameAssemblyPath = gameStructure.Il2CppGameAssemblyPath;
		UnityPlayerPath = gameStructure.UnityPlayerPath;
		MetaDataPath = gameStructure.Il2CppMetaDataPath;

		UnityVersion = gameStructure.Version ?? Cpp2IlApi.DetermineUnityVersion(UnityPlayerPath, GameDataPath);

		if (UnityVersion == default)
		{
			throw new Exception("Could not determine the unity version");
		}
		else
		{
			Logger.Info(LogCategory.Import, $"During Il2Cpp initialization, found Unity version: {UnityVersion}");
		}

		Logger.SendStatusChange("loading_step_parse_il2cpp_metadata");

		ClearStaticState?.Invoke();

		Cpp2IlApi.InitializeLibCpp2Il(GameAssemblyPath!, MetaDataPath!, UnityVersion, false);

		Il2CppPairValidation? pairValidation = ValidateNativePair ? Il2CppNativePairValidator.Validate(Cpp2IlApi.CurrentAppContext!) : null;
		recoveryReport = selection.Report with { PairValidation = pairValidation };

		Logger.SendStatusChange("loading_step_generate_dummy_dll");

		List<Cpp2IlProcessingLayer> processingLayers = selection.Layers;

		foreach (Cpp2IlProcessingLayer cpp2IlProcessingLayer in processingLayers.ToArray())
		{
			cpp2IlProcessingLayer.PreProcess(Cpp2IlApi.CurrentAppContext, processingLayers);
		}
		Il2CppRecoveryReport activeReport = selection.Report with
		{
			ProcessingLayers = Array.AsReadOnly(processingLayers.Select(layer => DescribeProvider(layer.GetType(), layer.Id)).ToArray()),
			PairValidation = pairValidation,
		};
		recoveryReport = activeReport;

		foreach (Cpp2IlProcessingLayer cpp2IlProcessingLayer in processingLayers)
		{
			cpp2IlProcessingLayer.Process(Cpp2IlApi.CurrentAppContext);
		}

		List<AssemblyDefinition> assemblies = selection.Output.BuildAssemblies(Cpp2IlApi.CurrentAppContext);
		recoveryReport = activeReport.WithMethodBodies(assemblies);

		foreach (AssemblyDefinition assembly in assemblies)
		{
			Add(assembly);
		}
	}

	~IL2CppManager()
	{
		Dispose(false);
	}
}
