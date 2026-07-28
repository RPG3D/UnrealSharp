// CSDotNetRuntimeHost_Mobile.cpp
// Android + iOS raw-CoreCLR backend for FCSDotNetRuntimeHost.
//
// Mobile has no hostfxr, so we bootstrap CoreCLR through the low-level
// coreclr_initialize / coreclr_create_delegate C API (coreclrhost.h). The runtime
// library (libcoreclr.so / libcoreclr.dylib) is compile-time linked
// (CoreClrSDK.Build.cs PublicAdditionalLibraries), so we call coreclr_* directly
// via their prototypes — no dlopen / GetDllExport string lookup.
//
// Flow:
//   1. EnsureRuntimeDllsExtracted() — copy BCL + project DLLs + LoadOrder.json
//      (UFS/PAK, Content/Managed/<Platform>) into a writable runtime dir
//      (ProjectSavedDir/Managed/<Platform>). CoreCLR needs real OS filesystem
//      paths for the TPA. A size pre-check + xxHash sidecar skips unchanged
//      files so subsequent launches are fast.
//   2. coreclr_set_error_writer → forward CoreCLR diagnostics to UE_LOG (logcat).
//   3. BuildTpa() — colon-separated *.dll scan of the runtime dir (port of build_tpa()).
//   4. coreclr_initialize with TRUSTED_PLATFORM_ASSEMBLIES / APP_CONTEXT_BASE_DIRECTORY /
//      NATIVE_DLL_SEARCH_DIRECTORIES. On iOS additionally passes HOST_RUNTIME_CONTRACT
//      (pinvoke_override for __Internal + external_assembly_probe for System.Private.CoreLib).
//   5. coreclr_create_delegate for UnrealSharp.Plugins.Main.InitializeUnrealSharp
//      (type name WITHOUT assembly suffix) and invoke it with the same args as the
//      desktop hostfxr path.
//
// Compiled for PLATFORM_ANDROID || PLATFORM_IOS.

#if PLATFORM_ANDROID || PLATFORM_IOS
#include <string>
#include <string>
#include "DotNet/CSDotNetRuntimeHost.h"
#include "CSBindsRegistry.h"
#include "CSManagedCallbacksCache.h"
#include "CSManagedPluginCallbacks.h"
#include "CSPathsUtilities.h"
#include "UnrealSharpCore.h"
#include "Misc/Paths.h"
#include "Misc/FileHelper.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "Hash/xxhash.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/ConfigCacheIni.h"   // GConfig (ini trigger)
#include "host_runtime_contract.h"
#include <string>
#include <cstdlib>   // setenv (DOTNET_DiagnosticPorts / DOTNET_DefaultDiagnosticPortSuspend)

#if PLATFORM_IOS
#import <Foundation/Foundation.h>
#include <dlfcn.h>

static host_runtime_contract GHostContract;

static const void* CoreClrPinvokeOverride(const char* LibraryName, const char* EntryPointName)
{
	return (strcmp(LibraryName, "__Internal") == 0) ? dlsym(RTLD_DEFAULT, EntryPointName) : nullptr;
}

static TArray<uint8> GSpclBytes;

static FString GCoreClrExtractionDir;

static bool CoreClrExternalAssemblyProbe(const char* Path, void** DataStart, int64* Size)
{
	const char* Name = strrchr(Path, '/');
	Name = Name ? Name + 1 : Path;
	
	if (strcmp(Name, "System.Private.CoreLib.dll") != 0)
	{
		return false;
	}
	
	if (GSpclBytes.Num() == 0)
	{
		FString SpclPath = FPaths::Combine(GCoreClrExtractionDir, TEXT("System.Private.CoreLib.dll"));
	
		if (!FFileHelper::LoadFileToArray(GSpclBytes, *SpclPath))
		{
			UE_LOG(LogUnrealSharp, Error, TEXT("[CoreClr] external_assembly_probe: SPCL not found at %s"), *SpclPath);
			return false;
		}
		UE_LOG(LogUnrealSharp, Log, TEXT("[CoreClr] external_assembly_probe: loaded SPCL at %s"), *SpclPath);
	}
	
	*DataStart = GSpclBytes.GetData();
	*Size = GSpclBytes.Num();
	return true;
}

