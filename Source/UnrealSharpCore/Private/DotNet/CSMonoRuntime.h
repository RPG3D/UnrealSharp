// CSMonoRuntime.h -- Mono Embedding API helpers for UnrealSharp
// Only compiled when UNREALSHARP_MONO=1 (set in MonoSDK.Build.cs)
#pragma once

#if UNREALSHARP_MONO

#include "CoreMinimal.h"

// Mono Embedding API headers
#include <mono/jit/jit.h>
#include <mono/metadata/appdomain.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/image.h>
#include <mono/metadata/class.h>
#include <mono/metadata/reflection.h>
#include <mono/metadata/threads.h>
#include <mono/metadata/mono-private-unstable.h>
#include <mono/metadata/object.h>
#include <mono/metadata/loader.h>
#include <mono/utils/mono-dl-fallback.h>
#include <mono/utils/mono-error.h>
#include <mono/utils/mono-logger.h>

// mono-config.h is not in this SDK's public headers; declare what we need directly.
extern "C" void mono_config_parse(const char* filename);

/**
 * Initialize the Mono runtime.
 *
 * Steps:
 *   1. Set log/print handlers for diagnostics
 *   2. Set assembly search paths (BCL + app assemblies)
 *   3. Install assembly preload hook (UFS/pak-aware)
 *   4. Configure platform-specific AOT mode
 *   5. Parse config
 *   6. Initialize JIT (create root domain)
 *   7. Set main thread
 *
 * @param RuntimeDir        Absolute path to BCL DLLs directory
 * @param ExtraSearchPaths  Colon/semicolon-separated paths for app assemblies
 * @return  Root MonoDomain*, or nullptr on failure
 */
MonoDomain* InitializeMonoRuntime(const FString& RuntimeDir, const FString& ExtraSearchPaths = TEXT(""));

/**
 * Resolve a static [UnmanagedCallersOnly] method and return its native function pointer.
 *
 * NOTE: mono_method_get_unmanaged_callers_only_ftnptr crashes in .NET 9/10 Mono when the
 * method has complex pointer parameters (PluginsCallbacks*, etc.) due to a bug in
 * marshal_get_managed_wrapper. Use FindMonoMethod + mono_runtime_invoke instead for such methods.
 *
 * @param Domain        The root MonoDomain
 * @param AssemblyPath  Absolute path to the .dll file (ANSI/UTF-8)
 * @param TypeName      Fully qualified type name, e.g. "UnrealSharp.Plugins.Main"
 * @param MethodName    Method name, e.g. "InitializeUnrealSharp"
 * @param OutFnPtr      Receives the unmanaged function pointer on success
 * @return  0 on success, negative error code on failure
 */
int32 GetUnmanagedCallersOnlyFnPtr(
	MonoDomain* Domain,
	const char* AssemblyPath,
	const char* TypeName,
	const char* MethodName,
	void** OutFnPtr);

/**
 * Look up a static method and return the MonoMethod* for use with mono_runtime_invoke.
 *
 * This is a safer alternative to GetUnmanagedCallersOnlyFnPtr when the method has
 * complex non-blittable or pointer parameters that trigger crashes in
 * mono_method_get_unmanaged_callers_only_ftnptr.
 *
 * @param Domain        The root MonoDomain
 * @param AssemblyPath  Absolute path to the .dll file
 * @param TypeName      Fully qualified type name (e.g. "UnrealSharp.Plugins.Main")
 * @param MethodName    Method name
 * @param ParamCount    Number of parameters (-1 = match by name)
 * @return  MonoMethod* on success, nullptr on failure
 */
MonoMethod* FindMonoMethod(
	MonoDomain* Domain,
	const char* AssemblyPath,
	const char* TypeName,
	const char* MethodName,
	int32 ParamCount = -1);

/**
 * Shutdown the Mono runtime.
 * @param Domain  The root MonoDomain returned by InitializeMonoRuntime
 */
void ShutdownMonoRuntime(MonoDomain* Domain);

#endif // UNREALSHARP_MONO
