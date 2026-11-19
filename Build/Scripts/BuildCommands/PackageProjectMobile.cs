using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutomationTool;
using UnrealBuildTool;
using UnrealSharp.Automation.Utilities;

namespace UnrealSharp.Automation.BuildCommands;

[Help("Publishes managed assemblies for Android or iOS before running BuildCookRun.")]
[Help("TargetPlatform=<Platform>", "Android or IOS. Defaults to Android.")]
[Help("TargetArchitecture=<Architecture>", "Arm64 or IOSSimulator. Defaults to Arm64.")]
[Help("UETargetType=<TargetType>", "Game, Client or Server. Defaults to Game.")]
[Help("UEBuildConfig=<Config>", "Target build configuration. Defaults to Development.")]
[Help("UserParams", "Additional arguments for the user solution publish.")]
public sealed class PackageProjectMobile : PackageProject
{
    public override void ExecuteBuild()
    {
        string PlatformName = ParseOptionalStringParam("TargetPlatform") ?? "Android";
        UnrealTargetPlatform Platform = UnrealTargetPlatform.Parse(PlatformName);
        if (Platform != UnrealTargetPlatform.Android && Platform != UnrealTargetPlatform.IOS)
        {
            throw new AutomationException("PackageProjectMobile supports Android and IOS. Received {0}.", Platform);
        }

        UnrealArch Architecture = UnrealArch.Parse(ParseOptionalStringParam("TargetArchitecture") ?? "arm64");
        if (Architecture != UnrealArch.Arm64 && !(Platform == UnrealTargetPlatform.IOS && Architecture == UnrealArch.IOSSimulator))
        {
            throw new AutomationException("The mobile runtime SDK supports Arm64 devices and IOSSimulator.");
        }
        string RuntimeIdentifier = DotNetSdkUtilities.GetDotNetRuntimeIdentifier(Platform, Architecture);

        TargetType Type = Enum.Parse<TargetType>(ParseOptionalStringParam("UETargetType") ?? "Game", ignoreCase: true);
        if (Type is not (TargetType.Game or TargetType.Client or TargetType.Server))
        {
            throw new AutomationException("A packaged mobile target must be Game, Client or Server.");
        }

        UnrealTargetConfiguration Configuration = Enum.Parse<UnrealTargetConfiguration>(
            ParseOptionalStringParam("UEBuildConfig") ?? "Development", ignoreCase: true);
        string ContentDirectory = Path.Combine(this.GetProjectRootFolder(), "Content", "Managed");
        string Destination = Path.Combine(ContentDirectory, Platform == UnrealTargetPlatform.Android ? "Android" : "IOS");
        string PublishDirectory = Path.Combine(this.GetUnrealSharpIntermediateDirectory(), "Publish");
        Directory.CreateDirectory(PublishDirectory);
        using FileStream PublishLock = File.Open(Path.Combine(PublishDirectory, "Mobile.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        DotNetSdkUtilities.CopyGlobalJson(this);

        string Temporary = Path.Combine(PublishDirectory, Path.GetRandomFileName());
        string Backup = Path.Combine(PublishDirectory, Platform + ".previous");
        if (Directory.Exists(Backup))
        {
            if (IsCompletePublish(Destination))
            {
                Directory.Delete(Backup, recursive: true);
            }
            else
            {
                if (Directory.Exists(Destination))
                {
                    Directory.Delete(Destination, recursive: true);
                }
                Directory.CreateDirectory(ContentDirectory);
                Directory.Move(Backup, Destination);
            }
        }

        string Stamp = Path.Combine(PublishDirectory, Platform + ".stamp");
        if (IsCompletePublish(Destination))
        {
            File.WriteAllText(Stamp, Guid.NewGuid().ToString());
        }

        Directory.CreateDirectory(Temporary);
        try
        {
            PackagingOptions Options = new(Destination, Type, Configuration, Platform, Architecture, false, ParseParamValues("UserParams"));
            // The native target chooses the runtime and supplies its own BCL through the SDK receipt.
            PublishMobileManagedCode(Options, Temporary, RuntimeIdentifier);
            if (!File.Exists(Path.Combine(Temporary, "UnrealSharp.Plugins.dll")))
            {
                throw new AutomationException("The managed publish did not produce UnrealSharp.Plugins.dll.");
            }

            if (File.Exists(Path.Combine(Temporary, "System.Private.CoreLib.dll")))
            {
                throw new AutomationException("The mobile managed publish must use the BCL from DotNetRuntimeSDK.");
            }

            HashSet<string> NativeAssets = GetNativeAssets(Temporary);
            if (NativeAssets.Count > 0)
            {
                throw new AutomationException("NuGet native assets require UE native-library staging and iOS signing. Once a native UE module owns these files, set ExcludeAssets=native on their PackageReference. Assets {0}.",
                    string.Join(", ", NativeAssets.Select(Path.GetFileName)));
            }

            Directory.CreateDirectory(ContentDirectory);
            if (Directory.Exists(Destination))
            {
                Directory.Move(Destination, Backup);
            }
            try
            {
                Directory.Move(Temporary, Destination);
            }
            catch
            {
                if (Directory.Exists(Backup))
                {
                    Directory.Move(Backup, Destination);
                }
                throw;
            }

            File.WriteAllText(Stamp, Guid.NewGuid().ToString());
            if (Directory.Exists(Backup))
            {
                Directory.Delete(Backup, recursive: true);
            }
        }
        finally
        {
            CleanBuildArtifacts(Temporary);
            CleanBuildArtifacts(Temporary + ".bindings");
            CleanBuildArtifacts(Temporary + ".user");
        }

        LoggerUtilities.LogUnrealSharpInfo($"Published managed assemblies to {Destination}. Build the native {Platform} target before staging to refresh its runtime files and receipt.");
    }

    private static HashSet<string> GetNativeAssets(string directory)
    {
        HashSet<string> Assets = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".so" or ".dylib" or ".a")
            .ToHashSet(StringComparer.Ordinal);
        foreach (string Manifest in Directory.GetFiles(directory, "*.deps.json"))
        {
            using JsonDocument Document = JsonDocument.Parse(File.ReadAllText(Manifest));
            if (!Document.RootElement.TryGetProperty("targets", out JsonElement Targets))
            {
                continue;
            }

            foreach (JsonProperty Target in Targets.EnumerateObject())
            {
                foreach (JsonProperty Library in Target.Value.EnumerateObject())
                {
                    if (Library.Value.TryGetProperty("native", out JsonElement Native))
                    {
                        Assets.UnionWith(Native.EnumerateObject().Select(asset => asset.Name));
                    }

                    if (Library.Value.TryGetProperty("runtimeTargets", out JsonElement RuntimeTargets))
                    {
                        foreach (JsonProperty Asset in RuntimeTargets.EnumerateObject())
                        {
                            if (Asset.Value.TryGetProperty("assetType", out JsonElement Type) && Type.GetString() == "native")
                            {
                                Assets.Add(Asset.Name);
                            }
                        }
                    }
                }
            }
        }
        return Assets;
    }

    private static bool IsCompletePublish(string directory)
        => File.Exists(Path.Combine(directory, "UnrealSharp.Plugins.dll"))
           && File.Exists(Path.Combine(directory, "UnrealSharpBuild.flag"));
}
