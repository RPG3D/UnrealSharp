using System.Reflection;
using System.Runtime.Loader;
using System.IO;

namespace UnrealSharp.Plugins;

// Android / raw-coreclr host fallback for PluginLoadContext.
// AssemblyDependencyResolver requires CoreCLR hostpolicy, which the raw
// coreclr_initialize host does not provide — its ctor throws
// PlatformNotSupportedException on Android (same as Mono). When the resolver
// is null (Plugin.cs catches the exception), resolve dependencies by name
// from the plugin's own directory. Mirrors the Mono port's
// PluginLoadContext_Mono.cs directory-based resolution.
//
// Isolated in a partial class so PluginLoadContext.cs stays close to upstream.
public partial class PluginLoadContext

{
    private readonly string? _pluginDir;

    // Android / raw-coreclr host: resolver may be null (AssemblyDependencyResolver
    // unsupported); fall back to directory-based resolution from pluginAssemblyPath.
    public PluginLoadContext(string pluginName, AssemblyDependencyResolver? resolver, bool isCollectible, string pluginAssemblyPath)
        : base(pluginName, isCollectible)
    {
        _resolver = resolver;
        _pluginDir = Path.GetDirectoryName(pluginAssemblyPath);
    }

    // Creates an AssemblyDependencyResolver, returning null on platforms where it is
    // unsupported (Android/raw coreclr throws PlatformNotSupportedException from the ctor).
    // Called by Plugin.cs; a null resolver triggers directory-based fallback in ResolveAssemblyPath.
    public static AssemblyDependencyResolver? TryCreateResolver(string assemblyPath)
    {
        try
        {
            return new AssemblyDependencyResolver(assemblyPath);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    // Resolves an assembly path: via AssemblyDependencyResolver when available
    // (desktop/hostfxr), otherwise via directory scan (Android/raw coreclr).
    private string? ResolveAssemblyPath(AssemblyName assemblyName)
    {
        if (_resolver != null)
        {
            return _resolver.ResolveAssemblyToPath(assemblyName);
        }
        return ResolveAssemblyPathManual(assemblyName.Name!);
    }

    private string? ResolveAssemblyPathManual(string assemblyName)
    {
        if (string.IsNullOrEmpty(_pluginDir))
        {
            return null;
        }
        string candidate = Path.Combine(_pluginDir, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }
}
