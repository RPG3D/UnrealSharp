using System.Reflection;
using System.Runtime.Loader;
using System.IO;

namespace UnrealSharp.Plugins;

// Unified for Mono and CoreCLR — no runtime-backend #if. The backend difference is
// probed at runtime: AssemblyDependencyResolver is hostpolicy-gated and throws on
// Mono (verified on mono 10.0.13), so TryCreateResolver returns null there and
// resolution falls back to a plain plugin-directory probe. The final miss
// (Default.LoadFromAssemblyName) is by design: in packaged Mono builds it reaches
// Mono's PAK-aware native preload hook. Do NOT turn that miss into an error.
public partial class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string? _pluginDir;

    public PluginLoadContext(string pluginName, string assemblyPath, bool isCollectible) : base(pluginName, isCollectible)
    {
        _resolver = TryCreateResolver(assemblyPath);
        if (_resolver == null)
        {
            _pluginDir = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        }
    }

    private static AssemblyDependencyResolver? TryCreateResolver(string assemblyPath)
    {
        try
        {
            return new AssemblyDependencyResolver(assemblyPath);
        }
        catch
        {
            // Mono (no hostpolicy) or no deps.json next to the assembly.
            return null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name))
        {
            return null;
        }

        Assembly? loadedAssembly = AssemblyCache.GetAssembly(assemblyName.Name!, this);
        if (loadedAssembly != null)
        {
            return loadedAssembly;
        }

        string? assemblyPath = _resolver != null
            ? _resolver.ResolveAssemblyToPath(assemblyName)
            : ResolveAssemblyPathFromPluginDir(assemblyName.Name!);

        Assembly? newAssembly;
        if (string.IsNullOrEmpty(assemblyPath))
        {
            newAssembly = Default.LoadFromAssemblyName(assemblyName);
        }
        else
        {
            using FileStream assemblyFile = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");

            if (File.Exists(pdbPath))
            {
                using FileStream pdbFile = File.Open(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                newAssembly = LoadFromStream(assemblyFile, pdbFile);
            }
            else
            {
                newAssembly = LoadFromAssemblyPath(assemblyPath);
            }
        }

        AssemblyCache.AddAssembly(newAssembly);
        return newAssembly;
    }

    private string? ResolveAssemblyPathFromPluginDir(string assemblyName)
    {
        string candidate = Path.Combine(_pluginDir!, assemblyName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }
}
