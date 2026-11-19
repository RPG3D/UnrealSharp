#include "DotNet/CSDotNetRuntimeHost.h"

#if !UNREALSHARP_MONO && (PLATFORM_ANDROID || PLATFORM_IOS)

#include "CSBindsRegistry.h"
#include "CSManagedPluginCallbacks.h"
#include "CSPathsUtilities.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "Hash/xxhash.h"
#include "Logging/StructuredLog.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "UnrealSharpCore.h"

#include <coreclrhost.h>
#include <host_runtime_contract.h>
#include <cstdio>

#if PLATFORM_IOS
#include <TargetConditionals.h>
#endif

namespace
{
	constexpr TCHAR CoreLibFile[] = TEXT("System.Private.CoreLib.dll");
	constexpr TCHAR EntryAssemblyFile[] = TEXT("UnrealSharp.Plugins.dll");
	TArray<uint8> CoreLibBytes;

	bool ProbeCoreLib(const char* Path, void** Data, int64_t* Size)
	{
		// APK native library directories cannot carry CoreLib beside libcoreclr.
		const char* File = FCStringAnsi::Strrchr(Path, '/');
		if (FCStringAnsi::Strcmp(File ? File + 1 : Path, "System.Private.CoreLib.dll") != 0)
		{
			return false;
		}

		*Data = CoreLibBytes.GetData();
		*Size = CoreLibBytes.Num();
		return true;
	}

	bool UpdateFile(const FString& Destination, const FString& PhysicalDestination, const TArray<uint8>& Source)
	{
		// UE reads can fall back to a different iOS write layer; compare the actual CoreCLR file.
		if (FILE* ExistingFile = std::fopen(TCHAR_TO_UTF8(*PhysicalDestination), "rb"))
		{
			bool bMatches = false;
			if (std::fseek(ExistingFile, 0, SEEK_END) == 0
				&& std::ftell(ExistingFile) == Source.Num()
				&& std::fseek(ExistingFile, 0, SEEK_SET) == 0)
			{
				TArray<uint8> Existing;
				Existing.SetNumUninitialized(Source.Num());
				bMatches = std::fread(Existing.GetData(), Source.Num(), 1, ExistingFile) == 1
					&& FXxHash64::HashBuffer(Source.GetData(), Source.Num()) == FXxHash64::HashBuffer(Existing.GetData(), Existing.Num());
			}
			std::fclose(ExistingFile);
			if (bMatches)
			{
				return true;
			}
		}

		const FString Temporary = Destination + TEXT(".tmp");
		const FString PhysicalTemporary = PhysicalDestination + TEXT(".tmp");
		// POSIX rename replaces the cache entry atomically after the new file was closed.
		return FFileHelper::SaveArrayToFile(Source, *Temporary)
			&& std::rename(TCHAR_TO_UTF8(*PhysicalTemporary), TCHAR_TO_UTF8(*PhysicalDestination)) == 0;
	}

	bool PrepareAssemblies(const FString& StagedDirectory, FString& OutDirectory, FString& OutTpa)
	{
		IFileManager& Files = IFileManager::Get();
		TArray<FString> DllNames;
		TArray<FString> PdbNames;
		Files.FindFiles(DllNames, *StagedDirectory, TEXT(".dll"));
		Files.FindFiles(PdbNames, *StagedDirectory, TEXT(".pdb"));
		if (!DllNames.Contains(CoreLibFile) || !DllNames.Contains(EntryAssemblyFile))
		{
			UE_LOGFMT(LogUnrealSharp, Error, "CoreCLR requires {0} and {1} in staged content {2}", CoreLibFile, EntryAssemblyFile, StagedDirectory);
			return false;
		}

		const FString CachePath = FPaths::ProjectSavedDir() / TEXT("UnrealSharp/CoreCLR");
		OutDirectory = IPlatformFile::GetPlatformPhysical().ConvertToAbsolutePathForExternalAppForWrite(*CachePath);
		// Keep logical paths in UE I/O: Simulator physical /Users paths would be remapped again.
		if (!Files.MakeDirectory(*CachePath, true))
		{
			UE_LOGFMT(LogUnrealSharp, Error, "Could not create CoreCLR cache {0}", OutDirectory);
			return false;
		}

		TArray<FString> Names = DllNames;
		Names.Append(PdbNames);
		TArray<FString> CachedNames;
		Files.FindFiles(CachedNames, *(CachePath / TEXT("*")), true, false);
		for (const FString& CachedName : CachedNames)
		{
			if (!Names.Contains(CachedName) && !Files.Delete(*(CachePath / CachedName)))
			{
				UE_LOGFMT(LogUnrealSharp, Error, "Could not remove an obsolete CoreCLR cache entry: {0}", CachedName);
				return false;
			}
		}

		for (const FString& Name : Names)
		{
			const FString SourcePath = StagedDirectory / Name;
			TArray<uint8> Source;
			if (!FFileHelper::LoadFileToArray(Source, *SourcePath) || Source.IsEmpty()
				|| !UpdateFile(CachePath / Name, OutDirectory / Name, Source))
			{
				UE_LOGFMT(LogUnrealSharp, Error, "Could not extract CoreCLR file {0} to {1}", SourcePath, OutDirectory);
				return false;
			}

			if (Name == CoreLibFile)
			{
				CoreLibBytes = MoveTemp(Source);
			}
		}

		DllNames.Sort();
		DllNames.Swap(0, DllNames.IndexOfByKey(CoreLibFile));
		OutTpa = FString::JoinBy(DllNames, TEXT(":"), [&OutDirectory](const FString& Name) { return OutDirectory / Name; });
		return true;
	}
}

