#pragma once

#include "CoreMinimal.h"
#include "Engine/DeveloperSettings.h"
#include "CSUnrealSharpSettings.generated.h"

UCLASS(config = UnrealSharp, defaultconfig, meta = (DisplayName = "UnrealSharp Settings"))
class UNREALSHARPCORE_API UCSUnrealSharpSettings : public UDeveloperSettings
{
	GENERATED_BODY()
	
public:

	UCSUnrealSharpSettings();

#if WITH_EDITOR
	// UObject interface
	virtual void PreEditChange(FProperty* PropertyAboutToChange) override;
	virtual void PostEditChangeProperty(FPropertyChangedEvent& PropertyChangedEvent) override;
	// End of UObject interface
#endif
	
	// Should we exit PIE when an exception is thrown in C#?
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging")
	bool bCrashOnException = true;

	// ---- Mono Debugger Settings (only used when bUseMono=true) --------------------

	// Performance mode disables the Mono debugger to reduce overhead.
	// Automatically enabled in Shipping builds.
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono")
	bool bMonoPerformanceMode = false;

	// Enable the Mono soft debugger in packaged builds (always on in the Editor
	// regardless of this flag).
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono")
	bool bEnableMonoDebugger = false;

	// Suspend the Mono runtime at startup until a debugger attaches.
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono")
	bool bMonoWaitDebugger = false;

	// Sleep duration after domain init when waiting for the debugger,
	// to avoid a mono_coop_mutex_lock STATE_BLOCKING crash.
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono")
	float MonoDelayStartTimeWhenWaitDebugger = 1.0f;

	// Mono soft debugger agent listen port.
	// VS: Debug -> Attach to Process -> Managed (.NET Core) -> 127.0.0.1:<port>
	// Rider: Run -> Attach to Process -> Mono Remote -> 127.0.0.1:<port>
	// NOTE: Changing this requires a restart (Mono is initialized once per process).
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono",
		meta = (ConfigRestartRequired = true))
	int32 MonoDebuggerPort = 56000;

	// Write the Mono debugger internal log to ProjectIntermediate/UnrealSharp/mono.log.
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono")
	bool bUseMonoLogFile = false;

	// Mono trace log level (0-10, higher = more verbose).
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Debugging | Mono")
	int32 MonoLogLevel = 10;

	// ---- End Mono Debugger Settings -----------------------------------------------

	bool HasNamespaceSupport() const;

protected:
	
	// Should we enable namespace support for generated types?
	// If false, all types will be generated in the global package and all types need to have unique names.
	// Currently destructive to the project if changed after BPs of C# types have been created.
	UPROPERTY(EditDefaultsOnly, config, Category = "UnrealSharp | Namespace", Experimental)
	bool bEnableNamespaceSupport = false;

	bool bRecentlyChangedNamespaceSupport = false;
	bool OldValueOfNamespaceSupport = false;
};
