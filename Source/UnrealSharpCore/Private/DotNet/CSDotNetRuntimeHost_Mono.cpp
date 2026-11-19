#include "DotNet/CSDotNetRuntimeHost.h"

#if UNREALSHARP_MONO

#include "CSBindsRegistry.h"
#include "CSManagedPluginCallbacks.h"
#include "CSPathsUtilities.h"
#include "HAL/PlatformProcess.h"
#include "Logging/StructuredLog.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "UnrealSharpCore.h"

#include <mono/jit/jit.h>
#include <mono/metadata/appdomain.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/class.h>
#include <mono/metadata/image.h>
#include <mono/metadata/mono-config.h>
#include <mono/metadata/object.h>
#include <mono/metadata/threads.h>
#include <mono/utils/mono-logger.h>

#if PLATFORM_IOS
#include <dlfcn.h>
#include <mono/utils/mono-dl-fallback.h>

extern void* mono_aot_module_System_Private_CoreLib_info;
#endif

namespace
{
#if PLATFORM_ANDROID || PLATFORM_IOS
	MonoAssembly* LoadStagedAssembly(const FString& Path)
	{
		TArray<uint8> Bytes;
		if (!FFileHelper::LoadFileToArray(Bytes, *Path, FILEREAD_Silent) || Bytes.IsEmpty())
		{
			return nullptr;
		}

		const FTCHARToUTF8 Name(*Path);
		MonoImageOpenStatus Status = MONO_IMAGE_OK;
		MonoImage* Image = mono_image_open_from_data_with_name(
			reinterpret_cast<char*>(Bytes.GetData()), Bytes.Num(), true, &Status, false, Name.Get());
		if (!Image)
		{
			UE_LOGFMT(LogUnrealSharp, Error, "Cannot open Mono assembly {0}: {1}", Path, FString(UTF8_TO_TCHAR(mono_image_strerror(Status))));
			return nullptr;
		}

		MonoAssembly* Assembly = mono_assembly_load_from(Image, Name.Get(), &Status);
		mono_image_close(Image);
		if (!Assembly)
		{
			UE_LOGFMT(LogUnrealSharp, Error, "Cannot load Mono assembly {0}: {1}", Path, FString(UTF8_TO_TCHAR(mono_image_strerror(Status))));
		}
		return Assembly;
	}

	MonoAssembly* PreloadStagedAssembly(MonoAssemblyName* Name, char**, void* Context)
	{
		const FString SimpleName = UTF8_TO_TCHAR(mono_assembly_name_get_name(Name));
		if (SimpleName.IsEmpty() || FPaths::GetCleanFilename(SimpleName) != SimpleName)
		{
			return nullptr;
		}

		const FString& Directory = *static_cast<const FString*>(Context);
		return LoadStagedAssembly(Directory / (SimpleName + TEXT(".dll")));
	}
#else
	FString GetDesktopMonoBcl()
	{
#if PLATFORM_WINDOWS
		const FString Platform = TEXT("Win64");
#else
		const FString Platform = TEXT("Mac");
#endif
#if PLATFORM_CPU_ARM_FAMILY
		const TCHAR* Architecture = TEXT("arm64");
#else
		const TCHAR* Architecture = TEXT("x64");
#endif
		const FString PlatformDir = FPaths::ConvertRelativePathToFull(UnrealSharp::Paths::GetPluginDirectory())
			/ TEXT("Source/ThirdParty/DotNetRuntimeSDK/Mono") / Platform;
		const FString ArchDirectory = PlatformDir / Architecture;
		return (FPaths::DirectoryExists(ArchDirectory) ? ArchDirectory : PlatformDir) / TEXT("runtime");
	}
#endif

#if PLATFORM_IOS
	void* LoadMonoFrameworkLibrary(const char* Name, int Flags, char**, void*)
	{
		if (!Name)
		{
			return nullptr;
		}
		const FString Library = UTF8_TO_TCHAR(Name);
		if (FPaths::GetCleanFilename(Library) != Library)
		{
			return nullptr;
		}

		FString Stem = Library;
		Stem.RemoveFromStart(TEXT("lib"), ESearchCase::CaseSensitive);
		Stem.RemoveFromEnd(TEXT(".dylib"), ESearchCase::CaseSensitive);
		if (!Stem.StartsWith(TEXT("System."), ESearchCase::CaseSensitive) || !Stem.Contains(TEXT(".Native")))
		{
			return nullptr;
		}

		const FString Path = FPaths::GetPath(FPlatformProcess::ExecutablePath())
			/ TEXT("Frameworks/Mono.framework/Frameworks") / (TEXT("lib") + Stem + TEXT(".dylib"));
		// Mono has already converted the fallback flags to the platform's RTLD_* flags.
		return dlopen(TCHAR_TO_UTF8(*Path), Flags);
	}

	void* FindMonoFrameworkSymbol(void* Handle, const char* Name, char**, void*)
	{
		return dlsym(Handle, Name);
	}

	void* CloseMonoFrameworkLibrary(void* Handle, void*)
	{
		dlclose(Handle);
		return nullptr;
	}
#endif
}

bool FCSDotNetRuntimeHost::InitializeManagedRuntime()
{
	FCSInitializationResult Result;
	if (!InitializeMono(UnrealSharp::Paths::GetUserAssemblyDirectory(), Result))
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize the Mono host.");
		return false;
	}

	if (!Result.bSuccess)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize UnrealSharp! Exception:\n{0}", FString(StringCast<TCHAR>(Result.Message)));
		return false;
	}

#if !UE_BUILD_SHIPPING && !PLATFORM_ANDROID && !PLATFORM_IOS
	if (FParse::Param(FCommandLine::Get(), TEXT("-waitformanageddebugger")))
	{
		while (!FPlatformMisc::IsDebuggerPresent());
	}
