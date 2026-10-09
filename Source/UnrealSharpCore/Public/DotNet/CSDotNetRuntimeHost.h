#pragma once

#include "CoreMinimal.h"

// When Mono is the active runtime (bUseMono=true), the hostfxr machinery is not
// used at all — under the unified Mono design the editor also runs Mono, so the
// CoreCLR/hostfxr path compiles out entirely on Mono builds.
#if !UNREALSHARP_MONO || WITH_EDITOR
#include <coreclr_delegates.h>
#include <hostfxr.h>
#endif

#include "HAL/PlatformProcess.h"

#if UNREALSHARP_MONO
typedef struct _MonoDomain MonoDomain;
#endif

struct FCSManagedCallbacks;
struct FCSManagedPluginCallbacks;

struct FCSInitializationResult
{
	bool bSuccess = false;
	const TCHAR* Message = nullptr;
};

using FInitializeUnrealSharp = void (*)(const UTF8CHAR*, FCSManagedPluginCallbacks*, const void*, FCSManagedCallbacks*, FCSInitializationResult*);

struct FCSDotNetLayout
{
	FString DotNetRoot;
	FString HostFxrPath;
	FString AppAssemblyPath;
	FString RuntimeConfigPath;
	bool bSelfContained = false;

	bool IsValid() const
	{
		return !DotNetRoot.IsEmpty() && !HostFxrPath.IsEmpty() && !RuntimeConfigPath.IsEmpty();
	}
};

class FCSDotNetRuntimeHost
{
public:
	FCSDotNetRuntimeHost() = default;
	~FCSDotNetRuntimeHost();

	bool InitializeManagedRuntime();
	void ShutdownManagedRuntime();

private:
#if UNREALSHARP_MONO
	// Mono backend (CSDotNetRuntimeHost_Mono.cpp). Selected at build time via
	// bUseMono in DefaultEngine.ini (MonoSDK.Build.cs defines UNREALSHARP_MONO).
	bool InitializeMonoHost();
	MonoDomain* MonoRootDomain = nullptr;
#endif

#if !UNREALSHARP_MONO || WITH_EDITOR
	static FCSDotNetLayout ResolveDotNetLayout(const FString& PluginAssemblyPath);

	load_assembly_and_get_function_pointer_fn InitializeHost();
	load_assembly_and_get_function_pointer_fn ConfigureRuntime(const FCSDotNetLayout& Layout) const;

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
#endif // !UNREALSHARP_MONO || WITH_EDITOR
};
