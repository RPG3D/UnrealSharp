using System.Collections.Generic;
using EpicGames.Core;
using EpicGames.UHT.Types;
using UnrealSharpManagedGlue.Utilities;

namespace UnrealSharpManagedGlue.SourceGeneration;

public static class GeneratorStringBuilderUtilities
{
    public static void BeginWithEditorPreproccesorBlock(this GeneratorStringBuilder stringBuilder)
    {
        stringBuilder.BeginPreproccesorBlock("WITH_EDITOR");
    }
    
    public static void TryAddWithEditor(this GeneratorStringBuilder stringBuilder, UhtField type)
    {
        if (!type.Package.IsEditorOnly())
        {
            return;
        }
        
        BeginWithEditorPreproccesorBlock(stringBuilder);
    }
    
    public static void TryAddWithEditor(this GeneratorStringBuilder stringBuilder, UhtProperty property)
    {
        if (!property.HasAllFlags(EPropertyFlags.EditorOnly))
        {
            return;
        }
        
        BeginWithEditorPreproccesorBlock(stringBuilder);
    }
    
    public static void TryEndWithEditor(this GeneratorStringBuilder stringBuilder, UhtType type)
    {
        if (!type.Package.IsEditorOnly())
        {
            return;
        }
        
        stringBuilder.EndPreproccesorBlock();
    }
    
    public static void TryEndWithEditor(this GeneratorStringBuilder stringBuilder,UhtProperty property)
    {
        if (!property.HasAllFlags(EPropertyFlags.EditorOnly))
        {
            return;
        }
        
        stringBuilder.EndPreproccesorBlock();
    }
    
    public static void TryEndWithEditor(this GeneratorStringBuilder stringBuilder,UhtFunction function)
    {
        if (!function.FunctionFlags.HasAllFlags(EFunctionFlags.EditorOnly))
        {
            return;
        }
        
        stringBuilder.EndPreproccesorBlock();
    }
    
    public static void TryAddWithEditor(this GeneratorStringBuilder stringBuilder,UhtFunction function)
    {
        if (!function.FunctionFlags.HasAllFlags(EFunctionFlags.EditorOnly))
        {
            return;
        }
        
        stringBuilder.BeginWithEditorPreproccesorBlock();
    }
    
    public static void BeginUnsafeBlock(this GeneratorStringBuilder stringBuilder)
    {
        stringBuilder.AppendLine("unsafe");
        stringBuilder.OpenBrace();
    }
    
    public static void EndUnsafeBlock(this GeneratorStringBuilder stringBuilder)
    {
        stringBuilder.CloseBrace();
    }
    
    public static void BeginNamespace(this GeneratorStringBuilder stringBuilder, UhtField type)
    {
        BeginNamespace(stringBuilder, type.Package);
    }
    
    public static void BeginNamespace(this GeneratorStringBuilder stringBuilder, UhtPackage package)
    {
        stringBuilder.AppendLine($"namespace {package.GetNamespace()}");
        stringBuilder.OpenBrace();
    }
    
    public static void EndNamespace(this GeneratorStringBuilder stringBuilder)
    {
        stringBuilder.CloseBrace();
    }
    
    
    public static void StartGlueFile(this GeneratorStringBuilder stringBuilder, UhtField type, bool blittable = false)
    {
        stringBuilder.DeclareDirective(ScriptGeneratorUtilities.AttributeNamespace);
        stringBuilder.DeclareDirective(ScriptGeneratorUtilities.CoreNamespace);
        stringBuilder.DeclareDirective(ScriptGeneratorUtilities.CoreAttributeNamespace);
        stringBuilder.DeclareDirective(ScriptGeneratorUtilities.InteropNamespace);
        stringBuilder.DeclareDirective(ScriptGeneratorUtilities.MarshallerNamespace);
        
        stringBuilder.AppendLine($"using static UnrealSharp.Interop.{ExporterCallbacks.Bind_FProperty};");
        stringBuilder.AppendLine("#nullable enable");
        
        if (blittable)
        {
            stringBuilder.DeclareDirective(ScriptGeneratorUtilities.InteropServicesNamespace);
        }

        stringBuilder.AppendLine();
        BeginNamespace(stringBuilder, type);
        stringBuilder.AppendLine();
    }
    
