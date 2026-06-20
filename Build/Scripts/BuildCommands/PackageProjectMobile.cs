using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutomationTool;
using UnrealBuildTool;
using UnrealSharp.Automation.Utilities;
using UnrealSharp.Shared;

namespace UnrealSharp.Automation.BuildCommands;

// PackageProjectMobile — MobileClr (Android/iOS) variant of PackageProject.
//
// Why a separate command (instead of modifying the upstream PackageProject):
//   The upstream PackageProject publishes managed DLLs to <ArchiveDirectory>/Binaries/Managed/<ver>
//   AFTER UAT has archived the build (editor -> PackageProject appends). The MobileClr pipeline
//   reverses this: managed DLLs are published to Content/Managed/<Platform> BEFORE UAT, so
//   CoreClrSDK.Build.cs can stage them into the PAK during cook/stage. Keeping this as a separate
//   command avoids touching the upstream PackageProject (zero merge conflicts when updating).
//
// Key differences from PackageProject:
//   - Output is always Content/Managed/<Platform> (derived from TargetPlatform, no PublishDir param).
//   - TargetType is always Game (mobile client/game share the same managed IL; no UETargetType param).
//   - MobileClr platforms (Android/iOS) use --no-self-contained (IL only; BCL is staged separately
//     by CoreClrSDK.Build.cs; the raw coreclr_initialize host has no hostfxr/hostpolicy).
//   - Does NOT pass -p:UseDefaultOutputPath=true. The default OutputPath (Directory.Build.props)
//     writes to the plugin's flat Binaries/Managed/net10.0/, which is where UnrealSharp.Shared.props
//     HintPaths point — overwrites any stale WITH_EDITOR build so glue/user-script publishes resolve
//     the clean Game-version DLLs and don't pull MSBuildLocator transitively.
//   - Always passes -p:DisableWithEditor=true (strips MSBuildLocator; Android/iOS have no dotnet SDK
//     so MSBuildLocator's static init throws at runtime).
[Help("Packages the UnrealSharp managed code for a MobileClr (Android/iOS) build. Reverse flow: publish managed to Content/Managed/<Platform> BEFORE UAT.")]
[Help("UEBuildConfig=<Config>", "REQUIRED. The build configuration (Debug, Development, Shipping, Test).")]
[Help("TargetPlatform=<Platform>", "Optional. Target platform (Android, iOS). Defaults to Android.")]
[Help("UserParams", "Optional. Additional parameters to forward to the user solution build (-UserParams=\"-p:Property=Value\").")]
public class PackageProjectMobile : BuildCommand
{
    private const string ManagedFolderName = "Managed";
    private const string BindingsProjectFolder = "UnrealSharp";
    private const int CleanupMaxAttempts = 5;
    private const int CleanupRetryDelayMs = 1000;

    // Mobile always uses Game target type (client/game share the same managed IL).
    private const TargetType MobileTargetType = TargetType.Game;

    public sealed record PackagingOptions(
        UnrealTargetConfiguration BuildConfiguration,
        UnrealTargetPlatform TargetPlatform,
        string[]? UserParams = null);

    public override void ExecuteBuild()
    {
        PackagingOptions Options = ParseOptionsFromCommandLine();
        StartPackaging(Options);
    }

    private void StartPackaging(PackagingOptions options)
    {
        ValidateOptions(options);
        LogOptions(options);

        DotNetSdkUtilities.CopyGlobalJson(this);

        // Output is always Content/Managed/<Platform> — CoreClrSDK.Build.cs stages from here.
        string PublishFolder = GetPublishFolder(options.TargetPlatform);
        CleanBuildArtifacts(PublishFolder);

        // Managed IL is platform-agnostic — no --runtime needed (MobileClr doesn't use
        // platform-specific NuGet deps; BCL is staged separately by CoreClrSDK.Build.cs).
        IList<string> Arguments = BuildBaseArguments(options, PublishFolder);

        // MobileClr: IL only, no .NET runtime bundled (CoreClrSDK.Build.cs stages BCL separately).
        Arguments.Add("--no-self-contained");
        Arguments.Add("-p:GenerateDocumentation=false");

        BuildBindingsSolution(Arguments, options.BuildConfiguration);
        BuildUserBindings(PublishFolder, options, Arguments);
        BuildUserSolution(PublishFolder, Arguments, options.BuildConfiguration, options.UserParams);

        EmitInstalledFlagFile(PublishFolder);

        LoggerUtilities.LogUnrealSharpInfo($"MobileClr packaging complete. Published files: {PublishFolder}");
    }

    /// <summary>
    /// Content/Managed/&lt;Platform&gt; — where CoreClrSDK.Build.cs stages managed DLLs into the PAK.
    /// </summary>
    private string GetPublishFolder(UnrealTargetPlatform platform)
    {
        string platformDir = platform == UnrealTargetPlatform.Android ? "Android" : "iOS";
        return Path.Combine(this.GetProjectRootFolder(), "Content", "Managed", platformDir);
    }

