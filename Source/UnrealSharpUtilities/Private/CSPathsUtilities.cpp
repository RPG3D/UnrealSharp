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
    return GetPluginAssembliesPath() / TEXT("UnrealSharp.Plugins.dll");
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
#if PLATFORM_ANDROID && !WITH_EDITOR
    // On Android packaged builds, the CoreCLR host extracts BCL + project managed DLLs
    // (and LoadOrder manifests) from the PAK/NonUFS sources into a writable runtime dir
    // (ProjectSavedDir/Managed/Android) at launch — CoreCLR needs real OS
    // filesystem paths for the TPA. The default Binaries/Managed/... path lives inside the
    // PAK and isn't where the DLLs actually are, so manifest discovery (DiscoverLoadOrder-
    // Manifests) and assembly loading (CSManager::LoadAssembly) would find nothing there.
    // Point GetUserAssemblyDirectory at the extracted runtime dir so all consumers agree.
    return IFileManager::Get().ConvertToAbsolutePathForExternalAppForRead(
        *FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Managed"), TEXT("Android")));
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
