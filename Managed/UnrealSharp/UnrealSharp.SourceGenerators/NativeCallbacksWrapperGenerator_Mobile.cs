using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis;
using UnrealSharp.SourceGenerator.Utilities;

namespace UnrealSharp.SourceGenerators;

// Interpreter ABI adaptations live here; discovery and wrapper declarations track upstream.
public partial class NativeCallbacksWrapperGenerator
{
    private static void AppendNativeCall(StringBuilder sourceBuilder, ClassInfo classInfo,
        DelegateInfo delegateInfo, SemanticModel model, string returnTypeFullName)
    {
        // The iOS CoreCLR interpreter rejects calli that requires runtime marshalling.
        // Pin by-reference arguments in place and encode narrow strings explicitly.
        List<DelegateParameterInfo> pinned = delegateInfo.Parameters
            .Where(parameter => CanPinForNativeCall(parameter, model))
            .ToList();
        for (int i = 0; i < pinned.Count; i++)
        {
            DelegateParameterInfo parameter = pinned[i];
            string name = parameter.Name;
            string scopeIndent = new string(' ', 12 + i * 4);
            string type = GetTypeName(parameter, model);
            if (IsString(parameter, model))
            {
                sourceBuilder.AppendLine($"{scopeIndent}byte[]{(classInfo.NullableAwareable ? "?" : "")} {name}Utf8 = null;");
                sourceBuilder.AppendLine($"{scopeIndent}if ({name} != null)");
                sourceBuilder.AppendLine(scopeIndent + "{");
                sourceBuilder.AppendLine($"{scopeIndent}    {name}Utf8 = new byte[global::System.Text.Encoding.UTF8.GetByteCount({name}) + 1];");
                sourceBuilder.AppendLine($"{scopeIndent}    global::System.Text.Encoding.UTF8.GetBytes({name}, 0, {name}.Length, {name}Utf8, 0);");
                sourceBuilder.AppendLine(scopeIndent + "}");
                sourceBuilder.AppendLine($"{scopeIndent}fixed (byte* {name}Ptr = {name}Utf8)");
            }
            else
            {
                sourceBuilder.AppendLine($"{scopeIndent}fixed ({type}* {name}Ptr = &{name})");
            }
            sourceBuilder.AppendLine(scopeIndent + "{");
        }

        string invocationTarget = delegateInfo.Name;
        if (pinned.Count > 0)
        {
            IEnumerable<string> abiTypes = delegateInfo.Parameters.Select(parameter =>
            {
                string type = GetTypeName(parameter, model);
                if (!pinned.Contains(parameter))
                {
                    return parameter.RefModifier + type;
                }
                return IsString(parameter, model) ? "byte*" : type + "*";
            });
            invocationTarget = $"((delegate* unmanaged<{string.Join(", ", abiTypes.Append(returnTypeFullName))}>)(void*){delegateInfo.Name})";
        }

        string arguments = string.Join(", ", delegateInfo.Parameters.Select(parameter =>
            pinned.Contains(parameter) ? parameter.Name + "Ptr" : parameter.RefModifier + parameter.Name));
        string returnPrefix = delegateInfo.HasReturnValue ? "return " : "";
        string indent = new string(' ', 12 + pinned.Count * 4);
        sourceBuilder.AppendLine($"{indent}{returnPrefix}{invocationTarget}({arguments});");

        for (int i = pinned.Count - 1; i >= 0; i--)
        {
            sourceBuilder.AppendLine(new string(' ', 12 + i * 4) + "}");
        }
    }

    private static string GetTypeName(DelegateParameterInfo parameter, SemanticModel model)
        => parameter.Type.GetAnnotatedTypeName(model) ?? parameter.Type.ToString();

    private static bool IsString(DelegateParameterInfo parameter, SemanticModel model)
        => model.GetTypeInfo(parameter.Type).Type?.SpecialType == SpecialType.System_String;

