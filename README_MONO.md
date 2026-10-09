# Mono Runtime Integration for UnrealSharp (Mono202610)

Mono runtime support for UnrealSharp, enabling C# scripting on **Android** and **iOS** where the CoreCLR hostfxr flow is not viable. When `bUseMono=true` in `DefaultEngine.ini [UnrealSharp]`, **both the editor and packaged builds use Mono** (unified runtime — lower maintenance cost); when absent/false, the plugin behaves exactly as upstream (CoreCLR via `hostfxr`). The runtime selection is made at **build time**: `MonoSDK.Build.cs` reads the ini and defines `UNREALSHARP_MONO=1/0`.

Based on the `mono20260729` port, re-implemented against the current upstream architecture with its known issues fixed (see "Changes vs mono20260729").

## Enable

```ini
[UnrealSharp]
bUseMono=true
```

> **CONVENTION**: set `bUseMono` in **DefaultEngine.ini only**. The UBT side reads it through the full config hierarchy for the target platform, while the UAT-side publish (`MonoProjectSettings`) reads the base layer alone — a platform-specific override (e.g. `AndroidEngine.ini`) would compile Mono but publish CoreCLR.

Managed code is compiled with `UNREALSHARP_MONO` defined (injected via `-p:UseMonoRuntime=true` by the automation scripts). Mono-specific settings (debugger port, wait-for-debugger, perf mode) live in **Project Settings → Plugins → UnrealSharp Settings** (`CSUnrealSharpSettings`).

## Supported platforms

| Platform | Mode | Runtime library | Code/staging | Packaged artifact | Device runtime |
|----------|------|-----------------|--------------|-------------------|----------------|
| Win64 Editor (bUseMono=true) | JIT | `coreclr.dll` (Mono runtime, Microsoft naming) | ✅ | — | ✅ Verified (mono20260729 era) |
| Android (arm64) | JIT | `libmonosgen-2.0.so` | ✅ | ✅ APK built on Mono202610 (2026-10-09) | ✅ Verified on device (2026-10-09): SMOKE PASS, 8 plugins from PAK |
| iOS device | INTERP + AOT | `libmonosgen-2.0.a` (static) + CoreLib AOT module | ✅ | Needs macOS signing step | ❌ Pending |
| Mac | JIT | `libcoreclr.dylib` | ✅ | — | ❌ Pending |

"Code/staging" = build integration, SDK staging and packaging command in place on Mono202610. "Device runtime" = actually booted on hardware.

**iOS simulator is not supported**: UE 5.8 dropped it (Metal requirements). Real devices only; on-Mac testing uses Apple's "Designed for iPad". The `IOSSimulator/` directory in the MonoSDK remains as dormant staging for older engine lines.

iOS forbids JIT (W^X) — Mono runs in interpreter mode with the AOT-compiled `System.Private.CoreLib` registered and a `mono_dl_fallback_register` hook resolving the BCL native interop dylibs from `Mono.framework/Frameworks/`. The iOS AOT block runs **regardless of debugger state** (fixed design; see below).

## Packaging (Android)

One-shot script (reverse flow — publish managed DLLs **before** UAT):

```bat
BuildAndroid.bat Development
```

Pipeline:
1. `Build.bat SharpDemo Win64 <Config>` — build Game target (UHT/Game glue; UHT only runs on the host platform)
2. `RunUAT PackageProjectMono -TargetPlatform=Android` — publish managed DLLs to `Content/Managed/Android/`
3. `Build.bat SharpDemoEditor Win64 Development` — fill flat `Binaries/Managed/` (cook needs it)
4. `RunUAT BuildCookRun -TargetPlatform=Android` — cook + pak + stage + archive

Key mechanics:
- **BCL** (runtime DLLs) staged as **NonUFS** (outside PAK) via `MonoSDK.Build.cs`
- **Project DLLs** staged as **UFS** (inside PAK, `Content/Managed/<Platform>/`) — hot-updatable alongside game assets
- **Load order** manifests tell UnrealSharp which assemblies to load
- Assembly loading goes through a **UFS/pak-aware preload hook** (`mono_install_assembly_preload_hook` + `FFileHelper::LoadFileToArray`) — POSIX `fopen` cannot read PAK paths

## How it works

### Runtime host

`FCSDotNetRuntimeHost::InitializeManagedRuntime()` branches on `UNREALSHARP_MONO` (build-time): Mono (`InitializeMonoHost` → `mono_jit_init_version` + preload hook) or upstream hostfxr. The hostfxr machinery compiles out entirely on Mono builds.

The C# entry point (`Main.InitializeUnrealSharp`) is resolved with `FindMonoMethod` + `mono_runtime_invoke` — **not** `mono_method_get_unmanaged_callers_only_ftnptr`, which crashes in .NET 9/10 Mono for methods with complex pointer parameters. Parameter passing rules: pointer-typed parameters are passed as pointer values; value-typed parameters (`nint`) as pointers to the value. The result is a `FMonoInitializationResult` mirroring the managed `FCSInitializationResult` layout (`NativeBool:byte` + fixed UTF-8 message buffer) — the C++ `FCSInitializationResult` in the CoreCLR path has a different layout and is not used here.

### JIT/cctor ordering (fix for the Android crash)

Mono runs a class's **cctor synchronously during JIT compilation** triggered by `GetFunctionPointer()`. Binding method handles per-function mid-registration would run the cctor against an incomplete class. Fix: **defer all method-handle binding until the class is fully mounted and `StaticLink`ed** (`FCSFunctionFactory::BindAllMethodHandles`, called from the class/interface compilers after linking; `FinalizeFunctionSetup` no longer binds). Skeleton classes are skipped entirely (`UCSFunctionBase::UpdateMethodHandle`).

