#include "Export/ULocalPlayerExporter.h"

#include "CSManager.h"
#include "GameFramework/PlayerController.h"
#include "Engine/LocalPlayer.h"
#include "Subsystems/LocalPlayerSubsystem.h"

void* UULocalPlayerExporter::GetLocalPlayerSubsystem(UClass* SubsystemClass, APlayerController* PlayerController)
{
	if (!IsValid(PlayerController) || !IsValid(SubsystemClass))
	{
		return nullptr;
	}

	ULocalPlayerSubsystem* LocalPlayerSubsystem = PlayerController->GetLocalPlayer()->GetSubsystemBase(SubsystemClass);
	return UCSManager::Get().FindManagedObject(LocalPlayerSubsystem);
}
