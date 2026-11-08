using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnrealSharp.Binds;

namespace UnrealSharp.Core;

public static unsafe class ManagedRuntimeStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Snapshot
    {
        public long EstimatedHeapBytes;
        public long TotalAllocatedBytes;
        public int Gen0Collections;
        public int Gen1Collections;
        public int Gen2Collections;
    }

    // Optional binder keeps the host callback table compatible with older binaries.
    public static void Register()
    {
        delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<Snapshot*, void>, void> register;
        try
        {
            register = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<Snapshot*, void>, void>)
                NativeBinds.TryGetBoundFunction("Bind_FManagedRuntimeStats", "RegisterSampler", sizeof(nint));
        }
        catch (Exception)
        {
            return; // A runtime without the optional stats binder still starts normally.
        }

        register(&Sample);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Sample(Snapshot* result)
    {
        *result = new Snapshot { EstimatedHeapBytes = -1, TotalAllocatedBytes = -1 };
        try
        {
            result->EstimatedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            result->Gen0Collections = GC.CollectionCount(0);
            if (GC.MaxGeneration >= 1) result->Gen1Collections = GC.CollectionCount(1);
            if (GC.MaxGeneration >= 2) result->Gen2Collections = GC.CollectionCount(2);
            result->TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        }
        catch (Exception)
        {
            // A runtime may omit an optional GC counter. Never unwind into native code.
        }
    }
}
