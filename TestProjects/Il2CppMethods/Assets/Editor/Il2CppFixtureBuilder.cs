using System;
using System.IO;
using System.Linq;
using AssetRipper.Il2CppFixture;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Il2CppFixtureBuilder
{
	public static void Build()
	{
		if (Application.unityVersion != "6000.4.0f1") throw new InvalidOperationException("Requires Unity 6000.4.0f1.");
		string output = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_OUTPUT");
		if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("Set absolute fixture output path.");
		if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Output must be empty.");
		Directory.CreateDirectory(output); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		const string scene = "Assets/Methods.unity";
		if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), scene)) throw new IOException("Scene save failed.");
		Observation[] cases = ProbeCases.Observe();
		int[] expected = { -74, -11, 3, 689, 2147483630, 7, 17, 4, 16, 0, 0, 21, 78, 5, -5, -999, 8, 0, 14, 42 };
		if (!cases.Select(c => c.Value).SequenceEqual(expected)) throw new InvalidOperationException("Managed Editor observations differ from independent constants.");
		File.WriteAllText(Path.Combine(output, "oracle.json"), JsonUtility.ToJson(new RuntimeReport { UnityVersion = Application.unityVersion, Backend = "EditorManaged", Cases = cases }, true));
		PlayerSettings.companyName = "AssetRipper"; PlayerSettings.productName = "OwnedMethodsFixture";
		// Target and stripping come from the Python builder; the defaults are the original Linux64 minimal-stripping fixture.
		bool android = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_TARGET") == "Android";
		ManagedStrippingLevel stripping = Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_FIXTURE_STRIPPING") == "High" ? ManagedStrippingLevel.High : ManagedStrippingLevel.Minimal;
		NamedBuildTarget named = android ? NamedBuildTarget.Android : NamedBuildTarget.Standalone;
		PlayerSettings.SetApiCompatibilityLevel(named, ApiCompatibilityLevel.NET_Standard);
		PlayerSettings.SetManagedStrippingLevel(named, stripping);
		PlayerSettings.SetIl2CppCompilerConfiguration(named, Il2CppCompilerConfiguration.Release);
		if (android)
		{
			// Unity has no ARM64 Mono player; the Android fixture is IL2CPP-only and is checked against the Editor oracle.
			PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.IL2CPP);
			PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
			PlayerSettings.SetApplicationIdentifier(named, "com.AssetRipper.OwnedMethodsFixture");
			PlayerSettings.SplashScreen.show = false;
			EditorUserBuildSettings.buildAppBundle = false;
			string directory = Path.Combine(output, "IL2CPP"); Directory.CreateDirectory(directory);
			BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, locationPathName = Path.Combine(directory, "Methods.apk"), target = BuildTarget.Android, options = BuildOptions.None });
			if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Player build failed: " + report.summary.result);
		}
		else foreach (bool il2cpp in new[] { false, true })
		{
			PlayerSettings.SetScriptingBackend(named, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
			string directory = Path.Combine(output, il2cpp ? "IL2CPP" : "Mono"); Directory.CreateDirectory(directory);
			BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, locationPathName = Path.Combine(directory, "Methods.x86_64"), target = BuildTarget.StandaloneLinux64, options = BuildOptions.None });
			if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Player build failed: " + report.summary.result);
		}
		string sources = Path.Combine(output, "Sources"); Directory.CreateDirectory(sources);
		foreach (string source in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories).Concat(new[] { scene, "Assets/link.xml" })) File.Copy(source, Path.Combine(sources, Path.GetFileName(source)));
	}
}
