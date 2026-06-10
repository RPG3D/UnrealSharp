using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using UnrealSharp.Core;

namespace UnrealSharp.Plugins;

public static class PluginLoader
{
    public static readonly List<Plugin> LoadedPlugins = [];

    public static Assembly? LoadPlugin(string assemblyPath, bool isCollectible)
    {
        try
        {
            AssemblyName assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(assemblyPath));

            foreach (Plugin loadedPlugin in LoadedPlugins)
            {
                if (loadedPlugin.Assembly?.Target is not Assembly assembly)
                {
                    continue;
                }

                if (assembly.GetName() != assemblyName)
                {
                    continue;
                }

                LogUnrealSharpPlugins.Log($"Plugin {assemblyName} is already loaded.");
                return assembly;
            }
            
            Plugin plugin = new Plugin(assemblyName, isCollectible, assemblyPath);
            if (!plugin.Load() || plugin.Assembly == null || plugin.Assembly.Target is not Assembly loadedAssembly)
            {
                throw new InvalidOperationException($"Failed to load plugin: {assemblyName}");
            }

            LoadedPlugins.Add(plugin);

            if (!StartupJobManager.HasJobs(loadedAssembly))
            {
                // Sometimes the module initializer doesn't run automatically, so we force it here
                RuntimeHelpers.RunModuleConstructor(loadedAssembly.ManifestModule.ModuleHandle);
            }
            
            StartupJobManager.RunForAssembly(loadedAssembly);
            
            LogUnrealSharpPlugins.Log($"Successfully loaded plugin: '{assemblyName}' at '{assemblyPath}'");
            return loadedAssembly;
        }
        catch (Exception ex)
        {
#if UNREALSHARP_MONO
            // Under Mono, avoid calling the logging system here because FMsgExporter may not yet
            // be initialized (its .cctor calls NativeBinds.TryGetBoundFunction), causing
            // re-entrant initialization and a crash. Write to a temp file instead.
            try { System.IO.File.WriteAllText("/tmp/UnrealSharp_LoadPlugin_Exception.txt", ex.ToString()); } catch { }
            Console.WriteLine($"[Mono] LoadPlugin error: {ex}");
#else
            LogUnrealSharpPlugins.LogError($"An error occurred while loading the plugin: {ex.Message}");
#endif
        }

        return null;
    }
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference? RemovePlugin(string assemblyName)
    {
        WeakReference? weakRefLoadContext = null;
        foreach (Plugin loadedPlugin in LoadedPlugins)
        {
            if (loadedPlugin.AssemblyName.Name != assemblyName)
            {
                continue;
            }
            
            LoadedPlugins.Remove(loadedPlugin);
            weakRefLoadContext = loadedPlugin.Unload();
            break;
        }
        
        return weakRefLoadContext;
    }

    public static void UnloadPlugin(string assemblyPath)
    {
        string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

#if UNREALSHARP_MONO
        // On Mono, ALC.Unload() is asynchronous — the ALC is not collected until the next
        // GC cycle. We call Unload() (via RemovePlugin → Plugin.Unload) and return
        // immediately; Mono's GC will reclaim the ALC in the background.
        RemovePlugin(assemblyName);
        LogUnrealSharpPlugins.Log($"[Mono] Unload requested for {assemblyName}. GC will reclaim ALC asynchronously.");
        return;
#else
        TaskTracker.WaitForAllActiveTasks();
        WeakReference? weakAlc = RemovePlugin(assemblyName);

        if (weakAlc == null)
        {
            LogUnrealSharpPlugins.Log($"Plugin {assemblyName} is not loaded or already removed from registry.");
            return;
        }

        try
        {
            LogUnrealSharpPlugins.Log($"Unloading plugin {assemblyName}...");

            Stopwatch stopWatch = Stopwatch.StartNew();

            int maxAttempts = 8;
            int attempt = 0;
            
            while (weakAlc.IsAlive && attempt < maxAttempts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (!weakAlc.IsAlive)
                {
                    break;
                }
                
                Thread.Sleep(1);
                attempt++;
            }
            
            if (weakAlc.IsAlive)
            {
                // https://github.com/dotnet/runtime/issues/124876
                LogUnrealSharpPlugins.LogWarning(
                    $"'{assemblyName}' did not fully unload. " +
                    "A known Visual Studio/Rider debugger issue may be holding strong references to assembly types, preventing the old assembly from being collected. " +
                    "Hot reload will continue to work with some additional memory overhead. " +
                    "Re-attaching the debugger usually releases these references and allows the assembly to be GC'd on the next hot reload.");
                return;
            }

            LogUnrealSharpPlugins.Log($"{assemblyName} unloaded successfully in {stopWatch.ElapsedMilliseconds}ms.");
        }
        catch (Exception exception)
        {
            LogUnrealSharpPlugins.LogError($"An error occurred while unloading the plugin: {exception}");
        }
#endif
    }
    
    public static Plugin? FindPluginByName(string assemblyName)
    {
        foreach (Plugin loadedPlugin in LoadedPlugins)
        {
            if (loadedPlugin.AssemblyName.Name == assemblyName)
            {
                return loadedPlugin;
            }
        }

        return null;
    }
}
