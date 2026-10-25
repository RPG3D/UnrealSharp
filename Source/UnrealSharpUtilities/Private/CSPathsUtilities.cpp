#include "CSPathsUtilities.h"

#include "CSCommonGlobalSettings.h"
#include "CSDotnetUtilties.h"
#include "CSInstallationUtilities.h"
#include "CSProjectUtilities.h"
#include "HAL/FileManager.h"
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
    // Mono packaged builds: UnrealSharp.Plugins.dll is in Content/Managed/<Platform>/ (inside PAK),
    // alongside the user assemblies.
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
    // Mono packaged build: project DLLs are staged into Content/Managed/<Platform>/ and
    // loaded from the PAK via UFS. The path must be absolute: FFileHelper::LoadFileToArray
    // resolves UFS entries through IFileManager's pak mount, which keys on absolute paths.
    return FPaths::ConvertRelativePathToFull(
        FPaths::Combine(FPaths::ProjectContentDir(), TEXT("Managed"), DotNetUtilities::GetMonoManagedPlatformDir()));
#elif (PLATFORM_ANDROID || PLATFORM_IOS) && !WITH_EDITOR
    // Mobile CoreCLR packaged build: FCSDotNetRuntimeHost::EnsureRuntimeDllsExtracted
    // copies BCL + project DLLs from the PAK into a real OS directory under Saved/
    // (CoreCLR needs real filesystem paths for the TPA). Must stay in sync with the
    // extraction dir in CSDotNetRuntimeHost_Mobile.cpp.
    return IFileManager::Get().ConvertToAbsolutePathForExternalAppForRead(
        *FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Managed"), UTF8_TO_TCHAR(FPlatformProperties::PlatformName())));
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