    public static void EndGlueFile(this GeneratorStringBuilder stringBuilder, UhtField type)
    {
        stringBuilder.CloseBrace();
    }
    
    public static void DeclareType(this GeneratorStringBuilder stringBuilder, UhtType? type , string typeName, string declaredTypeName, string? baseType = null, bool isPartial = true, string? modifiers = "", List<UhtClass>? nativeInterfaces = default, List<string>? csInterfaces = default)
    {
        string partialSpecifier = isPartial ? "partial " : string.Empty;
        List<string> inheritingFrom = new List<string>();

        if (!string.IsNullOrEmpty(baseType))
        {
            inheritingFrom.Add(baseType);
        }

        if (nativeInterfaces != null)
        {
            foreach (UhtType @interface in nativeInterfaces)
            {
                string fullInterfaceName = @interface.GetFullManagedName();
                inheritingFrom.Add(fullInterfaceName);
            }
        }

        if (csInterfaces != null)
        {
            foreach (string @interface in csInterfaces) inheritingFrom.Add(@interface);
        }

        string accessSpecifier = "public";
        if (type != null && type.HasMetadata("InternalType"))
        {
            accessSpecifier = "internal";
        }

        string inheritanceSpecifier = inheritingFrom.Count > 0 ? $" : {string.Join(", ", inheritingFrom)}" : string.Empty;
        
        stringBuilder.AppendLine($"{accessSpecifier}{modifiers} {partialSpecifier}{typeName} {declaredTypeName}{inheritanceSpecifier}");
        stringBuilder.OpenBrace();
    }

    public static void AppendNativeTypePtr(this GeneratorStringBuilder stringBuilder, UhtStruct structType)
    {
        stringBuilder.AppendLine($"static readonly IntPtr NativeClassPtr = {ExporterCallbacks.Bind_CoreUObject}.CallGetType({structType.ExportGetAssemblyName()}, \"{structType.GetNamespace()}\", \"{structType.EngineName}\");");
    }
    
    public static void AppendStackAlloc(this GeneratorStringBuilder stringBuilder, string sizeVariableName)
    {
        // 16-byte align the stackalloc'd param buffer. Under the Mono interpreter
        // (INTERP mode, iOS/Android ARM64) stackalloc byte[N] only guarantees byte
        // alignment, but UE structs with CppStructOps (e.g. FHitResult) require
        // >= 8-byte alignment — UScriptStruct::InitializeStruct has
        // checkf(IsAligned(ptr, alignment)) which crashes on a misaligned buffer.
        // Over-allocate by 15 and round up: ((p + 15) & ~15). Mirrors the JIT path
        // in UnrealSharp.GlueGenerator/StringBuilderExtensions.cs.
        stringBuilder.AppendLine($"byte* paramsBufferAllocation = stackalloc byte[{sizeVariableName} + 15];");
        stringBuilder.AppendLine("nint paramsBuffer = (nint)((((nint)paramsBufferAllocation) + 15) & ~(nint)15);");
    }

    public static void AppendStackAllocFunction(this GeneratorStringBuilder stringBuilder, string sizeVariableName, string structName, bool appendInitializer = true)
    {
        stringBuilder.AppendStackAlloc(sizeVariableName);
        
        if (appendInitializer)
        {
            stringBuilder.AppendLine($"{ExporterCallbacks.Bind_UFunction}.CallInitializeFunctionParams({structName}, paramsBuffer);");
        }
    }

    public static void AppendStackAllocProperty(this GeneratorStringBuilder stringBuilder, string sizeVariableName, string sourcePropertyName)
    {
        stringBuilder.AppendStackAlloc(sizeVariableName);
        stringBuilder.AppendLine($"CallInitializeValue({sourcePropertyName}, paramsBuffer);");
    }
}