#endif

// CoreCLR error-writer callback — forwards each diagnostic line to UE_LOG.
// Routed to logcat on Android. DOTNET_HOST_TRACE is hostfxr-only and unavailable.
static void CoreClrErrorWriter(const char* Message)
{
	UE_LOG(LogUnrealSharp, Error, TEXT("[CORECLR] %s"),
		Message ? ANSI_TO_TCHAR(Message) : TEXT("(null)"));
}

// Whether to expose a CoreCLR diagnostic port and pause at startup so a managed
// debugger (VS .NET debugger via dotnet-dsrouter + `adb forward`) can attach
// before any managed code runs. Disabled in shipping builds.
//
// Two triggers, either enables:
//   - Command-line flag `-waitformanageddebugger` (parity with the desktop hostfxr
//     path in CSDotNetRuntimeHost.cpp). Passed to the Android activity via the
//     `cmdline` intent extra.
//   - `[UnrealSharp] bWaitForManagedDebuggerAndroid=true` in DefaultEngine.ini -
//     a robust fallback, since the Android intent-extra cmdline plumbing can be
//     finicky to pass per-launch.
#if !(UE_BUILD_SHIPPING)
static bool ShouldWaitForManagedDebuggerAndroid()
{
	if (FParse::Param(FCommandLine::Get(), TEXT("waitformanageddebugger")))
	{
		return true;
	}
	bool bWait = false;
	GConfig->GetBool(TEXT("UnrealSharp"), TEXT("bWaitForManagedDebuggerAndroid"), bWait, GEngineIni);
	return bWait;
}
#endif

// Extract every file matching the given extension(s) from a UE-virtual source
// directory (PAK/UFS or NonUFS — FFileHelper reads both) into DestDir. Uses a size
// pre-check + xxHash to skip unchanged files: a sidecar `DllHash/<name>.txt` stores
// the source hash from the last extraction; on launch we re-hash the source and only
// re-extract when size or hash differs. This is reliable (unlike mtime, which is
// bogus for PAK/NonUFS sources) and makes subsequent launches fast — only
// hot-updated DLLs/JSON re-extract.
static void ExtractDirToRuntime(const FString& SourceDir, const FString& DestDir,
                                const TArray<FString>& Extensions,
                                TArray<FString>& OutSourceFileNames)
{
	IPlatformFile& PF = FPlatformFileManager::Get().GetPlatformFile();
	IFileManager& FM = IFileManager::Get();

	if (!PF.DirectoryExists(*SourceDir))
	{
		UE_LOG(LogUnrealSharp, Warning,
			TEXT("[CoreClr] Source dir not found, skipping extraction: %s"), *SourceDir);
		return;
	}

	int32 Extracted = 0;
	int32 Skipped = 0;

	for (const FString& Ext : Extensions)
	{
		TArray<FString> FoundFiles;
		FM.FindFiles(FoundFiles, *SourceDir, *Ext);

		for (const FString& FileName : FoundFiles)
		{
			const FString SourceFile = SourceDir / FileName;
			const FString DestFile = DestDir / FileName;
			const FString HashFile = DestDir / FString("DllHash") / (FileName + FString(".txt"));

			// Record the source file name unconditionally (even when we skip extraction)
			// so BuildTpa enumerates from the PAK/NonUFS sources — avoids loading stale
			// DLLs that were removed from the PAK but linger in the extraction dir.
			OutSourceFileNames.Add(FileName);

			// Read source bytes and compute hash.
			TArray<uint8> SourceFileBytes;
			if (!FFileHelper::LoadFileToArray(SourceFileBytes, *SourceFile, FILEREAD_Silent))
			{
				UE_LOG(LogUnrealSharp, Error,
					TEXT("[CoreClr] Failed to read source file: %s"), *SourceFile);
				continue;
			}

			const FXxHash64 SourceHash = FXxHash64::HashBuffer(SourceFileBytes.GetData(), SourceFileBytes.Num());
			const FString SourceHashStr = FString::Printf(TEXT("%016llx"), SourceHash.Hash);

			// Fast path: dest + sidecar exist and the stored hash matches the source.
			if (PF.FileSize(*DestFile) == SourceFileBytes.Num() && PF.FileExists(*HashFile))
			{
				FString StoredHash;
				if (FFileHelper::LoadFileToString(StoredHash, *HashFile) && StoredHash == SourceHashStr)
				{
					++Skipped;
					continue;   
				}
			}

			// Extract (first launch or hot-updated file).
			if (!FFileHelper::SaveArrayToFile(SourceFileBytes, *DestFile))
			{
				UE_LOG(LogUnrealSharp, Error,
					TEXT("[CoreClr] Failed to write runtime file: %s"), *DestFile);
				continue;
			}

			// Write the hash sidecar so the next launch can skip this file.
			FFileHelper::SaveStringToFile(SourceHashStr, *HashFile);
			++Extracted;
		}
	}

	UE_LOG(LogUnrealSharp, Log,
		TEXT("[CoreClr] Extracted %d, skipped %d (unchanged) from %s"),
		Extracted, Skipped, *SourceDir);
}

