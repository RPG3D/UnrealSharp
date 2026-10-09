// PackageProjectMono.cs - Mono packaging command.
// Reverse flow: publish managed DLLs to Content/Managed/<Platform> BEFORE UAT,
// so MonoSDK.Build.cs stages them (project DLLs as UFS into the PAK; the BCL is
// staged separately as NonUFS by MonoSDK.Build.cs from the MonoSDK submodule).
//
// Differences from PackageProject (CoreCLR):
//   - No ArchiveDirectory: publishes before UAT instead of into an archived build.
//   - -p:UseMonoRuntime=true so UNREALSHARP_MONO is defined in all managed DLLs.
//   - BCL-duplicate DLLs are removed from the publish output (BCL lives outside the PAK).
//   - Editor-only NuGet leakage (Microsoft.Build*/Microsoft.CodeAnalysis*/Newtonsoft.Json)
//     is stripped recursively.
//   - Validates the MonoSDK native library exists for the target platform up front.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutomationTool;
using UnrealBuildTool;
using UnrealSharp.Automation.Utilities;
using UnrealSharp.Shared;

namespace UnrealSharp.Automation.BuildCommands;

[Help("Packages UnrealSharp managed code for the Mono runtime. Reverse flow: publish BEFORE UAT.")]
[Help("UEBuildConfig=<Config>", "REQUIRED. Build configuration (Debug, Development, Shipping, Test).")]
[Help("TargetPlatform=<Platform>", "REQUIRED. Target platform (Android, IOS, Mac, Win64). iOS is real-device only (UE 5.8 dropped the simulator).")]
[Help("UserParams", "Optional. Additional parameters (-UserParams=\"-p:Property=Value\").")]
public class PackageProjectMono : BuildCommand
{
    private const string ManagedFolderName = "Managed";
    private const string BindingsProjectFolder = "UnrealSharp";
    private const int CleanupMaxAttempts = 5;
    private const int CleanupRetryDelayMs = 1000;
    private const TargetType MobileTargetType = TargetType.Game;

    public sealed record MonoPackagingOptions(
        UnrealTargetConfiguration BuildConfiguration,
        UnrealTargetPlatform TargetPlatform,
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

        string PublishFolder = GetPublishFolder(options.TargetPlatform);
        CleanBuildArtifacts(PublishFolder);

        IList<string> Arguments = BuildBaseArguments(options, PublishFolder);
        Arguments.Add("--no-self-contained");
        Arguments.Add("-p:GenerateDocumentation=false");

        BuildBindingsSolution(Arguments, options.BuildConfiguration);
        BuildUserBindings(PublishFolder, options, Arguments);
        BuildUserSolution(PublishFolder, Arguments, options.BuildConfiguration, options.UserParams);

        DeduplicateBclDlls(options.TargetPlatform, PublishFolder);
        StripEditorOnlyDlls(PublishFolder);
        EmitInstalledFlagFile(PublishFolder);

        LoggerUtilities.LogUnrealSharpInfo($"Mono packaging complete. Published files: {PublishFolder}");
    }

    private string GetPublishFolder(UnrealTargetPlatform platform)
    {
        string platformDir = GetManagedSubDir(platform);
        return Path.Combine(this.GetProjectRootFolder(), "Content", "Managed", platformDir);
    }

    private static string GetManagedSubDir(UnrealTargetPlatform targetPlatform)
        => targetPlatform.ToString();

    private MonoPackagingOptions ParseOptionsFromCommandLine()
    {
        UnrealTargetConfiguration TargetConfiguration = ParseRequiredEnumParamEnum<UnrealTargetConfiguration>("UEBuildConfig");
        string PlatformString = ParseRequiredStringParam("TargetPlatform");
        UnrealTargetPlatform TargetPlatform = UnrealTargetPlatform.Parse(PlatformString);
        string[] UserParams = ParseParamValues("UserParams");
        return new MonoPackagingOptions(TargetConfiguration, TargetPlatform, UserParams);
    }

    private static void LogOptions(MonoPackagingOptions options)
    {
        LoggerUtilities.LogUnrealSharpInfo("Mono packaging project with parameters:");
        LoggerUtilities.LogUnrealSharpInfo($"Target Platform: {options.TargetPlatform}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Build Configuration: {options.BuildConfiguration}");
        LoggerUtilities.LogUnrealSharpInfo($"Output: Content/Managed/{GetManagedSubDir(options.TargetPlatform)}");
        if (options.UserParams is { Length: > 0 })
            LoggerUtilities.LogUnrealSharpInfo($"User Params: {string.Join(' ', options.UserParams)}");
    }

