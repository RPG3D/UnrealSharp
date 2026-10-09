// CSDotNetRuntimeHost_Mono.cpp
// Mono-backend implementation of FCSDotNetRuntimeHost::InitializeMonoHost().
// This file is compiled only when UNREALSHARP_MONO is defined.
// All Mono-specific logic is isolated here so that CSDotNetRuntimeHost.cpp
// stays in sync with upstream with minimal merge conflicts.

#if UNREALSHARP_MONO

#include "DotNet/CSDotNetRuntimeHost.h"
#include "CSMonoRuntime.h"
#include "CSUnrealSharpSettings.h"
#include "CSBindsRegistry.h"
#include "CSManagedCallbacksCache.h"
#include "CSManagedPluginCallbacks.h"
#include "CSPathsUtilities.h"
#include "CSDotnetUtilties.h"
#include "UnrealSharpCore.h"
#include "Misc/Paths.h"
#include "Misc/FileHelper.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"

bool FCSDotNetRuntimeHost::InitializeMonoHost()
{
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] InitializeMonoHost: starting Mono runtime initialization..."));

	// --- 1. Determine the BCL runtime directory ---
	// The BCL lives in the MonoSDK submodule at Source/ThirdParty/MonoSDK/<Platform>/runtime/,
	// both in the editor and in packaged builds (MonoSDK.Build.cs stages it as NonUFS,
	// which preserves this plugin-relative layout next to the staged executable).
	const FString PluginDir = FPaths::ConvertRelativePathToFull(UnrealSharp::Paths::GetPluginDirectory());
	const FString RuntimeDir = FPaths::Combine(
		PluginDir, TEXT("Source"), TEXT("ThirdParty"), TEXT("MonoSDK"),
		UnrealSharp::DotNetUtilities::GetMonoManagedPlatformDir(), TEXT("runtime"));

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] BCL runtime dir: %s"), *RuntimeDir);

	if (!FPaths::DirectoryExists(RuntimeDir))
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] BCL runtime directory not found: %s"), *RuntimeDir);
		return false;
	}

	// --- 2. Determine assembly paths ---
	// All paths use relative format -- UE I/O APIs (FFileHelper, FPaths) handle both
	// editor filesystem and packaged PAK/UFS transparently.
	const FString UnrealSharpLibraryAssembly = UnrealSharp::Paths::GetUnrealSharpPluginsPath();
	const FString UserWorkingDirectory = UnrealSharp::Paths::GetUserAssemblyDirectory();

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

	// --- 3. Initialize Mono Debugger (before runtime init) ---
	const UCSUnrealSharpSettings* Settings = GetDefault<UCSUnrealSharpSettings>();
	if (Settings && !Settings->bMonoPerformanceMode)
	{
		InitMonoDebugger(Settings);
	}

	// --- 4. Initialize Mono runtime ---
	MonoRootDomain = InitializeMonoRuntime(RuntimeDir, ExtraSearchPaths);
	if (!MonoRootDomain)
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] InitializeMonoRuntime failed"));
		return false;
	}

	// Wait for debugger if configured (avoids mono_coop_mutex_lock crash)
	if (IsMonoDebuggerActive() && Settings && Settings->bMonoWaitDebugger)
	{
		const float SleepTime = Settings->MonoDelayStartTimeWhenWaitDebugger;
		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Sleeping %.1f seconds for debugger attach..."), SleepTime);
		FPlatformProcess::Sleep(SleepTime);
	}

	// --- 5. Resolve and invoke the C# entry point via Mono Embedding API ---
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Assembly: %s"), *UnrealSharpLibraryAssembly);
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] User dir: %s"), *UserWorkingDirectory);

	// We use FindMonoMethod + mono_runtime_invoke instead of
	// mono_method_get_unmanaged_callers_only_ftnptr: the latter crashes in .NET 10 Mono
	// when the method has complex pointer parameters (PluginsCallbacks*, etc.).
	MonoMethod* InitMethod = FindMonoMethod(
		MonoRootDomain,
		TCHAR_TO_UTF8(*UnrealSharpLibraryAssembly),
		"UnrealSharp.Plugins.Main",
		"InitializeUnrealSharp",
		5); // 5 parameters

	if (!InitMethod)
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] Failed to find InitializeUnrealSharp entry point"));
		return false;
	}

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Found InitializeUnrealSharp. Invoking via mono_runtime_invoke..."));

	// Managed signature (UnrealSharp.Plugins.Main, [UnmanagedCallersOnly], void return):
	//   InitializeUnrealSharp(byte* workingDirectoryUtf8, PluginsCallbacks* pluginCallbacks,
	//       nint bindsCallbacks, nint managedCallbacks, FCSInitializationResult* result)
	//
	// mono_runtime_invoke parameter passing rules:
	//   - pointer type (byte*, void*, struct*): args[i] = the pointer VALUE directly
	//   - value type (nint, int, ...):          args[i] = pointer TO the value (&var)
	FTCHARToUTF8 WorkDirUtf8(*UserWorkingDirectory);
	intptr_t BindsFnIntPtr = (intptr_t)&FCSBindsRegistry::GetBoundFunction;
	intptr_t ManagedCallbacksIntPtr = (intptr_t)&GetManagedCallbacks();
	FMonoInitializationResult Result;

	void* Args[5] = {
		(void*)WorkDirUtf8.Get(),
		(void*)&GetManagedPluginCallbacks(),
		&BindsFnIntPtr,
		&ManagedCallbacksIntPtr,
		(void*)&Result
	};

	MonoObject* Exception = nullptr;
	mono_runtime_invoke(InitMethod, nullptr, Args, &Exception);

	if (Exception)
	{
		// REVIEW-fix: mono_string_to_utf8 returns malloc'ed memory -- mono_free it (leak in old port).
		const char* ExMsgUtf8 = nullptr;
		if (MonoString* ExMsg = mono_object_to_string(Exception, nullptr))
		{
			ExMsgUtf8 = mono_string_to_utf8(ExMsg);
		}
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] InitializeUnrealSharp threw exception: %s"),
			ANSI_TO_TCHAR(ExMsgUtf8 ? ExMsgUtf8 : "unknown exception"));
		if (ExMsgUtf8)
		{
			mono_free((void*)ExMsgUtf8);
		}
		return false;
	}

	if (Result.Success != 1)
	{
		// Managed code writes the full exception text into FMonoInitializationResult.Message
		// (UTF-8, NUL-terminated) -- no temp-file roundtrip needed.
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] C# InitializeUnrealSharp failed! Exception:\n%s"),
			UTF8_TO_TCHAR(Result.Message));
		return false;
	}

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] InitializeMonoHost completed successfully."));
	return true;
}

#endif // UNREALSHARP_MONO
