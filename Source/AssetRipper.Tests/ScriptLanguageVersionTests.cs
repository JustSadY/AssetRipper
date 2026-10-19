using AssetRipper.Export.Configuration;
using AssetRipper.Export.Scripts;
using AssetRipper.Primitives;
using AssetRipper.Tests.Decompilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Xml.Linq;
using ILSpyLanguageVersion = ICSharpCode.Decompiler.CSharp.LanguageVersion;

namespace AssetRipper.Tests;

[Category("ScriptLanguageProfile")]
public class ScriptLanguageVersionTests
{
	private const string Source = """
		using System.Collections.Generic;
		namespace AssetRipper.LanguageFixture
		{
			public class LanguageFeatures
			{
				public static List<int> Build(int value) => new() { value };
				public static int Evaluate(object value)
				{
					if (value is int number and > 0)
						return number switch { < 10 => number + 1, _ => 99 };
					return -1;
				}
			}
		}
		""";

	[TestCase("2018.4.0f1", ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp4)]
	[TestCase("2018.4.0f1", ScriptLanguageVersion.AutoExperimental, LanguageVersion.CSharp7_3)]
	[TestCase("2019.2.0f1", ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp7_3)]
	[TestCase("2020.2.0f1", ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp8)]
	[TestCase("2021.2.0b5", ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp8)]
	[TestCase("2021.2.0b6", ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp9)]
	[TestCase("6000.4.0f1", ScriptLanguageVersion.AutoSafe, LanguageVersion.CSharp9)]
	[TestCase("6000.4.0f1", ScriptLanguageVersion.CSharp7_3, LanguageVersion.CSharp7_3)]
	public void BothExportPathsCompileUnderTheRequestedUnityLanguage(string unityVersion, ScriptLanguageVersion requested, LanguageVersion expected)
	{
		FixtureCompilation input = FixtureCompiler.Compile(new Dictionary<string, string> { ["original.cs"] = Source }, new(LanguageVersion.CSharp9, OptimizationLevel.Release));
		Assert.That(input.Check.Status, Is.EqualTo(CheckStatus.Passed));
		foreach (bool primary in new[] { false, true })
		{
			using ScriptExportFixture manager = new(input.AssemblyBytes!, FixtureCompiler.ReferenceImages);
			var export = manager.Export(primary, UnityVersion.Parse(unityVersion), requested, "LanguageFeatures", "AssetRipper.LanguageFixture");
			Assert.That(export.Succeeded, Is.True, export.FileSystem.File.ReadAllText(export.ReportPath));
			var sources = ScriptExportFixture.ReadSources(export.FileSystem, export.ScriptDirectory);
			Assert.That(sources, Has.Count.GreaterThan(0));
			FixtureCompilation candidate = FixtureCompiler.Compile(sources, new(expected, OptimizationLevel.Release));
			Assert.That(candidate.Check.Status, Is.EqualTo(CheckStatus.Passed), $"primary={primary}, Unity={unityVersion}, requested={requested}: " + JsonSerializer.Serialize(candidate.Check));
			using JsonDocument report = JsonDocument.Parse(export.FileSystem.File.ReadAllText(export.ReportPath));
			JsonElement selection = report.RootElement.GetProperty("LanguageSelection");
			Assert.That(selection.GetProperty("UnityVersion").GetString(), Is.EqualTo(unityVersion));
			Assert.That(selection.GetProperty("Requested").GetString(), Is.EqualTo(requested.ToString()));
			Assert.That(selection.GetProperty("Effective").GetString(), Is.EqualTo(requested.ToCSharpLanguageVersion(UnityVersion.Parse(unityVersion)).ToString()));
			Assert.That(selection.GetProperty("UnityVersionUnknown").GetBoolean(), Is.False);
			if (primary)
			{
				string project = export.FileSystem.File.ReadAllText(export.ScriptDirectory + "/ManagedRoundTrip.csproj");
				XDocument xml = XDocument.Parse(project);
				Assert.That(xml.Descendants("TargetFramework").Select(e => e.Value).Distinct().Single(),
					Is.EqualTo(report.RootElement.GetProperty("ProjectScaffoldFramework").GetString()));
				Assert.That(xml.Descendants("NoStandardLib").Single().Value, Is.EqualTo("True"));
				Assert.That(xml.Descendants("DisableImplicitFrameworkReferences").Single().Value, Is.EqualTo("True"));
				string projectLanguage = xml.Descendants("LangVersion").Single().Value;
				Assert.That(LanguageVersionFacts.TryParse(projectLanguage, out LanguageVersion parsed), Is.True, projectLanguage);
				Assert.That(parsed, Is.EqualTo(expected));
			}
		}
	}

