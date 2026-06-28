#include "UnrealSharpCore.h"
#include "CoreMinimal.h"
#include "CSManager.h"
#include "CSDotnetUtilties.h"
#include "Properties/CSPropertyGeneratorManager.h"
#include "Modules/ModuleManager.h"

#if defined(__APPLE__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wpragma-once-outside-header"
#endif
#pragma once
#if defined(__APPLE__)
#pragma clang diagnostic pop
#endif

#define LOCTEXT_NAMESPACE "FUnrealSharpCoreModule"

DEFINE_LOG_CATEGORY(LogUnrealSharp);

void FUnrealSharpCoreModule::StartupModule()
{
#if WITH_EDITOR
	// Build the user solution at startup so managed DLLs are up-to-date.
	// BuildUserSolution auto-detects bUseMono from DefaultEngine.ini and injects
	// -p:UseMonoRuntime=true, so both CoreCLR and Mono paths work correctly.
	//
	// Cap the retry recursion (e.g. transient dotnet SDK / build failures) at 5 attempts
	// to avoid unbounded stack growth / OOM if the environment never becomes valid.
	static constexpr int32 MaxStartupRetries = 5;
	static int32 StartupRetryCount = 0;
	if (!UnrealSharp::DotNetUtilities::VerifyCSharpEnvironment() || !UnrealSharp::DotNetUtilities::BuildUserSolution())
	{
		if (++StartupRetryCount <= MaxStartupRetries)
		{
			StartupModule();
		}
		else
		{
			UE_LOG(LogUnrealSharp, Error, TEXT("UnrealSharp startup aborted after %d failed retries."), MaxStartupRetries);
		}
		return;
	}
#endif

	if (!DotNetRuntimeHost.InitializeManagedRuntime())
	{
		return;
	}

	UCSManager::Get().Initialize();
}

void FUnrealSharpCoreModule::ShutdownModule()
{
	FCSPropertyGeneratorManager::Shutdown();
}

#undef LOCTEXT_NAMESPACE
	
IMPLEMENT_MODULE(FUnrealSharpCoreModule, UnrealSharpCore)