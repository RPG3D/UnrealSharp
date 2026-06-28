#include "CSDotnetUtilties.h"

#include "CSBuildUtilties.h"
#include "CSDialogUtilities.h"
#include "CSInstallationUtilities.h"
#include "CSPathsUtilities.h"
#include "CSProjectUtilities.h"
#include "UnrealSharpUtils.h"

static TAutoConsoleVariable<int32> CVarSimulateNoDotNetSDK(
	TEXT("UnrealSharp.SimulateNoDotNetSDK"),
	0,
	TEXT("Simulate an environment where no .NET SDK is installed. This is useful for testing the UnrealSharp installation experience. Note that this will not affect UnrealSharp's ability to find a bundled .NET runtime, so it can be used to test both installed and non-installed scenarios."),
	ECVF_Default);

FString UnrealSharp::DotNetUtilities::GetDotNetDirectory()
{
#if WITH_EDITOR
	if (CVarSimulateNoDotNetSDK.GetValueOnAnyThread() == 1)
	{
		return FString();
	}
#endif
	
#if defined(__APPLE__)
    constexpr const TCHAR* DefaultDotNetPath = TEXT("/usr/local/share/dotnet/");
    if (FPaths::DirectoryExists(DefaultDotNetPath))
    {
       return DefaultDotNetPath;
    }
#endif

    const FString PathVariable = FPlatformMisc::GetEnvironmentVariable(TEXT("PATH"));

    TArray<FString> Paths;
    PathVariable.ParseIntoArray(Paths, FPlatformMisc::GetPathVarDelimiter());

    // Marker has NO trailing separator — we match with EndsWith against a normalized path
    // so PATH entries are found regardless of whether they end in a slash/backslash.
    // (Windows PATH entries are commonly stored without a trailing backslash, e.g.
    // "C:\Program Files\dotnet" — the old marker "Program Files\\dotnet\\" missed those.)
#if defined(_WIN32)
    const FString PathMarker = TEXT("Program Files\\dotnet");
    const TCHAR PathSeparator = TEXT('\\');
#else
    const FString PathMarker = TEXT("dotnet");
    const TCHAR PathSeparator = TEXT('/');
#endif

    FString DotNetPathFromEnv;
    for (const FString& RawPath : Paths)
    {
       // Normalize: strip trailing path separators so EndsWith matches both
       // "C:\Program Files\dotnet" and "C:\Program Files\dotnet\".
       FString NormalizedPath = RawPath;
       while (NormalizedPath.EndsWith(TEXT("/")) || NormalizedPath.EndsWith(TEXT("\\")))
       {
          NormalizedPath.LeftChopInline(1);
       }

       if (!NormalizedPath.EndsWith(PathMarker, ESearchCase::IgnoreCase))
       {
          continue;
       }

       if (!FPaths::DirectoryExists(NormalizedPath))
       {
          UE_LOGFMT(LogUnrealSharpUtilities, Warning, "Found path to DotNet, but the directory doesn't exist: {0}", NormalizedPath);
          break;
       }

        // Return with a guaranteed trailing separator so callers that do
        // GetDotNetDirectory() + "dotnet.exe" produce a valid path.
        DotNetPathFromEnv = NormalizedPath + PathSeparator;
        break;
    }

    return DotNetPathFromEnv;
}

FString UnrealSharp::DotNetUtilities::GetDotNetExecutablePath()
{
#if defined(_WIN32)
    return GetDotNetDirectory() + TEXT("dotnet.exe");
#else
    return GetDotNetDirectory() + TEXT("dotnet");
#endif
}

