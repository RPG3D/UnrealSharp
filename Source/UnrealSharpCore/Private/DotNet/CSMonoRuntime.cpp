// CSMonoRuntime.cpp -- Mono Embedding API helpers for UnrealSharp
// Implements runtime initialization, assembly loading, method resolution,
// and platform-specific AOT/INTERP/DL-fallback setup.

#if UNREALSHARP_MONO

#include "CSMonoRuntime.h"
#include "UnrealSharpCore.h"
#include "CSPathsUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"

#if PLATFORM_IOS
#include <dlfcn.h>
#import <Foundation/Foundation.h>
#endif

// Cache: assembly path -> MonoAssembly* (avoid reloading the same DLL)
static TMap<FString, MonoAssembly*> LoadedMonoAssemblies;

// Ordered list of directories to search for assemblies.
// Populated once in InitializeMonoRuntime.
static TArray<FString> GMonoAssemblySearchPaths;

// ============================================================================
//  iOS BCL native library fallback loader
// ============================================================================
// .NET 9 Mono removed mono_dllmap_insert (g_asserts on call).
// Instead we use mono_dl_fallback_register to intercept P/Invoke DLL loads.
// BCL native libs live inside Mono.embeddedframework at
// <App>/Frameworks/Mono.framework/Frameworks/libFoo.dylib

#if PLATFORM_IOS
static FString GMonoFrameworksPath;

static const char* const kBclNativeLibs[] = {
	"System.Native",
	"System.Net.Security.Native",
	"System.IO.Compression.Native",
	"System.Security.Cryptography.Native.Apple",
	"System.Globalization.Native",
	nullptr
};

static bool IsBclNativeLib(const char* Name)
{
	for (int i = 0; kBclNativeLibs[i]; ++i)
	{
		if (FCStringAnsi::Stricmp(Name, kBclNativeLibs[i]) == 0)
			return true;
		if (FCStringAnsi::Strnicmp(Name, "lib", 3) == 0 &&
		    FCStringAnsi::Stricmp(Name + 3, kBclNativeLibs[i]) == 0)
			return true;
	}
	return false;
}

static void* OnMonoDlFallbackLoad(const char* Name, int Flags, char** Err, void* /*UserData*/)
{
	if (!Name) return nullptr;
	if (!IsBclNativeLib(Name)) return nullptr;

	const char* CleanName = (FCStringAnsi::Strnicmp(Name, "lib", 3) == 0) ? Name + 3 : Name;
	FString FullPath = FPaths::Combine(GMonoFrameworksPath,
		FString::Printf(TEXT("lib%s.dylib"), UTF8_TO_TCHAR(CleanName)));
	void* Handle = dlopen(TCHAR_TO_UTF8(*FullPath), Flags ? Flags : RTLD_LAZY);

	if (Handle)
	{
		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS DL fallback: loaded %s from %s"),
			UTF8_TO_TCHAR(Name), *FullPath);
	}
	else
	{
		Handle = dlopen(TCHAR_TO_UTF8(*FString::Printf(TEXT("lib%s.dylib"),
			UTF8_TO_TCHAR(CleanName))), RTLD_LAZY);
		if (Handle)
		{
			UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS DL fallback: loaded %s via @rpath"),
				UTF8_TO_TCHAR(Name));
		}
		else if (Err)
		{
			*Err = strdup(TCHAR_TO_UTF8(*FString::Printf(
				TEXT("iOS DL fallback: failed to open %s"), UTF8_TO_TCHAR(Name))));
		}
	}
	return Handle;
}

static void* OnMonoDlFallbackSymbol(void* Handle, const char* Name, char** Err, void* /*UserData*/)
{
	if (!Handle || !Name) return nullptr;
	void* Sym = dlsym(Handle, Name);
	if (!Sym && Err)
	{
		const char* DlErr = dlerror();
		*Err = DlErr ? strdup(DlErr) : nullptr;
	}
	return Sym;
}

