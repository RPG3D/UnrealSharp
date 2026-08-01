#pragma once

#include "ReflectionData/CSClassReflectionData.h"
#include "Functions/CSFunction.h"

class UCSBlueprint;
class UClass;

class FCSFunctionFactory
{
public:
	
	static UCSFunctionBase* CreateFunctionFromReflectionData(UClass* Outer, const FCSFunctionReflectionData& FunctionReflectionData);
	static UCSFunctionBase* CreateOverriddenFunction(UClass* Outer, UFunction* ParentFunction);
	
	static void GetOverriddenFunctions(const UClass* Outer, const TSharedPtr<const FCSClassReflectionData>& ClassReflectionData, TArray<UFunction*>& VirtualFunctions);
	UNREALSHARPCORE_API static void GenerateVirtualFunctions(UClass* Outer, const TSharedPtr<const FCSClassReflectionData>& ClassReflectionData);
	UNREALSHARPCORE_API static void GenerateFunctions(UClass* Outer, const TArray<FCSFunctionReflectionData>& FunctionsReflectionData);

	static void AddFunctionToOuter(UClass* Outer, UCSFunctionBase* Function);

	// Binds the managed method handle (Invoke_XXX) of every function of the
	// class. MUST be called after ALL functions are mounted and the class is
	// StaticLink()ed: Mono's GetFunctionPointer() runs the class cctor
	// synchronously during JIT compile (mono_runtime_class_init_full), and the
	// cctor reads every function of the class — binding mid-registration would
	// run it against an incomplete class and fail to resolve not-yet-mounted
	// functions. CoreCLR (editor) is unaffected (GetFunctionPointer doesn't
	// trigger the cctor there), which is why this only surfaces on Mono.
	UNREALSHARPCORE_API static void BindAllMethodHandles(UClass* Outer);

	static FProperty* CreateParameter(UFunction* Function, const FCSPropertyReflectionData& PropertyReflectionData);
	static void CreateParameters(UFunction* Function, const FCSFunctionReflectionData& PropertyReflectionData);

private:
	static void FinalizeFunctionSetup(UClass* Outer, UCSFunctionBase* Function);
	static UCSFunctionBase* CreateFunction_Internal(UClass* Outer, const FName& Name, const FCSFunctionReflectionData& FunctionReflectionData, EFunctionFlags FunctionFlags = FUNC_None, UStruct* ParentFunction = nullptr);
};
