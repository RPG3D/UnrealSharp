// PluginLoadContext_Mono.cs
// Mono-backend implementation of PluginLoadContext.
// Compiled only when UNREALSHARP_MONO is defined.
// All Mono-specific constructor and path resolution logic is isolated here
// so that PluginLoadContext.cs stays in sync with upstream with minimal merge conflicts.

#if UNREALSHARP_MONO
using System.Reflection;
using System.Runtime.Loader;

namespace UnrealSharp.Plugins;

public partial class PluginLoadContext : AssemblyLoadContext
{
    // Mono does not support AssemblyDependencyResolver (requires CoreCLR hostpolicy).
    // Use simple directory-based resolution instead.
    private readonly string _pluginDir;

    public PluginLoadContext(string pluginName, string pluginDir, bool isCollectible) : base(pluginName, isCollectible)
    {
        _pluginDir = pluginDir;
    }

    // Called from PluginLoadContext.Load() via the #if UNREALSHARP_MONO branch.
    private string? ResolveAssemblyPath(string assemblyName)
    {
        string candidate = Path.Combine(_pluginDir, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }
}
#endif
