using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnrealSharp.Automation.Utilities;

namespace UnrealSharp.Automation.BuildCommands;

// Mobile RID publishes share generated bindings with desktop builds.
// Keep their synchronization here so BuildSolution tracks upstream closely.
public partial class BuildSolution
{
    private static void RunWithBindingsLock(string solutionFolder, string? sharedBuildRoot, Action build)
    {
        using Mutex? BindingsMutex = CreateBindingsMutex(solutionFolder, sharedBuildRoot);
        bool OwnsMutex = false;
        try
        {
            if (BindingsMutex != null)
            {
                try
                {
                    if (!BindingsMutex.WaitOne(0))
                    {
                        LoggerUtilities.LogUnrealSharpInfo("Waiting for another UnrealSharp managed build to finish...");
                        BindingsMutex.WaitOne();
                    }
                }
                catch (AbandonedMutexException)
                {
                    LoggerUtilities.LogUnrealSharpWarning("The previous UnrealSharp managed build exited without releasing its lock. Continuing with the next build.");
                }
                OwnsMutex = true;
            }

            build();
        }
        finally
        {
            if (OwnsMutex)
            {
                BindingsMutex!.ReleaseMutex();
            }
        }
    }

    private static Mutex? CreateBindingsMutex(string solutionFolder, string? sharedBuildRoot)
    {
        DirectoryInfo? Folder = new DirectoryInfo(Path.GetFullPath(solutionFolder));
        while (sharedBuildRoot == null && Folder != null)
        {
            if (Folder.Name.Equals("UnrealSharp", StringComparison.OrdinalIgnoreCase)
                && Folder.Parent?.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase) == true
                && Folder.Parent.Parent is { } PluginRoot
                && File.Exists(Path.Combine(PluginRoot.FullName, "UnrealSharp.uplugin")))
            {
                sharedBuildRoot = PluginRoot.FullName;
            }
            Folder = Folder.Parent;
        }
        if (sharedBuildRoot == null)
        {
            return null;
        }

        // Resolve ancestor links too: multiple projects can link the same plugin source.
        string FullRoot = Path.GetFullPath(sharedBuildRoot);
        string CanonicalRoot = Path.GetPathRoot(FullRoot)!;
        foreach (string Component in FullRoot[CanonicalRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo Directory = new DirectoryInfo(Path.Combine(CanonicalRoot, Component));
            CanonicalRoot = Directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Directory.FullName;
        }
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            CanonicalRoot = CanonicalRoot.ToUpperInvariant();
        }

        string Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalRoot)));
        return new Mutex(initiallyOwned: false, "UnrealSharp.Bindings." + Key);
    }
}
