// CSMonoRuntime.h -- Mono Embedding API helpers for UnrealSharp
// Compiled only when UNREALSHARP_MONO=1 (defined by MonoSDK.Build.cs).
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

class UCSUnrealSharpSettings;

/** Managed-side mirror of UnrealSharp.Plugins.Main.FCSInitializationResult.
 *  Managed layout ([StructLayout(Sequential)], NativeBool : byte): Success @0 (1 byte), Message @1 (UTF-8, NUL-terminated).
 *  The CoreCLR FCSInitializationResult in CSDotNetRuntimeHost.h has a different layout and must not be used here.
 *  KEEP IN SYNC with Main.cs — the static_assert below catches size drift, but a field type
 *  change on the managed side (e.g. NativeBool widened to int) would only misalign offsets
 *  silently, so update both together. */
struct FMonoInitializationResult
{
	uint8 Success = 0;
	char Message[4096] = {};
};

static_assert(sizeof(FMonoInitializationResult) == 4097, "FMonoInitializationResult must mirror the managed FCSInitializationResult layout (1-byte Success + 4096-byte UTF-8 message). See UnrealSharp.Plugins/Main.cs.");

/**
 * Initialize the Mono soft debugger agent. Must be called BEFORE mono_jit_init_version.
 *
 * Starts a single debugger agent on 127.0.0.1:{MonoDebuggerPort} (default 56000).
 * Both VS (Attach to Process -> Managed .NET Core) and Rider (Mono Remote Debugger)
 * can connect to this same port.
 *
 * Uses JIT mode - INTERP_ONLY breaks MethodHandle.GetFunctionPointer().
 *
 * @param Settings  UnrealSharp settings (reads debugger configuration).
 */
UNREALSHARPCORE_API void InitMonoDebugger(const UCSUnrealSharpSettings* Settings);

/** Returns true if the Mono debugger agent was successfully started. */
UNREALSHARPCORE_API bool IsMonoDebuggerActive();

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
 * @param RuntimeDir        Absolute path to the BCL DLLs directory (filesystem, not UFS)
 * @param ExtraSearchPaths  Semicolon/colon-separated additional search paths for app assemblies (UFS-allowed)
 * @return  Root MonoDomain*, or nullptr on failure
 */
UNREALSHARPCORE_API MonoDomain* InitializeMonoRuntime(const FString& RuntimeDir, const FString& ExtraSearchPaths = TEXT(""));

/**
 * Look up a static method and return the MonoMethod* for use with mono_runtime_invoke.
 *
 * This is preferred over mono_method_get_unmanaged_callers_only_ftnptr for methods with
 * complex pointer parameters: the ftnptr path crashes in .NET 9/10 Mono for such signatures
 * (marshal_get_managed_wrapper bug).
 *
 * @param Domain        The root MonoDomain (unused, kept for API symmetry)
 * @param AssemblyPath  Path to the .dll (UFS-allowed, resolved through the preload hook)
 * @param TypeName      Fully qualified type name (e.g. "UnrealSharp.Plugins.Main")
 * @param MethodName    Method name (e.g. "InitializeUnrealSharp")
 * @param ParamCount    Number of parameters (-1 = match by name only)
 * @return  MonoMethod* on success, nullptr on failure
 */
UNREALSHARPCORE_API MonoMethod* FindMonoMethod(
	MonoDomain* Domain,
	const char* AssemblyPath,
	const char* TypeName,
	const char* MethodName,
	int32 ParamCount = -1);

/**
 * Shutdown the Mono runtime.
 * @param Domain  The root MonoDomain returned by InitializeMonoRuntime
 */
UNREALSHARPCORE_API void ShutdownMonoRuntime(MonoDomain* Domain);

#endif // UNREALSHARP_MONO
