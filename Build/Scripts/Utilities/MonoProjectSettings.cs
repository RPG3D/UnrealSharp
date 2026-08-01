// MonoProjectSettings.cs -- Mono-specific project configuration reader.
// Reads bUseMono from {ProjectDirectory}/Config/DefaultEngine.ini [UnrealSharp].
//
// This file is our own addition (not in upstream UnrealSharp) so it will never conflict
// with upstream merges. All Mono-aware call sites read their settings through this class.
//
// Usage:
//   MonoProjectSettings.IsMonoBuild(projectRoot) -- returns true when bUseMono=true

using System;
using System.IO;

namespace UnrealSharp.Automation.Utilities;

public static class MonoProjectSettings
{
    /// <summary>
    /// Returns true when the project is configured to use the Mono runtime.
    /// Reads [UnrealSharp] bUseMono=true from DefaultEngine.ini.
    /// </summary>
    public static bool IsMonoBuild(string projectDirectory)
    {
        return ReadEngineIniBool(projectDirectory, "UnrealSharp", "bUseMono");
    }

    /// <summary>
    /// Reads a boolean value from {ProjectDirectory}/Config/DefaultEngine.ini.
    /// Returns false when the file, section, or key is absent.
    /// Supports the standard UE ini format: key=value (with optional spaces around '=').
    /// </summary>
    private static bool ReadEngineIniBool(string projectDirectory, string section, string key)
    {
        string iniPath = Path.Combine(projectDirectory, "Config", "DefaultEngine.ini");

        if (!File.Exists(iniPath))
            return false;

        bool inSection = false;
        string sectionHeader = $"[{section}]";

        foreach (string raw in File.ReadLines(iniPath))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = string.Equals(line, sectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string k = line[..eq].Trim();
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;

            string v = line[(eq + 1)..].Trim();
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