    private static bool CanPinForNativeCall(DelegateParameterInfo parameter, SemanticModel model)
    {
        ITypeSymbol? type = model.GetTypeInfo(parameter.Type).Type;
        return parameter.RefModifier.Length == 0
            ? type?.SpecialType == SpecialType.System_String
            : type != null && IsBlittableForNativeCall(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
    }

    private static bool IsBlittableForNativeCall(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        // Unmanaged types can still need marshalling (for example, a bool field
        // annotated with MarshalAs). Only pin types whose native layout is unchanged.
        if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return true;
        }

        if (type is not INamedTypeSymbol namedType || !type.IsUnmanagedType)
        {
            return false;
        }

        if (namedType.TypeKind == TypeKind.Enum)
        {
            return namedType.EnumUnderlyingType != null
                && IsBlittableForNativeCall(namedType.EnumUnderlyingType, visiting);
        }

        if (type.SpecialType != SpecialType.None || type.TypeKind != TypeKind.Struct
            || namedType.TypeArguments.Any(argument => argument.TypeKind == TypeKind.TypeParameter)
            || !visiting.Add(type))
        {
            return false;
        }

        bool isAutoLayout = type.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute"
            && attribute.ConstructorArguments.FirstOrDefault().Value is int layout
            && layout == (int)System.Runtime.InteropServices.LayoutKind.Auto);

        bool blittable = !isAutoLayout && HasBlittableMetadataLayout(namedType)
            && namedType.GetMembers().OfType<IFieldSymbol>()
            .Where(field => !field.IsStatic && !field.IsConst)
            .All(field => !field.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.MarshalAsAttribute")
                && IsBlittableForNativeCall(
                    field.IsFixedSizeBuffer && field.Type is IPointerTypeSymbol pointer ? pointer.PointedAtType : field.Type,
                    visiting));

        visiting.Remove(type);
        return blittable;
    }

    private static bool HasBlittableMetadataLayout(INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.Length > 0)
        {
            return true;
        }

        // StructLayout and MarshalAs are encoded in metadata tables, not returned
        // by GetAttributes() for a referenced type. Do not infer their absence.
        ModuleMetadata? metadata = type.ContainingModule?.GetMetadata();
        if (metadata == null)
        {
            return false;
        }

        try
        {
            MetadataReader reader = metadata.GetMetadataReader();
            TypeDefinitionHandle match = default;
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                if (!MatchesMetadataType(reader, handle, type.OriginalDefinition))
                {
                    continue;
                }

                if (!match.IsNil)
                {
                    return false;
                }
                match = handle;
            }

            if (match.IsNil)
            {
                return false;
            }

            TypeDefinition definition = reader.GetTypeDefinition(match);
            TypeAttributes layout = definition.Attributes & TypeAttributes.LayoutMask;
            if (layout != TypeAttributes.SequentialLayout && layout != TypeAttributes.ExplicitLayout)
            {
                return false;
            }

            int instanceFieldCount = 0;
            foreach (FieldDefinitionHandle handle in definition.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(handle);
                if ((field.Attributes & FieldAttributes.Static) != 0)
                {
                    continue;
                }

                instanceFieldCount++;
                if (!field.GetMarshallingDescriptor().IsNil)
                {
                    return false;
                }
            }

            return instanceFieldCount == type.GetMembers().OfType<IFieldSymbol>().Count(field => !field.IsStatic && !field.IsConst);
        }
        catch (System.BadImageFormatException)
        {
            return false;
        }
        catch (System.InvalidOperationException)
        {
            return false;
        }
    }

    private static bool MatchesMetadataType(MetadataReader reader, TypeDefinitionHandle handle, INamedTypeSymbol type)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        if (!reader.StringComparer.Equals(definition.Name, type.MetadataName))
        {
            return false;
        }

        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (type.ContainingType != null)
        {
            return !declaringType.IsNil && MatchesMetadataType(reader, declaringType, type.ContainingType);
        }

        List<string> namespaces = [];
        for (INamespaceSymbol? ns = type.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
        {
            namespaces.Add(ns.MetadataName);
        }
        namespaces.Reverse();
        return declaringType.IsNil && reader.StringComparer.Equals(definition.Namespace, string.Join(".", namespaces));
    }

}
