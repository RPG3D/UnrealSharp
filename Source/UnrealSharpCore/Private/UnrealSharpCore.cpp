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
	// Guard against the runaway retry loop: when the .NET SDK can't be found (e.g. the
	// editor's PATH doesn't include "Program Files\dotnet\"), VerifyCSharpEnvironment()
	// pops a dialog and returns false. The original code recursively re-entered
	// StartupModule() on failure, which under UAT/unattended cooks spun forever — each
	// iteration re-popping the dialog and ballooning memory until OOM. Cap the retries.
	static int32 StartupRetryCount = 0;
	constexpr int32 MaxStartupRetries = 10;
	if (!UnrealSharp::DotNetUtilities::VerifyCSharpEnvironment() || !UnrealSharp::DotNetUtilities::BuildUserSolution())
	{
		if (++StartupRetryCount < MaxStartupRetries)
		{
			StartupModule();
		}
		else
		{
			UE_LOGFMT(LogUnrealSharp, Fatal,
				"UnrealSharp failed to verify the C# environment after {0} attempts. Aborting to avoid runaway retry loop.",
				MaxStartupRetries);
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