bool FCSDotNetRuntimeHost::EnsureRuntimeDllsExtracted(FString& OutRuntimeDir, TArray<FString>& OutSourceDllNames)
{
	OutSourceDllNames.Reset();
	const FString PlatformName = UTF8_TO_TCHAR(FPlatformProperties::PlatformName());
#if PLATFORM_ANDROID
	const TCHAR* ProjectPlatformDir = TEXT("Android");
#elif PLATFORM_IOS
	// Project managed DLLs are cross-platform IL — simulator and device share the
	// unified Content/Managed/IOS/ PAK dir. (BCL + native libs are arch-specific and
	// staged from CoreClrSDK/{IOS|IOSSimulator}/ by CoreClrSDK.Build.cs.)
	const TCHAR* ProjectPlatformDir = TEXT("IOS");
#endif
	
	OutRuntimeDir = FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Managed"), PlatformName);

	IPlatformFile& PF = FPlatformFileManager::Get().GetPlatformFile();
	PF.CreateDirectoryTree(*OutRuntimeDir);

	if (!PF.DirectoryExists(*OutRuntimeDir))
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to create {0} CoreCLR runtime dir: {1}", PlatformName, *OutRuntimeDir);
		return false;
	}

	// Hash sidecars live in a DllHash subdirectory so they don't clutter the runtime
	// dir alongside the DLLs. Create it up front — FFileHelper::SaveStringToFile does
	// not create parent directories, so without this the sidecars would silently fail
	// to write and the xxHash skip path would never engage (every launch re-extracts).
	PF.CreateDirectoryTree(*(OutRuntimeDir / TEXT("DllHash")));

	// BCL + project managed DLLs + LoadOrder.json — all staged UFS (inside PAK) by
	// CoreClrSDK.Build.cs into Content/Managed/<Platform>/ (unified PAK dir: BCL and
	// project DLLs share one directory so Android/iOS follow the same layout).
	// Readable via FFileHelper (UE I/O resolves UFS/PAK files).
	const FString ProjectSourceDir = FPaths::Combine(
		FPaths::ProjectContentDir(), TEXT("Managed"), ProjectPlatformDir);

	// Collect file names from the PAK source (not the extraction dir) so the TPA
	// reflects what's currently in the PAK — a DLL removed from the PAK won't be loaded
	// even if it lingers in the extraction dir.
	TArray<FString> ProjectNames;
	ExtractDirToRuntime(ProjectSourceDir, OutRuntimeDir, {TEXT(".dll"), TEXT(".pdb"), TEXT(".json")}, ProjectNames);

	// TPA only wants .dll names (BCL + project). Sidecar .pdb / .json are excluded —
	// filter to .dll only.
	for (const FString& Name : ProjectNames)
	{
		if (Name.EndsWith(TEXT(".dll"), ESearchCase::IgnoreCase))
		{
			OutSourceDllNames.Add(Name);
		}
	}

	// The entry-point assembly must be present after extraction.
	const FString PluginsDll = FPaths::Combine(OutRuntimeDir, TEXT("UnrealSharp.Plugins.dll"));
	if (!PF.FileExists(*PluginsDll))
	{
		UE_LOGFMT(LogUnrealSharp, Fatal,
			"UnrealSharp.Plugins.dll not found in runtime dir after extraction: {0}. "
			"Ensure the managed bindings were published to Content/Managed/{1}/ and the "
			"CoreClrSDK BCL is populated.", *PluginsDll, ProjectPlatformDir);
		return false;
	}

	return true;
}

