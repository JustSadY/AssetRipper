using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace AssetRipper.Import.Structure.Assembly.Managers;

/// <summary>
/// Rejects a native binary paired with foreign global metadata before any processing layer or output runs.
/// LibCpp2IL can locate registration structures and then silently zero-fill unreadable method pointer tables;
/// these checks compare the binary's own tables with the metadata instead of trusting that search.
/// </summary>
public static class Il2CppNativePairValidator
{
	private const int MaxReportedFailures = 8;

	public static Il2CppPairValidation Validate(ApplicationAnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		Il2CppMetadata metadata = context.Metadata;
		Il2CppBinary binary = context.Binary;
		List<string> failures = [];
		int images = metadata.imageDefinitions.Length;
		int types = metadata.typeDefs.Length;
		int methods = metadata.methodDefs.Length;

		if (images == 0 || types == 0)
		{
			failures.Add("The metadata declares no images or type definitions.");
		}
		if (binary.TypeDefinitionSizePointers.Length != types)
		{
			failures.Add($"The binary type-size table has {binary.TypeDefinitionSizePointers.Length} entries; the metadata declares {types} type definitions.");
		}

		// Code generation modules exist from metadata 24.2. Older pairs only receive the global checks above.
		bool hasModules = metadata.MetadataVersion >= 24.2f;
		int checkedModules = 0;
		long nonZeroPointers = 0, unmappedPointers = 0, declaredMethods = 0;
		if (hasModules)
		{
			for (int imageIndex = 0; imageIndex < images; imageIndex++)
			{
				Il2CppImageDefinition image = metadata.imageDefinitions[imageIndex];
				string name = image.Name ?? "";
				long imageMethods = image.Types.Sum(type => (long)type.MethodCount);
				declaredMethods += imageMethods;

				Il2CppCodeGenModule? module;
				try
				{
					module = binary.GetCodegenModuleByName(name);
				}
				catch (KeyNotFoundException)
				{
					module = null;
				}
				if (module is null)
				{
					failures.Add($"The binary has no code generation module for metadata image '{name}'.");
					continue;
				}

				// From metadata 27, LibCpp2IL finds a method's module by name. Before that it indexes the module table
				// with the image's assembly index, so only there must the two orders agree.
				int moduleIndex = binary.GetCodegenModuleIndex(module);
				if (metadata.MetadataVersion < 27f && moduleIndex != image.assemblyIndex)
				{
					failures.Add($"Module '{name}' is at index {moduleIndex}; its metadata image uses assembly index {image.assemblyIndex}.");
				}
				if (module.methodPointerCount != imageMethods)
				{
					failures.Add($"Module '{name}' has {module.methodPointerCount} method pointers; its metadata image declares {imageMethods} methods.");
				}

				ulong[] pointers = binary.GetCodegenModuleMethodPointers(moduleIndex);
				if (pointers.LongLength != module.methodPointerCount)
				{
					failures.Add($"Module '{name}' pointer table has {pointers.LongLength} readable entries; it declares {module.methodPointerCount}.");
				}
				foreach (ulong pointer in pointers)
				{
					if (pointer == 0)
					{
						continue;
					}
					nonZeroPointers++;
					if (!binary.TryMapVirtualAddressToRaw(pointer, out long raw) || raw < 0 || raw >= binary.RawLength)
					{
						unmappedPointers++;
					}
				}
				checkedModules++;
			}

			if (declaredMethods != methods)
			{
				failures.Add($"Metadata images declare {declaredMethods} methods; the method table has {methods}.");
			}
			if (methods > 0 && nonZeroPointers == 0)
			{
				failures.Add("No module method pointer is set; the pointer tables were not readable for this metadata.");
			}
			if (unmappedPointers > 0)
			{
				failures.Add($"{unmappedPointers} of {nonZeroPointers} method pointers do not map into the binary.");
			}
		}

		if (failures.Count > 0)
		{
			string summary = string.Join(" ", failures.Take(MaxReportedFailures));
			if (failures.Count > MaxReportedFailures)
			{
				summary += $" ({failures.Count - MaxReportedFailures} more.)";
			}
			throw new Il2CppPairMismatchException(summary);
		}

		return new(hasModules ? "Passed" : "Partial", binary.InstructionSetId.Name, binary.PointerSizeBytes, metadata.MetadataVersion,
			images, types, methods, checkedModules, nonZeroPointers);
	}
}

public sealed class Il2CppPairMismatchException(string message) : Exception("The native binary and global metadata are not a structural pair. " + message);