#endif
	return true;
}

void FCSDotNetRuntimeHost::ShutdownManagedRuntime()
{
	if (MonoDomain)
	{
		mono_jit_cleanup(MonoDomain);
		MonoDomain = nullptr;
	}
}

bool FCSDotNetRuntimeHost::InitializeMono(const FString& WorkingDirectory, FCSInitializationResult& Result)
{
	const FString EntryAssemblyPath = UnrealSharp::Paths::GetUnrealSharpPluginsPath();
#if PLATFORM_ANDROID || PLATFORM_IOS
	// The preload callback can run on managed threads for the lifetime of the runtime.
	static FString StagedDirectory = WorkingDirectory;
	const FString& BclDirectory = StagedDirectory;
#else
	const FString BclDirectory = GetDesktopMonoBcl();
#endif

	if (!FPaths::FileExists(BclDirectory / TEXT("System.Private.CoreLib.dll")) || !FPaths::FileExists(EntryAssemblyPath))
	{
		UE_LOGFMT(LogUnrealSharp, Error, "Mono runtime files are missing. BCL: {0}; entry assembly: {1}", BclDirectory, EntryAssemblyPath);
		return false;
	}

	UE_LOGFMT(LogUnrealSharp, Display, "Initializing UnrealSharp with Mono from {0}", BclDirectory);

	mono_trace_set_log_handler([](const char* Domain, const char* Level, const char* Message, mono_bool Fatal, void*)
	{
		if (Fatal)
		{
			UE_LOGFMT(LogUnrealSharp, Error, "Mono [{0}]: {1}", FString(UTF8_TO_TCHAR(Domain ? Domain : "runtime")), FString(UTF8_TO_TCHAR(Message)));
		}
		else
		{
			UE_LOGFMT(LogUnrealSharp, Verbose, "Mono [{0}/{1}]: {2}", FString(UTF8_TO_TCHAR(Domain ? Domain : "runtime")),
				FString(UTF8_TO_TCHAR(Level ? Level : "")), FString(UTF8_TO_TCHAR(Message)));
		}
	}, nullptr);

#if PLATFORM_ANDROID || PLATFORM_IOS
	mono_install_assembly_preload_hook(PreloadStagedAssembly, &StagedDirectory);
	const FString AssemblySearchPath = BclDirectory;
#else
#if PLATFORM_WINDOWS
	const TCHAR* Separator = TEXT(";");
#else
	const TCHAR* Separator = TEXT(":");
#endif
	const FString AssemblySearchPath = BclDirectory + Separator + FPaths::ConvertRelativePathToFull(FPaths::GetPath(EntryAssemblyPath))
		+ Separator + WorkingDirectory;
#endif
	mono_set_assemblies_path(TCHAR_TO_UTF8(*AssemblySearchPath));

#if PLATFORM_IOS
	FPlatformMisc::SetEnvironmentVar(TEXT("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"), TEXT("1"));
	mono_dl_fallback_register(LoadMonoFrameworkLibrary, FindMonoFrameworkSymbol, CloseMonoFrameworkLibrary, nullptr);
	mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP);
	mono_aot_register_module(static_cast<void**>(mono_aot_module_System_Private_CoreLib_info));
#endif

	mono_config_parse(nullptr);
	mono_set_signal_chaining(true);
	mono_set_crash_chaining(true);
	MonoDomain = mono_jit_init_version("UnrealSharp", "v4.0.30319");
	if (!MonoDomain)
	{
		return false;
	}
	mono_thread_set_main(mono_thread_current());

#if PLATFORM_ANDROID || PLATFORM_IOS
	MonoAssembly* Assembly = LoadStagedAssembly(EntryAssemblyPath);
#else
	MonoAssembly* Assembly = mono_domain_assembly_open(MonoDomain, TCHAR_TO_UTF8(*FPaths::ConvertRelativePathToFull(EntryAssemblyPath)));
#endif
	MonoClass* EntryClass = Assembly ? mono_class_from_name(mono_assembly_get_image(Assembly), "UnrealSharp.Plugins", "Main") : nullptr;
	MonoMethod* EntryMethod = EntryClass ? mono_class_get_method_from_name(EntryClass, "InitializeMono", 5) : nullptr;
	if (!EntryMethod)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "Could not resolve the Mono entry point in {0}", EntryAssemblyPath);
		return false;
	}

	// mono_runtime_invoke expects raw values for pointers and addresses for value types (nint).
	FTCHARToUTF8 WorkingDirectoryUtf8(*WorkingDirectory);
	UPTRINT Binds = reinterpret_cast<UPTRINT>(&FCSBindsRegistry::GetBoundFunction);
	UPTRINT Callbacks = reinterpret_cast<UPTRINT>(&GetManagedCallbacks());
	void* Arguments[] = {
		const_cast<ANSICHAR*>(WorkingDirectoryUtf8.Get()),
		&GetManagedPluginCallbacks(),
		&Binds,
		&Callbacks,
		&Result,
	};
	MonoObject* Exception = nullptr;
	mono_runtime_invoke(EntryMethod, nullptr, Arguments, &Exception);
	if (Exception)
	{
		MonoClass* ExceptionClass = mono_object_get_class(Exception);
		UE_LOGFMT(LogUnrealSharp, Error, "Mono entry point raised {0}.{1}",
			FString(UTF8_TO_TCHAR(mono_class_get_namespace(ExceptionClass))), FString(UTF8_TO_TCHAR(mono_class_get_name(ExceptionClass))));
		return false;
	}
	return true;
}

#endif
