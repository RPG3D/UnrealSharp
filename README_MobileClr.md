# MobileClr — Android / iOS CoreCLR Support for UnrealSharp

Unified Android (arm64) + iOS CoreCLR host for UnrealSharp. Mobile platforms have **no
hostfxr**, so the runtime is bootstrapped via the raw `coreclr_initialize` /
`coreclr_create_delegate` C API (`coreclrhost.h`) instead of the desktop hostfxr path.
Managed IL is platform-agnostic — the same C# scripts authored for Win64 run on mobile.

## Architecture

| Platform | Host | Runtime Library | Status |
|----------|------|-----------------|--------|
| Win64 | hostfxr | `hostfxr.dll` | ✅ |
| Mac | hostfxr | `libhostfxr.dylib` | ✅ |
| Android (arm64) | raw `coreclr_initialize` | `libcoreclr.so` | ✅ Verified |
| iOS | raw `coreclr_initialize` | `libcoreclr.dylib` | 🚧 WIP |

Unified host: `CSDotNetRuntimeHost_Mobile.cpp` (replaces the old `_Android.cpp`).
Platform dispatch: `#if PLATFORM_ANDROID || PLATFORM_IOS` in `CSDotNetRuntimeHost.h`.

## Packaging flow (reverse: publish managed BEFORE UAT)

The upstream `PackageProject` publishes managed DLLs to `Binaries/Managed/` **after** UAT
has archived the build. MobileClr reverses this: `PackageProjectMobile` publishes managed
DLLs to `Content/Managed/<Platform>/` **before** UAT, so `CoreClrSDK.Build.cs` can stage
them into the PAK during cook/stage.

### Step 1: Build native Game target

Generates `UHT/Game/` glue (game-version, without `WITH_EDITOR`-only bindings).

```bash
<EngineDir>/Engine/Build/BatchFiles/Build.bat <GameTarget> Win64 Development \
  -Project="<ProjectDir>/<GameName>.uproject" -WaitMutex
```

### Step 2: Publish managed DLLs → Content/Managed/<Platform>

Uses the `PackageProjectMobile` UAT command (separate from upstream `PackageProject`,
zero merge conflicts on update). Auto-derives output to `Content/Managed/<Platform>`.

```bash
<EngineDir>/Engine/Build/BatchFiles/RunUAT.bat PackageProjectMobile \
  -ScriptDir="<PluginDir>/Build/Scripts" \
  -Project="<ProjectDir>/<GameName>.uproject" \
  -UEBuildConfig=Development \
  -TargetPlatform=Android \
  -nocompileuat
```

- `DisableWithEditor=true` strips `MSBuildLocator` (mobile has no dotnet SDK).
- `--no-self-contained` — IL only; BCL is staged separately by `CoreClrSDK.Build.cs`.
- Serial build (`-m:1`) — prevents parallel compilation races on shared `obj/` files.
- Does NOT use `UseDefaultOutputPath` — writes to the plugin's flat
  `Binaries/Managed/net10.0/` (where `Shared.props` HintPaths point), overwriting stale
  `WITH_EDITOR` DLLs so the glue/user-script publishes resolve clean Game-version DLLs.
  See `MSBUILD_LOCATOR.md`.

**Verify:** `UnrealSharp.Plugins.dll` present, `Microsoft.Build.Locator.dll` absent,
2 `LoadOrder.json` files, ~9 DLLs.

### Step 2.5: Build Editor target (force UHT)

UAT cook launches `UnrealEditor-Cmd`, whose `VerifyCSharpEnvironment()` checks that the
plugin's flat `Binaries/Managed/net10.0/UnrealSharp.Plugins.dll` exists. If `BuildBindings`
skipped (UHT incremental), the flat stays empty → cook fails. **Force UHT re-export:**

```bash
rm -rf "<PluginDir>/Intermediate/Build/Win64/UnrealEditor/Inc/UnrealSharpCore/UHT/"
<EngineDir>/Engine/Build/BatchFiles/Build.bat <GameName>Editor Win64 Development \
  -Project="<ProjectDir>/<GameName>.uproject" -WaitMutex
```