static void* OnMonoDlFallbackClose(void* Handle, void* /*UserData*/)
{
	if (Handle) dlclose(Handle);
	return nullptr;
}
#endif // PLATFORM_IOS

// ============================================================================
//  UFS assembly loader (pak-aware)
// ============================================================================

static MonoAssembly* LoadAssemblyFromBytes(const FString& FilePath)
{
	TArray<uint8> FileBytes;
	if (!FFileHelper::LoadFileToArray(FileBytes, *FilePath, FILEREAD_Silent))
		return nullptr;

	MonoImageOpenStatus Status = MONO_IMAGE_OK;
	MonoImage* Image = mono_image_open_from_data_with_name(
		reinterpret_cast<char*>(FileBytes.GetData()),
		static_cast<uint32_t>(FileBytes.Num()),
		true,   // need_copy
		&Status,
		false,  // refonly
		TCHAR_TO_UTF8(*FilePath));

	if (!Image || Status != MONO_IMAGE_OK)
	{
		UE_LOG(LogUnrealSharp, Warning,
			TEXT("[Mono] mono_image_open_from_data failed for %s (status=%d)"),
			*FilePath, (int32)Status);
		return nullptr;
	}

	MonoAssembly* Assembly = mono_assembly_load_from(Image, TCHAR_TO_UTF8(*FilePath), &Status);
	if (!Assembly || Status != MONO_IMAGE_OK)
	{
		UE_LOG(LogUnrealSharp, Warning,
			TEXT("[Mono] mono_assembly_load_from failed for %s (status=%d)"),
			*FilePath, (int32)Status);
		mono_image_close(Image);
		return nullptr;
	}

	LoadedMonoAssemblies.Add(FilePath, Assembly);
	return Assembly;
}

static MonoAssembly* LoadAssemblyFromUFS(const char* AssemblyNameUtf8)
{
	FString AssemblyName = FString(UTF8_TO_TCHAR(AssemblyNameUtf8));
	if (AssemblyName.EndsWith(TEXT(".dll"), ESearchCase::IgnoreCase))
		AssemblyName = FPaths::GetBaseFilename(AssemblyName);

	const FString DllFilename = AssemblyName + TEXT(".dll");

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Resolving assembly '%s' across %d search paths"),
		*DllFilename, GMonoAssemblySearchPaths.Num());

	for (const FString& SearchDir : GMonoAssemblySearchPaths)
	{
		const FString FilePath = FPaths::Combine(SearchDir, DllFilename);
		MonoAssembly* Assembly = LoadAssemblyFromBytes(FilePath);
		if (Assembly)
		{
			UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] UFS-loaded assembly: %s"), *FilePath);
			return Assembly;
		}
		// Distinguish "file not found" (path resolution / UFS miss) from "found but bad image"
		// so packaging path issues are diagnosable instead of silently returning null.
		const bool bFileReadable = FPaths::FileExists(FilePath);
		UE_LOG(LogUnrealSharp, Verbose, TEXT("[Mono]   miss: %s (file exists via IFileManager: %s)"),
			*FilePath, bFileReadable ? TEXT("yes") : TEXT("no"));
	}

	UE_LOG(LogUnrealSharp, Warning, TEXT("[Mono] UFS-load failed for assembly: %s"), *DllFilename);
	return nullptr;
}

static MonoAssembly* OnMonoAssemblyPreload(
	MonoAssemblyName* AName,
	char** /*AssembliesPath*/,
	void* /*UserData*/)
{
	const char* Name = mono_assembly_name_get_name(AName);
	if (!Name) return nullptr;
	return LoadAssemblyFromUFS(Name);
}

// ============================================================================
//  Mono log callbacks
// ============================================================================

static void OnMonoLog(const char* InLogDomain, const char* InLogLevel, const char* InMessage,
	mono_bool /*Fatal*/, void* /*UserData*/)
{
	UE_LOG(LogUnrealSharp, Display, TEXT("[Mono|%s|%s] %s"),
		ANSI_TO_TCHAR(InLogLevel ? InLogLevel : "?"),
		ANSI_TO_TCHAR(InLogDomain ? InLogDomain : ""),
		ANSI_TO_TCHAR(InMessage ? InMessage : ""));
}