FString FCSDotNetRuntimeHost::BuildTpa(const FString& RuntimeDir, const TArray<FString>& SourceDllNames) const
{
	// Build TPA from the source .dll names (enumerated from PAK/NonUFS), rooted at the
	// extracted runtime dir. This avoids loading stale DLLs removed from the PAK.
	TArray<FString> DllNames = SourceDllNames;

	// Force System.Private.CoreLib to the front — CoreCLR requires it to be the
	// first TPA entry it loads.
	DllNames.Sort();
	const int32 CoreLibIdx = DllNames.IndexOfByPredicate([](const FString& P){
		return P.EndsWith(TEXT("System.Private.CoreLib.dll"), ESearchCase::IgnoreCase);
	});
	if (CoreLibIdx > 0)
	{
		const FString CoreLibName = DllNames[CoreLibIdx];
		DllNames.RemoveAt(CoreLibIdx);
		DllNames.Insert(CoreLibName, 0);
	}

	FString Tpa;
	for (const FString& FileName : DllNames)
	{
		if (!Tpa.IsEmpty())
		{
			Tpa += TEXT(":");
		}
		Tpa += FPaths::Combine(RuntimeDir, FileName);
	}

	UE_LOG(LogUnrealSharp, Log, TEXT("[CoreClr] TPA built: %d chars, %d assemblies"),
		Tpa.Len(), DllNames.Num());
	return Tpa;
}

bool FCSDotNetRuntimeHost::InitializeManagedRuntimeMobile()
{
	const FString PlatformName = UTF8_TO_TCHAR(FPlatformProperties::PlatformName());
	
	// 1. Prepare the writable runtime dir (extract BCL + project DLLs from PAK/NonUFS).
	FString RuntimeDir;
	TArray<FString> SourceDllNames;
	if (!EnsureRuntimeDllsExtracted(RuntimeDir, SourceDllNames))
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to prepare {0} CoreCLR runtime directory.",PlatformName);
		return false;
	}
	
	const FString CoreClrRuntimeDir = IFileManager::Get().ConvertToAbsolutePathForExternalAppForRead(*RuntimeDir);

#if PLATFORM_IOS
	GCoreClrExtractionDir = RuntimeDir;
	setenv("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1", 1);
#endif
	
	// 2. libcoreclr.so is compile-time linked (CoreClrSDK.Build.cs) and staged into
	//    APK lib/arm64-v8a/ by the APL, so the OS linker resolves it at load time.
	//    We call the coreclr_* functions directly via the coreclrhost.h prototypes.

	// 3. Forward CoreCLR diagnostics to logcat (DOTNET_HOST_TRACE is hostfxr-only).
	coreclr_set_error_writer(&CoreClrErrorWriter);

	// 4. Build TRUSTED_PLATFORM_ASSEMBLIES.
	const FString Tpa = BuildTpa(CoreClrRuntimeDir, SourceDllNames);
	
	// 5. coreclr_initialize. Keep UTF-8 std::strings alive across the call.
	const std::string TpaUtf8 = TCHAR_TO_UTF8(*Tpa);
	const std::string DirUtf8 = TCHAR_TO_UTF8(*CoreClrRuntimeDir);
	
#if PLATFORM_ANDROID
	const std::string ExePathUtf8 = TCHAR_TO_UTF8(*(CoreClrRuntimeDir / TEXT("UnrealSharp.Plugins.dll")));

	const char* PropertyKeys[] = {
		"TRUSTED_PLATFORM_ASSEMBLIES",
		"APP_CONTEXT_BASE_DIRECTORY",
		"NATIVE_DLL_SEARCH_DIRECTORIES",
	};
	const char* PropertyValues[] = {
		TpaUtf8.c_str(),
		DirUtf8.c_str(),
		DirUtf8.c_str(),  // native .so are in the linker search path (loaded by APL)
	};
	