FString UnrealSharp::DotNetUtilities::GetLatestHostFxrPath()
{
#if UNREALSHARP_MONO
    // Mono runtime is build-time linked; hostfxr is not used.
    // Return the Mono runtime library path for callers that need a runtime library reference.
#if PLATFORM_ANDROID
    return FPaths::Combine(Paths::GetPluginDirectory(), TEXT("Source/ThirdParty"), TEXT("MonoSDK"),
        FString(FPlatformProperties::PlatformName()), TEXT("lib"), TEXT("libmonosgen-2.0.so"));
#elif PLATFORM_MAC
    return FPaths::Combine(Paths::GetPluginDirectory(), TEXT("Source/ThirdParty"), TEXT("MonoSDK"),
        FString(FPlatformProperties::PlatformName()), TEXT("lib"), TEXT("libcoreclr.dylib"));
#elif PLATFORM_IOS
    // iOS uses static linking -- no dynamic library path needed.
    return TEXT("");
#elif PLATFORM_WINDOWS
    return FPaths::Combine(Paths::GetPluginDirectory(), TEXT("Source/ThirdParty"), TEXT("MonoSDK"),
        TEXT("Win64"), TEXT("lib"), TEXT("coreclr.dll"));
#else
    #error "UNREALSHARP_MONO is not supported on this platform. Add a platform-specific MonoSDK library path."
#endif
#else
    const FString DotNetRoot = GetDotNetDirectory();
    const FString HostFxrRoot = FPaths::Combine(DotNetRoot, TEXT("host"), TEXT("fxr"));

    TArray<FString> Folders;
    IFileManager::Get().FindFiles(Folders, *(HostFxrRoot / TEXT("*")), true, true);

    FString HighestVersion;
    for (const FString& Folder : Folders)
    {
       if (HighestVersion.IsEmpty() || IsVersionHigher(Folder, HighestVersion))
       {
          HighestVersion = Folder;
       }
    }

    if (HighestVersion.IsEmpty())
    {
       UE_LOGFMT(LogUnrealSharpUtilities, Fatal, "Failed to find hostfxr version in {0}", HostFxrRoot);
    }

    if (!IsVersionGreaterOrEqual(HighestVersion, TEXT(DOTNET_MAJOR_VERSION)))
    {
       UE_LOGFMT(LogUnrealSharpUtilities, Fatal, "Hostfxr version {0} is less than the required version " DOTNET_MAJOR_VERSION, HighestVersion);
    }

#if defined(_WIN32)
    return FPaths::Combine(HostFxrRoot, HighestVersion, HOSTFXR_WINDOWS);
#elif defined(__APPLE__)
    return FPaths::Combine(HostFxrRoot, HighestVersion, HOSTFXR_MAC);
#else
    return FPaths::Combine(HostFxrRoot, HighestVersion, HOSTFXR_LINUX);
#endif
#endif // !UNREALSHARP_MONO
}

FString UnrealSharp::DotNetUtilities::GetRuntimeHostPath()
{
#if UNREALSHARP_MONO
    // Mono runtime: return the build-time linked Mono library path.
    return GetLatestHostFxrPath();
#else
    if (InstallationUtilities::IsUnrealSharpInstalled())
    {
#if defined(_WIN32)
    return FPaths::Combine(Paths::GetPluginAssembliesPath(), HOSTFXR_WINDOWS);
#elif defined(__APPLE__)
    return FPaths::Combine(Paths::GetPluginAssembliesPath(), HOSTFXR_MAC);
#else
    return FPaths::Combine(Paths::GetPluginAssembliesPath(), HOSTFXR_LINUX);
#endif
    }

    return GetLatestHostFxrPath();
#endif
}

FString UnrealSharp::DotNetUtilities::GetRuntimeConfigPath()
{
    return Paths::GetPluginAssembliesPath() / TEXT("UnrealSharp.runtimeconfig.json");
}

