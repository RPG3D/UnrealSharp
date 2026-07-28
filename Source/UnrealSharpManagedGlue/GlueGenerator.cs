using UnrealSharpManagedGlue.Exporters;
using UnrealSharpManagedGlue.Utilities;

namespace UnrealSharpManagedGlue;

public static class GlueGenerator
{
    public static void GenerateBindings()
    {
        ExporterValidator.ValidateExporter();
        
        ConsoleUtilities.Log("Generating C# bindings...");
        PackageExporter.ExportPackages();
        PreprocessorExporter.ExportBuildDefines();
        PackageHeadersTracker.SerializeModuleData();

        // Write a Timestamp file so the next run can skip re-export when nothing changed.
        // Safe to keep enabled: the Timestamp + glue output are PER-TARGET
        // (Inc/UnrealSharpCore/UHT/{Platform}/{Target}/Timestamp, plus
        // Intermediate/UnrealSharp/UHT/{Editor,Game}/ for UE5Rules.Defines.props), so the
        // skip reuses the SAME target's previous glue - no editor<->game cross-contamination.
        // The MSBuildLocator leak was a separate managed-DLL issue (plugin-local Binaries
        // HintPath transitive dep), not glue - see MSBUILD_LOCATOR.md.
        //string timestampPath = System.IO.Path.Combine(GeneratorStatics.PluginModule.OutputDirectory, "Timestamp");
        //System.IO.File.WriteAllText(timestampPath, System.DateTime.UtcNow.ToString("O"));
    }
}