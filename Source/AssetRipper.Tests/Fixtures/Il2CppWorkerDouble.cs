using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

// Controlled child-process faults. It never reads or executes a game binary.
public static class WorkerDouble
{
	public static int Main(string[] args)
	{
		string mode = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
		if (mode == "HangChild")
		{
			var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
			start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Hang.dll"));
			start.ArgumentList.Add("--request"); start.ArgumentList.Add(args[1]);
			var child = Process.Start(start); File.WriteAllText("child.pid", child.Id.ToString());
		}
		if (mode is "Hang" or "HangChild") { Thread.Sleep(Timeout.Infinite); return 0; }
		if (mode == "Exit") return 37;
		if (mode == "Malformed") { File.WriteAllText("response.json", "{bad json"); return 0; }
		if (mode == "Oversized") { File.WriteAllText("response.json", new string(' ', 16 * 1024 * 1024 + 1)); return 0; }
		if (mode == "Flood") { Console.Out.Write(new string('o', 2 * 1024 * 1024)); Console.Error.Write(new string('e', 2 * 1024 * 1024)); }
		if (mode == "HeapHog")
		{
			// Retain far more than the configured hard limit; the runtime must stop this process.
			var retained = new List<byte[]>();
			for (int i = 0; i < 64; i++) { byte[] block = new byte[16 * 1024 * 1024]; block.AsSpan().Fill(1); retained.Add(block); }
			GC.KeepAlive(retained);
		}
		File.WriteAllText("heap-limit.txt", Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") ?? "unset");
		JsonNode request = JsonNode.Parse(File.ReadAllText(args[1]));
		bool candidates = request["Candidates"] is not null;
		Directory.CreateDirectory("Assemblies");
		string assembly = "Assemblies/00000.dll";
		byte[] bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "OwnedFixture.dll"));
		File.WriteAllBytes(assembly, bytes);
		if (mode == "Symlink") { File.Delete(assembly); File.CreateSymbolicLink(assembly, Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "OwnedFixture.dll")); }
		var output = new JsonObject { ["Path"] = assembly, ["Name"] = "ManagedRoundTrip", ["Length"] = bytes.Length, ["Sha256"] = Hash(bytes) };
		bool level3 = request["ContentLevel"].GetValue<string>() == "Level3";
		var report = new JsonObject
		{
			["SchemaVersion"] = 1, ["RequestedContentLevel"] = request["ContentLevel"].GetValue<string>(),
			["OutputFormat"] = candidates
				? new JsonObject { ["TypeName"] = "AssetRipper.Import.Structure.Assembly.Workers.Il2CppCandidateOutput", ["AssemblyName"] = "AssetRipper.Import", ["AssemblyVersion"] = "1.0.0.0", ["Id"] = "experimental_checked_candidates" }
				: new JsonObject { ["TypeName"] = "Cpp2IL.Core.OutputFormats.AsmResolverDllOutputFormatDefault", ["AssemblyName"] = "Cpp2IL.Core", ["AssemblyVersion"] = "1.0.9.0", ["Id"] = "dll_default" },
			["ProcessingLayers"] = new JsonArray(), ["UsesKnownStubOutput"] = !candidates,
			["MethodBodies"] = new JsonObject { ["GeneratedStub"] = candidates ? 0 : 1, ["NoBodyByDesign"] = 0, ["Unknown"] = 0, ["RecoveredNative"] = candidates ? 1 : 0 },
			["Validation"] = "NotRun",
			["RecoveryCapability"] = candidates ? "ExperimentalCandidates" : level3 ? "RecoveryUnavailable" : "NotRequested",
			["PairValidation"] = new JsonObject
			{
				["Status"] = "Passed", ["InstructionSet"] = "x86_64", ["PointerSize"] = 8, ["MetadataVersion"] = 39,
				["Images"] = 1, ["TypeDefinitions"] = 2, ["MethodDefinitions"] = 1, ["CheckedModules"] = 1, ["MethodPointers"] = 1,
			},
		};
		var methods = new JsonArray();
		if (candidates)
		{
			Directory.CreateDirectory("Evidence");
			byte[] evidence = Encoding.UTF8.GetBytes("# controlled sidecar\n");
			File.WriteAllBytes("Evidence/00000.txt", evidence);
			string type = request["Candidates"]["TypeNames"][0].GetValue<string>();
			methods.Add(new JsonObject
			{
				["OriginalToken"] = 0x06000001, ["Type"] = type, ["Name"] = "Value", ["Signature"] = "System.Int32 Owned.Value()",
				["Pointer"] = 0x1000, ["Rva"] = 0x1000, ["RawOffset"] = 0x1000, ["SharedAddressMethods"] = 1,
				["BodyOrigin"] = "RecoveredNative", ["StructuralValidation"] = "TypedCilChecked", ["BehaviorValidation"] = "NotRun", ["Diagnostic"] = null,
				["IsilInstructionCount"] = 1, ["CilInstructionCount"] = 2, ["Cil"] = new JsonArray("IL_0000: ldc.i4.0", "IL_0001: ret"), ["EvidenceTruncated"] = false,
				["Gates"] = new JsonArray("IsilChecked", "StackHeightChecked", "TypedCilChecked"), ["IsGeneric"] = false,
				["Sidecar"] = new JsonObject { ["Path"] = "Evidence/00000.txt", ["Length"] = evidence.Length, ["Sha256"] = Hash(evidence) },
			});
		}
		var response = new JsonObject
		{
			["SchemaVersion"] = 2, ["JobId"] = request["JobId"].DeepClone(), ["ProcessId"] = Environment.ProcessId, ["Status"] = "Completed", ["Error"] = null,
			["Native"] = request["Native"].DeepClone(), ["Metadata"] = request["Metadata"].DeepClone(), ["Recovery"] = report,
			["Assemblies"] = new JsonArray(output), ["Methods"] = methods
		};
		response["UnityVersion"] = request["UnityVersion"].DeepClone();
		response["VersionFiles"] = request["VersionFiles"].DeepClone();
		JsonNode method = candidates ? methods[0] : null;
		switch (mode)
		{
			case "WrongJob": response["JobId"] = Guid.NewGuid().ToString("N"); break;
			case "WrongPid": response["ProcessId"] = 0; break;
			case "WrongInput": response["Native"]["Sha256"] = new string('0', 64); break;
			case "WrongOutput": output["Sha256"] = new string('0', 64); break;
			case "Traversal": output["Path"] = "../outside.dll"; break;
			case "BehaviorClaim": report["Validation"] = "BehaviorPassed"; break;
			case "UnknownProvider": report["UsesKnownStubOutput"] = false; break;
			case "NoAssemblies": response["Assemblies"] = new JsonArray(); break;
			case "Duplicate": response["Assemblies"].AsArray().Add(output.DeepClone()); break;
			case "ExtraField": response["Unrecognized"] = true; break;
			case "OldSchema": response["SchemaVersion"] = 1; break;
			case "NoPairValidation": report.Remove("PairValidation"); break;
			case "PairFailed": report["PairValidation"]["Status"] = "Failed"; break;
			case "UnknownBodies": report["MethodBodies"]["Unknown"] = 1; break;
			case "RecoveredClaim": report["MethodBodies"]["RecoveredNative"] = 1; break;
			case "WrongCapability": report["RecoveryCapability"] = "ExperimentalCandidates"; break;
			case "CandidateMissingGate": method["Gates"] = new JsonArray("StackHeightChecked", "TypedCilChecked"); break;
			case "CandidateUntyped": method["StructuralValidation"] = "StackHeightChecked"; method["Gates"] = new JsonArray("IsilChecked", "StackHeightChecked"); break;
			case "CandidateGeneric": method["IsGeneric"] = true; break;
			case "CandidateShared": method["SharedAddressMethods"] = 2; break;
			case "CandidateCount": report["MethodBodies"]["RecoveredNative"] = 0; break;
			case "CandidateOldOrigin": method["BodyOrigin"] = "CandidateUnverified"; break;
			case "EvidenceTraversal": method["Sidecar"]["Path"] = "../outside.txt"; break;
			case "EvidenceHash": method["Sidecar"]["Sha256"] = new string('0', 64); break;
		}
		File.WriteAllText("response.json", response.ToJsonString());
		return mode == "ExitAfterResponse" ? 37 : 0;
	}

	private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