#if WITH_EDITOR
bool UnrealSharp::DotNetUtilities::VerifyCSharpEnvironment()
{
	FString DotNetInstallationPath = GetDotNetDirectory();
	if (DotNetInstallationPath.IsEmpty() && !InstallationUtilities::IsUnrealSharpInstalled())
	{
		FString DialogText = FString::Printf(TEXT("UnrealSharp can't be initialized. An installation of .NET %s SDK can't be found on your system."), TEXT(DOTNET_MAJOR_VERSION));
		// Log the failure alongside the dialog — the dialog is easy to miss/dismiss and
		// these messages otherwise never reach the output log.
		UE_LOG(LogUnrealSharpUtilities, Error, TEXT("%s (PATH dotnet lookup returned empty; IsUnrealSharpInstalled=%s)"), *DialogText, InstallationUtilities::IsUnrealSharpInstalled() ? TEXT("true") : TEXT("false"));
		FMessageDialog::Open(EAppMsgType::Ok, FText::FromString(DialogText));
		return false;
	}

	FString UnrealSharpLibraryPath = Paths::GetUnrealSharpPluginsPath();
	if (!FPaths::FileExists(UnrealSharpLibraryPath))
	{
		FString FullPath = FPaths::ConvertRelativePathToFull(UnrealSharpLibraryPath);
		FString DialogText = FString::Printf(TEXT(
			"The bindings library could not be found at the following location:\n%s\n\n"
			"Most likely, the bindings library failed to build due to invalid generated glue."
		), *FullPath);

		UE_LOG(LogUnrealSharpUtilities, Error, TEXT("%s"), *DialogText);
		FMessageDialog::Open(EAppMsgType::Ok, FText::FromString(DialogText));
		return false;
	}

	return true;
}

bool UnrealSharp::DotNetUtilities::BuildUserSolution()
{
	TArray<FString> ProjectPaths;
	Project::GetAllProjectPaths(ProjectPaths);
	
	if (ProjectPaths.IsEmpty())
	{
		return true;
	}
	
	if (FCSUnrealSharpUtils::IsStandalonePIE() || FApp::IsUnattended())
	{
		return true;
	}
	
	return Build::BuildUserSolution(Dialogs::MakeOkCancelDialogOnError());
}
#endif

FString& UnrealSharp::DotNetUtilities::GetManagedBinaries()
{
	static FString ManagedBinaries = FPaths::Combine(TEXT("Binaries"), TEXT("Managed"), TEXT(DOTNET_DISPLAY_NAME));
	return ManagedBinaries;
}

bool UnrealSharp::DotNetUtilities::ParseDotNetVersion(const FString& VersionString, int32& OutMajor, int32& OutMinor, int32& OutPatch)
{
	TArray<FString> Parts;
	VersionString.ParseIntoArray(Parts, TEXT("."));

	if (Parts.Num() < 3)
	{
		return false;
	}

	OutMajor = FCString::Atoi(*Parts[0]);
	OutMinor = FCString::Atoi(*Parts[1]);
	OutPatch = FCString::Atoi(*Parts[2]);
	return true;
}

bool UnrealSharp::DotNetUtilities::IsVersionGreaterOrEqual(const FString& Version, const FString& MinVersion)
{
	int32 Major, Minor, Patch;
	int32 MinMajor, MinMinor, MinPatch;

	if (!ParseDotNetVersion(Version, Major, Minor, Patch) || !ParseDotNetVersion(MinVersion, MinMajor, MinMinor, MinPatch))
	{
		return false;
	}

	if (Major != MinMajor)
	{
		return Major > MinMajor;
	}
	
	if (Minor != MinMinor)
	{
		return Minor > MinMinor;
	}
	
	return Patch >= MinPatch;
}

bool UnrealSharp::DotNetUtilities::IsVersionHigher(const FString& A, const FString& B)
{
	int32 MajorA, MinorA, PatchA;
	int32 MajorB, MinorB, PatchB;

	if (!ParseDotNetVersion(A, MajorA, MinorA, PatchA) || !ParseDotNetVersion(B, MajorB, MinorB, PatchB))
	{
		return false;
	}

	if (MajorA != MajorB)
	{
		return MajorA > MajorB;
	}
	
	if (MinorA != MinorB)
	{
		return MinorA > MinorB;
	}
	
	return PatchA > PatchB;
}
