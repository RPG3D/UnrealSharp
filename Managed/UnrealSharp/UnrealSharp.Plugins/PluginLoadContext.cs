using System.Reflection;
using System.Runtime.Loader;
using System.IO;

namespace UnrealSharp.Plugins;

public partial class PluginLoadContext : AssemblyLoadContext
{
#if !UNREALSHARP_MONO
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginName, AssemblyDependencyResolver resolver, bool isCollectible)
        : base(pluginName, isCollectible)
    {
        _resolver = resolver;
    }
#endif

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

#if UNREALSHARP_MONO
        string? assemblyPath = ResolveAssemblyPath(assemblyName.Name!);
#else
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
#endif
        
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
}