using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnrealSharp.Core.Attributes;
using UnrealSharp.Core.Marshallers;

namespace UnrealSharp.Core;

public static class UnmanagedCallbacks
{
    [UnmanagedCallersOnly]
    public static unsafe IntPtr CreateNewManagedObject(IntPtr nativeObject, IntPtr typeHandlePtr, char** error)
    {
        try
        {
            if (nativeObject == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(nativeObject));
            }
            
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type == null)
            {
                throw new InvalidOperationException("The provided type handle does not point to a valid type.");
            }

            return UnrealSharpObject.Create(type, nativeObject);
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Failed to create new managed object: {ex.Message}");
            *error = (char*)Marshal.StringToHGlobalUni(ex.ToString());
        }

        return IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    public static IntPtr CreateNewManagedObjectWrapper(IntPtr managedObjectHandle, IntPtr typeHandlePtr)
    {
        try
        {
            if (managedObjectHandle == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(managedObjectHandle));
            }
            
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type is null)
            {
                throw new InvalidOperationException("The provided type handle does not point to a valid type.");
            }
            
            object? managedObject = GCHandleUtilities.GetObjectFromHandlePtr<object>(managedObjectHandle);
            if (managedObject is null)
            {
                throw new InvalidOperationException("The provided managed object handle does not point to a valid object.");
            }

            MethodInfo? wrapMethod = type.GetMethod("Wrap", BindingFlags.Public | BindingFlags.Static);
            if (wrapMethod is null)
            {
                throw new InvalidOperationException("The provided type does not have a static Wrap method.");
            }
            
            object? createdObject = wrapMethod.Invoke(null, [managedObject]);
            if (createdObject is null)
            {
                throw new InvalidOperationException("The Wrap method did not return a valid object.");
            }

            return GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(createdObject, createdObject.GetType().Assembly));
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Failed to create new managed object: {ex.Message}");
        }

        return IntPtr.Zero;
    }
    
    [UnmanagedCallersOnly]
    public static unsafe IntPtr LookupManagedMethod(IntPtr typeHandlePtr, nint methodNamePtr)
    {
        // CRITICAL: Copy the native string to a managed string IMMEDIATELY before any other operations.
        // Under Mono INTERP, native function pointer calls (e.g. logging) may cause the C++ heap at
        // methodNamePtr to be reallocated, corrupting the string if we read it later.
#if UNREALSHARP_MONO
        string methodNameString = new string((char*)methodNamePtr);
#endif
        try
        {
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);

            if (type == null)
            {
                throw new Exception("Invalid type handle");
            }

#if !UNREALSHARP_MONO
            string methodNameString = new string((char*)methodNamePtr);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
#endif
            Type? currentType = type;

            while (currentType != null)
            {
#if UNREALSHARP_MONO
                // Under Mono AOT INTERP, Type.GetMethod() may fail to find source-generator-generated
                // methods (Invoke_XXX) due to a known Mono reflection limitation with partial class
                // source-generated methods. Use TypeInfo.DeclaredMethods instead.
                MethodInfo? method = null;
                foreach (var candidate in currentType.GetTypeInfo().DeclaredMethods)
                {
                    if (candidate.Name == methodNameString)
                    {
                        method = candidate;
                        break;
                    }
                }
#else
                MethodInfo? method = currentType.GetMethod(methodNameString, flags);
#endif

                if (method != null)
                {
#if UNREALSHARP_MONO
                    // Under Mono, GetFunctionPointer() triggers JIT compilation via
                    // mono_method_get_unmanaged_wrapper_ftnptr_internal which can SIGSEGV.
                    // Instead, store the MethodInfo object itself as a GCHandle.
                    // InvokeManagedMethod will use reflection to call it under Mono.
                    GCHandle methodHandle = GCHandleUtilities.AllocateStrongPointer(method, type.Assembly);
                    return GCHandle.ToIntPtr(methodHandle);
#else
                    IntPtr functionPtr = method.MethodHandle.GetFunctionPointer();
                    GCHandle methodHandle = GCHandleUtilities.AllocateStrongPointer(functionPtr, type.Assembly);
                    return GCHandle.ToIntPtr(methodHandle);
#endif
                }

                currentType = currentType.BaseType;
            }

            return IntPtr.Zero;
        }
        catch (Exception e)
        {
            LogUnrealSharpCore.LogError($"Exception while trying to look up managed method: {e.Message}");
        }

        return IntPtr.Zero;
    }
    
    [UnmanagedCallersOnly]
    public static void InitializeStruct(IntPtr structHandle, IntPtr buffer)
    {
        try
        {
            Type? structType = GCHandleUtilities.GetObjectFromHandlePtr<Type>(structHandle);
            
            if (structType == null)
            {
                throw new Exception("Invalid struct type handle");
            }
            
            object? structInstance = Activator.CreateInstance(structType);
            
            if (structInstance == null)
            {
                throw new Exception("Failed to create struct instance");
            }

            MethodInfo? methodInfo = structType.GetMethod("ToNative");
            
            if (methodInfo == null)
            {
                throw new Exception("The struct type does not have a ToNative method");
            }
            
            methodInfo.Invoke(structInstance, [buffer]);
        }
        catch (Exception e)
        {
            LogUnrealSharpCore.LogError($"Exception while trying to initialize struct: {e.Message}");
        }
    }
    
    [UnmanagedCallersOnly]
    public static unsafe IntPtr LookupManagedType(IntPtr assemblyHandle, nint fullTypeNamePtr)
    {
        // CRITICAL: Copy the native string FIRST before any native callbacks that could corrupt it.
        string fullTypeNameString = new string((char*)fullTypeNamePtr);
        try
        {
            Assembly? loadedAssembly = GCHandleUtilities.GetObjectFromHandlePtr<Assembly>(assemblyHandle);

            if (loadedAssembly == null)
            {
                throw new InvalidOperationException("The provided assembly handle does not point to a valid assembly.");
            }

            return FindTypeInAssembly(loadedAssembly, fullTypeNameString);
        }
        catch (TypeLoadException ex)
        {
            LogUnrealSharpCore.LogError($"TypeLoadException while trying to look up managed type: {ex.Message}");
            return IntPtr.Zero;
        }
    }
    
    private static IntPtr FindTypeInAssembly(Assembly assembly, string fullTypeName)
    {
        Type[] types = assembly.GetTypes();
        foreach (Type type in types)
        {
            foreach (CustomAttributeData attributeData in type.CustomAttributes)
            {
                if (attributeData.AttributeType.FullName != typeof(GeneratedTypeAttribute).FullName)
                {
                    continue;
                }

                if (attributeData.ConstructorArguments.Count != 2)
                {
                    continue;
                }

                string fullName = (string)attributeData.ConstructorArguments[1].Value!;
                if (fullName == fullTypeName)
                {
                    return GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(type, assembly));
                }
            }
        }

        return IntPtr.Zero;
    }
    
    [UnmanagedCallersOnly]
    public static unsafe int InvokeManagedMethod(IntPtr managedObjectHandle,
        IntPtr methodHandlePtr,
        IntPtr argumentsBuffer,
        IntPtr returnValueBuffer,
        IntPtr exceptionTextBuffer)
    {
        try
        {
#if UNREALSHARP_MONO
            // Under Mono, LookupManagedMethod stores a MethodInfo GCHandle (not a function pointer).
            // Use reflection to invoke it.
            MethodInfo? methodInfo = GCHandleUtilities.GetObjectFromHandlePtrFast<MethodInfo>(methodHandlePtr);
            object managedObject = GCHandleUtilities.GetObjectFromHandlePtrFast<object>(managedObjectHandle)!;
            methodInfo!.Invoke(managedObject, new object[] { argumentsBuffer, returnValueBuffer });
#else
            IntPtr methodHandle = GCHandleUtilities.GetObjectFromHandlePtrFast<IntPtr>(methodHandlePtr)!;
            object managedObject = GCHandleUtilities.GetObjectFromHandlePtrFast<object>(managedObjectHandle)!;
            delegate*<object, IntPtr, IntPtr, void> methodPtr = (delegate*<object, IntPtr, IntPtr, void>) methodHandle;
            methodPtr(managedObject, argumentsBuffer, returnValueBuffer);
#endif
            return 0;
        }
        catch (Exception ex)
        {
            StringMarshaller.ToNative(exceptionTextBuffer, 0, ex.ToString());
            LogUnrealSharpCore.LogError($"Exception during InvokeManagedMethod: {ex.Message}");
            return 1;
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeDelegate(IntPtr delegatePtr)
    {
        try
        {
            Delegate? foundDelegate = GCHandleUtilities.GetObjectFromHandlePtr<Delegate>(delegatePtr);
            
            if (foundDelegate == null)
            {
                throw new Exception("Invalid delegate handle");
            }

            foundDelegate.DynamicInvoke();
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Exception during InvokeDelegate: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly]
    public static void Dispose(IntPtr handle, IntPtr assemblyHandle)
    {
        GCHandle foundHandle = GCHandle.FromIntPtr(handle);
        
        if (!foundHandle.IsAllocated)
        {
            return;
        }
        
        if (foundHandle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Assembly? foundAssembly = GCHandleUtilities.GetObjectFromHandlePtr<Assembly>(assemblyHandle);
        GCHandleUtilities.Free(foundHandle, foundAssembly);
    }

    [UnmanagedCallersOnly]
    public static void FreeHandle(IntPtr handle)
    {
        GCHandle foundHandle = GCHandle.FromIntPtr(handle);
        if (!foundHandle.IsAllocated) return;
        
        if (foundHandle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }
            
        foundHandle.Free();
    }
}