#include "CSRuntimeStats.h"
#include "CSBindsRegistry.h"
#include "CSManager.h"
#include "Containers/Ticker.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformTime.h"
#include "Stats/Stats.h"
#include "UnrealSharpCore.h"

#if UNREALSHARP_MONO && !UE_BUILD_SHIPPING && PLATFORM_WINDOWS
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include <mono/metadata/class.h>
#include <mono/metadata/mono-gc.h>
#include <mono/metadata/profiler.h>
#endif

namespace
{
	// Keep in sync with UnrealSharp.Core.ManagedRuntimeStats.Snapshot.
	struct FManagedRuntimeSnapshot
	{
		int64 EstimatedHeapBytes = -1;
		int64 TotalAllocatedBytes = -1;
		int32 Gen0Collections = 0;
		int32 Gen1Collections = 0;
		int32 Gen2Collections = 0;
	};

	using FManagedSampler = void (*)(FManagedRuntimeSnapshot*);
	FManagedSampler ManagedSampler = nullptr;
	FTSTicker::FDelegateHandle TickHandle;
}

#if UNREALSHARP_MONO && !UE_BUILD_SHIPPING && PLATFORM_WINDOWS
namespace
{
	constexpr uint32 HeapBucketCount = 16384;
	struct FHeapTypeBucket
	{
		MonoClass* Class;
		uint64 Count;
		uint64 Bytes;
		char Namespace[160];
		char Name[160];
	};

	struct FHeapCapture
	{
		TArray<FHeapTypeBucket> Buckets;
		uint64 ObjectCount = 0;
		uint64 Bytes = 0;
		uint64 UnbucketedObjects = 0;
		uint64 UnbucketedBytes = 0;
		uint32 TrackedTypes = 0;
		uint32 Generation = 0;
		bool bWalked = false;
		bool bSupported = false;
	};

	FHeapCapture* ActiveHeapCapture = nullptr; // game thread only
	MonoProfilerHandle HeapProfiler = nullptr;
	uint64 LastHeapObjectCount = 0;
	uint64 LastHeapObjectBytes = 0;

	int CountHeapObject(MonoObject*, MonoClass* Class, uintptr_t Size, uintptr_t, MonoObject**, uintptr_t*, void* Data)
	{
		// Objects with many references can be reported again with size zero.
		if (Size == 0) return 0;

		FHeapCapture& Capture = *static_cast<FHeapCapture*>(Data);
		++Capture.ObjectCount;
		Capture.Bytes += Size;
		uint32 Index = PointerHash(Class) & (HeapBucketCount - 1);
		for (uint32 Probe = 0; Probe < HeapBucketCount; ++Probe, Index = (Index + 1) & (HeapBucketCount - 1))
		{
			FHeapTypeBucket& Bucket = Capture.Buckets[Index];
			if (!Bucket.Class)
			{
				// Preserve bounded lookup cost even in a very large type universe.
				if (Capture.TrackedTypes >= HeapBucketCount * 3 / 4) break;
				Bucket.Class = Class;
				++Capture.TrackedTypes;
			}
			if (Bucket.Class == Class)
			{
				++Bucket.Count;
				Bucket.Bytes += Size;
				return 0;
			}
		}
		++Capture.UnbucketedObjects;
		Capture.UnbucketedBytes += Size;
		return 0;
	}