static void OnMonoPrint(const char* InMessage, mono_bool /*IsStdout*/)
{
	UE_LOG(LogUnrealSharp, Display, TEXT("[Mono] %s"),
		ANSI_TO_TCHAR(InMessage ? InMessage : ""));
}

// ============================================================================
//  Stale DLL cleanup for packaged builds
// ============================================================================

#if !WITH_EDITOR
static void PurgeStaleOverrideDlls(const FString& SavedOverrideDir)
{
#if PLATFORM_ANDROID
	extern FString GAPKFilename;
	const FString ExecPath = GAPKFilename;
#else
	const FString ExecPath = FPlatformProcess::ExecutablePath();
#endif

	IPlatformFile& PF = FPlatformFileManager::Get().GetPlatformFile();
	const FFileStatData ExecStat = PF.GetStatData(*ExecPath);

	if (!ExecStat.bIsValid)
	{
		UE_LOG(LogUnrealSharp, Warning,
			TEXT("[Mono] Could not stat executable for override-DLL staleness check: %s"), *ExecPath);
		return;
	}

	const FDateTime ExecMtime = ExecStat.ModificationTime;

	if (PF.DirectoryExists(*SavedOverrideDir))
	{
		PF.IterateDirectoryStat(*SavedOverrideDir,
			[&](const TCHAR* FilenameOrDir, const FFileStatData& DllStat) -> bool
			{
				if (DllStat.bIsDirectory) return true;
				const FString DllPath(FilenameOrDir);
				if (!DllPath.EndsWith(TEXT(".dll"), ESearchCase::IgnoreCase)) return true;

				if (ExecMtime > DllStat.ModificationTime)
				{
					UE_LOG(LogUnrealSharp, Warning,
						TEXT("[Mono] Removing stale override DLL (exe newer than DLL): %s"),
						FilenameOrDir);
					PF.DeleteFile(FilenameOrDir);
				}
				return true;
			});
	}
}
#endif // !WITH_EDITOR

// ============================================================================
//  Main initialization
// ============================================================================

MonoDomain* InitializeMonoRuntime(const FString& RuntimeDir, const FString& ExtraSearchPaths)
{
	LoadedMonoAssemblies.Empty();

	// Step 0: Diagnostic logging
	mono_trace_set_log_handler(OnMonoLog, nullptr);
	mono_trace_set_print_handler(OnMonoPrint);
	mono_trace_set_printerr_handler(OnMonoPrint);
	mono_trace_set_level_string("warning");

	const FString RuntimeDirNormalized = FPaths::ConvertRelativePathToFull(RuntimeDir);

	// Step 1: Set assembly search paths
#if PLATFORM_WINDOWS
	const TCHAR* MonoPathSep = TEXT(";");
#else
	const TCHAR* MonoPathSep = TEXT(":");
#endif
	FString AssembliesPath = RuntimeDirNormalized;
	if (!ExtraSearchPaths.IsEmpty())
		AssembliesPath += MonoPathSep + ExtraSearchPaths;

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] mono_set_assemblies_path: %s"), *AssembliesPath);
	mono_set_assemblies_path(TCHAR_TO_UTF8(*AssembliesPath));

	// Step 1b: Populate search paths for preload hook
	GMonoAssemblySearchPaths.Empty();

#if PLATFORM_IOS && WITH_IOS_SIMULATOR
	const FString ManagedPlatformDir = TEXT("IOSSimulator");
#elif PLATFORM_WINDOWS
	const FString ManagedPlatformDir = TEXT("Win64");
#elif PLATFORM_MAC
	const FString ManagedPlatformDir = TEXT("Mac");
#elif PLATFORM_ANDROID
	const FString ManagedPlatformDir = TEXT("Android");
#elif PLATFORM_IOS
	const FString ManagedPlatformDir = TEXT("IOS");
#else
	const FString ManagedPlatformDir = FPlatformProperties::PlatformName();
