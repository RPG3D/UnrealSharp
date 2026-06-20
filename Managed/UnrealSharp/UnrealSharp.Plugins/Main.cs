
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices;
using UnrealSharp.Binds;
using UnrealSharp.Core;

#if WITH_EDITOR
using Microsoft.Build.Locator;
#endif

namespace UnrealSharp.Plugins;

public static class Main
{
    // Public so coreclr_create_delegate can resolve it on Android (raw CoreCLR path).
    // The desktop hostfxr path resolves via load_assembly_and_get_function_pointer and
    // does not require public visibility, but [UnmanagedCallersOnly] methods cannot be
    // invoked from managed code anyway, so making it public has no managed-call surface.
    [UnmanagedCallersOnly]
    public static unsafe NativeBool InitializeUnrealSharp(char* workingDirectoryPath, nint assemblyPath, PluginsCallbacks* pluginCallbacks, IntPtr bindsCallbacks, IntPtr managedCallbacks)
    {
        try
        {
            #if WITH_EDITOR
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
            return NativeBool.False;
        }
    }
}