	void CaptureMonoHeapOnGc(MonoProfiler*, MonoProfilerGCEvent Event, uint32 Generation, mono_bool)
	{
		if (Event != MONO_GC_EVENT_PRE_START_WORLD || !IsInGameThread() || !ActiveHeapCapture ||
			ActiveHeapCapture->bWalked || Generation != ActiveHeapCapture->Generation)
		{
			return;
		}

		FHeapCapture& Capture = *ActiveHeapCapture;
		Capture.bWalked = true;
		Capture.bSupported = mono_gc_walk_heap(0, &CountHeapObject, &Capture) == 0;
		if (!Capture.bSupported) return;

		// Copy metadata while classes are still valid. No allocations or logging while Mono is stopped.
		for (FHeapTypeBucket& Bucket : Capture.Buckets)
		{
			if (!Bucket.Class) continue;
			const char* Namespace = mono_class_get_namespace(Bucket.Class);
			const char* Name = mono_class_get_name(Bucket.Class);
			FCStringAnsi::Strncpy(Bucket.Namespace, Namespace ? Namespace : "", UE_ARRAY_COUNT(Bucket.Namespace));
			FCStringAnsi::Strncpy(Bucket.Name, Name ? Name : "", UE_ARRAY_COUNT(Bucket.Name));
		}
	}