#endif

	const FString SavedOverrideDir = FPaths::ConvertRelativePathToFull(
		FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Managed"), ManagedPlatformDir));

#if !WITH_EDITOR
	PurgeStaleOverrideDlls(SavedOverrideDir);
#endif

	GMonoAssemblySearchPaths.Add(SavedOverrideDir);
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Override search dir (Saved): %s"), *SavedOverrideDir);

	TArray<FString> PathParts;
	AssembliesPath.ParseIntoArray(PathParts, MonoPathSep, true);
	for (FString& Part : PathParts)
		GMonoAssemblySearchPaths.Add(Part);

	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Registering UFS assembly preload hook (%d search paths)"),
		GMonoAssemblySearchPaths.Num());
	mono_install_assembly_preload_hook(OnMonoAssemblyPreload, nullptr);

	// Step 2: Platform-specific thread suspension
#if PLATFORM_MAC || PLATFORM_LINUX
	FPlatformMisc::SetEnvironmentVar(TEXT("MONO_THREADS_SUSPEND"), TEXT("preemptive"));
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] MONO_THREADS_SUSPEND=preemptive"));
#endif

	// Step 2b: Platform-specific AOT mode
#if PLATFORM_IOS
	{
		extern void* mono_aot_module_System_Private_CoreLib_info;

		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS: setting MONO_AOT_MODE_INTERP"));
		mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP);

		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS: registering System.Private.CoreLib AOT module"));
		mono_aot_register_module(static_cast<void**>(mono_aot_module_System_Private_CoreLib_info));

		// Resolve Mono.framework sub-Frameworks path
		NSString* BundlePath = [[NSBundle mainBundle] bundlePath];
		FString AppDir = FString(BundlePath);
		GMonoFrameworksPath = FPaths::Combine(AppDir, TEXT("Frameworks"),
			TEXT("Mono.framework"), TEXT("Frameworks"));
		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS: BCL native lib path: %s"), *GMonoFrameworksPath);

		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS: registering DL fallback for BCL native libs"));
		mono_dl_fallback_register(OnMonoDlFallbackLoad, OnMonoDlFallbackSymbol,
			OnMonoDlFallbackClose, nullptr);

		setenv("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1", 1);
		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] iOS: DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"));
	}
#else
	{
		const FString InterpFlagPath = FPaths::Combine(SavedOverrideDir, TEXT("mono_interp.flag"));
		const bool bForceInterp = FPaths::FileExists(InterpFlagPath);
		if (bForceInterp)
		{
			UE_LOG(LogUnrealSharp, Log,
				TEXT("[Mono] mono_interp.flag found - forcing MONO_AOT_MODE_INTERP_ONLY"));
			mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP_ONLY);
		}
		else
		{
			UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Using JIT (MONO_AOT_MODE_NONE)"));
			mono_jit_set_aot_mode(MONO_AOT_MODE_NONE);
		}
	}
#endif

	// Step 3: Parse config
	mono_config_parse(nullptr);

	// Step 4: Initialize JIT
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Calling mono_jit_init_version(\"UnrealSharp\", \"v4.0.30319\")..."));
	MonoDomain* Domain = mono_jit_init_version("UnrealSharp", "v4.0.30319");

	if (!Domain)
	{
		UE_LOG(LogUnrealSharp, Fatal, TEXT("[Mono] mono_jit_init_version failed - Domain is null"));
		return nullptr;
	}

	// Step 5: Set main thread
	mono_thread_set_main(mono_thread_current());

	char* BuildInfo = mono_get_runtime_build_info();
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Runtime initialized: %s"), ANSI_TO_TCHAR(BuildInfo));
	mono_free(BuildInfo);

	return Domain;
}

// ============================================================================
//  Method resolution helpers
// ============================================================================

