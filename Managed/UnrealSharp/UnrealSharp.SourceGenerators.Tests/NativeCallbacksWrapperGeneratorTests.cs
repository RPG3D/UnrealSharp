using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace UnrealSharp.SourceGenerators.Tests;

public class NativeCallbacksWrapperGeneratorTests
{
    [Fact]
    public void WrapperCallsBlittableSignatureAndPreservesManagedArguments()
    {
        const string Source = """
            using System;
            using System.Runtime.InteropServices;

            namespace UnrealSharp.Binds
            {
                public class NativeCallbacksAttribute : Attribute;

                public static unsafe class NativeBinds
                {
                    public static IntPtr TryGetBoundFunction(string type, string name, int size)
                    {
                        if (size != 3 * IntPtr.Size + sizeof(int))
                            throw new InvalidOperationException();
                        return (IntPtr)(delegate* unmanaged<byte*, int*, char**, int>)&Callback;
                    }

                    [UnmanagedCallersOnly]
                    private static int Callback(byte* text, int* value, char** output)
                    {
                        *value += 3;
                        *output = null;
                        return text == null ? -1 : Marshal.PtrToStringUTF8((IntPtr)text) == "太极" ? 1 : 0;
                    }
                }
            }

            namespace Test
            {
                using UnrealSharp.Binds;

                [NativeCallbacks]
                public static unsafe partial class Bind_Test
                {
                    private static delegate* unmanaged<string?, ref int, out char*, int> Invoke;

                    public static bool Run()
                    {
                        int value = 4;
                        return CallInvoke("太极", ref value, out char* output) == 1 && value == 7 && output == null
                            && CallInvoke(null, ref value, out output) == -1 && value == 10 && output == null;
                    }
                }
            }
            """;

        (string wrapper, Compilation generated) = Generate(Source);
        Assert.Contains("return ((delegate* unmanaged<byte*, int*, char**, int>)(void*)Invoke)", wrapper);
        AssertRun(generated);
    }

    [Fact]
    public void WrapperPreservesCustomStructMarshallingAndNativeWriteBack()
    {
        const string Source = """
            using System;
            using System.Runtime.InteropServices;

            namespace UnrealSharp.Binds
            {
                public class NativeCallbacksAttribute : Attribute;

                public static unsafe class NativeBinds
                {
                    [StructLayout(LayoutKind.Sequential)]
                    private struct NativeValue
                    {
                        public int Flag;
                        public byte Tail;
                    }

                    public static IntPtr TryGetBoundFunction(string type, string name, int size)
                    {
                        if (size != IntPtr.Size + sizeof(int))
                            throw new InvalidOperationException();
                        return (IntPtr)(delegate* unmanaged<NativeValue*, int>)&Callback;
                    }

                    [UnmanagedCallersOnly]
                    private static int Callback(NativeValue* value)
                    {
                        if (value->Flag != 1 || value->Tail != 73)
                            return 0;
                        value->Flag = 0;
                        value->Tail = 96;
                        return 1;
                    }
                }
            }

            namespace Test
            {
                using UnrealSharp.Binds;

                [NativeCallbacks]
                public static unsafe partial class Bind_Test
                {
                    [StructLayout(LayoutKind.Sequential)]
                    public struct MarshalledValue
                    {
                        [MarshalAs(UnmanagedType.Bool)] public bool Flag;
                        public byte Tail;
                    }

                    private static delegate* unmanaged<ref MarshalledValue, int> Invoke;

                    public static bool Run()
                    {
                        // Extra elements keep the native read in bounds even if the
                        // generator accidentally passes the two-byte managed layout.
                        MarshalledValue[] values = new MarshalledValue[8];
                        values[0] = new MarshalledValue { Flag = true, Tail = 73 };
                        values[1] = new MarshalledValue { Flag = true, Tail = 17 };
                        return CallInvoke(ref values[0]) == 1 && !values[0].Flag && values[0].Tail == 96
                            && values[1].Flag && values[1].Tail == 17;
                    }
                }
            }
            """;

        (string wrapper, Compilation generated) = Generate(Source);
        AssertRun(generated);
        Assert.Contains("return Invoke(ref a);", wrapper);
    }

