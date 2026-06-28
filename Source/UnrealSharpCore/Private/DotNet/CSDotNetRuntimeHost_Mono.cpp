// CSDotNetRuntimeHost_Mono.cpp
// Mono-backend implementation of FCSDotNetRuntimeHost::InitializeMonoHost().
// This file is compiled only when UNREALSHARP_MONO is defined.
// All Mono-specific logic is isolated here so that CSDotNetRuntimeHost.cpp
// stays in sync with upstream with minimal merge conflicts.

#if UNREALSHARP_MONO

#include "DotNet/CSDotNetRuntimeHost.h"
#include "CSMonoRuntime.h"
#include "CSBindsRegistry.h"
#include "CSManagedCallbacksCache.h"
#include "CSManagedPluginCallbacks.h"
#include "CSPathsUtilities.h"
#include "UnrealSharpCore.h"
#include "Misc/Paths.h"
#include "Misc/FileHelper.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"

bool FCSDotNetRuntimeHost::InitializeMonoHost()
{
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] InitializeMonoHost: starting Mono runtime initialization..."));

	// --- 1. Determine the BCL runtime directory ---
#if !WITH_EDITOR
	// In packaged builds, BCL DLLs are staged as NonUFS (outside PAK) alongside the executable.
	// The MonoSDK.Build.cs RuntimeDependencies(NonUFS) places them at:
	//   Plugins/UnrealSharp/Source/ThirdParty/MonoSDK/{Platform}/runtime/
	// relative to the staged build root.
	//
	// Note: FPlatformProperties::PlatformName() returns "Windows" in packaged builds,
	// but the MonoSDK directory uses "Win64". We use the same mapping as MonoSDK.Build.cs.
#if PLATFORM_WINDOWS
	const FString PlatformDir = TEXT("Win64");
#elif PLATFORM_MAC
	const FString PlatformDir = TEXT("Mac");
#elif PLATFORM_ANDROID
	const FString PlatformDir = TEXT("Android");
#elif PLATFORM_IOS
#if WITH_IOS_SIMULATOR
	const FString PlatformDir = TEXT("IOSSimulator");
#else
	const FString PlatformDir = TEXT("IOS");
#endif
#else
#error "UNREALSHARP_MONO packaged builds: unsupported platform."
#endif

	const FString PluginDir = FPaths::ConvertRelativePathToFull(
		UnrealSharp::Paths::GetPluginDirectory());
	const FString RuntimeDir = FPaths::Combine(
		PluginDir, TEXT("Source/ThirdParty"), TEXT("MonoSDK"), PlatformDir, TEXT("runtime"));

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Packaged: BCL runtime dir: %s"), *RuntimeDir);

	if (!FPaths::DirectoryExists(RuntimeDir))
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] BCL runtime directory not found: %s"), *RuntimeDir);
		return false;
	}
#else
	// In editor builds, the BCL lives in Source/ThirdParty/MonoSDK/{Platform}/runtime/.
#if PLATFORM_MAC
	const FString PlatformDir = TEXT("Mac");
#elif PLATFORM_WINDOWS
	const FString PlatformDir = TEXT("Win64");
#else
#error "UNREALSHARP_MONO editor builds: unsupported platform. Add MonoSDK path for this platform."
#endif
	const FString MonoSdkDir = FPaths::Combine(
		UnrealSharp::Paths::GetPluginDirectory(), TEXT("Source/ThirdParty"), TEXT("MonoSDK"), PlatformDir);
	const FString RuntimeDir = FPaths::Combine(MonoSdkDir, TEXT("runtime"));

	if (!FPaths::DirectoryExists(RuntimeDir))
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] BCL runtime directory not found: %s"), *RuntimeDir);
		return false;
	}
#endif

	// --- 2. Determine assembly paths ---
	// All paths use relative format — UE I/O APIs (FFileHelper, FPaths) handle both
	// editor filesystem and packaged PAK/UFS transparently.
	const FString UnrealSharpLibraryAssembly =
		UnrealSharp::Paths::GetUnrealSharpPluginsPath();
	const FString UserWorkingDirectory =
		UnrealSharp::Paths::GetUserAssemblyDirectory();

#if WITH_EDITOR
	if (!FPaths::FileExists(UnrealSharpLibraryAssembly))
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] UnrealSharp.Plugins.dll not found: %s"),
			*UnrealSharpLibraryAssembly);
		return false;
	}

#if PLATFORM_WINDOWS
	const TCHAR* MonoPathSep = TEXT(";");
#else
	const TCHAR* MonoPathSep = TEXT(":");