bool FCSDotNetRuntimeHost::InitializeManagedRuntime()
{
	FString WorkingDirectory = UnrealSharp::Paths::GetUserAssemblyDirectory();
	FInitializeUnrealSharp InitializeUnrealSharp = InitializeMobileCoreClr(WorkingDirectory);
	if (!InitializeUnrealSharp)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize the CoreCLR host. Check logs for details.");
		return false;
	}

	FCSInitializationResult Result;
	const FTCHARToUTF8 WorkingDirectoryUtf8(*WorkingDirectory);
	InitializeUnrealSharp(
		reinterpret_cast<const UTF8CHAR*>(WorkingDirectoryUtf8.Get()),
		&GetManagedPluginCallbacks(),
		reinterpret_cast<const void*>(&FCSBindsRegistry::GetBoundFunction),
		&GetManagedCallbacks(),
		&Result);

	if (!Result.bSuccess)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize UnrealSharp! Exception:\n{0}", FString(StringCast<TCHAR>(Result.Message)));
		return false;
	}
	return true;
}

void FCSDotNetRuntimeHost::ShutdownManagedRuntime()
{
	if (CoreClrHost)
	{
		coreclr_shutdown(CoreClrHost, CoreClrDomain);
		CoreClrHost = nullptr;
	}
}

FInitializeUnrealSharp FCSDotNetRuntimeHost::InitializeMobileCoreClr(FString& OutWorkingDirectory)
{
	FString Tpa;
	FString CacheDirectory;
	if (!PrepareAssemblies(OutWorkingDirectory, CacheDirectory, Tpa))
	{
		return nullptr;
	}

	UE_LOGFMT(LogUnrealSharp, Display, "Initializing UnrealSharp with embedded CoreCLR from {0}", CacheDirectory);

	coreclr_set_error_writer([](const char* Message)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "CoreCLR: {0}", FString(UTF8_TO_TCHAR(Message)));
	});

	// CoreCLR retains the contract and the assembly bytes after initialization.
	static host_runtime_contract Contract{};
	Contract.size = sizeof(Contract);
	Contract.external_assembly_probe = &ProbeCoreLib;
	const FString ContractAddress = FString::Printf(TEXT("0x%llx"), static_cast<uint64>(reinterpret_cast<UPTRINT>(&Contract)));

#if PLATFORM_IOS
	FPlatformMisc::SetEnvironmentVar(TEXT("DOTNET_Interpreter"), TEXT("1"));
	FPlatformMisc::SetEnvironmentVar(TEXT("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"), TEXT("1"));
	const FString HostPath = FPlatformProcess::ExecutablePath();
	const FString NativeDirectory = FPaths::GetPath(HostPath) / TEXT("Frameworks/CoreCLR.framework/Frameworks");
#if TARGET_OS_SIMULATOR
	const char* RuntimeIdentifier = "iossimulator-arm64";
#else
	const char* RuntimeIdentifier = "ios-arm64";
#endif
#else
	const FString HostPath = CacheDirectory / EntryAssemblyFile;
	const FString& NativeDirectory = CacheDirectory;
	const char* RuntimeIdentifier = "android-arm64";
#endif

	const FTCHARToUTF8 HostPathUtf8(*HostPath);
	const FTCHARToUTF8 TpaUtf8(*Tpa);
	const FTCHARToUTF8 CacheUtf8(*CacheDirectory);
	const FTCHARToUTF8 NativeUtf8(*NativeDirectory);
	const FTCHARToUTF8 ContractUtf8(*ContractAddress);
	const char* Keys[] = {
		HOST_PROPERTY_TRUSTED_PLATFORM_ASSEMBLIES,
		HOST_PROPERTY_NATIVE_DLL_SEARCH_DIRECTORIES,
		HOST_PROPERTY_RUNTIME_CONTRACT,
		"APP_CONTEXT_BASE_DIRECTORY",
		"RUNTIME_IDENTIFIER",
	};
	const char* Values[] = { TpaUtf8.Get(), NativeUtf8.Get(), ContractUtf8.Get(), CacheUtf8.Get(), RuntimeIdentifier };
	static_assert(UE_ARRAY_COUNT(Keys) == UE_ARRAY_COUNT(Values));

	const int InitResult = coreclr_initialize(HostPathUtf8.Get(), "UnrealSharp", UE_ARRAY_COUNT(Keys), Keys, Values, &CoreClrHost, &CoreClrDomain);
	if (InitResult != 0 || !CoreClrHost)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "coreclr_initialize failed: {0}", InitResult);
		return nullptr;
	}

	void* EntryPoint = nullptr;
	const int DelegateResult = coreclr_create_delegate(CoreClrHost, CoreClrDomain, "UnrealSharp.Plugins",
		"UnrealSharp.Plugins.Main", "InitializeMobileCoreClr", &EntryPoint);
	if (DelegateResult != 0 || !EntryPoint)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "coreclr_create_delegate failed: {0}", DelegateResult);
		return nullptr;
	}

	OutWorkingDirectory = MoveTemp(CacheDirectory);
	return reinterpret_cast<FInitializeUnrealSharp>(EntryPoint);
}

#endif
