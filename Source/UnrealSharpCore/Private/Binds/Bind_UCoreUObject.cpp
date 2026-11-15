#include "CSBindsRegistry.h"
#include "CSManagedAssembly.h"
#include "CSManager.h"
#include "Logging/StructuredLog.h"

DECLARE_UNREALSHARP_BINDER(Bind_UCoreUObject)
{
	UField* GetNativeField(const char* InAssemblyName, const char* InNamespace, const char* InTypeName, ECSFieldType InFieldType)
	{
		TRACE_CPUPROFILER_EVENT_SCOPE(Bind_UCoreUObject::GetType);
		UCSManagedAssembly* Assembly = UCSManager::Get().FindOrLoadAssembly(InAssemblyName);
		ensure(Assembly);

		FCSFieldName FieldName(InTypeName, FCSNamespace(InNamespace), InAssemblyName, InFieldType);
		TSharedPtr<FCSManagedTypeDefinition> ManagedTypeDefinition = Assembly->FindOrAddManagedTypeDefinition(FieldName);
		UField* Field = ManagedTypeDefinition->GetDefinition();

	#if WITH_EDITOR
		if (UCSClass* Class = Cast<UCSClass>(Field))
		{
			UBlueprint* Blueprint = Cast<UBlueprint>(Class->ClassGeneratedBy);
			Field = Blueprint->SkeletonGeneratedClass;
		}
	#endif

		if (!IsValid(Field))
		{
			UE_LOG(LogUnrealSharp, Warning, TEXT("Failed to find type: %s.%s in assembly %s"),
				UTF8_TO_TCHAR(InNamespace), UTF8_TO_TCHAR(InTypeName), UTF8_TO_TCHAR(InAssemblyName));
			return nullptr;
		}

		return Field;
	}

	UDelegateFunction* GetNativeDelegate(const char* PackageName, const char* OuterName, const char* DelegateName)
	{
		TRACE_CPUPROFILER_EVENT_SCOPE(GetNativeDelegate);
		
		UPackage* Package = FindPackage(nullptr, UTF8_TO_TCHAR(PackageName));
		
		if (!IsValid(Package))
		{
			UE_LOG(LogUnrealSharp, Fatal, TEXT("GetNativeDelegate: Could not find package %s"), UTF8_TO_TCHAR(PackageName));
		}
		
		UObject* Outer = Package;
		if (OuterName)
		{
			Outer = FindObject<UObject>(Package, UTF8_TO_TCHAR(OuterName));
			if (!IsValid(Outer))
			{
				UE_LOG(LogUnrealSharp, Fatal, TEXT("GetNativeDelegate: Could not find outer %s in package %s"),
				UTF8_TO_TCHAR(OuterName), UTF8_TO_TCHAR(PackageName));
			}
		}
		
		UDelegateFunction* DelegateFunction = FindObject<UDelegateFunction>(Outer, UTF8_TO_TCHAR(DelegateName));
		if (!IsValid(DelegateFunction))
		{
			UE_LOG(LogUnrealSharp, Fatal, TEXT("GetNativeDelegate: Could not find delegate %s in outer %s in package %s"),
				UTF8_TO_TCHAR(DelegateName), OuterName ? UTF8_TO_TCHAR(OuterName) : TEXT("<null>"), UTF8_TO_TCHAR(PackageName));
		}
		
		return DelegateFunction;
	}

	UField* GetGeneratedClassFromSkeleton(UField* InType)
	{
		TRACE_CPUPROFILER_EVENT_SCOPE(GetGeneratedClassFromSkeleton);
		
		if (!IsValid(InType))
		{
			UE_LOGFMT(LogUnrealSharp, Warning, "GetGeneratedClassFromSkeleton called with invalid type");
			return nullptr;
		}

		UCSSkeletonClass* SkeletonClass = Cast<UCSSkeletonClass>(InType);

		if (!IsValid(SkeletonClass))
		{
			return nullptr;
		}

		return SkeletonClass->GetGeneratedClass();
	}
	
	BIND_UNREALSHARP_FUNCTION(GetNativeField)
	BIND_UNREALSHARP_FUNCTION(GetNativeDelegate)
	BIND_UNREALSHARP_FUNCTION(GetGeneratedClassFromSkeleton)
}