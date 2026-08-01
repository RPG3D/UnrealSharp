
#if WITH_EDITOR

#include "CSBuildUtilties.h"
#include "CSBuildActionUtilities.h"
#include "UnrealSharpUtilities.h"
#include "CSPathsUtilities.h"
#include "CSProcessUtilities.h"
#include "CSUnrealSharpUtilitiesSettings.h"
#include "IUATHelperModule.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/MonitoredProcess.h"
#include "Styling/SlateStyleRegistry.h"

#define LOCTEXT_NAMESPACE "UnrealSharpBuildUtilities"

bool UnrealSharp::Build::InvokeUnrealSharpAutomation(const FString& BuildAction, const TMap<FString, FString>* ActionArgs, const FCSCommandError& OnError)
{
	FString Arguments;
	BuildArguments(BuildAction, ActionArgs, Arguments);

	UE_LOG(LogUnrealSharpUtilities, Display, TEXT("[UAT] Invoking AutomationTool: %s %s"), *BuildAction, *Arguments);

	// UAT enforces a single-instance mutex (ProcessSingleton). Another AutomationTool
	// holding the lock (e.g. Turnkey VerifySdk auto-triggered by UBT during editor
	// launch, a packaging run, ...) makes our invocation exit immediately with
	// "A conflicting instance of AutomationTool is already running". Instead of
	// failing outright, wait for the lock to be released and retry a few times.
	constexpr int32 MaxRetries = 3;
	constexpr float RetryDelaySeconds = 3.0f;

	int32 ReturnCode = 0;
	FString Output;
	for (int32 Attempt = 1; Attempt <= MaxRetries; ++Attempt)
	{
		ReturnCode = 0;
		Output.Reset();

		// On retry attempts, suppress OnError (an OkCancel dialog that would let the
		// user quit the editor) — a transient mutex conflict is expected here.
		const FCSCommandError& ErrorCallback = (Attempt > 1) ? FCSCommandError() : OnError;
		const bool bSuccess = Process::InvokeCommand(FSerializedUATProcess::GetUATPath(), Arguments, ReturnCode, Output, nullptr, ErrorCallback);
		if (bSuccess)
		{
			UE_LOG(LogUnrealSharpUtilities, Display, TEXT("[UAT] AutomationTool '%s' finished: success=true returncode=%d"), *BuildAction, ReturnCode);
			return true;
		}

		// Only retry on the UAT single-instance mutex conflict; any other failure is final.
		const bool bIsMutexConflict = Output.Contains(TEXT("conflicting instance"), ESearchCase::IgnoreCase);
		if (!bIsMutexConflict || Attempt >= MaxRetries)
		{
			UE_LOG(LogUnrealSharpUtilities, Display, TEXT("[UAT] AutomationTool '%s' finished: success=false returncode=%d"), *BuildAction, ReturnCode);
			return false;
		}

		UE_LOG(LogUnrealSharpUtilities, Warning, TEXT("[UAT] Conflicting AutomationTool instance detected (attempt %d/%d). Waiting %.0fs before retry..."), Attempt, MaxRetries, RetryDelaySeconds);
		FPlatformProcess::Sleep(RetryDelaySeconds);
	}

	UE_LOG(LogUnrealSharpUtilities, Display, TEXT("[UAT] AutomationTool '%s' finished: success=false"), *BuildAction);
	return false;
}

void UnrealSharp::Build::InvokeUnrealSharpAutomation_Async(const FString& BuildAction, const FText& BuildActionDisplayName, const TMap<FString, FString>* ActionArgs, const IUATHelperModule::UatTaskResultCallack& ResultCallback)
{
	if (!IsValid(GEditor))
	{
		if (InvokeUnrealSharpAutomation(BuildAction, ActionArgs))
		{
			 ResultCallback(TEXT("Completed"), 0.0);
		}
		else
		{
			 ResultCallback(TEXT("Failed"), -1.0);
		}
		
		return;
	}
	
	FString Arguments;
	BuildArguments(BuildAction, ActionArgs, Arguments);
	
#if PLATFORM_WINDOWS
	FText PlatformName = LOCTEXT("PlatformName_Windows", "Windows");
#elif PLATFORM_MAC
	FText PlatformName = LOCTEXT("PlatformName_Mac", "Mac");
#elif PLATFORM_LINUX
	FText PlatformName = LOCTEXT("PlatformName_Linux", "Linux");
#else
	FText PlatformName = LOCTEXT("PlatformName_Other", "Other OS");
#endif
	
	IUATHelperModule::Get().CreateUatTask(Arguments, 
		PlatformName, 
		BuildActionDisplayName,
	BuildActionDisplayName, 
	GetBuildActionIcon(),
	nullptr, 
	ResultCallback);
}

bool UnrealSharp::Build::BuildUserSolution(const FCSCommandError& OnError)
{
	FString SolutionDirectory = FPaths::ConvertRelativePathToFull(Paths::GetScriptFolderDirectory());
	
	TMap<FString, FString> Arguments;
	Arguments.Add(TEXT("OutputPath"), Paths::MakeQuotedPath(Paths::GetUserAssemblyDirectory()));
	Arguments.Add(TEXT("TargetConfiguration"), LexToString(FApp::GetBuildConfiguration()));
	
	if (!GetDefault<UCSUnrealSharpUtilitiesSettings>()->bShowBuildWarnings)
	{
		Arguments.Add(TEXT("clp"), TEXT("ErrorsOnly"));
	}

	return InvokeUnrealSharpAutomation(BuildAction::BuildUserSolution, &Arguments, OnError);
}

void UnrealSharp::Build::BuildArguments(const FString& BuildAction, const TMap<FString, FString>* ActionArgs, FString& OutArgs)
{
	const FString PluginFolder = FPaths::ConvertRelativePathToFull(IPluginManager::Get().FindPlugin(UE_PLUGIN_NAME)->GetBaseDir());
	
	OutArgs.Reset();
	OutArgs += BuildAction;
	OutArgs += FString::Printf(TEXT(" -ScriptDir=\"%s\""), *FPaths::Combine(PluginFolder, TEXT("Build"), TEXT("Scripts")));
	OutArgs += FString::Printf(TEXT(" -Project=\"%s\""), *FPaths::ConvertRelativePathToFull(FPaths::GetProjectFilePath()));
	
	if (ActionArgs && ActionArgs->Num() > 0)
	{
		for (const auto& [Key, Value] : *ActionArgs)
		{
			OutArgs += FString::Printf(TEXT(" -%s=%s"), *Key, *Value);
		}
	}
}

const FSlateBrush* UnrealSharp::Build::GetBuildActionIcon()
{
	const ISlateStyle* Style = FSlateStyleRegistry::FindSlateStyle("UnrealSharpStyle");
	return Style->GetBrush("UnrealSharp.Toolbar");
}

#endif

#undef LOCTEXT_NAMESPACE
