using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using ICSharpCode.Decompiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Resources;

namespace AssetRipper.Tests.Decompilation;

internal sealed record TypeRecoveryFixture(string Source, byte[] OriginalAssembly, byte[] InputAssembly)
{
	private const string Template = """
		using System;
		namespace AssetRipper.TypeFixture
		{
			public class SharedName
			{
				public int Value;
				public int Add(int amount) { Value += amount; return Value; }
				public class Nested { public static int Evaluate(int value) { return value * 3; } }
			}
			public class SharedName<T> { public const decimal Broken = 3.14m; }
			public class Independent { public static int Read(int value) { return value + 11; } }
		}
		""";

	public static TypeRecoveryFixture Create(bool damage = true, bool extraType = false, bool damageAll = false, bool resource = false)
	{
		string source = Template;
		if (extraType) source = source.Replace("public class Independent", "public class SharedName<T, U> { public static int Twice(int value) { return value * 2; } } public class Independent", StringComparison.Ordinal);
		if (damageAll) source = source.Replace("public int Value;", "public int Value; public const decimal AlsoBroken = 1.25m;", StringComparison.Ordinal);
		FixtureCompilation original = FixtureCompiler.Compile(new Dictionary<string, string> { ["original.cs"] = source }, new(LanguageVersion.CSharp9, OptimizationLevel.Release));
		Assert.That(original.Check.Status, Is.EqualTo(CheckStatus.Passed));
		if (!damage && !resource) return new(source, original.AssemblyBytes!, original.AssemblyBytes!);
		AssemblyDefinition assembly = AssemblyDefinition.FromBytes(original.AssemblyBytes!, createRuntimeContext: false);
		if (damage)
		{
			foreach (TypeDefinition type in assembly.ManifestModule!.TopLevelTypes.Where(t => t.Name == "SharedName`1" || damageAll && t.Name == "SharedName"))
			{
				var attribute = type.Fields.Single(f => f.Name!.Value.EndsWith("Broken", StringComparison.Ordinal)).CustomAttributes
					.Single(a => a.Constructor?.DeclaringType?.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute");
				attribute.Signature!.FixedArguments[0] = new(assembly.ManifestModule.CorLibTypeFactory.Byte, (byte)255);
			}
		}
		if (resource)
		{
			using MemoryStream bytes = new();
			// An empty, valid container exercises the conservative companion-map guard
			// without the upstream resource writer's physical-filesystem dependency.
			using (ResourceWriter writer = new(bytes)) writer.Generate();
			assembly.ManifestModule!.Resources.Add(new ManifestResource("Fixture.g.resources", ManifestResourceAttributes.Public, new DataSegment(bytes.ToArray())));
		}
		using BaseManager manager = new(_ => { });
		manager.Add(assembly);
		using MemoryStream output = new();
		manager.SaveAssembly(assembly, output);
		return new(source, original.AssemblyBytes!, output.ToArray());
	}

	public (ScriptDecompilationResult Result, VirtualFileSystem FileSystem) Decompile(bool nestedDirectories = true, bool qualified = false,
		VirtualFileSystem? fileSystem = null, Func<DecompilerSettings, ILSpyAssemblyResolver, VirtualFileSystem, ILSpyWholeProjectDecompiler>? createDecompiler = null)
	{
		using ScriptExportFixture manager = new(InputAssembly, FixtureCompiler.ReferenceImages);
		VirtualFileSystem fs = fileSystem ?? new();
		fs.Directory.Create("/scripts");
		ILSpyAssemblyResolver resolver = new(manager);
		DecompilerSettings settings = new(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp9_0)
		{
			UseNestedDirectoriesForNamespaces = nestedDirectories, AlwaysUseGlobal = qualified, UsingDeclarations = !qualified,
		};
		ILSpyWholeProjectDecompiler decompiler = createDecompiler?.Invoke(settings, resolver, fs)
			?? new(settings, resolver, NullProjectFileWriter.Instance, fs);
		ScriptDecompilationResult result = ScriptDecompilation.Run(decompiler, resolver, manager.GetAssemblies().Single(), "/scripts", TextWriter.Null);
		return (result, fs);
	}
}
