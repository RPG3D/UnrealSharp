using System.Runtime.InteropServices;

namespace UnrealSharp.Plugins;

// SDK-hosted entry points share the common callback setup in Main.cs.
internal static partial class Main
{
    internal static bool HasHostPolicy { get; private set; } = true;

    // The raw CoreCLR API resolves the entry point by public method name.
    [UnmanagedCallersOnly]
    public static unsafe void InitializeMobileCoreClr(
        byte* workingDirectoryUtf8,
        PluginsCallbacks* pluginCallbacks,
        nint bindsCallbacks,
        nint managedCallbacks,
        FCSInitializationResult* result)
        => Initialize(workingDirectoryUtf8, pluginCallbacks, bindsCallbacks, managedCallbacks, result, hasHostPolicy: false);

    // The Mono embedding API invokes an ordinary managed method.
    internal static unsafe void InitializeMono(
        byte* workingDirectoryUtf8,
        PluginsCallbacks* pluginCallbacks,
        nint bindsCallbacks,
        nint managedCallbacks,
        FCSInitializationResult* result)
        => Initialize(workingDirectoryUtf8, pluginCallbacks, bindsCallbacks, managedCallbacks, result, hasHostPolicy: false);
}
