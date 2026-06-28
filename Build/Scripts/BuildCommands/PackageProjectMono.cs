// PackageProjectMono.cs -- Mono-specific packaging command for UnrealSharp.
// Handles Mono packaged builds: publishes managed DLLs to Content/Managed/{Platform}/
// and copies BCL from Source/ThirdParty/MonoSDK/{Platform}/runtime/ into the same folder
// so UAT stages everything into the PAK via DirectoriesToAlwaysStageAsUFS.
//
// Registered as a separate BuildCommand when bUseMono=true.
// Kept in a separate file to minimise merge conflicts with upstream PackageProject.cs.
//
// Supported platforms and their MonoSDK BCL source directories:
//
//   TargetPlatform   bIOSSimulator   MonoSDK BCL source dir
//   ──────────────   ─────────────   ───────────────────────────────────────
//   Mac              —               MonoSDK/Mac/runtime/
//   Android          —               MonoSDK/Android/runtime/
//   IOS              false           MonoSDK/IOS/runtime/
//   IOS              true            MonoSDK/IOSSimulator/runtime/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutomationTool;
using UnrealBuildTool;
using UnrealSharp.Automation.Utilities;
using UnrealSharp.Shared;

namespace UnrealSharp.Automation.BuildCommands;

[Help("Packages the UnrealSharp managed code for Mono runtime into an Unreal Engine packaged build.")]
[Help("ArchiveDirectory=<Path>", "REQUIRED. The base directory containing the packaged game.")]
[Help("UETargetType=<TargetType>", "REQUIRED. The Unreal Engine target type (Editor, Game, Client, Server).")]
[Help("UEBuildConfig=<Config>", "REQUIRED. The build configuration (Debug, Development, Shipping, Test).")]
[Help("TargetPlatform=<Platform>", "REQUIRED. Target platform (Mac, Android, IOS).")]
[Help("bIOSSimulator=<true|false>", "Optional. True if packaging for iOS Simulator. Defaults to false.")]
[Help("UserParams", "Optional. Additional parameters to forward to the user solution build.")]
public class PackageProjectMono : BuildCommand
{
    private const string ManagedFolderName = "Managed";
    private const string BindingsProjectFolder = "UnrealSharp";

    public sealed record MonoPackagingOptions(
        string ArchiveDirectory,
        TargetType TargetType,
        UnrealTargetConfiguration BuildConfiguration,
        UnrealTargetPlatform TargetPlatform,
        bool IsIOSSimulator,
        string[]? UserParams = null);

    public override void ExecuteBuild()
    {
        MonoPackagingOptions Options = ParseOptionsFromCommandLine();
        StartPackaging(Options);
    }

    private void StartPackaging(MonoPackagingOptions options)
    {
        ValidateOptions(options);
        LogOptions(options);

        DotNetSdkUtilities.CopyGlobalJson(this);

        // Resolve the effective managed sub-directory name for this build.
        // For IOS + bIOSSimulator=true → "IOSSimulator"; everything else → targetPlatform.
        string ManagedSubDir = GetManagedSubDir(options.TargetPlatform, options.IsIOSSimulator);

        // Mono publishes everything into Content/Managed/{managedSubDir}/ so UAT stages it into PAK.
        string ProjectContentDir = Path.Combine(options.ArchiveDirectory, "..", "..", "Content");
        string PublishFolder = Path.Combine(ProjectContentDir, ManagedFolderName, ManagedSubDir);

        // Clean and create output directory
        if (Directory.Exists(PublishFolder))
        {
            Directory.Delete(PublishFolder, recursive: true);
        }
        Directory.CreateDirectory(PublishFolder);

        IList<string> Arguments = BuildBaseArguments(options, PublishFolder);

        BuildBindingsSolution(Arguments, options.BuildConfiguration);
        BuildUserSolution(PublishFolder, Arguments, options.BuildConfiguration, options.UserParams);

        // Remove DLLs from publish output that also exist in BCL (MonoSDK).
        // BCL is staged as NonUFS (PAK 外) by MonoSDK.Build.cs; duplicates in Content/Managed/ would waste PAK space.
        DeduplicateBclDlls(options.TargetPlatform, options.IsIOSSimulator, PublishFolder);

        // Emit load order
        EmitUserLoadOrder(PublishFolder);

        LoggerUtilities.LogUnrealSharpInfo($"Mono packaging complete. Published files: {PublishFolder}");
    }

    private MonoPackagingOptions ParseOptionsFromCommandLine()
    {
        string ArchiveDirectory = ParseRequiredStringParam("ArchiveDirectory");
        TargetType TargetType = ParseRequiredEnumParamEnum<TargetType>("UETargetType");
        UnrealTargetConfiguration TargetConfiguration = ParseRequiredEnumParamEnum<UnrealTargetConfiguration>("UEBuildConfig");
        string? PlatformString = ParseRequiredStringParam("TargetPlatform");
        UnrealTargetPlatform TargetPlatform = UnrealTargetPlatform.Parse(PlatformString);
        bool IsIOSSimulator = ParseParam("bIOSSimulator");
        string[] UserParams = ParseParamValues("UserParams");

        return new MonoPackagingOptions(ArchiveDirectory, TargetType, TargetConfiguration, TargetPlatform, IsIOSSimulator, UserParams);
    }

    private static void LogOptions(MonoPackagingOptions options)
    {
        LoggerUtilities.LogUnrealSharpInfo("Packaging project for Mono runtime with parameters:");
        LoggerUtilities.LogUnrealSharpInfo($"Archive Directory: {options.ArchiveDirectory}");
        LoggerUtilities.LogUnrealSharpInfo($"Target Platform: {options.TargetPlatform}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Build Configuration: {options.BuildConfiguration}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Target Type: {options.TargetType}");
        LoggerUtilities.LogUnrealSharpInfo($"iOS Simulator: {options.IsIOSSimulator}");

        if (options.UserParams is { Length: > 0 })
        {
            LoggerUtilities.LogUnrealSharpInfo($"User Params: {string.Join(' ', options.UserParams)}");
        }
    }