### Step 3: Package (Cook + Pak + Stage + Archive)

`CoreClrSDK.Build.cs` + `CoreClrSDK_APL.xml` (Android) stage:
- native `.so` → APK `lib/arm64-v8a/` (APL)
- BCL `.dll` → NonUFS (outside PAK, from `CoreClrSDK/<Platform>/runtime/`)
- project `.dll` / `.pdb` / `.json` → UFS (inside PAK, from `Content/Managed/<Platform>/`)

```bash
export NDKROOT='<NDK path>'
export ANDROID_NDK_ROOT="$NDKROOT"
export ANDROID_HOME='<SDK path>'

<EngineDir>/Engine/Build/BatchFiles/RunUAT.bat BuildCookRun \
  -project="<ProjectDir>/<GameName>.uproject" \
  -Build -Target=<GameTarget> -TargetPlatform=Android -ClientConfig=Development \
  -Cook -Pak -Stage -Archive -package -nocompileuat
```

### Step 4: Verify

```bash
ls -la "<ProjectDir>/Binaries/Android/<GameName>-arm64.apk"
# Confirm MSBuildLocator did NOT leak:
grep -ci "Microsoft.Build.Locator" \
  "<ProjectDir>/Saved/StagedBuilds/Android/Manifest_UFSFiles_Android.txt"  # -> 0
```

Expected runtime log:
```
[CoreClr-Android] Extracted 27, skipped 178 (unchanged) from .../Content/Managed/Android
[CoreClr-Android] Runtime dir: .../Saved/Managed/Android
[CoreClr-Android] TPA built: 26648 chars, 181 assemblies
[CoreClr-Android] coreclr_initialize => 0x0 (domain 1)
[CoreClr-Android] coreclr_create_delegate => 0x0
UnrealSharp initialized successfully.
```

## Key files

| File | Purpose |
|------|---------|
| `Source/UnrealSharpCore/Private/DotNet/CSDotNetRuntimeHost_Mobile.cpp` | Unified Android+iOS raw CoreCLR host |
| `Source/UnrealSharpCore/Public/DotNet/CSDotNetRuntimeHost.h` | Platform dispatch (`#if PLATFORM_ANDROID \|\| PLATFORM_IOS`) |
| `Source/ThirdParty/CoreClrSDK/CoreClrSDK.Build.cs` | Compile-time link + NonUFS/UFS staging + APL |
| `Source/ThirdParty/CoreClrSDK/CoreClrSDK_APL.xml` | Android `.so` → APK `lib/arm64-v8a/` |
| `Source/ThirdParty/CoreClrSDK/include/coreclrhost.h` | Raw CoreCLR C API header |
| `Source/ThirdParty/CoreClrSDK/include/host_runtime_contract.h` | iOS host runtime contract |
| `Build/Scripts/BuildCommands/PackageProjectMobile.cs` | MobileClr reverse-flow publish command |
| `Managed/UnrealSharp/UnrealSharp.Plugins/PluginLoadContext_Android.cs` | Android ALC fallback (partial class) |

## SDK setup

```bash
# Fetch CoreCLR runtime + BCL from NuGet (binaries are git-ignored)
Plugins\UnrealSharp\Source\ThirdParty\CoreClrSDK\FetchCoreClrSDK.bat   # Windows
Plugins/UnrealSharp/Source/ThirdParty/CoreClrSDK/FetchCoreClrSDK.sh    # macOS / Linux
```

## iOS notes

iOS support is WIP. The unified host (`CSDotNetRuntimeHost_Mobile.cpp`) compiles for both
Android and iOS with `#if PLATFORM_ANDROID || PLATFORM_IOS`. Syntax errors fixed (2026-07-28).
Pending:
- iOS CoreCLR SDK binaries (`libcoreclr.dylib`, BCL) — `CoreClrSDK/iOS/` not yet populated
- Platform directory logic (simulator vs device)
- iOS native lib staging (no APL equivalent)