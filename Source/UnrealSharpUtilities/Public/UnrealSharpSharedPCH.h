#pragma once

// iOS-only non-IWYU private PCH for the UnrealSharp runtime modules.
//
// The Game-target unity grouping differs per platform (Android happens to compile a
// .gen.cpp first, iOS can start with CSWorldExtensions.cpp), so source files that rely
// on a neighbour having included a header fail only on iOS. Force-include the common
// Engine/CoreUObject surface there instead of making every file self-contained.
#include "CoreMinimal.h"

#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Engine/Blueprint.h"
#include "Engine/DataTable.h"
#include "Engine/Engine.h"
#include "Engine/GameInstance.h"
#include "Engine/InheritableComponentHandler.h"
#include "Engine/LocalPlayer.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "Misc/App.h"
#include "Misc/MessageDialog.h"
#include "Misc/Paths.h"
#include "Runtime/Launch/Resources/Version.h"
#include "Serialization/AsyncLoadingEvents.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Styling/SlateStyle.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "Subsystems/LocalPlayerSubsystem.h"
#include "Subsystems/Subsystem.h"
#include "Subsystems/SubsystemCollection.h"
#include "Tickable.h"
#include "TimerManager.h"
#include "UObject/LinkerLoad.h"
#include "UObject/UObjectIterator.h"
