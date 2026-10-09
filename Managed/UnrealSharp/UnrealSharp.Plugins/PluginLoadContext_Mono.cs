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
    //
    // NOTE: this probe uses System.IO, so it only works for real directories (editor/dev
    // layouts). In packaged builds the plugin directory is inside the PAK and this
    // returns null BY DESIGN — the dependency then falls through to
    // Default.LoadFromAssemblyName in PluginLoadContext.Load(), which reaches Mono's
    // default resolution and finally the UFS/pak-aware preload hook
    // (CSMonoRuntime.cpp OnMonoAssemblyPreload). Do NOT turn the miss into an error.
    private string? ResolveAssemblyPath(string assemblyName)
    {
        string candidate = Path.Combine(_pluginDir, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }
}
#endif