	void DumpManagedHeap()
	{
		if (!TickHandle.IsValid() || !ManagedSampler || !IsInGameThread())
		{
			UE_LOG(LogUnrealSharp, Warning, TEXT("Mono heap capture is not ready."));
			return;
		}

		if (!HeapProfiler) HeapProfiler = mono_profiler_create(nullptr);
		if (!HeapProfiler) return;

		FHeapCapture Capture;
		Capture.Buckets.SetNumZeroed(HeapBucketCount);
		Capture.Generation = mono_gc_max_generation();
		const double Start = FPlatformTime::Seconds();
		ActiveHeapCapture = &Capture;
		mono_profiler_set_gc_event_callback(HeapProfiler, &CaptureMonoHeapOnGc);
		mono_gc_collect(Capture.Generation);
		mono_profiler_set_gc_event_callback(HeapProfiler, nullptr);
		ActiveHeapCapture = nullptr;
		const double CaptureMs = (FPlatformTime::Seconds() - Start) * 1000.0;

		if (!Capture.bWalked || !Capture.bSupported)
		{
			UE_LOG(LogUnrealSharp, Warning, TEXT("Mono did not return a full heap capture."));
			return;
		}

		LastHeapObjectCount = Capture.ObjectCount;
		LastHeapObjectBytes = Capture.Bytes;

		Capture.Buckets.Sort([](const FHeapTypeBucket& A, const FHeapTypeBucket& B) { return A.Bytes > B.Bytes; });
		FString Csv = TEXT("namespace,type,runtimeClass,objects,bytes\n");
		for (const FHeapTypeBucket& Bucket : Capture.Buckets)
		{
			if (!Bucket.Class) continue;
			const FString Namespace = FString(UTF8_TO_TCHAR(Bucket.Namespace)).Replace(TEXT("\""), TEXT("\"\""));
			const FString Name = FString(UTF8_TO_TCHAR(Bucket.Name)).Replace(TEXT("\""), TEXT("\"\""));
			Csv += FString::Printf(TEXT("\"%s\",\"%s\",%p,%llu,%llu\n"),
				*Namespace, *Name, static_cast<void*>(Bucket.Class), Bucket.Count, Bucket.Bytes);
		}
		if (Capture.UnbucketedObjects > 0)
		{
			Csv += FString::Printf(TEXT(",<bucket limit>,,%llu,%llu\n"), Capture.UnbucketedObjects, Capture.UnbucketedBytes);
		}

		const FString Directory = FPaths::ProjectSavedDir() / TEXT("UnrealSharp");
		IFileManager::Get().MakeDirectory(*Directory, true);
		const FString File = Directory / FString::Printf(TEXT("ManagedHeap-%s.csv"), *FGuid::NewGuid().ToString(EGuidFormats::Digits));
		if (FFileHelper::SaveStringToFile(Csv, *File, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
		{
			UE_LOG(LogUnrealSharp, Display, TEXT("Mono heap capture: objects=%llu bytes=%llu gcAndWalkMs=%.1f unbucketedObjects=%llu file=%s"),
				Capture.ObjectCount, Capture.Bytes, CaptureMs, Capture.UnbucketedObjects, *File);
		}
		else
		{
			UE_LOG(LogUnrealSharp, Warning, TEXT("Could not write Mono heap capture to %s."), *File);
		}
	}

	FAutoConsoleCommand DumpManagedHeapCommand(
		TEXT("UnrealSharp.DumpManagedHeap"),
		TEXT("Force a Mono full GC and save a by-type live object census."),
		FConsoleCommandDelegate::CreateStatic(&DumpManagedHeap));
}
#endif

DECLARE_UNREALSHARP_BINDER(Bind_FManagedRuntimeStats)
{
	void RegisterSampler(FManagedSampler Sampler)
	{
		ManagedSampler = Sampler;
	}

	BIND_UNREALSHARP_FUNCTION(RegisterSampler)
}

#if STATS
DECLARE_STATS_GROUP(TEXT("UnrealSharp"), STATGROUP_UnrealSharp, STATCAT_Advanced);
DECLARE_MEMORY_STAT(TEXT("Managed heap (estimate)"), STAT_UnrealSharpManagedHeap, STATGROUP_UnrealSharp);
DECLARE_MEMORY_STAT(TEXT("Managed allocation per second"), STAT_UnrealSharpAllocationRate, STATGROUP_UnrealSharp);
DECLARE_MEMORY_STAT(TEXT("Process physical memory"), STAT_UnrealSharpProcessPhysical, STATGROUP_UnrealSharp);
DECLARE_MEMORY_STAT(TEXT("Process virtual memory"), STAT_UnrealSharpProcessVirtual, STATGROUP_UnrealSharp);
DECLARE_MEMORY_STAT(TEXT("Available system virtual memory"), STAT_UnrealSharpAvailableVirtual, STATGROUP_UnrealSharp);
DECLARE_DWORD_ACCUMULATOR_STAT(TEXT("Tracked UObject handles"), STAT_UnrealSharpObjectHandles, STATGROUP_UnrealSharp);
DECLARE_DWORD_ACCUMULATOR_STAT(TEXT("UObjects with interface wrappers"), STAT_UnrealSharpInterfaceObjects, STATGROUP_UnrealSharp);
DECLARE_DWORD_ACCUMULATOR_STAT(TEXT("GC Gen 0 collections"), STAT_UnrealSharpGen0, STATGROUP_UnrealSharp);
DECLARE_DWORD_ACCUMULATOR_STAT(TEXT("GC Gen 1 collections"), STAT_UnrealSharpGen1, STATGROUP_UnrealSharp);
DECLARE_DWORD_ACCUMULATOR_STAT(TEXT("GC Gen 2 collections"), STAT_UnrealSharpGen2, STATGROUP_UnrealSharp);
#if UNREALSHARP_MONO && !UE_BUILD_SHIPPING && PLATFORM_WINDOWS
DECLARE_DWORD_ACCUMULATOR_STAT(TEXT("Last Mono census objects"), STAT_UnrealSharpHeapCensusObjects, STATGROUP_UnrealSharp);
DECLARE_MEMORY_STAT(TEXT("Last Mono census object bytes"), STAT_UnrealSharpHeapCensusBytes, STATGROUP_UnrealSharp);
#endif
#endif

namespace
{
	bool PublishStats(float DeltaTime)
	{
#if STATS
		static int64 PreviousAllocation = -1;
		static double PreviousTime = 0.0;
		if (!FThreadStats::IsCollectingData())
		{
			PreviousAllocation = -1;
			return true;
		}

		FManagedRuntimeSnapshot Managed;
		if (ManagedSampler) ManagedSampler(&Managed);

		const double Now = FPlatformTime::Seconds();
		const int64 Rate = PreviousAllocation >= 0 && Managed.TotalAllocatedBytes >= PreviousAllocation && Now > PreviousTime
			? static_cast<int64>((Managed.TotalAllocatedBytes - PreviousAllocation) / (Now - PreviousTime)) : 0;
		PreviousAllocation = Managed.TotalAllocatedBytes;
		PreviousTime = Now;

		const FPlatformMemoryStats Process = FPlatformMemory::GetStats();
		UCSManager& Manager = UCSManager::Get();
		SET_MEMORY_STAT(STAT_UnrealSharpManagedHeap, FMath::Max<int64>(0, Managed.EstimatedHeapBytes));
		SET_MEMORY_STAT(STAT_UnrealSharpAllocationRate, Rate);
		SET_MEMORY_STAT(STAT_UnrealSharpProcessPhysical, Process.UsedPhysical);
		SET_MEMORY_STAT(STAT_UnrealSharpProcessVirtual, Process.UsedVirtual);
		SET_MEMORY_STAT(STAT_UnrealSharpAvailableVirtual, Process.AvailableVirtual);
		SET_DWORD_STAT(STAT_UnrealSharpObjectHandles, Manager.GetManagedObjectHandles().Num());
		SET_DWORD_STAT(STAT_UnrealSharpInterfaceObjects, Manager.GetManagedInterfaceWrappers().Num());
		SET_DWORD_STAT(STAT_UnrealSharpGen0, Managed.Gen0Collections);
		SET_DWORD_STAT(STAT_UnrealSharpGen1, Managed.Gen1Collections);
		SET_DWORD_STAT(STAT_UnrealSharpGen2, Managed.Gen2Collections);
#if UNREALSHARP_MONO && !UE_BUILD_SHIPPING && PLATFORM_WINDOWS
		SET_DWORD_STAT(STAT_UnrealSharpHeapCensusObjects, LastHeapObjectCount);
		SET_MEMORY_STAT(STAT_UnrealSharpHeapCensusBytes, LastHeapObjectBytes);
#endif
#endif
		return true;
	}

#if !UE_BUILD_SHIPPING
	void DumpStats()
	{
		if (!TickHandle.IsValid())
		{
			UE_LOG(LogUnrealSharp, Display, TEXT("UnrealSharp runtime stats are not ready."));
			return;
		}

		FManagedRuntimeSnapshot Managed;
		if (ManagedSampler) ManagedSampler(&Managed);
		const FPlatformMemoryStats Process = FPlatformMemory::GetStats();
		UCSManager& Manager = UCSManager::Get();
		UE_LOG(LogUnrealSharp, Display,
			TEXT("Runtime stats: managedHeapBytes=%lld totalManagedAllocatedBytes=%lld gc=%d/%d/%d trackedUObjectHandles=%d interfaceWrapperObjects=%d processPhysicalBytes=%llu processVirtualBytes=%llu availableVirtualBytes=%llu"),
			Managed.EstimatedHeapBytes, Managed.TotalAllocatedBytes, Managed.Gen0Collections, Managed.Gen1Collections, Managed.Gen2Collections,
			Manager.GetManagedObjectHandles().Num(), Manager.GetManagedInterfaceWrappers().Num(),
			Process.UsedPhysical, Process.UsedVirtual, Process.AvailableVirtual);
	}

	FAutoConsoleCommand DumpRuntimeStatsCommand(
		TEXT("UnrealSharp.DumpStats"),
		TEXT("Log a snapshot of managed GC, UnrealSharp UObject tracking and process memory."),
		FConsoleCommandDelegate::CreateStatic(&DumpStats));
#endif
}

void UnrealSharp::RuntimeStats::Start()
{
#if STATS || !UE_BUILD_SHIPPING
	TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateStatic(&PublishStats), 1.0f);
#endif
}

void UnrealSharp::RuntimeStats::Stop()
{
	if (TickHandle.IsValid())
	{
		FTSTicker::RemoveTicker(TickHandle);
		TickHandle.Reset();
	}
	ManagedSampler = nullptr;
}
