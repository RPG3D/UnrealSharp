// PackageProjectMono.cs - Mono packaging command.
// Reverse flow: publish managed DLLs to Binaries/Managed/<Platform> BEFORE UAT, so
// MonoSDK.Build.cs can map them (and the SDK's BCL) to the UFS virtual path
// Content/Managed/<Platform>/ without a physical copy under Content/. Android and iOS
// then serve both the BCL and the project assemblies from the PAK via the preload
// hook; editor builds keep reading the BCL from the SDK directory.
//
// Differences from PackageProject (CoreCLR):
//   - No ArchiveDirectory: publishes before UAT instead of into an archived build.
//   - -p:UseMonoRuntime=true so UNREALSHARP_MONO is defined in all managed DLLs.
//   - BCL-duplicate DLLs are removed from the publish output (the BCL is staged separately).
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
[Help("TargetArch=<arch>", "Optional. MonoSDK architecture directory (arm64, x64). Defaults to the platform's only arch; Mac defaults to the host architecture.")]
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
        string[]? UserParams = null,
        string? TargetArch = null);

    /// <summary>Resolved MonoSDK directory: ArchName is null for the legacy flat layout.</summary>
    private sealed record SdkLayout(string PlatformName, string? ArchName)
    {
        public string RelativeDir => ArchName == null ? PlatformName : $"{PlatformName}/{ArchName}";

        public string ResolvePath(string sdkRoot) => ArchName == null
            ? Path.Combine(sdkRoot, PlatformName)
            : Path.Combine(sdkRoot, PlatformName, ArchName);
    }

    public override void ExecuteBuild()
    {
        MonoPackagingOptions Options = ParseOptionsFromCommandLine();
        StartPackaging(Options);
    }

    private void StartPackaging(MonoPackagingOptions options)
    {
        SdkLayout Sdk = ValidateOptions(options);
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

        DeduplicateBclDlls(Sdk, PublishFolder);
        StripEditorOnlyDlls(PublishFolder);
        EmitInstalledFlagFile(PublishFolder);

        LoggerUtilities.LogUnrealSharpInfo($"Mono packaging complete. Published files: {PublishFolder}");
    }

    private string GetPublishFolder(UnrealTargetPlatform platform)
    {
        string platformDir = GetManagedSubDir(platform);
        return Path.Combine(this.GetProjectRootFolder(), "Binaries", "Managed", platformDir);
    }

    private static string GetManagedSubDir(UnrealTargetPlatform targetPlatform)
        => targetPlatform.ToString();

    private MonoPackagingOptions ParseOptionsFromCommandLine()
    {
        UnrealTargetConfiguration TargetConfiguration = ParseRequiredEnumParamEnum<UnrealTargetConfiguration>("UEBuildConfig");
        string PlatformString = ParseRequiredStringParam("TargetPlatform");
        UnrealTargetPlatform TargetPlatform = UnrealTargetPlatform.Parse(PlatformString);
        string[] UserParams = ParseParamValues("UserParams");
        string? TargetArch = ParseParamValue("TargetArch")?.ToLowerInvariant();
        return new MonoPackagingOptions(TargetConfiguration, TargetPlatform, UserParams, TargetArch);
    }

    private static void LogOptions(MonoPackagingOptions options)
    {
        LoggerUtilities.LogUnrealSharpInfo("Mono packaging project with parameters:");
        LoggerUtilities.LogUnrealSharpInfo($"Target Platform: {options.TargetPlatform}");
        if (!string.IsNullOrEmpty(options.TargetArch))
            LoggerUtilities.LogUnrealSharpInfo($"Target Architecture: {options.TargetArch}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Build Configuration: {options.BuildConfiguration}");
        LoggerUtilities.LogUnrealSharpInfo($"Output: Binaries/Managed/{GetManagedSubDir(options.TargetPlatform)}");
        if (options.UserParams is { Length: > 0 })
            LoggerUtilities.LogUnrealSharpInfo($"User Params: {string.Join(' ', options.UserParams)}");
    }

    private SdkLayout ValidateOptions(MonoPackagingOptions options)
    {
        if (options.TargetPlatform != UnrealTargetPlatform.Mac &&
            options.TargetPlatform != UnrealTargetPlatform.Android &&
            options.TargetPlatform != UnrealTargetPlatform.IOS &&
            options.TargetPlatform != UnrealTargetPlatform.Win64)
            throw new NotSupportedException($"Mono packaging not supported for '{options.TargetPlatform}'. Supported: Mac, Android, IOS, Win64.");

        if (!string.IsNullOrEmpty(options.TargetArch) &&
            options.TargetArch != "x64" &&
            options.TargetArch != "arm64")
            throw new NotSupportedException($"Unsupported TargetArch '{options.TargetArch}'. Expected x64 or arm64.");

        // Fail fast with a clear message when the MonoSDK runtime for this platform/arch is
        // missing, instead of packaging "successfully" and Fatal'ing at runtime.
        // Android requires the arch layout: MonoSDK_APL.xml copies the runtime .so files from
        // Android/arm64/ verbatim, so a legacy flat Android/ SDK would build fine and then
        // ship an APK with no native Mono runtime at all.
        bool bRequireArchLayout = options.TargetPlatform == UnrealTargetPlatform.Android;
        SdkLayout Sdk = ResolveSdkLayout(options.TargetPlatform, options.TargetArch, bRequireArchLayout);

        LoggerUtilities.LogUnrealSharpInfo($"[Mono] MonoSDK layout: {Sdk.RelativeDir}");

        string RuntimeDir = Path.Combine(Sdk.ResolvePath(GetMonoSdkRootFolder()), "runtime");
        if (!Directory.Exists(RuntimeDir))
            throw new DirectoryNotFoundException(
                $"MonoSDK BCL runtime directory not found: {RuntimeDir}. The SDK download/build is incomplete.");

        return Sdk;
    }

    private string GetMonoSdkRootFolder()
        => Path.Combine(this.GetUnrealSharpRootFolder(), "Source", "ThirdParty", "MonoSDK");

    /// <summary>Architecture directory name for the target platform. Single-arch platforms are
    /// fixed by the target; macOS ships both Mac/x64 and Mac/arm64, so without -TargetArch the
    /// host architecture is assumed (the editor-triggered flow packages for itself).</summary>
    private static string? GetSdkArchName(UnrealTargetPlatform platform, string? requestedArch)
    {
        if (!string.IsNullOrEmpty(requestedArch))
            return requestedArch;

        if (platform == UnrealTargetPlatform.Android || platform == UnrealTargetPlatform.IOS)
            return "arm64";

        if (platform == UnrealTargetPlatform.Win64 || platform == UnrealTargetPlatform.Linux)
            return "x64";

        if (platform == UnrealTargetPlatform.Mac)
        {
            try
            {
                return UnrealArch.Host.Value.bIsX64 ? "x64" : "arm64";
            }
            catch (Exception)
            {
                // Host arch unavailable in this UAT context; the directory scan decides.
                return null;
            }
        }

        return null;
    }

    /// <summary>Resolves the MonoSDK directory the same way MonoSDK.Build.cs does:
    /// &lt;Platform&gt;/&lt;arch&gt; first, then the legacy flat &lt;Platform&gt; (unless the
    /// platform requires the arch layout). A sibling arch is only used when the host
    /// architecture is unknown; never as a substitute for a known target arch.
    /// Keeping the two sides aligned means UAT never validates a different SDK than UBT links.</summary>
    private SdkLayout ResolveSdkLayout(UnrealTargetPlatform platform, string? requestedArch, bool bRequireArchLayout)
    {
        string PlatformName = GetManagedSubDir(platform);
        string SdkRoot = GetMonoSdkRootFolder();
        string PlatformRoot = Path.Combine(SdkRoot, PlatformName);
        string? Arch = GetSdkArchName(platform, requestedArch);

        if (Arch != null && Directory.Exists(Path.Combine(PlatformRoot, Arch, "runtime")))
            return new SdkLayout(PlatformName, Arch);

        if (Arch == null && Directory.Exists(PlatformRoot))
        {
            foreach (string Candidate in Directory.EnumerateDirectories(PlatformRoot).OrderBy(D => D, StringComparer.Ordinal))
            {
                if (!Directory.Exists(Path.Combine(Candidate, "runtime")))
                    continue;

                string CandidateArch = Path.GetFileName(Candidate);
                LoggerUtilities.LogUnrealSharpWarning(
                    $"[Mono] MonoSDK host architecture unknown; using '{PlatformName}/{CandidateArch}' instead.");
                return new SdkLayout(PlatformName, CandidateArch);
            }
        }

        if (!bRequireArchLayout && Directory.Exists(Path.Combine(PlatformRoot, "runtime")))
        {
            LoggerUtilities.LogUnrealSharpWarning(
                $"[Mono] Using the legacy flat MonoSDK layout '{PlatformName}/runtime'; " +
                "download the arch-aware SDK (<Platform>/<arch>/) to match MonoSDK.Build.cs resolution.");
            return new SdkLayout(PlatformName, null);
        }

        throw new DirectoryNotFoundException(
            $"MonoSDK for '{PlatformName}' (arch '{Arch ?? "unknown"}') not found under {SdkRoot}. " +
            $"Expected '{Path.Combine(PlatformRoot, Arch ?? "<arch>", "runtime")}'" +
            (bRequireArchLayout ? " (the arch layout is required on this platform)." : " or the legacy flat layout.") +
            " Populate Source/ThirdParty/MonoSDK from the MonoSDK release zip (see README_MONO.md).");
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
    private void DeduplicateBclDlls(SdkLayout sdk, string publishFolder)
    {
        string BclDir = Path.Combine(sdk.ResolvePath(GetMonoSdkRootFolder()), "runtime");
        if (!Directory.Exists(BclDir))
        {
            // ValidateOptions already accepted this layout, so a missing BCL here means the
            // SDK was removed mid-build. Warn loudly: silent dedup skips ship BCL DLLs in the PAK.
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
