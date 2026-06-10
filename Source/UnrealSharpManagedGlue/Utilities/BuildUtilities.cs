using System;
using System.Collections.Generic;
using System.IO;
using UnrealBuildTool;

namespace UnrealSharpManagedGlue.Utilities;

public static class BuildUtilities
{
    public static void BuildBindings()
    {
        if (GeneratorStatics.TargetType != TargetRules.TargetType.Editor || !FileExporter.HasModifiedEngineGlue)
        {
            return;
        }

        ConsoleUtilities.Log("Engine glue has been modified since the last build. Rebuilding bindings...");

        List<KeyValuePair<string, string>> actionArgs =
        [
            new("Folders", Path.Combine(GeneratorStatics.ManagedPath, "UnrealSharp")),
            new("TargetConfiguration", GeneratorStatics.TargetConfiguration.ToString())
        ];

        // Inject UseMonoRuntime when Mono is configured, so UNREALSHARP_MONO is defined in all managed DLLs.
        // Read bUseMono directly from DefaultEngine.ini [UnrealSharp] section.
        string projectDir = GeneratorStatics.Factory.Session.ProjectDirectory!;
        if (ReadBoolFromEngineIni(projectDir, "UnrealSharp", "bUseMono"))
        {
            ConsoleUtilities.Log("Mono mode detected (DefaultEngine.ini bUseMono=true). Adding -p:UseMonoRuntime=true.");
            actionArgs.Add(new("ExtraArguments", "-p:UseMonoRuntime=true"));
        }

        UnrealSharpAutomationUtilities.InvokeUnrealSharpAutomation("BuildSolution", actionArgs);
    }

    /// <summary>
    /// Read a boolean key from DefaultEngine.ini under a given [Section].
    /// </summary>
    private static bool ReadBoolFromEngineIni(string projectDirectory, string section, string key)
    {
        string iniPath = Path.Combine(projectDirectory, "Config", "DefaultEngine.ini");
        if (!File.Exists(iniPath))
            return false;

        string sectionHeader = $"[{section}]";
        bool inSection = false;

        foreach (string rawLine in File.ReadLines(iniPath))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;

            int eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;

            string lineKey = line.Substring(0, eqIdx).Trim();
            if (!lineKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            string value = line.Substring(eqIdx + 1).Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        return false;
    }
    
    public static void GenerateUserSolution()
    {
        if (GeneratorStatics.TargetType != TargetRules.TargetType.Editor)
        {
            return;
        }
        
        UnrealSharpAutomationUtilities.InvokeUnrealSharpAutomation("GenerateUserSolution");
    }
}