#elif PLATFORM_IOS
	const std::string ExePathUtf8 = [[[[NSBundle mainBundle] executableURL] path] UTF8String];
	memset(&GHostContract, 0, sizeof(GHostContract));
	GHostContract.size = sizeof(host_runtime_contract);
	GHostContract.pinvoke_override = &CoreClrPinvokeOverride;
	GHostContract.external_assembly_probe = &CoreClrExternalAssemblyProbe;
	char ContractStr[32];
	snprintf(ContractStr, sizeof(ContractStr), "0x%zx", reinterpret_cast<size_t>(&GHostContract));
	
	const char* PropertyKeys[] = {
		"RUNTIME_IDENTIFIER",
		"APP_CONTEXT_BASE_DIRECTORY",
		"TRUSTED_PLATFORM_ASSEMBLIES",
		"HOST_RUNTIME_CONTRACT",
		"System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported",
		"System.Runtime.InteropServices.EnableConsumingManagedCodeFromNativeHosting",
		"System.Threading.EnableAutoreleasePool",
		"System.Globalization.InvariantGlobalization",
	};
	
	const char* PropertyValues[] = {
		"iossimulator-arm64",
		DirUtf8.c_str(),
		TpaUtf8.c_str(),
		ContractStr,
		"false",
		"true",
		"true",
		"true",
	};
	
#endif
	UE_LOG(LogUnrealSharp, Log, TEXT("[CoreClr] coreclr_initialize..."));
	const int32 InitResult = coreclr_initialize(
		ExePathUtf8.c_str(),
		"UnrealSharp",
		sizeof(PropertyKeys) / sizeof(PropertyKeys[0]),
		PropertyKeys,
		PropertyValues,
		&CoreClrHandle,
		&CoreClrDomainId);

	if (InitResult != 0 || !CoreClrHandle)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "coreclr_initialize failed with code: 0x{0}", InitResult);
		return false;
	}
	UE_LOG(LogUnrealSharp, Log, TEXT("[CoreClr] coreclr_initialize => 0x%x (domain %u)"),
		InitResult, CoreClrDomainId);

	// 6. coreclr_create_delegate for the managed entry point.
	// The type name is passed WITHOUT the assembly suffix (per AndroidClrDemo).
	FInitializeRuntimeHost InitializeUnrealSharp = nullptr;
	const int32 DelegateResult = coreclr_create_delegate(
		CoreClrHandle,
		CoreClrDomainId,
		"UnrealSharp.Plugins",          // entryPointAssemblyName
		"UnrealSharp.Plugins.Main",     // entryPointTypeName (no assembly suffix)
		"InitializeUnrealSharp",        // entryPointMethodName
		reinterpret_cast<void**>(&InitializeUnrealSharp));

	if (DelegateResult != 0 || !InitializeUnrealSharp)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal,
			"coreclr_create_delegate failed with code: 0x{0}. The entry point must be a public "
			"[UnmanagedCallersOnly] static method.", DelegateResult);
		return false;
	}
	UE_LOG(LogUnrealSharp, Log, TEXT("[CoreClr] coreclr_create_delegate => 0x%x, fn=%p"),
		DelegateResult, (void*)InitializeUnrealSharp);

	// 7. Invoke the entry point with the same arguments as the desktop path.
	// On Android TCHAR == char, so the TCHAR* args map directly to the managed
	// char* / nint parameters. The working directory is the extracted runtime dir.
	const FString UserWorkingDirectory = CoreClrRuntimeDir;
	const FString UnrealSharpLibraryAssembly = CoreClrRuntimeDir / TEXT("UnrealSharp.Plugins.dll");

	if (!InitializeUnrealSharp(*UserWorkingDirectory,
		*UnrealSharpLibraryAssembly,
		&GetManagedPluginCallbacks(),
		(const void*)&FCSBindsRegistry::GetBoundFunction,
		&GetManagedCallbacks()))
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize UnrealSharp (managed entry returned false)!");
		return false;
	}

	UE_LOG(LogUnrealSharp, Log, TEXT("[CoreClr] UnrealSharp initialized successfully."));
	return true;
}

#endif // PLATFORM_ANDROID || PLATFORM_IOS
