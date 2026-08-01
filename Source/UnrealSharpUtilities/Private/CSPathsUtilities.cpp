#include "CSPathsUtilities.h"

#include "CSCommonGlobalSettings.h"
#include "CSDotnetUtilties.h"
#include "CSInstallationUtilities.h"
#include "CSProjectUtilities.h"
#include "Interfaces/IPluginManager.h"
#include "Logging/StructuredLog.h"

FString UnrealSharp::Paths::GetPluginDirectory()
{
    TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(UE_PLUGIN_NAME);
    return Plugin->GetBaseDir();
}

FString UnrealSharp::Paths::GetUnrealSharpDirectory()
{
    return FPaths::Combine(GetPluginDirectory(), TEXT("Managed"), TEXT("UnrealSharp"));
}

FString UnrealSharp::Paths::GetPluginAssembliesPath()
{
    if (InstallationUtilities::IsUnrealSharpInstalled())
    {
        return GetUserAssemblyDirectory();
    }
    
    return FPaths::Combine(GetPluginDirectory(), DotNetUtilities::GetManagedBinaries());
}

FString UnrealSharp::Paths::GetUnrealSharpPluginsPath()
{
#if UNREALSHARP_MONO && !WITH_EDITOR
    // In Mono packaged builds, UnrealSharp.Plugins.dll is in Content/Managed/{Platform}/ (inside PAK).
    return GetUserAssemblyDirectory() / TEXT("UnrealSharp.Plugins.dll");
#else
    return GetPluginAssembliesPath() / TEXT("UnrealSharp.Plugins.dll");
#endif
}

FString UnrealSharp::Paths::GetUnrealSharpBuildToolPath()
{
#if PLATFORM_WINDOWS || PLATFORM_MAC
    return FPaths::ConvertRelativePathToFull(GetPluginAssembliesPath() / TEXT("UnrealSharpBuildTool.dll"));
#else
    return FPaths::ConvertRelativePathToFull(GetPluginAssembliesPath() / TEXT("UnrealSharpBuildTool"));
#endif
}

FString UnrealSharp::Paths::GetUserAssemblyDirectory()
{
#if UNREALSHARP_MONO && !WITH_EDITOR
    // Mono packaged build: DLLs are staged into Content/Managed/{Platform}/ and loaded from PAK via UFS.
#if PLATFORM_WINDOWS
    const FString ManagedPlatformDir = TEXT("Win64");
#elif PLATFORM_MAC
    const FString ManagedPlatformDir = TEXT("Mac");
#elif PLATFORM_ANDROID
    const FString ManagedPlatformDir = TEXT("Android");
#elif PLATFORM_IOS
#if WITH_IOS_SIMULATOR
    const FString ManagedPlatformDir = TEXT("IOSSimulator");
#else
    const FString ManagedPlatformDir = TEXT("IOS");
#endif
#else
    const FString ManagedPlatformDir = FPlatformProperties::PlatformName();
#endif
    // Convert to an absolute path. Project DLLs live inside the PAK (UFS); FFileHelper::LoadFileToArray
    // resolves UFS entries through IFileManager's pak mount, which keys on absolute paths. A relative
    // path (e.g. "../../../SharpDemo/Content/Managed/Win64") fails to match the pak mount point in
    // packaged builds where the exe sits at <Root>/Binaries/Win64/, so the assembly load silently
    // returns null. The non-Mono branch below already converts; Mono must too.
    return FPaths::ConvertRelativePathToFull(
        FPaths::Combine(FPaths::ProjectContentDir(), TEXT("Managed"), ManagedPlatformDir));
#else
    return FPaths::ConvertRelativePathToFull(FPaths::Combine(FPaths::ProjectDir(), DotNetUtilities::GetManagedBinaries()));
#endif
}

FString UnrealSharp::Paths::GetUnrealSharpMetadataPath()
{
    return FPaths::Combine(GetUserAssemblyDirectory(), TEXT("Assembly.LoadOrder.json"));
}

FString UnrealSharp::Paths::GetGeneratedClassesDirectory()
{
    return FPaths::Combine(GetUnrealSharpDirectory(), TEXT("UnrealSharp"), TEXT("Generated"));
}

const FString& UnrealSharp::Paths::GetScriptFolderDirectory()
{
    static FString ScriptFolderDirectory = FPaths::Combine(FPaths::ProjectDir(), GlobalSettings::Common::GetScriptDirectoryName());
    return ScriptFolderDirectory;
}

const FString& UnrealSharp::Paths::GetPluginsDirectory()
{
    static FString PluginsDirectory = FPaths::Combine(FPaths::ProjectDir(), TEXT("Plugins"));
    return PluginsDirectory;
}

FString UnrealSharp::Paths::GetPathToManagedSolution()
{
    static FString SolutionPath = GetScriptFolderDirectory() / Project::GetUserManagedProjectName() + TEXT(".sln");
    return SolutionPath;
}

FString UnrealSharp::Paths::MakeQuotedPath(const FString& Path)
{
    if (Path.IsEmpty())
    {
        return TEXT("");
    }

    if (Path.StartsWith(TEXT("\"")) && Path.EndsWith(TEXT("\"")))
    {
        return Path;
    }

    return FString::Printf(TEXT("\"%s\""), *Path);
}
