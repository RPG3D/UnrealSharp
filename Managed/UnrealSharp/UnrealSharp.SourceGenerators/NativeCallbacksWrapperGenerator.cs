using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnrealSharp.SourceGenerator.Utilities;

namespace UnrealSharp.SourceGenerators;

public struct ParameterInfo
{
    public DelegateParameterInfo Parameter { get; set; }
}

[Generator]
public class NativeCallbacksWrapperGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedStringShape = new(
        id: "US0015",
        title: "String in unsupported native callback position",
        messageFormat: "'{0}.{1}' declares a string as a return value or a ref/out parameter; the ABI twin cannot express that shape and interpreted calli will reject the marshalled signature. Pass char*/IntPtr with an explicit length instead.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
                static (syntaxNode, _) => syntaxNode is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                static (syntaxContext, _) => GetClassInfoOrNull(syntaxContext));

        var classAndCompilation = classDeclarations.Combine(context.CompilationProvider);

        context.RegisterSourceOutput(classAndCompilation, (spc, pair) =>
        {
            var maybeClassInfo = pair.Left; // ClassInfo?
            var compilation = pair.Right;
            if (!maybeClassInfo.HasValue)
            {
                return;
            }
            GenerateForClass(spc, compilation, maybeClassInfo.Value);
        });
    }

    private static void GenerateForClass(SourceProductionContext context, Compilation compilation, ClassInfo classInfo)
    {
        var model = compilation.GetSemanticModel(classInfo.ClassDeclaration.SyntaxTree);

        // Only by-value string parameters are rewritten. A string return or a ref/out
        // string has no ABI twin here; flag it at build time instead of failing at
        // runtime on iOS with InvalidProgramException.
        foreach (DelegateInfo delegateInfo in classInfo.Delegates)
        {
            if (IsStringType(delegateInfo.ReturnValue.Type, model))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedStringShape, classInfo.ClassDeclaration.GetLocation(), classInfo.Name, delegateInfo.Name));
            }

            foreach (DelegateParameterInfo parameter in delegateInfo.Parameters)
            {
                if ((parameter.IsOutParameter || parameter.IsRefParameter) && IsStringType(parameter.Type, model))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedStringShape, classInfo.ClassDeclaration.GetLocation(), classInfo.Name, delegateInfo.Name));
                }
            }
        }

        var sourceBuilder = new StringBuilder();

        // Managed strings cannot cross a `calli` boundary in interpreted code: CoreCLR's
        // interpreter rejects calli sites that need P/Invoke marshalling ("PInvoke
        // marshalling for calli is not supported in interpreted code"), which is what
        // iOS runs (no JIT). The native side already takes `const char*` UTF-8, so the
        // wrapper reinterprets the bound function pointer as its `char*` ABI twin and
        // marshals the arguments by hand. The public API keeps `string`.

        HashSet<string> namespaces = [];
        foreach (DelegateInfo delegateInfo in classInfo.Delegates)
        {
            foreach (var parameter in delegateInfo.ParametersAndReturnValue)
            {
                var typeInfo = model.GetTypeInfo(parameter.Type);
                var typeSymbol = typeInfo.Type;

                if (typeSymbol == null || typeSymbol.ContainingNamespace == null)
                {
                    continue;
                }

                if (typeSymbol is INamedTypeSymbol nts && nts.IsGenericType)
                {
                    namespaces.UnionWith(nts.TypeArguments.Where(t => t.ContainingNamespace != null).Select(t => t.ContainingNamespace!.ToDisplayString()));
                }

                namespaces.Add(typeSymbol.ContainingNamespace.ToDisplayString());
            }
        }

        if (classInfo.NullableAwareable)
        {
            sourceBuilder.AppendLine("#nullable enable");
        }
        else
        {
            sourceBuilder.AppendLine("#nullable disable");
        }
        
        sourceBuilder.AppendLine();

        foreach (string? ns in namespaces)
        {
            if (string.IsNullOrWhiteSpace(ns)) continue;
            sourceBuilder.AppendLine($"using {ns};");
        }

        sourceBuilder.AppendLine();
        sourceBuilder.AppendLine($"namespace {classInfo.Namespace}");
        sourceBuilder.AppendLine("{");
        sourceBuilder.AppendLine($"    public static unsafe partial class {classInfo.Name}");
        sourceBuilder.AppendLine("    {");

        sourceBuilder.AppendLine("        static " + classInfo.Name + "()");
        sourceBuilder.AppendLine("        {");

        foreach (DelegateInfo delegateInfo in classInfo.Delegates)
        {
            string delegateName = delegateInfo.Name;

            string totalSizeDelegateName = delegateName + "TotalSize";
            if (!delegateInfo.HasReturnValue && delegateInfo.Parameters.Count == 0)
            {
                sourceBuilder.AppendLine($"             int {totalSizeDelegateName} = 0;");
            }
            else
            {
                sourceBuilder.Append($"             int {totalSizeDelegateName} = ");

                void AppendSizeOf(DelegateParameterInfo param)
                {
                    string typeFullName = param.Type.GetAnnotatedTypeName(model) ?? param.Type.ToString();

                    if (param.IsOutParameter || param.IsRefParameter)
                    {
                        sourceBuilder.Append($"IntPtr.Size");
                    }
                    else
                    {
                        sourceBuilder.Append($"sizeof({typeFullName})");
                    }
                }

                List<DelegateParameterInfo> parameters = delegateInfo.ParametersAndReturnValue;

                for (int i = 0; i < parameters.Count; i++)
                {
                    AppendSizeOf(parameters[i]);

                    if (i != parameters.Count - 1)
                    {
                        sourceBuilder.Append(" + ");
                    }
                }

                sourceBuilder.AppendLine(";");
            }

            string funcPtrName = delegateName + "FuncPtr";
            sourceBuilder.AppendLine($"             IntPtr {funcPtrName} = UnrealSharp.Binds.NativeBinds.TryGetBoundFunction(\"{classInfo.Name}\", \"{delegateInfo.Name}\", {totalSizeDelegateName});");
            sourceBuilder.Append($"             {delegateName} = (delegate* unmanaged<");
            sourceBuilder.Append(string.Join(", ", delegateInfo.Parameters.Select(p =>
            {
                string prefix = p.IsOutParameter ? "out " : p.IsRefParameter ? "ref " : string.Empty;
                return prefix + (p.Type.GetAnnotatedTypeName(model) ?? p.Type.ToString());
            })));

            if (delegateInfo.Parameters.Count > 0)
            {
                sourceBuilder.Append(", ");
            }
            
            sourceBuilder.Append(delegateInfo.ReturnValue.Type.GetAnnotatedTypeName(model) ?? delegateInfo.ReturnValue.Type.ToString());

            sourceBuilder.Append($">){funcPtrName};");
            sourceBuilder.AppendLine();
        }

        sourceBuilder.AppendLine("        }");

        foreach (DelegateInfo delegateInfo in classInfo.Delegates)
        {
            string returnTypeFullName = delegateInfo.ReturnValue.Type.GetAnnotatedTypeName(model) ?? delegateInfo.ReturnValue.Type.ToString();
            sourceBuilder.AppendLine($"        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            sourceBuilder.Append($"        public static {returnTypeFullName} Call{delegateInfo.Name}(");

            bool firstParameter = true;
            foreach (DelegateParameterInfo parameter in delegateInfo.Parameters)
            {
                if (!firstParameter)
                {
                    sourceBuilder.Append(", ");
                }

                firstParameter = false;

                if (parameter.IsOutParameter)
                {
                    sourceBuilder.Append("out ");
                }

                if (parameter.IsRefParameter)
                {
                    sourceBuilder.Append("ref ");
                }

                string typeFullName = parameter.Type.GetAnnotatedTypeName(model) ?? parameter.Type.ToString();
                sourceBuilder.Append($"{typeFullName} {parameter.Name}");
            }

            sourceBuilder.AppendLine(")");
            sourceBuilder.AppendLine("        {");

            string delegateName = delegateInfo.Name;

            // Managed forms that cannot cross a calli boundary:
            //   string  -> needs P/Invoke marshalling
            //   ref/out -> ELEMENT_TYPE_BYREF makes PInvoke::MarshalingRequired return TRUE
            //              (dllimport.cpp has no BYREF case) and the CoreCLR interpreter
            //              rejects such calli sites with InvalidProgramException.
            // Both are replaced by their ABI-equivalent (char* / T*) in the calli twin.
            List<DelegateParameterInfo> stringParameters = delegateInfo.Parameters
                .Where(p => IsStringParameter(p, model))
                .ToList();
            List<DelegateParameterInfo> byRefParameters = delegateInfo.Parameters
                .Where(p => p.IsOutParameter || p.IsRefParameter)
                .ToList();
            List<DelegateParameterInfo> boolParameters = delegateInfo.Parameters
                .Where(p => IsBooleanParameter(p, model))
                .ToList();
            bool returnsBool = IsBooleanParameter(delegateInfo.ReturnValue, model);

            bool needsAbiTwin = stringParameters.Count > 0
                || byRefParameters.Count > 0
                || boolParameters.Count > 0
                || returnsBool;

            string callTarget = delegateName;
            if (needsAbiTwin)
            {
                callTarget = delegateName + "Abi";
                string abiSignature = BuildFunctionPointerSignature(delegateInfo, model, useAbiTypes: true);
                sourceBuilder.AppendLine($"            {abiSignature} {callTarget} = ({abiSignature}){delegateName};");
            }

            // Strings are encoded into a NUL-terminated UTF-8 buffer. The terminator is
            // mandatory: native consumers read these as C strings (FName(const ANSICHAR*),
            // FString via UTF8_TO_TCHAR, UE_LOGFMT) and Encoding.UTF8.GetBytes does not
            // append one, so without it they would run into adjacent heap memory. Short
            // strings use the stack, longer ones a pinned heap array.
            const int StackAllocLimit = 256;
            foreach (DelegateParameterInfo parameter in stringParameters)
            {
                sourceBuilder.AppendLine($"            int {parameter.Name}ByteCount = {parameter.Name} != null ? System.Text.Encoding.UTF8.GetByteCount({parameter.Name}) : 0;");
                sourceBuilder.AppendLine($"            var {parameter.Name}Heap = {parameter.Name}ByteCount > {StackAllocLimit} ? new byte[{parameter.Name}ByteCount + 1] : null;");
            }

            // Out parameters are not readable: route them through a temporary and assign
            // back after the call. The temporary always exists up front; whether its
            // address needs pinning depends on the type (see below).
            foreach (DelegateParameterInfo parameter in byRefParameters)
            {
                if (parameter.IsOutParameter)
                {
                    string typeName = parameter.Type.GetAnnotatedTypeName(model) ?? parameter.Type.ToString();
                    sourceBuilder.AppendLine($"            {typeName} {parameter.Name}Tmp = default;");
                }
            }

            // Take the address of every ref/out parameter with Unsafe.AsPointer: a plain
            // `&value` is rejected for ref parameters (CS0213 "already fixed") or requires
            // `fixed` (CS0212) depending on the type's managed-ness, while AsPointer is
            // exactly the byref -> pointer conversion we need and compiles for all of them.
            foreach (DelegateParameterInfo parameter in byRefParameters)
            {
                string typeName = parameter.Type.GetAnnotatedTypeName(model) ?? parameter.Type.ToString();
                string source = parameter.IsOutParameter ? $"{parameter.Name}Tmp" : parameter.Name;

                // Unsafe.AsPointer<T> cannot be instantiated with a pointer type (CS0306),
                // but a pointer variable is unmanaged, so its address can be taken directly.
                if (parameter.Type is PointerTypeSyntax)
                {
                    sourceBuilder.AppendLine($"            {typeName}* {parameter.Name}Ptr = &{source};");
                }
                else
                {
                    sourceBuilder.AppendLine($"            {typeName}* {parameter.Name}Ptr = ({typeName}*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref {source});");
                }
            }

            for (int i = 0; i < stringParameters.Count; i++)
            {
                DelegateParameterInfo parameter = stringParameters[i];
                string pad = new string(' ', 12 + 4 * i);
                string inner = new string(' ', 16 + 4 * i);

                sourceBuilder.AppendLine($"{pad}fixed (byte* {parameter.Name}HeapPtr = {parameter.Name}Heap)");
                sourceBuilder.AppendLine($"{pad}{{");
                sourceBuilder.AppendLine($"{inner}byte* {parameter.Name}Ptr = {parameter.Name}HeapPtr;");
                sourceBuilder.AppendLine($"{inner}if ({parameter.Name} != null && {parameter.Name}Heap == null)");
                sourceBuilder.AppendLine($"{inner}{{");
                sourceBuilder.AppendLine($"{inner}    byte* {parameter.Name}Stack = stackalloc byte[{parameter.Name}ByteCount + 1];");
                sourceBuilder.AppendLine($"{inner}    {parameter.Name}Ptr = {parameter.Name}Stack;");
                sourceBuilder.AppendLine($"{inner}}}");
                sourceBuilder.AppendLine($"{inner}if ({parameter.Name}ByteCount > 0)");
                sourceBuilder.AppendLine($"{inner}{{");
                sourceBuilder.AppendLine($"{inner}    System.Text.Encoding.UTF8.GetBytes(System.MemoryExtensions.AsSpan({parameter.Name}), new System.Span<byte>({parameter.Name}Ptr, {parameter.Name}ByteCount));");
                sourceBuilder.AppendLine($"{inner}}}");
                sourceBuilder.AppendLine($"{inner}if ({parameter.Name} != null)");
                sourceBuilder.AppendLine($"{inner}{{");
                sourceBuilder.AppendLine($"{inner}    {parameter.Name}Ptr[{parameter.Name}ByteCount] = 0;");
                sourceBuilder.AppendLine($"{inner}}}");
            }

            // Each string parameter opened a block above, so the call sits one level
            // deeper per string.
            string callIndent = new string(' ', 12 + 4 * stringParameters.Count);

            bool returnsValue = returnsBool || delegateInfo.ReturnValue.Type.ToString() != "void";
            sourceBuilder.Append($"{callIndent}{(returnsValue ? "return " : string.Empty)}{callTarget}(");

            sourceBuilder.Append(string.Join(", ", delegateInfo.Parameters.Select(p =>
            {
                if (IsStringParameter(p, model))
                {
                    return $"(char*){p.Name}Ptr";
                }

                if (IsBooleanParameter(p, model))
                {
                    return $"(byte)({p.Name} ? 1 : 0)";
                }

                if (p.IsOutParameter || p.IsRefParameter)
                {
                    return $"{p.Name}Ptr";
                }

                return p.Name;
            })));

            sourceBuilder.AppendLine(returnsBool ? ") != 0;" : ");");

            for (int i = stringParameters.Count - 1; i >= 0; i--)
            {
                sourceBuilder.AppendLine(new string(' ', 12 + 4 * i) + "}");
            }

            foreach (DelegateParameterInfo parameter in byRefParameters)
            {
                if (parameter.IsOutParameter)
                {
                    sourceBuilder.AppendLine($"            {parameter.Name} = {parameter.Name}Tmp;");
                }
            }

            sourceBuilder.AppendLine("        }");
        }

        // End class definition
        sourceBuilder.AppendLine("    }");
        sourceBuilder.AppendLine("}");

        context.AddSource($"{classInfo.Name}.generated.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
    }

    private static bool IsStringParameter(DelegateParameterInfo parameter, SemanticModel semanticModel)
    {
        return !parameter.IsOutParameter
            && !parameter.IsRefParameter
            && IsStringType(parameter.Type, semanticModel);
    }

    private static bool IsStringType(TypeSyntax type, SemanticModel semanticModel)
    {
        return semanticModel.GetTypeInfo(type).Type?.SpecialType == SpecialType.System_String;
    }

    /// <summary>
    /// True for a by-value bool. ELEMENT_TYPE_BOOLEAN counts as "requires marshalling"
    /// whenever runtime marshalling is enabled, which makes the interpreted calli site
    /// illegal; the ABI twin uses byte instead (the native side passes a 1-byte bool).
    /// </summary>
    private static bool IsBooleanParameter(DelegateParameterInfo parameter, SemanticModel semanticModel)
    {
        return !parameter.IsOutParameter
            && !parameter.IsRefParameter
            && semanticModel.GetTypeInfo(parameter.Type).Type?.SpecialType == SpecialType.System_Boolean;
    }

    /// <summary>
    /// Function pointer signature for a bound callback. With <paramref name="useAbiTypes"/>
    /// the signature is the ABI twin the wrapper calls through: managed `string` becomes
    /// `char*` and `ref`/`out T` becomes `T*` (all ABI-equivalent, but free of the
    /// marshalling flag that makes interpreted calli illegal).
    /// </summary>
    private static string BuildFunctionPointerSignature(DelegateInfo delegateInfo, SemanticModel model, bool useAbiTypes)
    {
        var parameterTypes = delegateInfo.Parameters.Select(parameter =>
        {
            string typeName = parameter.Type.GetAnnotatedTypeName(model) ?? parameter.Type.ToString();

            if (useAbiTypes)
            {
                if (IsStringParameter(parameter, model))
                {
                    return "char*";
                }

                if (IsBooleanParameter(parameter, model))
                {
                    return "byte";
                }

                if (parameter.IsOutParameter || parameter.IsRefParameter)
                {
                    return typeName + "*";
                }
            }

            string prefix = parameter.IsOutParameter ? "out " : parameter.IsRefParameter ? "ref " : string.Empty;
            return prefix + typeName;
        }).ToList();

        string returnType = delegateInfo.ReturnValue.Type.GetAnnotatedTypeName(model) ?? delegateInfo.ReturnValue.Type.ToString();
        if (useAbiTypes && IsBooleanParameter(delegateInfo.ReturnValue, model))
        {
            returnType = "byte";
        }

        string parameters = parameterTypes.Count > 0 ? string.Join(", ", parameterTypes) + ", " : string.Empty;
        return $"delegate* unmanaged<{parameters}{returnType}>";
    }

    private static ClassInfo? GetClassInfoOrNull(GeneratorSyntaxContext context)
    {
        if (context.Node is not ClassDeclarationSyntax classDeclaration)
        {
            return null;
        }

        // Check attribute (support both NativeCallbacks and NativeCallbacksAttribute)
        bool hasNativeCallbacksAttribute = classDeclaration.AttributeLists
            .SelectMany(a => a.Attributes)
            .Any(a => a.Name.ToString() is "NativeCallbacks" or "NativeCallbacksAttribute");

        if (!hasNativeCallbacksAttribute)
        {
            return null;
        }

        string namespaceName = classDeclaration.GetFullNamespace();

        if (string.IsNullOrEmpty(namespaceName))
        {
            return null;
        }

        var classInfo = new ClassInfo
        {
            ClassDeclaration = classDeclaration,
            Name = classDeclaration.Identifier.ValueText,
            Namespace = namespaceName,
            Delegates = new List<DelegateInfo>(),
            NullableAwareable = context.SemanticModel.GetNullableContext(context.Node.Span.Start).HasFlag(NullableContext.AnnotationsEnabled)
        };

        foreach (MemberDeclarationSyntax member in classDeclaration.Members)
        {
            if (member is not FieldDeclarationSyntax fieldDeclaration ||
                fieldDeclaration.Declaration.Type is not FunctionPointerTypeSyntax functionPointerTypeSyntax)
            {
                continue;
            }

            var delegateInfo = new DelegateInfo
            {
                Name = fieldDeclaration.Declaration.Variables.First().Identifier.ValueText,
                Parameters = new List<DelegateParameterInfo>()
            };

            char paramName = 'a';

            for (int i = 0; i < functionPointerTypeSyntax.ParameterList.Parameters.Count; i++)
            {
                FunctionPointerParameterSyntax param = functionPointerTypeSyntax.ParameterList.Parameters[i];

                DelegateParameterInfo parameter = new DelegateParameterInfo
                {
                    Name = paramName.ToString(),
                    IsOutParameter = param.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OutKeyword)),
                    IsRefParameter = param.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword)),
                    Type = param.Type,
                };

                bool isReturnParameter = i == functionPointerTypeSyntax.ParameterList.Parameters.Count - 1;
                if (isReturnParameter)
                {
                    delegateInfo.ReturnValue = parameter;
                }
                else
                {
                    delegateInfo.Parameters.Add(parameter);
                }

                paramName++;
            }

            classInfo.Delegates.Add(delegateInfo);
        }

        return classInfo;
    }
}

internal struct ClassInfo
{
    public ClassDeclarationSyntax ClassDeclaration;
    public string Name;
    public string Namespace;
    public List<DelegateInfo> Delegates;
    public bool NullableAwareable;
}

internal struct DelegateInfo
{
    public string Name;
    public List<DelegateParameterInfo> Parameters;
    public List<DelegateParameterInfo> ParametersAndReturnValue
    {
        get
        {
            List<DelegateParameterInfo> allParameters = new List<DelegateParameterInfo>(Parameters);

            if (ReturnValue.Type.ToString() != "void")
            {
                allParameters.Add(ReturnValue);
            }

            return allParameters;
        }
    }

    public bool HasReturnValue => ReturnValue.Type.ToString() != "void";
    public DelegateParameterInfo ReturnValue;
}

public struct DelegateParameterInfo
{
    public string Name;
    public TypeSyntax Type;
    public bool IsOutParameter;
    public bool IsRefParameter;
}