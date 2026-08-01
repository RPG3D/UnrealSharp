# Mono Runtime Integration for UnrealSharp

Mono runtime support for UnrealSharp, enabling C# scripting on **Android** and **iOS** where CoreCLR is not available. When `bUseMono=true`, **both the editor and packaged builds use Mono** (unified runtime — lower maintenance cost and complexity); when `false`, the plugin behaves as upstream (CoreCLR via `hostfxr`). Mono is the dotnet/runtime Mono (same version as CoreCLR).

Reference: [UnrealCSharp](https://github.com/UnrealCSharp/UnrealCSharp) plugin (Mono embedding patterns).

## Enable

```ini
[UnrealSharp]
bUseMono=true
```

Managed code is compiled with `UNREALSHARP_MONO` defined (injected via `-p:UseMonoRuntime=true`), and `#if UNREALSHARP_MONO` branches select Mono behaviour. Mono-specific settings (debugger port, wait-for-debugger, perf mode) live in **Project Settings → UnrealSharp → Debugging | Mono** (`CSUnrealSharpSettings`).

## Supported platforms

| Platform | Mode | Runtime Library | Status |
|----------|------|-----------------|--------|
| Win64 Editor (bUseMono=true) | JIT | `libmonosgen-2.0.dll` | ✅ Verified |
| Android | JIT | `libmonosgen-2.0.so` | ✅ Verified (arm64) |
| iOS | INTERP+AOT | `libmonosgen-2.0.a` (static) | Planned |
| Mac | JIT | `libmonosgen-2.0.dylib` | Planned |

iOS forbids JIT (W^X) — Mono runs in interpreter mode with AOT-compiled BCL trampolines.

## Packaging (Android)

One-shot script (reverse flow — publish managed DLLs **before** UAT):

```bat
BuildAndroid.bat Development
```

Pipeline:
1. `Build.bat SharpDemo Win64 <Config>` — build Game target (generates UHT/Game/ glue; UHT only runs on the host platform)
2. `RunUAT PackageProjectMono -TargetPlatform=Android` — publish managed DLLs to `Content/Managed/Android/`
3. `Build.bat SharpDemoEditor Win64 Development` — fill flat `Binaries/Managed/` (cook needs it)
4. `RunUAT BuildCookRun -TargetPlatform=Android` — cook + pak + stage + archive

Key mechanics:
- **BCL** (runtime DLLs) staged as **NonUFS** (outside PAK) via `MonoSDK.Build.cs` RuntimeDependencies
- **Project DLLs** staged as **UFS** (inside PAK) — hot-updatable alongside game assets
- **Load order** (`GlueCode.LoadOrder.json` / `UserCode.LoadOrder.json`) tells UnrealSharp which assemblies to load
- Assembly loading goes through a **UFS/pak-aware preload hook** (`mono_install_assembly_preload_hook` + `FFileHelper::LoadFileToArray`) — `mono_assembly_open` (POSIX `fopen`) cannot read PAK paths

## How it works

### Runtime host

`CSDotNetRuntimeHost` selects the runtime at startup: Mono (`InitializeMonoHost` → `mono_jit_init_version` + preload hook) when `bUseMono`, else upstream CoreCLR (editor with `bUseMono=false`). Method invocation uses the same fast path as CoreCLR: `MethodHandle.GetFunctionPointer()` + `delegate*` — Mono's JIT compiles the method and returns a native pointer (INTERP_ONLY is not used: it would make `GetFunctionPointer()` return null).

### JIT/cctor ordering (fix for the Android crash)

Mono runs a class's **cctor synchronously during JIT compilation** (`mono_runtime_class_init_full`) triggered by `GetFunctionPointer()`, unlike CoreCLR. The class cctor reads every function of the class, so binding method handles **during** class registration (function-by-function) crashed with a null `UFunction`. Fix: **defer all method-handle binding until the class is fully mounted and `StaticLink`ed** (`FCSFunctionFactory::BindAllMethodHandles`, called from the class/interface compilers after linking). Skeleton classes are skipped entirely (they only need layout).

### Hot reload (editor)

Under Mono, hot reload uses `dotnet build` instead of the Roslyn backend (`UnrealSharp.Editor.dll` is incompatible with the Mono BCL). ALCs are not collectible on Mono — reloaded assemblies create new load contexts; the old one is intentionally leaked (acceptable during development). Packaged runtime builds never unload.

### Crash diagnostics

Mono's crash handler prints native+managed stacks before chaining to the OS (Android tombstone). On Android, stderr/stdout are redirected to `files/mono_stderr.log` (`ProjectPersistentDownloadDir`) since the platform discards them by default. Native stack printing uses Mono's unwinder patched into `mini-posix.c` (see memory notes); requires a Debug MonoSDK build with `KeepNativeSymbols=true`.

## IDE Debugging

Mono's built-in soft debugger agent — attach from **Visual Studio** or **Rider**:

| Setting | Default | Description |
|---------|---------|-------------|
| `bEnableMonoDebugger` | `false` | Enable debugger agent |
| `MonoDebuggerPort` | `56000` | Debugger agent listen port |
| `bMonoWaitDebugger` | `false` | Suspend at startup until debugger attaches |

1. Start the editor (JIT mode — required for `GetFunctionPointer()`)
2. VS: **Debug → Attach to Process → Managed (.NET Core)** → `127.0.0.1:56000`
3. Rider: **Run → Attach to Process → Mono Remote** → `127.0.0.1:56000`

> Changing `MonoDebuggerPort` requires an editor restart (Mono is already initialized).

## MonoSDK dependency

| | |
|---|---|
| **GitHub** | [RPG3D/MonoSDK](https://github.com/RPG3D/MonoSDK) |
| **Clone location** | `Source/ThirdParty/MonoSDK/` |

The repository holds **build scripts + UBT integration only — no binaries**. Populate platform directories (`Win64/`, `Mac/`, `Android/`, `IOS/`, `IOSSimulator/`) by either:
- **Option A**: download the prebuilt SDK from [GitHub Releases](https://github.com/RPG3D/MonoSDK/releases) and extract into `Source/ThirdParty/MonoSDK/`
- **Option B**: build from dotnet/runtime source — `./BuildMonoSDK.bat` (Win64) / `./BuildMonoSDK.sh <src> <platform> <build-type>` (Debug keeps symbols; use `KeepNativeSymbols` for crash-stack symbolization)

## Integration philosophy

Mono-specific logic lives in **new files** (`CSMonoRuntime.h/.cpp`, `CSDotNetRuntimeHost_Mono.cpp`, `PluginLoadContext_Mono.cs`, `PackageProjectMono.cs`, `MonoProjectSettings.cs`) rather than inline `#if UNREALSHARP_MONO` blocks, to keep upstream merges cheap. Existing files are touched with additive-only edits where possible (`#if` guards, `DefineConstants` appends); the few behavioural seams are `CSDotNetRuntimeHost.cpp/.h`, `UnmanagedCallbacks.cs`, `PluginLoader.cs`, `CSHotReloadSubsystem.cpp`, `CSDotnetUtilties.cpp`.

## Key files

| File | Purpose |
|------|---------|
| `CSMonoRuntime.h/cpp` | Mono initialization, UFS assembly loading, crash diagnostics |
| `CSDotNetRuntimeHost_Mono.cpp` | Mono host entry point (`InitializeMonoHost`) |
| `Source/ThirdParty/MonoSDK/MonoSDK.Build.cs` | BCL NonUFS + project UFS RuntimeDependencies |
| `Build/Scripts/BuildCommands/PackageProjectMono.cs` | Mono packaging command |
| `Build/Scripts/Utilities/MonoProjectSettings.cs` | Read `bUseMono` from `DefaultEngine.ini` |
| `Managed/Directory.Build.props` | Define `UNREALSHARP_MONO` when `UseMonoRuntime=true` |
| `CSUnrealSharpSettings.h` | Mono debugger configuration |

## TODO

- **Editor "Package Project" button**: route to `PackageProjectMono` under Mono (currently passes `Windows` to the CoreCLR command, which fails)
- **Android/iOS remote debug**: extend debugger agent support to mobile platforms

## License

MIT. See [`LICENSE`](LICENSE) for the full text.
