using System.Reflection;
using System.Runtime.Loader;
using System.IO;

namespace UnrealSharp.Plugins;

// NOTE: Android/raw-coreclr fallback (directory-based resolution when
// AssemblyDependencyResolver is unavailable) lives in the partial
// PluginLoadContext_Android.cs. This file stays close to upstream so merges
// conflict only on the minimal _resolver-nullable + ResolveAssemblyPath hook.
public partial class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginName, AssemblyDependencyResolver resolver, bool isCollectible)
        : base(pluginName, isCollectible)
    {
        _resolver = resolver;
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

        string? assemblyPath = ResolveAssemblyPath(assemblyName);

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
