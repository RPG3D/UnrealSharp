
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using UnrealSharp.Binds;
using UnrealSharp.Core;

#if !PACKAGE && !UNREALSHARP_MONO
using Microsoft.Build.Locator;
#endif

namespace UnrealSharp.Plugins;

public static class Main
{
    [UnmanagedCallersOnly]
    private static unsafe NativeBool InitializeUnrealSharp(char* workingDirectoryPath, nint assemblyPath, PluginsCallbacks* pluginCallbacks, IntPtr bindsCallbacks, IntPtr managedCallbacks)
    {
        try
        {
            #if WITH_EDITOR && !UNREALSHARP_MONO
            IEnumerable<VisualStudioInstance> instances = MSBuildLocator.QueryVisualStudioInstances();
            VisualStudioInstance? visualStudioInstance = instances.OrderByDescending(i => i.Version).FirstOrDefault();

            if (visualStudioInstance is not null)
            {
                MSBuildLocator.RegisterInstance(visualStudioInstance);
            }
            else
            {
                MSBuildLocator.RegisterDefaults();
            }
            #endif
            
            AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", new string(workingDirectoryPath));
            
            PluginsCallbacks.Initialize(pluginCallbacks);
            ManagedCallbacks.Initialize(managedCallbacks);
            NativeBinds.Initialize(bindsCallbacks);

            Console.WriteLine("UnrealSharp initialized successfully.");
            return NativeBool.True;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
#if UNREALSHARP_MONO
            // Under Mono, write exception to a temp file so CSMonoRuntime.cpp can read it for diagnostics.
            // On CoreCLR the exception propagates through hostfxr and is logged via standard channels.
            try { System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "UnrealSharp_InitException.txt"), exception.ToString()); } catch { }
#endif
            return NativeBool.False;
        }
    }
}