static MonoMethod* FindMonoMethodInternal(
	const char* AssemblyPath,
	const char* TypeName,
	const char* MethodName,
	int32 ParamCount)
{
	FString PathKey(AssemblyPath);
	MonoAssembly* Assembly = nullptr;

	if (MonoAssembly** Cached = LoadedMonoAssemblies.Find(PathKey))
	{
		Assembly = *Cached;
	}
	else
	{
		Assembly = LoadAssemblyFromUFS(AssemblyPath);
		if (!Assembly)
		{
			UE_LOG(LogUnrealSharp, Error, TEXT("[Mono] UFS-load failed for assembly: %s"),
				ANSI_TO_TCHAR(AssemblyPath));
			return nullptr;
		}
	}

	MonoImage* Image = mono_assembly_get_image(Assembly);
	if (!Image)
	{
		UE_LOG(LogUnrealSharp, Error, TEXT("[Mono] Failed to get image: %s"),
			ANSI_TO_TCHAR(AssemblyPath));
		return nullptr;
	}

	MonoType* MType = mono_reflection_type_from_name(const_cast<char*>(TypeName), Image);
	if (!MType)
	{
		UE_LOG(LogUnrealSharp, Error, TEXT("[Mono] Type not found: %s"),
			ANSI_TO_TCHAR(TypeName));
		return nullptr;
	}

	MonoClass* Klass = mono_class_from_mono_type(MType);
	if (!Klass)
	{
		UE_LOG(LogUnrealSharp, Error, TEXT("[Mono] Class not found for type: %s"),
			ANSI_TO_TCHAR(TypeName));
		return nullptr;
	}

	MonoMethod* Method = mono_class_get_method_from_name(Klass, MethodName, ParamCount);
	if (!Method)
	{
		UE_LOG(LogUnrealSharp, Error, TEXT("[Mono] Method not found: %s.%s"),
			ANSI_TO_TCHAR(TypeName), ANSI_TO_TCHAR(MethodName));
		return nullptr;
	}

	return Method;
}

MonoMethod* FindMonoMethod(
	MonoDomain* /*Domain*/,
	const char* AssemblyPath,
	const char* TypeName,
	const char* MethodName,
	int32 ParamCount)
{
	return FindMonoMethodInternal(AssemblyPath, TypeName, MethodName, ParamCount);
}

int32 GetUnmanagedCallersOnlyFnPtr(
	MonoDomain* /*Domain*/,
	const char* AssemblyPath,
	const char* TypeName,
	const char* MethodName,
	void** OutFnPtr)
{
	check(AssemblyPath);
	check(TypeName);
	check(MethodName);
	check(OutFnPtr);

	*OutFnPtr = nullptr;

	MonoMethod* Method = FindMonoMethodInternal(AssemblyPath, TypeName, MethodName, -1);
	if (!Method)
		return -5;

	MonoError Error;
	mono_error_init(&Error);
	void* FnPtr = mono_method_get_unmanaged_callers_only_ftnptr(Method, &Error);

	if (!FnPtr)
	{
		const unsigned short ErrorCode = mono_error_get_error_code(&Error);
		const char* ErrorMsg = mono_error_get_message(&Error);
		UE_LOG(LogUnrealSharp, Error,
			TEXT("[Mono] mono_method_get_unmanaged_callers_only_ftnptr failed for %s.%s: 0x%x, %s"),
			ANSI_TO_TCHAR(TypeName), ANSI_TO_TCHAR(MethodName),
			(uint32)ErrorCode, ANSI_TO_TCHAR(ErrorMsg));
		mono_error_cleanup(&Error);
		return -6;
	}

	mono_error_cleanup(&Error);
	UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Resolved %s.%s -> 0x%p"),
		ANSI_TO_TCHAR(TypeName), ANSI_TO_TCHAR(MethodName), FnPtr);

	*OutFnPtr = FnPtr;
	return 0;
}

void ShutdownMonoRuntime(MonoDomain* Domain)
{
	if (Domain)
	{
		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Shutting down Mono runtime..."));
		LoadedMonoAssemblies.Empty();
		mono_jit_cleanup(Domain);
		UE_LOG(LogUnrealSharp, Log, TEXT("[Mono] Mono runtime shut down."));
	}
}

#endif // UNREALSHARP_MONO