    private void ValidateOptions(MonoPackagingOptions options)
    {
        if (options.TargetPlatform != UnrealTargetPlatform.Mac &&
            options.TargetPlatform != UnrealTargetPlatform.Android &&
            options.TargetPlatform != UnrealTargetPlatform.IOS &&
            options.TargetPlatform != UnrealTargetPlatform.Win64)
            throw new NotSupportedException($"Mono packaging not supported for '{options.TargetPlatform}'. Supported: Mac, Android, IOS, Win64.");

        // REVIEW-fix: fail fast with a clear message when the MonoSDK native runtime is missing,
        // instead of packaging "successfully" and Fatal'ing at runtime.
        string PlatformDir = GetManagedSubDir(options.TargetPlatform);
        string SdkDir = Path.Combine(this.GetUnrealSharpRootFolder(), "Source", "ThirdParty", "MonoSDK", PlatformDir);

        if (!Directory.Exists(SdkDir))
            throw new DirectoryNotFoundException(
                $"MonoSDK directory not found for platform '{PlatformDir}': {SdkDir}. " +
                "Populate Source/ThirdParty/MonoSDK (see README_MONO.md) before packaging.");

        string RuntimeDir = Path.Combine(SdkDir, "runtime");
        if (!Directory.Exists(RuntimeDir))
            throw new DirectoryNotFoundException(
                $"MonoSDK BCL runtime directory not found: {RuntimeDir}. The SDK download/build is incomplete.");
    }

    private static void CleanBuildArtifacts(string folder)
    {
        if (!Directory.Exists(folder)) return;
        LoggerUtilities.LogUnrealSharpInfo($"Cleaning existing output at '{folder}'.");
        for (int Attempt = 1; Attempt <= CleanupMaxAttempts; Attempt++)
        {
            try { Directory.Delete(folder, recursive: true); return; }
            catch (Exception Ex)
            {
                if (Attempt == CleanupMaxAttempts)
                    throw new IOException($"Failed to clean '{folder}' after {CleanupMaxAttempts} attempts.", Ex);
                LoggerUtilities.LogUnrealSharpWarning($"Attempt {Attempt} to clean '{folder}' failed. Retrying... Exception: {Ex.Message}");
                System.Threading.Thread.Sleep(CleanupRetryDelayMs);
            }
        }
    }

    private IList<string> BuildBaseArguments(MonoPackagingOptions options, string publishFolder)
    {
        return new List<string>
        {
            "-m:1",
            "-p:UseMonoRuntime=true",
            // Publish ONLY into PublishFolder. Without this the bindings build also writes
            // Game-target outputs into the plugin's own flat Binaries/Managed/<tfm>/,
            // clobbering the editor-glue DLLs the cook's editor process needs.
            "-p:UseDefaultOutputPath=true",
            $"-p:UETargetType={MobileTargetType}",
            $"-p:UEBuildConfig={options.BuildConfiguration}",
            $"-p:PublishDir=\"{publishFolder}\"",
        };
    }

    private void BuildBindingsSolution(IList<string> arguments, UnrealTargetConfiguration buildConfig)
    {
        string BindingsPath = Path.Combine(this.GetUnrealSharpRootFolder(), ManagedFolderName, BindingsProjectFolder);
        BuildCommands.BuildSolution.RunBuild(BindingsPath, buildConfig, publish: true, arguments);
    }

    private void BuildUserBindings(string publishFolder, MonoPackagingOptions options, IList<string> buildArguments)
    {
        if (this.IsInstalledUnrealSharpBuild())
        {
            CopyInstalledGlue(publishFolder);
            return;
        }
        LoggerUtilities.LogUnrealSharpInfo("Source build detected. Building glue from generated projects...");
        BuildUserGlue.Build(this, MobileTargetType, options.BuildConfiguration, publishFolder, false, buildArguments);
    }

    private void BuildUserSolution(string publishFolder, IList<string> buildArguments, UnrealTargetConfiguration buildConfig, string[]? userParams)
    {
        string ScriptFolder = this.GetProjectScriptFolder();
        IList<string> args = buildArguments;
        if (userParams is { Length: > 0 })
        {
            args = new List<string>(buildArguments);
            foreach (string p in userParams) args.Add(p);
        }
        BuildCommands.BuildSolution.RunBuild(ScriptFolder, buildConfig, publish: true, args);
        EmitUserLoadOrder(publishFolder);
    }