### Hot reload (editor)

Under Mono, hot reload uses `dotnet build` (BuildEmitLoadOrder) instead of the Roslyn backend (`UnrealSharp.Editor.dll` is not loadable under the Mono BCL — it is never loaded). ALCs are not collectible on Mono; reloaded assemblies create new load contexts and the old one is intentionally leaked (acceptable during development). Packaged runtime builds never unload.

### Crash diagnostics

`mono_set_signal_chaining` + `mono_set_crash_chaining` print Mono's crash report (native + managed thread dump) and then chain to the OS so native crashes produce Android tombstones. On Android, stderr/stdout are redirected to `files/mono_stderr.log` (`ProjectPersistentDownloadDir`). Symbolized native stacks require a Debug MonoSDK build with `KeepNativeSymbols=true`.

### Generated-code alignment & visibility

- Generated `Invoke_XXX` thunks are emitted as `internal` (not implicit `private`): Mono INTERP reflection cannot reliably enumerate private methods of partial classes.
- Generated parameter buffers are stackalloc'd with 16-byte manual alignment in **both** generators: `stackalloc byte[N + 15]; ptr = (p + 15) & ~15` — UE's `UScriptStruct::InitializeStruct` asserts alignment that the Mono interpreter's byte-aligned stackalloc violates (e.g. `FHitResult`).

## IDE Debugging

Mono's built-in soft debugger agent — attach from **Visual Studio** or **Rider**:

| Setting | Default | Description |
|---------|---------|-------------|
| `bEnableMonoDebugger` | `false` | Enable debugger agent in packaged builds (editor always enables when not in perf mode) |
| `MonoDebuggerPort` | `56000` | Debugger agent listen port |
| `bMonoWaitDebugger` | `false` | Suspend at startup until a debugger attaches |
| `bMonoPerformanceMode` | `false` | Disable the debugger entirely (forced in Shipping) |

1. Start the editor (JIT mode — required for `GetFunctionPointer()`)
2. VS: **Debug → Attach to Process → Managed (.NET Core)** → `127.0.0.1:56000`
3. Rider: **Run → Attach to Process → Mono Remote** → `127.0.0.1:56000`

## MonoSDK dependency

| | |
|---|---|
| **GitHub** | [RPG3D/MonoSDK](https://github.com/RPG3D/MonoSDK) |
| **Clone location** | `Source/ThirdParty/MonoSDK/` (git submodule) |

The repository holds **build scripts + UBT integration only — no binaries**. Populate platform directories (`Win64/`, `Mac/`, `Android/`, `IOS/`, `IOSSimulator/`) by either:
- **Option A**: download the prebuilt SDK from [GitHub Releases](https://github.com/RPG3D/MonoSDK/releases) and extract into `Source/ThirdParty/MonoSDK/`
- **Option B**: build from dotnet/runtime source — `./BuildMonoSDK.bat` / `./BuildMonoSDK.sh`

## Integration philosophy

Mono-specific logic lives in **new files** (`CSMonoRuntime.h/.cpp`, `CSDotNetRuntimeHost_Mono.cpp`, `PluginLoadContext_Mono.cs`, `PackageProjectMono.cs`, `MonoProjectSettings.cs`) rather than inline `#if UNREALSHARP_MONO` blocks, to keep upstream merges cheap. Existing files are touched with additive-only edits (`#if` guards, `DefineConstants` appends); the behavioural seams are `CSDotNetRuntimeHost.cpp/.h`, `PluginLoader.cs`, `CSHotReloadSubsystem.cpp`, `UnrealSharpEditor.cpp`.

## Changes vs mono20260729

- Re-based on the rewritten class-based `FCSDotNetRuntimeHost` (upstream no longer has `GetRuntimeHostPath`); the `CSDotnetUtilties` seam from the old port is **gone entirely**.
- Entry-point invocation adapted to the new signature (UTF-8 working directory + `FCSInitializationResult` out-struct); the temp-file exception roundtrip is no longer needed, and `mono_string_to_utf8` results are `mono_free`'d (old leak).
- Platform-dir mapping (Win64/Mac/Android/IOS/IOSSimulator) deduplicated into `DotNetUtilities::GetMonoManagedPlatformDir()` (was repeated in 3 C++ files).
- `PackageProjectMono` validates the MonoSDK native/BCL directories up front (old: silent success on missing SDK); BCL dedup and editor-DLL strip are recursive and include `Newtonsoft.Json.dll`.
- `PluginLoader.Plugins` is a `ConcurrentDictionary` (thread-safe load/unload).
- Startup retry recursion capped at 5 attempts; UAT invocation retries on the single-instance mutex conflict.
- `UpdateMethodHandle` invocation path double-checks the handle after a lazy update (skeleton classes return true without binding).
- The editor "Package C# Project" button routes to `PackageProjectMono` under Mono (`BuildAction::PackageProject` becomes `PackageProjectMono` via the `UNREALSHARP_MONO` conditional), passing the SDK-layout platform name (`Win64`/`Mac`/`IOS`...) instead of `FPlatformProperties::PlatformName()` (which returns `Windows` on Win64 and would fail platform parsing).
- The assembly-path cache in `CSMonoRuntime.cpp` is mutex-guarded (the preload hook can fire on non-game threads); the search-path list is populated before `mono_jit_init` and read-only afterwards.

## TODO

- iOS on-device validation (INTERP+AOT path implemented but not yet re-validated on this branch)
- Android re-validation on device
