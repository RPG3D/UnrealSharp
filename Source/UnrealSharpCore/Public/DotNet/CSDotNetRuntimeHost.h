#pragma once

#include <coreclr_delegates.h>
#include <hostfxr.h>
#if PLATFORM_ANDROID
#include <coreclrhost.h>
#endif
#include "HAL/PlatformProcess.h"

struct FCSManagedCallbacks;
struct FCSManagedPluginCallbacks;

using FInitializeRuntimeHost = bool (*)(const TCHAR*, const TCHAR*, FCSManagedPluginCallbacks*, const void*, FCSManagedCallbacks*);

class FCSDotNetRuntimeHost
{
public:
	FCSDotNetRuntimeHost() = default;
	~FCSDotNetRuntimeHost();

	bool InitializeManagedRuntime();
	void ShutdownManagedRuntime();

private:
	// ── Desktop (Win64/Mac) hostfxr path ────────────────────────────────────
	load_assembly_and_get_function_pointer_fn InitializeHost();
	load_assembly_and_get_function_pointer_fn ConfigureRuntime() const;

	template <typename FunctionPointer>
	bool BindExport(FunctionPointer& OutFunctionPointer, const TCHAR* ExportName)
	{
		OutFunctionPointer = reinterpret_cast<FunctionPointer>(FPlatformProcess::GetDllExport(RuntimeHost, ExportName));
		return OutFunctionPointer != nullptr;
	}

	hostfxr_initialize_for_dotnet_command_line_fn Hostfxr_InitForCommandLine = nullptr;
	hostfxr_initialize_for_runtime_config_fn Hostfxr_InitForRuntimeConfig = nullptr;
	hostfxr_get_runtime_delegate_fn Hostfxr_GetRuntimeDelegate = nullptr;
	hostfxr_close_fn Hostfxr_Close = nullptr;

	void* RuntimeHost = nullptr;

	// ── Android raw-CoreCLR path (PLATFORM_ANDROID) ──────────────────────────
	// Android has no hostfxr; we bootstrap CoreCLR via coreclr_initialize /
	// coreclr_create_delegate (see coreclrhost.h). libcoreclr.so is compile-time
	// linked (CoreClrSDK.Build.cs PublicAdditionalLibraries), so we call the
	// coreclr_* functions directly via their prototypes — no dlopen / GetDllExport.
	// No opt-in switch — Android is a first-class supported platform, dispatched the
	// same way Win64/Mac dispatch to hostfxr.
#if PLATFORM_ANDROID
	bool InitializeManagedRuntimeAndroid();

	// Extract BCL (NonUFS, from Source/ThirdParty/CoreClrSDK/Android/runtime/) +
	// project DLLs + LoadOrder.json (UFS/PAK, from Content/Managed/Android/) into a
	// writable runtime dir (ProjectSavedDir/Managed/Android/). CoreCLR
	// requires real OS filesystem paths for the TPA. Uses a size pre-check + xxHash
	// sidecar to skip unchanged files (only extracts/overwrites when the source
	// differs) — so subsequent launches are fast and hot-updated DLLs re-extract.
	// OutSourceDllNames returns the .dll file names enumerated from the PAK/NonUFS
	// sources (not the extraction dir), so BuildTpa can avoid loading stale DLLs that
	// were removed from the PAK but linger in the extraction dir.
	bool EnsureRuntimeDllsExtracted(FString& OutRuntimeDir, TArray<FString>& OutSourceDllNames);

	// Build the colon-separated TRUSTED_PLATFORM_ASSEMBLIES list from the source .dll
	// names (enumerated from PAK/NonUFS), rooted at the extracted runtime dir.
	FString BuildTpa(const FString& RuntimeDir, const TArray<FString>& SourceDllNames) const;

	void* CoreClrHandle = nullptr;      // coreclr host handle (from coreclr_initialize)
	unsigned int CoreClrDomainId = 0;   // app domain id (from coreclr_initialize)
#endif
};