#endif
	const FString PluginBinDir = FPaths::GetPath(UnrealSharpLibraryAssembly);
	const FString ExtraSearchPaths = PluginBinDir + MonoPathSep + UserWorkingDirectory;
#else
	const FString ExtraSearchPaths = UserWorkingDirectory;
#endif

	// --- 3. Initialize Mono runtime ---
	MonoRootDomain = InitializeMonoRuntime(RuntimeDir, ExtraSearchPaths);
	if (!MonoRootDomain)
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] InitializeMonoRuntime failed"));
		return false;
	}

	// --- 4. Resolve and invoke the C# entry point via Mono Embedding API ---
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Assembly: %s"), *UnrealSharpLibraryAssembly);
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] User dir: %s"), *UserWorkingDirectory);

	// Find the C# entry point method using Mono Embedding API.
	// We use FindMonoMethod + mono_runtime_invoke instead of
	// GetUnmanagedCallersOnlyFnPtr/mono_method_get_unmanaged_callers_only_ftnptr because
	// the latter crashes in .NET 10 Mono when the method has complex pointer parameters.
	MonoMethod* InitMethod = FindMonoMethod(
		MonoRootDomain,
		TCHAR_TO_UTF8(*UnrealSharpLibraryAssembly),
		"UnrealSharp.Plugins.Main",
		"InitializeUnrealSharp",
		5);  // 5 parameters

	if (!InitMethod)
	{
		UE_LOG(LogUnrealSharp, Fatal,
			TEXT("[Mono] Failed to find InitializeUnrealSharp entry point"));
		return false;
	}

	UE_LOG(LogUnrealSharp, Log,
		TEXT("[Mono] Found InitializeUnrealSharp. Invoking via mono_runtime_invoke..."));

	// Parameters for InitializeUnrealSharp(char* workDir, nint assemblyPath,
	//   PluginsCallbacks* pluginCallbacks, IntPtr bindsCallbacks, IntPtr managedCallbacks)
	//
	// mono_runtime_invoke parameter passing rules:
	//   - unsafe pointer type (char*, void*, struct*): args[i] = the pointer VALUE directly
	//   - value type (IntPtr, nint, int): args[i] = pointer TO the value (&var)
	const TCHAR* WorkDirPtr = *UserWorkingDirectory;
	intptr_t AssemblyNint = (intptr_t)(*UnrealSharpLibraryAssembly);
	FCSManagedPluginCallbacks* PluginsCallbacksPtr = &GetManagedPluginCallbacks();
	const void* BindsFn = (const void*)&FCSBindsRegistry::GetBoundFunction;
	intptr_t ManagedCallbacksIntPtr = (intptr_t)&GetManagedCallbacks();

	void* Args[5] = {
		(void*)WorkDirPtr,
		&AssemblyNint,
		(void*)PluginsCallbacksPtr,
		&BindsFn,
		&ManagedCallbacksIntPtr
	};

	MonoObject* Exception = nullptr;
	MonoObject* Result = mono_runtime_invoke(InitMethod, nullptr, Args, &Exception);

	if (Exception)
	{
		MonoString* ExMsg = mono_object_to_string(Exception, nullptr);
		const char* ExMsgUtf8 = ExMsg ? mono_string_to_utf8(ExMsg) : "unknown exception";
		UE_LOG(LogUnrealSharp, Fatal,
			TEXT("[Mono] InitializeUnrealSharp threw exception: %s"),
			ANSI_TO_TCHAR(ExMsgUtf8));
		return false;
	}

	bool bSuccess = false;
	if (Result)
	{
		void* Unboxed = mono_object_unbox(Result);
		if (Unboxed)
			bSuccess = (*(uint8*)Unboxed != 0);
	}

	if (!bSuccess)
	{
		FString ExceptionLog;
		FString ExceptionLogPath = FPaths::Combine(
			FPlatformProcess::UserTempDir(), TEXT("UnrealSharp_InitException.txt"));
		if (FFileHelper::LoadFileToString(ExceptionLog, *ExceptionLogPath))
		{
			UE_LOG(LogUnrealSharp, Fatal,
				TEXT("[Mono] C# InitializeUnrealSharp returned false! Exception:\n%s"),
				*ExceptionLog);
		}
		else
		{
			UE_LOG(LogUnrealSharp, Fatal,
				TEXT("[Mono] C# InitializeUnrealSharp returned false!"));
		}
		return false;
	}

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] InitializeMonoHost completed successfully."));
	return true;
}

#endif // UNREALSHARP_MONO
