using AssetRipper.Tests.Decompilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;

namespace AssetRipper.Tests;

[Category("ManagedRoundTrip")]
public class ManagedRoundTripTests
{
	[TestCase(LanguageVersion.CSharp7_3, OptimizationLevel.Debug)]
	[TestCase(LanguageVersion.CSharp7_3, OptimizationLevel.Release)]
	[TestCase(LanguageVersion.CSharp9, OptimizationLevel.Debug)]
	[TestCase(LanguageVersion.CSharp9, OptimizationLevel.Release)]
	public void SourceOwnedFixturePreservesCompilationContractAndBehavior(LanguageVersion language, OptimizationLevel optimization)
	{
		ManagedFixtureReport report = ManagedFixtureHarness.Run(new(language, optimization));
		Assert.That(report.Succeeded, Is.True, Describe(report));
		Assert.That(report.Behaviors, Has.Count.EqualTo(20));
		Assert.That(report.UnityEditorValidation, Is.EqualTo(CheckStatus.NotRun));
		Assert.That(report.References.Images, Is.Not.Empty);
	}

	[Test]
	public void CompilableStubFailsBehaviorValidation()
	{
		ManagedFixtureReport report = ManagedFixtureHarness.Run(new(LanguageVersion.CSharp7_3, OptimizationLevel.Release), FixtureMutation.StubClamp);
		Assert.That(report.Recompilation.Status, Is.EqualTo(CheckStatus.Passed), Describe(report));
		Assert.That(report.Contract.Status, Is.EqualTo(CheckStatus.Passed), Describe(report));
		Assert.That(report.Behaviors.All(b => b.OracleMatched), Is.True, Describe(report));
		Assert.That(report.Behaviors.Where(b => !b.Matched).Select(b => b.Name), Is.EqualTo(new[] { "clamp-positive" }));
		Assert.That(report.BehaviorStatus, Is.EqualTo(CheckStatus.Failed));
		Assert.That(report.Succeeded, Is.False);
	}

	[Test]
	public void MissingPublicFieldFailsContractEvenWhenBehaviorMatches()
	{
		ManagedFixtureReport report = ManagedFixtureHarness.Run(new(LanguageVersion.CSharp7_3, OptimizationLevel.Release), FixtureMutation.RemoveSerializedField);
		Assert.That(report.Recompilation.Status, Is.EqualTo(CheckStatus.Passed), Describe(report));
		Assert.That(report.BehaviorStatus, Is.EqualTo(CheckStatus.Passed), Describe(report));
		Assert.That(report.Contract.Status, Is.EqualTo(CheckStatus.Failed));
		Assert.That(report.Contract.Missing.Single(), Does.Contain(".SerializedField: System.Int32"));
		Assert.That(report.Succeeded, Is.False);
	}

	[Test]
	public void CompileFailureLeavesBehaviorAndContractNotRun()
	{
		ManagedFixtureReport report = ManagedFixtureHarness.Run(new(LanguageVersion.CSharp7_3, OptimizationLevel.Release), FixtureMutation.InvalidReturn);
		Assert.That(report.Recompilation.Status, Is.EqualTo(CheckStatus.Failed), Describe(report));
		Assert.That(report.Recompilation.Diagnostics.Any(d => d.Id == "CS0103" && d.File!.EndsWith("Sample.cs", StringComparison.Ordinal) && d.Line > 0), Is.True);
		Assert.That(report.BehaviorStatus, Is.EqualTo(CheckStatus.NotRun));
		Assert.That(report.Contract.Status, Is.EqualTo(CheckStatus.NotRun));
		Assert.That(report.Succeeded, Is.False);
	}

	[Test]
	public void LanguageGateRejectsCSharp9SyntaxUnderCSharp73()
	{
		Dictionary<string, string> sources = new() { ["record.cs"] = "public record Position(int X, int Y);" };
		FixtureCompilation older = FixtureCompiler.Compile(sources, new(LanguageVersion.CSharp7_3, OptimizationLevel.Release));
		FixtureCompilation supported = FixtureCompiler.Compile(sources, new(LanguageVersion.CSharp9, OptimizationLevel.Release));
		Assert.That(supported.Check.Status, Is.EqualTo(CheckStatus.Passed), JsonSerializer.Serialize(supported.Check));
		Assert.That(older.Check.Status, Is.EqualTo(CheckStatus.Failed));
		Assert.That(older.Check.Diagnostics.Any(d => d.Severity == "Error" && d.File == "record.cs" && d.Line == 1 && d.Column > 0), Is.True);
	}

	[Test]
	public void MissingTargetReferencesCannotFallBackToHostRuntime()
	{
		Dictionary<string, string> sources = new() { ["minimal.cs"] = "public class Minimal { public int Value; }" };
		FixtureProfile profile = new(LanguageVersion.CSharp7_3, OptimizationLevel.Release);
		FixtureCompilation missing = FixtureCompiler.Compile(sources, profile,
			FixtureCompiler.References.Where(r => r.Display != "System.Runtime.dll"));
		Assert.That(FixtureCompiler.Compile(sources, profile).Check.Status, Is.EqualTo(CheckStatus.Passed));
		Assert.That(missing.Check.Status, Is.EqualTo(CheckStatus.Failed));
		Assert.That(missing.Check.Diagnostics.Any(d => d.Severity == "Error"), Is.True);
	}

	private static string Describe(ManagedFixtureReport report) => JsonSerializer.Serialize(new
	{
		report.HarnessError,
		report.OriginalCompilation,
		report.Decompilation,
		report.Recompilation,
		report.Contract,
		BehaviorFailures = report.Behaviors.Where(b => !b.OracleMatched || !b.Matched),
		report.BehaviorStatus,
	}, new JsonSerializerOptions { WriteIndented = true });
}