    private void ValidateOptions(MonoPackagingOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.ArchiveDirectory);

        if (!Directory.Exists(options.ArchiveDirectory))
        {
            throw new DirectoryNotFoundException($"Archive directory does not exist: {options.ArchiveDirectory}");
        }

        // Validate supported platforms
        if (options.TargetPlatform != UnrealTargetPlatform.Mac &&
            options.TargetPlatform != UnrealTargetPlatform.Android &&
            options.TargetPlatform != UnrealTargetPlatform.IOS)
        {
            throw new NotSupportedException($"Mono packaging is not supported for platform '{options.TargetPlatform}'. " +
                "Supported platforms: Mac, Android, IOS.");
        }
    }

    private static string GetManagedSubDir(UnrealTargetPlatform targetPlatform, bool isIOSSimulator)
    {
        if (targetPlatform == UnrealTargetPlatform.IOS)
        {
            return isIOSSimulator ? "IOSSimulator" : "IOS";
        }
        return targetPlatform.ToString();
    }

    private static string GetBclSourceSubDir(UnrealTargetPlatform targetPlatform, bool isIOSSimulator)
    {
        return GetManagedSubDir(targetPlatform, isIOSSimulator);
    }

    private IList<string> BuildBaseArguments(MonoPackagingOptions options, string publishFolder)
    {
        // For Mono packaging we always use --no-self-contained, so only pure managed DLLs
        // are published. The BCL is copied separately by CopyMonoBcl().
        IList<string> arguments =
        [
            "--no-self-contained",

            "-p:UseMonoRuntime=true",
            "-p:UseDefaultOutputPath=true",

            $"-p:UETargetType={options.TargetType}",
            $"-p:UEBuildConfig={options.BuildConfiguration}",

            $"-p:PublishDir=\"{publishFolder}\"",
        ];

        return arguments;
    }

    private void BuildBindingsSolution(IList<string> arguments, UnrealTargetConfiguration buildConfig)
    {
        string BindingsPath = Path.Combine(this.GetUnrealSharpRootFolder(), ManagedFolderName, BindingsProjectFolder);
        BuildCommands.BuildSolution.RunBuild(BindingsPath, buildConfig, publish: true, arguments);
    }

    private void BuildUserSolution(string publishFolder, IList<string> buildArguments, UnrealTargetConfiguration buildConfig, string[]? userParams)
    {
        string ScriptFolder = this.GetProjectScriptFolder();

        IList<string> BuildUserSolutionArguments = buildArguments;
        if (userParams is { Length: > 0 })
        {
            BuildUserSolutionArguments = new List<string>(buildArguments);
            foreach (string UserParam in userParams)
            {
                BuildUserSolutionArguments.Add(UserParam);
            }
        }

        BuildCommands.BuildSolution.RunBuild(ScriptFolder, buildConfig, publish: true, BuildUserSolutionArguments);
    }

    private void EmitUserLoadOrder(string publishFolder)
    {
        List<FileInfo> RuntimeProjectFiles = this.GetManagedProjectFiles()
            .Where(file => !ProjectUtilities.IsEditorOnlyProject(file.FullName))
            .ToList();

        if (RuntimeProjectFiles.Count == 0)
        {
            LoggerUtilities.LogUnrealSharpInfo("No runtime projects found. Skipping user load order emission.");
            return;
        }

        LoadOrderOptions Options = new LoadOrderOptions
        {
            Collectible = false,
            Priority = LoadOrderUtilities.UserLoadOrderPriority
        };

        LoadOrderUtilities.TryEmitLoadOrder(RuntimeProjectFiles.Select(file => file.FullName), publishFolder, LoadOrderUtilities.UserLoadOrderName, Options);
    }

    /// <summary>
    /// Remove DLLs from the publish output that also exist in the BCL (MonoSDK) directory.
    /// BCL is staged as NonUFS (PAK 外) by MonoSDK.Build.cs RuntimeDependencies.
    /// Having the same DLL in both PAK (via Content/Managed/) and outside PAK wastes space
    /// and can cause assembly loading ambiguity.
    /// </summary>
    private void DeduplicateBclDlls(UnrealTargetPlatform targetPlatform, bool isIOSSimulator, string publishFolder)
    {
        string PluginDir = this.GetUnrealSharpRootFolder();
        string BclSubDir = GetBclSourceSubDir(targetPlatform, isIOSSimulator);
        string BclDir = Path.Combine(PluginDir, "Source", "ThirdParty", "MonoSDK", BclSubDir, "runtime");

        if (!Directory.Exists(BclDir))
        {
            LoggerUtilities.LogUnrealSharpWarning($"[Mono] BCL directory not found, skipping dedup: {BclDir}");
            return;
        }

        // Build set of BCL DLL filenames (case-insensitive)
        HashSet<string> bclDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string bclFile in Directory.EnumerateFiles(BclDir, "*.dll"))
        {
            bclDlls.Add(Path.GetFileName(bclFile));
        }

        int removed = 0;
        foreach (string publishDll in Directory.EnumerateFiles(publishFolder, "*.dll"))
        {
            string fileName = Path.GetFileName(publishDll);
            if (bclDlls.Contains(fileName))
            {
                File.Delete(publishDll);
                removed++;
            }
        }

        if (removed > 0)
        {
            LoggerUtilities.LogUnrealSharpInfo($"[Mono] Dedup: removed {removed} DLLs from publish output that exist in BCL ({BclDir})");
        }
    }

}