	[TestCase(ScriptLanguageVersion.AutoSafe)]
	[TestCase(ScriptLanguageVersion.AutoExperimental)]
	public void UnknownUnityVersionUsesConservativeLanguageAndIsReported(ScriptLanguageVersion requested)
	{
		ScriptLanguageSelection selection = ScriptLanguageSelection.ForUnity(requested, default);
		Assert.That(selection.UnityVersion, Is.Null);
		Assert.That(selection.UnityVersionUnknown, Is.True);
		Assert.That(selection.IsAutomatic, Is.True);
		Assert.That(selection.Effective, Is.EqualTo(ILSpyLanguageVersion.CSharp4));
	}

	[Test]
	public void ExplicitLatestDoesNotBecomeAutomaticSelection()
	{
		ScriptLanguageSelection selection = ScriptLanguageSelection.ForUnity(ScriptLanguageVersion.Latest, UnityVersion.Parse("2018.4.0f1"));
		Assert.That(selection.Effective, Is.EqualTo(ILSpyLanguageVersion.Latest));
		Assert.That(selection.IsAutomatic, Is.False);
	}

	[Test]
	public void DirectCollectionConstructorKeepsTheExtractorLanguage()
	{
		using AssetRipper.Import.Structure.Assembly.Managers.BaseManager manager = new(_ => { });
		AssetRipper.Export.PrimaryContent.Scripts.ScriptContentExtractor extractor = new(manager,
			ScriptLanguageSelection.ForUnity(ScriptLanguageVersion.AutoSafe, UnityVersion.Parse("2018.4.0f1")));
		AssetRipper.Export.PrimaryContent.Scripts.ScriptExportCollection collection = new(extractor);
		Assert.That(collection.LanguageVersion, Is.EqualTo(ILSpyLanguageVersion.CSharp4));
		Assert.That(collection.LanguageSelection, Is.SameAs(extractor.LanguageSelection));
	}

	[TestCase("2017.0.0f1", ScriptLanguageVersion.AutoExperimental, ILSpyLanguageVersion.CSharp4)]
	[TestCase("2017.1.0f1", ScriptLanguageVersion.AutoExperimental, ILSpyLanguageVersion.CSharp6)]
	[TestCase("2018.2.0f1", ScriptLanguageVersion.AutoExperimental, ILSpyLanguageVersion.CSharp6)]
	[TestCase("2018.3.0f1", ScriptLanguageVersion.AutoExperimental, ILSpyLanguageVersion.CSharp7_3)]
	[TestCase("2019.1.0f1", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp4)]
	[TestCase("2019.2.0f1", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp7_3)]
	[TestCase("2020.1.0f1", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp7_3)]
	[TestCase("2020.2.0f1", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp8_0)]
	[TestCase("2021.2.0b5", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp8_0)]
	[TestCase("2021.2.0b6", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp9_0)]
	[TestCase("2022.1.0a2", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp8_0)]
	[TestCase("2022.1.0a3", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp9_0)]
	[TestCase("6000.4.0f1", ScriptLanguageVersion.AutoSafe, ILSpyLanguageVersion.CSharp9_0)]
	public void AutomaticSelectionRespectsReleaseBoundaries(string unityVersion, ScriptLanguageVersion requested, ILSpyLanguageVersion expected)
	{
		Assert.That(requested.ToCSharpLanguageVersion(UnityVersion.Parse(unityVersion)), Is.EqualTo(expected));
	}
}
