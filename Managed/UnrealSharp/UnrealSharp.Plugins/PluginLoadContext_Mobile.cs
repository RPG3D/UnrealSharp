using System.Reflection;
using System.IO;

namespace UnrealSharp.Plugins;

// Embedded runtimes resolve staged assemblies without desktop hostpolicy.
public partial class PluginLoadContext
{
    private readonly string? _embeddedPluginDirectory;

    internal PluginLoadContext(string pluginName, string assemblyPath, bool isCollectible) : base(pluginName, isCollectible)
    {
        _embeddedPluginDirectory = Path.GetDirectoryName(assemblyPath);
    }

    private Assembly? ResolveEmbeddedAssembly(AssemblyName assemblyName, ref string? assemblyPath)
    {
        if (_embeddedPluginDirectory != null && Path.GetFileName(assemblyName.Name) == assemblyName.Name)
        {
            string candidatePath = Path.Combine(_embeddedPluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(candidatePath))
            {
                assemblyPath = candidatePath;
            }
        }

        if (!IsCollectible && _embeddedPluginDirectory != null && !string.IsNullOrEmpty(assemblyPath))
        {
            try
            {
                // Keep TPA and Mono's staged-assembly hook authoritative for shared assemblies.
                Assembly sharedAssembly = Default.LoadFromAssemblyName(assemblyName);
                AssemblyCache.AddAssembly(sharedAssembly);
                return sharedAssembly;
            }
            catch (FileNotFoundException)
            {
                // Fall back to the explicitly supplied physical directory below.
            }
        }
        return null;
    }
}