    [Theory]
    [InlineData("bool", false)]
    [InlineData("char", false)]
    [InlineData("decimal", false)]
    [InlineData("AutoLayout", false)]
    [InlineData("NestedBool", false)]
    [InlineData("FixedChar", false)]
    [InlineData("int", true)]
    [InlineData("NativeBool", true)]
    [InlineData("Blittable<int>", true)]
    [InlineData("FixedByte", true)]
    [InlineData("int*", true)]
    [InlineData("delegate* unmanaged<int, int>", true)]
    public void WrapperPinsOnlyBlittableReferences(string parameterType, bool pinned)
    {
        string source = $$"""
            global using System;
            using System.Runtime.InteropServices;

            namespace UnrealSharp.Binds
            {
                public class NativeCallbacksAttribute : Attribute;
                public static class NativeBinds
                {
                    public static IntPtr TryGetBoundFunction(string type, string name, int size) => IntPtr.Zero;
                }
            }

            namespace Test
            {
                using UnrealSharp.Binds;

                public enum NativeBool : byte { False, True }
                [StructLayout(LayoutKind.Auto)] public struct AutoLayout { public int Value; }
                public struct WithBool { public bool Value; }
                public struct NestedBool { public WithBool Value; }
                public struct Blittable<T> where T : unmanaged { public T Value; public NativeBool Flag; }
                public unsafe struct FixedChar { public fixed char Values[2]; }
                public unsafe struct FixedByte { public fixed byte Values[2]; }

                [NativeCallbacks]
                public static unsafe partial class Bind_Test
                {
                    private static delegate* unmanaged<ref {{parameterType}}, void> Invoke;
                }
            }
            """;

        (string wrapper, Compilation generated) = Generate(source);
        Assert.Equal(pinned, wrapper.Contains("fixed (", StringComparison.Ordinal));
        if (!pinned)
            Assert.Contains("Invoke(ref a);", wrapper);
        Assert.DoesNotContain(generated.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("External.AutoLayout", false)]
    [InlineData("External.MarshalledInt", false)]
    [InlineData("External.Blittable", true)]
    [InlineData("External.Outer<int>.SameName", true)]
    [InlineData("External.Other<int>.SameName", false)]
    [InlineData("@event.@struct", true)]
    [InlineData("Alias", false)]
    public void WrapperChecksReferencedTypeMetadata(string parameterType, bool pinned)
    {
        const string ExternalSource = """
            using System.Runtime.InteropServices;

            namespace External
            {
                [StructLayout(LayoutKind.Auto)] public struct AutoLayout { public int Value; }
                public struct MarshalledInt { [MarshalAs(UnmanagedType.U1)] private int value; public byte Tail; }
                public struct Blittable { private int value; public byte Tail; }
                public class Outer<T> { public struct SameName { public int Value; } }
                public class Other<T> { [StructLayout(LayoutKind.Auto)] public struct SameName { public int Value; } }
            }
            namespace @event { public struct @struct { public int Value; } }
            """;

        string[] platformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        CSharpCompilation external = CSharpCompilation.Create(
            "External_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(ExternalSource)],
            platformAssemblies.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream image = new();
        var emit = external.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        MetadataReference reference = MetadataReference.CreateFromImage(image.ToArray());

        string source = $$"""
            global using System;
            using Alias = External.AutoLayout;

            namespace UnrealSharp.Binds
            {
                public class NativeCallbacksAttribute : Attribute;
                public static class NativeBinds
                {
                    public static IntPtr TryGetBoundFunction(string type, string name, int size) => IntPtr.Zero;
                }
            }
            namespace Test
            {
                using UnrealSharp.Binds;
                [NativeCallbacks]
                public static unsafe partial class Bind_Test
                {
                    private static delegate* unmanaged<ref {{parameterType}}, void> Invoke;
                }
            }
            """;

        (string wrapper, Compilation generated) = Generate(source, reference);
        Assert.Equal(MetadataImportOptions.Public, generated.Options.MetadataImportOptions);
        Assert.Equal(pinned, wrapper.Contains("fixed (", StringComparison.Ordinal));
        if (!pinned)
            Assert.Contains("Invoke(ref a);", wrapper);
        Assert.DoesNotContain(generated.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static (string Wrapper, Compilation Generated) Generate(string source, params MetadataReference[] additionalReferences)
    {
        string[] platformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        CSharpCompilation compilation = CSharpCompilation.Create(
            nameof(NativeCallbacksWrapperGeneratorTests) + "_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            platformAssemblies.Select(path => MetadataReference.CreateFromFile(path)).Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NativeCallbacksWrapperGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out _);
        return (driver.GetRunResult().GeneratedTrees.Single().ToString(), generated);
    }

    private static void AssertRun(Compilation generated)
    {
        using MemoryStream image = new();
        var emit = generated.Emit(image);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var fixture = System.Reflection.Assembly.Load(image.ToArray());
        Assert.True((bool)fixture.GetType("Test.Bind_Test")!.GetMethod("Run")!.Invoke(null, null)!);
    }
}
