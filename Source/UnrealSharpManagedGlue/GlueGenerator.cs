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
        FunctionExporter.BindExtensionMethods();
        AutocastExporter.BindAutocasts();
        
        PackageHeadersTracker.SerializeModuleData();

        // Write a Timestamp file so the next run can skip re-export when nothing changed.
        string timestampPath = System.IO.Path.Combine(GeneratorStatics.PluginModule.OutputDirectory, "Timestamp");
        System.IO.File.WriteAllText(timestampPath, System.DateTime.UtcNow.ToString("O"));
    }
}