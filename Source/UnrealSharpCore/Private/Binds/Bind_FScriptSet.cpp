#include "CSBindsRegistry.h"

DECLARE_UNREALSHARP_BINDER(Bind_FScriptSet)
{
	bool IsValidIndex(FScriptSet* ScriptSet, int32 Index)
	{
		return ScriptSet->IsValidIndex(Index);
	}

	int Num(FScriptSet* ScriptSet)
	{
		int Num = ScriptSet->Num();
		return Num;
	}

	int GetMaxIndex(FScriptSet* ScriptSet)
	{
		return ScriptSet->GetMaxIndex();
	}

	void* GetData(int Index, FScriptSet* ScriptSet, FSetProperty* Property)
	{
		return ScriptSet->GetData(Index, Property->SetLayout);
	}

	void Empty(int Slack, FScriptSet* ScriptSet, FSetProperty* Property)
	{
		return ScriptSet->Empty(Slack, Property->SetLayout);
	}

	void EmptyElements(FScriptSet* ScriptSet, FSetProperty* Property)
	{
		FScriptSetHelper Helper(Property, ScriptSet);
		Helper.EmptyElements();
	}

	void RemoveAt(int Index, FScriptSet* ScriptSet, FSetProperty* Property)
	{
		return ScriptSet->RemoveAt(Index, Property->SetLayout);
	}

	int AddUninitialized(FScriptSet* ScriptSet, FSetProperty* Property)
	{
		return ScriptSet->AddUninitialized(Property->SetLayout);
	}

	// The element's FProperty defines all set operations. Keep these callbacks native so
	// the same bindings can be called when managed code is running under an interpreter.
	void Add(FScriptSet* ScriptSet, FSetProperty* Property, const void* Element)
	{
		const FProperty* ElementProperty = Property->ElementProp;
		ScriptSet->Add(Element, Property->SetLayout,
			[ElementProperty](const void* Value) { return ElementProperty->GetValueTypeHash(Value); },
			[ElementProperty](const void* A, const void* B) { return ElementProperty->Identical(A, B); },
			[ElementProperty, Element](void* Destination)
			{
				ElementProperty->InitializeValue(Destination);
				ElementProperty->CopySingleValue(Destination, Element);
			},
			[ElementProperty](void* Value)
			{
				if (!ElementProperty->HasAnyPropertyFlags(CPF_IsPlainOldData | CPF_NoDestructor))
				{
					ElementProperty->DestroyValue(Value);
				}
			});
	}

	int32 FindOrAdd(FScriptSet* ScriptSet, FSetProperty* Property, const void* Element)
	{
		const FProperty* ElementProperty = Property->ElementProp;
		return ScriptSet->FindOrAdd(Element, Property->SetLayout,
			[ElementProperty](const void* Value) { return ElementProperty->GetValueTypeHash(Value); },
			[ElementProperty](const void* A, const void* B) { return ElementProperty->Identical(A, B); },
			[ElementProperty, Element](void* Destination)
			{
				ElementProperty->InitializeValue(Destination);
				ElementProperty->CopySingleValue(Destination, Element);
			});
	}

	int FindIndex(FScriptSet* ScriptSet, FSetProperty* Property, const void* Element)
	{
		return FScriptSetHelper(Property, ScriptSet).FindElementIndexFromHash(Element);
	}

	BIND_UNREALSHARP_FUNCTION(IsValidIndex)
	BIND_UNREALSHARP_FUNCTION(Num)
	BIND_UNREALSHARP_FUNCTION(GetMaxIndex)
	BIND_UNREALSHARP_FUNCTION(GetData)
	BIND_UNREALSHARP_FUNCTION(Empty)
	BIND_UNREALSHARP_FUNCTION(EmptyElements)
	BIND_UNREALSHARP_FUNCTION(RemoveAt)
	BIND_UNREALSHARP_FUNCTION(AddUninitialized)
	BIND_UNREALSHARP_FUNCTION(Add)
	BIND_UNREALSHARP_FUNCTION(FindOrAdd)
	BIND_UNREALSHARP_FUNCTION(FindIndex)
}