    private void EmitUserLoadOrder(string publishFolder)
    {
        List<FileInfo> RuntimeProjectFiles = this.GetManagedProjectFiles()
            .Where(file => !ProjectUtilities.IsEditorOnlyProject(file.FullName)).ToList();
        if (RuntimeProjectFiles.Count == 0)
        {
            LoggerUtilities.LogUnrealSharpInfo("No runtime projects. Skipping user load order.");
            return;
        }
        LoadOrderOptions Options = new() { Collectible = false, Priority = LoadOrderUtilities.UserLoadOrderPriority };
        LoadOrderUtilities.TryEmitLoadOrder(RuntimeProjectFiles.Select(f => f.FullName), publishFolder, LoadOrderUtilities.UserLoadOrderName, Options);
    }

    private void CopyInstalledGlue(string publishFolder)
    {
        string GlueFileName = AssemblyUtilities.MakeLoadOrderFileName(LoadOrderUtilities.GlueLoadOrderName);
        string GlueSource = PathUtilities.BuildOutputPath(this.GetProjectRootFolder());
        string GlueManifest = Path.Combine(GlueSource, GlueFileName);
        if (!File.Exists(GlueManifest))
        {
            LoggerUtilities.LogUnrealSharpWarning($"Runtime glue manifest not found at {GlueManifest}. Was the C++ project built at least once? Packaged build may be missing generated glue.");
            return;
        }
        File.Copy(GlueManifest, Path.Combine(publishFolder, GlueFileName), true);
        foreach (string AssemblyName in AssemblyUtilities.ReadLoadOrder(GlueManifest))
        {
            CopyIfExists(Path.Combine(GlueSource, AssemblyName + ".dll"), publishFolder);
            CopyIfExists(Path.Combine(GlueSource, AssemblyName + ".pdb"), publishFolder);
        }
    }

    private static void CopyIfExists(string sourceFile, string destFolder)
    {
        if (File.Exists(sourceFile))
            File.Copy(sourceFile, Path.Combine(destFolder, Path.GetFileName(sourceFile)), true);
    }

    private void EmitInstalledFlagFile(string publishFolder)
    {
        File.WriteAllText(Path.Combine(publishFolder, BuildUtilities.UnrealSharpBuildFlagFileName), string.Empty);
    }

    /// <summary>Remove DLLs from the publish output that also exist in the MonoSDK BCL.
    /// The BCL is staged as NonUFS by MonoSDK.Build.cs; duplicates would waste PAK space.
    /// REVIEW-fix: enumerates recursively (subfolders too).</summary>
    private void DeduplicateBclDlls(UnrealTargetPlatform targetPlatform, string publishFolder)
    {
        string PluginDir = this.GetUnrealSharpRootFolder();
        string BclSubDir = GetManagedSubDir(targetPlatform);
        string BclDir = Path.Combine(PluginDir, "Source", "ThirdParty", "MonoSDK", BclSubDir, "runtime");
        if (!Directory.Exists(BclDir))
        {
            LoggerUtilities.LogUnrealSharpWarning($"[Mono] BCL directory not found, skipping dedup: {BclDir}");
            return;
        }
        HashSet<string> bclDlls = new(StringComparer.OrdinalIgnoreCase);
        foreach (string bclFile in Directory.EnumerateFiles(BclDir, "*.dll", SearchOption.AllDirectories))
            bclDlls.Add(Path.GetFileName(bclFile));

        int removed = 0;
        foreach (string publishDll in Directory.EnumerateFiles(publishFolder, "*.dll", SearchOption.AllDirectories))
        {
            if (bclDlls.Contains(Path.GetFileName(publishDll)))
            {
                File.Delete(publishDll);
                removed++;
            }
        }
        if (removed > 0)
            LoggerUtilities.LogUnrealSharpInfo($"[Mono] Dedup: removed {removed} DLLs from publish output (BCL in {BclDir})");
    }

    /// <summary>Remove editor-only NuGet packages that leak into the publish output through
    /// flat Binaries HintPath references. MSBuildLocator is guarded by #if !UNREALSHARP_MONO in
    /// source but the NuGet reference still resolves the DLLs transitively.
    /// REVIEW-fix: recursive search + Newtonsoft.Json (MSBuildLocator transitive dependency).</summary>
    private static void StripEditorOnlyDlls(string publishFolder)
    {
        string[] stripPatterns = { "Microsoft.Build*.dll", "Microsoft.CodeAnalysis*.dll", "Newtonsoft.Json.dll" };
        int removed = 0;
        foreach (string pattern in stripPatterns)
        {
            foreach (string file in Directory.GetFiles(publishFolder, pattern, SearchOption.AllDirectories))
            {
                File.Delete(file);
                removed++;
            }
        }
        if (removed > 0)
            LoggerUtilities.LogUnrealSharpInfo($"[Mono] Stripped {removed} editor-only DLLs from publish output.");
    }
}