    private PackagingOptions ParseOptionsFromCommandLine()
    {
        UnrealTargetConfiguration TargetConfiguration = ParseRequiredEnumParamEnum<UnrealTargetConfiguration>("UEBuildConfig");

        string? PlatformString = ParseOptionalStringParam("TargetPlatform");
        UnrealTargetPlatform TargetPlatform = string.IsNullOrEmpty(PlatformString) ? UnrealTargetPlatform.Android : UnrealTargetPlatform.Parse(PlatformString);

        string[] UserParams = ParseParamValues("UserParams");

        return new PackagingOptions(TargetConfiguration, TargetPlatform, UserParams);
    }

    private static void LogOptions(PackagingOptions options)
    {
        LoggerUtilities.LogUnrealSharpInfo("MobileClr packaging project with parameters:");
        LoggerUtilities.LogUnrealSharpInfo($"Target Platform: {options.TargetPlatform}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Build Configuration: {options.BuildConfiguration}");
        LoggerUtilities.LogUnrealSharpInfo($"Output: Content/Managed/{(options.TargetPlatform == UnrealTargetPlatform.Android ? "Android" : "iOS")}");

        if (options.UserParams is { Length: > 0 })
        {
            LoggerUtilities.LogUnrealSharpInfo($"User Params: {string.Join(' ', options.UserParams)}");
        }
    }

    private void ValidateOptions(PackagingOptions options)
    {
        if (!IsMobileClrPlatform(options.TargetPlatform))
        {
            throw new ArgumentException($"PackageProjectMobile only supports MobileClr platforms (Android, iOS). Got: {options.TargetPlatform}. Use PackageProject for desktop platforms.");
        }

        string HostFxrPath = DotNetUtilities.LatestHostFxrPath;
        if (!File.Exists(HostFxrPath))
        {
            throw new FileNotFoundException($"Could not locate hostfxr library at expected path: {HostFxrPath}. Ensure that the .NET SDK is installed and accessible.");
        }
    }

    private static bool IsMobileClrPlatform(UnrealTargetPlatform platform)
    {
        return platform == UnrealTargetPlatform.Android || platform == UnrealTargetPlatform.IOS;
    }

    private static void CleanBuildArtifacts(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        LoggerUtilities.LogUnrealSharpInfo($"Cleaning existing output at '{folder}'.");

        for (int Attempt = 1; Attempt <= CleanupMaxAttempts; Attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception Ex)
            {
                if (Attempt == CleanupMaxAttempts)
                {
                    throw new IOException($"Failed to clean output directory '{folder}' after {CleanupMaxAttempts} attempts. See inner exception for details.", Ex);
                }

                LoggerUtilities.LogUnrealSharpWarning($"Attempt {Attempt} to clean output directory '{folder}' failed. Retrying... Exception: {Ex.Message}");
                System.Threading.Thread.Sleep(CleanupRetryDelayMs);
            }
        }
    }

    private static IList<string> BuildBaseArguments(PackagingOptions options, string publishFolder)
    {
        List<string> args =
        [
            // Serial build (-m:1) — prevents parallel compilation of bindings projects from
            // racing on shared obj/ files (CS2012 "cannot open for write"). Combined with
            // DOTNET_DISABLE_BUILD_SERVERS=1 (no VBCSCompiler), this is the reliable path.

            "-m:1",

            // MobileClr: do NOT pass -p:UseDefaultOutputPath=true. The default OutputPath
            // (Directory.Build.props) writes to the plugin's flat Binaries/Managed/net10.0/,
            // which is where UnrealSharp.Shared.props HintPaths point. Writing there overwrites
            // any stale WITH_EDITOR build so the glue/user-script publishes resolve the clean
            // Game-version DLLs and don't pull MSBuildLocator transitively.

            // No --runtime: managed IL is platform-agnostic. MobileClr doesn't use platform-specific
            // NuGet deps (BCL staged separately by CoreClrSDK.Build.cs).

            $"-p:UETargetType={MobileTargetType}",
            $"-p:UEBuildConfig={options.BuildConfiguration}",

            $"-p:PublishDir=\"{publishFolder}\"",

            // Strip editor-only code (MSBuildLocator, UnrealSharp.Editor). Android/iOS have no
            // dotnet SDK, so MSBuildLocator's static init throws at runtime.
            "-p:DisableWithEditor=true",
        ];

        return args;
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

        EmitUserLoadOrder(publishFolder);
    }

    private void BuildUserBindings(string publishFolder, PackagingOptions options, IList<string> buildArguments)
    {
        if (this.IsInstalledUnrealSharpBuild())
        {
            CopyInstalledGlue(publishFolder);
            return;
        }

        LoggerUtilities.LogUnrealSharpInfo("Source build detected. Building glue from generated projects and emitting glue load order...");
        BuildUserGlue.Build(this, MobileTargetType, options.BuildConfiguration, publishFolder, buildArguments);
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
        if (!File.Exists(sourceFile))
        {
            return;
        }

        File.Copy(sourceFile, Path.Combine(destFolder, Path.GetFileName(sourceFile)), true);
    }

    private void EmitInstalledFlagFile(string publishFolder)
    {
        string InstalledFlagFilePath = Path.Combine(publishFolder, BuildUtilities.UnrealSharpBuildFlagFileName);
        File.WriteAllText(InstalledFlagFilePath, string.Empty);
    }
}
