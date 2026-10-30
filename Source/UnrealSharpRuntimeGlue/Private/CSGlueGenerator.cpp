#include "CSGlueGenerator.h"

#include "UnrealSharpRuntimeGlue.h"
#include "Logging/StructuredLog.h"

void UCSGlueGenerator::SaveRuntimeGlue(FCSScriptBuilder& ScriptBuilder, const FString& FileName, const FString& Suffix)
{
	// Cooks and other commandlets start this module before plugin tag sources and the
	// project collision settings are registered, so they would overwrite the committed
	// glue with a degraded subset (missing plugin tags and object channels). The glue
	// assembly is built before a cook, so the checked-in file is the correct input there.
	if (IsRunningCommandlet())
	{
		return;
	}

	FString FullFileName = FileName + Suffix;
	FString Path = FPaths::Combine(FUnrealSharpRuntimeGlueModule::GetGlueFolder(), FullFileName);

	FString CurrentRuntimeGlue;
	FFileHelper::LoadFileToString(CurrentRuntimeGlue, *Path);

	if (CurrentRuntimeGlue == ScriptBuilder.ToString())
	{
		// No changes, return
		return;
	}

	if (!FFileHelper::SaveStringToFile(ScriptBuilder.ToString(), *Path))
	{
		UE_LOGFMT(LogUnrealSharpRuntimeGlue, Error, "Failed to save runtime glue to {0}", *Path);
		return;
	}

	UE_LOGFMT(LogUnrealSharpRuntimeGlue, Display, "Saved {0}", *FileName);
	FUnrealSharpRuntimeGlueModule::Get().GetOnRuntimeGlueChanged().Broadcast(this, Path);
}
