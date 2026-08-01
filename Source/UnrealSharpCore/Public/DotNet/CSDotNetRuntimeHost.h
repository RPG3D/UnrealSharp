#pragma once

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

using FInitializeRuntimeHost = bool (*)(const TCHAR*, const TCHAR*, FCSManagedPluginCallbacks*, const void*, FCSManagedCallbacks*);

class FCSDotNetRuntimeHost
{
public:
	FCSDotNetRuntimeHost() = default;
	~FCSDotNetRuntimeHost();
	
	bool InitializeManagedRuntime();
	void ShutdownManagedRuntime();

private:
#if UNREALSHARP_MONO
	bool InitializeMonoHost();
	MonoDomain* MonoRootDomain = nullptr;
#endif
#if !UNREALSHARP_MONO || WITH_EDITOR
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
#endif
};
