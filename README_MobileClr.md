# MobileClr — Android / iOS CoreCLR Support for UnrealSharp

Android (arm64) + iOS CoreCLR host for UnrealSharp. Mobile has **no hostfxr**, so the
runtime is bootstrapped via the raw `coreclr_initialize` / `coreclr_create_delegate`
C API (`coreclrhost.h`). Managed IL is cross-platform — the same C# scripts run on
Win64 and mobile.

## Platform status

| Platform | Host | Runtime | Status |
|---|---|---|---|
| Win64 / Mac | hostfxr | hostfxr | ✅ |
| Android (arm64) | raw `coreclr_initialize` | `libcoreclr.so` | ✅ verified |
| iOS simulator | raw `coreclr_initialize` | `libcoreclr.dylib` | 🚧 in testing |
| iOS device | raw `coreclr_initialize` | `libcoreclr.dylib` | 🚧 pending (`RUNTIME_IDENTIFIER` → `ios-arm64`) |

## Directory design

| Content | Location | Arch-specific? |
|---|---|---|
| Project managed DLLs (IL) | `Content/Managed/{Android\|IOS}` — **simulator & device share `IOS`** | ❌ cross-platform IL |
| BCL DLLs | source `CoreClrSDK/{Android\|IOS\|IOSSimulator}/runtime` → staged into `Content/Managed/{Android\|IOS}` (UFS/PAK) | ✅ source is arch-specific |
| Native runtime libs | `CoreClrSDK/{Android\|IOS\|IOSSimulator}/lib` → APK `lib/` (APL) / `.app` bundle (RuntimeDependencies) | ✅ |

Writable extraction dir (CoreCLR needs real OS paths for the TPA):
`ProjectSavedDir/Managed/<PlatformName>` (from `FPlatformProperties::PlatformName()`).

## Packaging flow (reverse: publish managed BEFORE UAT)

### Step 1: Build native Game target

Generates `UHT/Game/` glue AND compiles the native lib (UBT won't recompile in Step 3).

```bash
<EngineDir>/Engine/Build/BatchFiles/Build.bat <GameTarget> Android Development \
  -Project="<ProjectDir>/<GameName>.uproject" -WaitMutex
```

### Step 2: Publish managed → Content/Managed/<Platform>

```bash
<EngineDir>/Engine/Build/BatchFiles/RunUAT.bat PackageProjectMobile \
  -ScriptDir="<PluginDir>/Build/Scripts" \
  -Project="<ProjectDir>/<GameName>.uproject" \
  -UEBuildConfig=Development \
  -TargetPlatform=Android \
  -nocompileuat
```

- `-TargetPlatform=IOS` publishes to `Content/Managed/IOS` — **shared by simulator and
  device** (no `bIOSSimulator` param, unlike the mono flow).
- Order = bindings → glue → user solution (same as mono). `UETargetType=Game` excludes
  editor-only code (`WITH_EDITOR` / `MSBuildLocator`) at reference level;
  `StripEditorOnlyDlls` removes any stragglers post-publish.
- `--no-self-contained` (IL only; BCL staged by `CoreClrSDK.Build.cs`), `-m:1` serial build.
- **Verify:** `UnrealSharp.Plugins.dll` present, `Microsoft.Build.Locator.dll` absent,
  2 `LoadOrder.json` files, ~9 DLLs.

### Step 2.5: Force UHT re-export (for cook)

```bash
rm -rf "<PluginDir>/Intermediate/Build/Win64/UnrealEditor/Inc/UnrealSharpCore/UHT/"
<EngineDir>/Engine/Build/BatchFiles/Build.bat <GameName>Editor Win64 Development \
  -Project="<ProjectDir>/<GameName>.uproject" -WaitMutex
```

### Step 3: Package (Cook + Pak + Stage + Archive)

```bash
<EngineDir>/Engine/Build/BatchFiles/RunUAT.bat BuildCookRun \
  -project="<ProjectDir>/<GameName>.uproject" \
  -Build -Target=<GameTarget> -TargetPlatform=Android -ClientConfig=Development \
  -Cook -Pak -Stage -Archive -package -nocompileuat
```

### Step 4: Verify

Expected runtime logs:

```
[CoreClr] Extracted 27, skipped 178 (unchanged) from .../Content/Managed/Android
[CoreClr] TPA built: 26648 chars, 181 assemblies
[CoreClr] coreclr_initialize => 0x0 (domain 1)
[CoreClr] coreclr_create_delegate => 0x0
UnrealSharp initialized successfully.
```

## iOS notes

- SDK fetch (on Mac): `FetchCoreClrSDK_iOS.sh` — copies the self-consistent DotNet10
  runtime-pack (`iossimulator-arm64`) into `CoreClrSDK/iOSSimulator/{lib,runtime}`;
  binaries are git-ignored.
- `host_runtime_contract`: `pinvoke_override` resolves `__Internal` symbols via
  `dlsym(RTLD_DEFAULT)`; `external_assembly_probe` feeds `System.Private.CoreLib.dll`
  to CoreCLR from memory (loaded from the extraction dir).
- dylibs staged into the `.app` bundle via `RuntimeDependencies` (NonUFS).
- `RUNTIME_IDENTIFIER` is currently hardcoded `iossimulator-arm64` — parameterize to
  `ios-arm64` before device testing.

## Key files

| File | Purpose |
|------|---------|
| `Source/UnrealSharpCore/Private/DotNet/CSDotNetRuntimeHost_Mobile.cpp` | Unified Android+iOS raw CoreCLR host |
| `Source/UnrealSharpCore/Public/DotNet/CSDotNetRuntimeHost.h` | Platform dispatch (`#if PLATFORM_ANDROID \|\| PLATFORM_IOS`) |
| `Source/ThirdParty/CoreClrSDK/CoreClrSDK.Build.cs` | BCL/native staging + compile-time link |
| `Source/ThirdParty/CoreClrSDK/CoreClrSDK_APL.xml` | Android `.so` → APK `lib/arm64-v8a/` |
| `Source/ThirdParty/CoreClrSDK/include/host_runtime_contract.h` | iOS host contract |
| `Build/Scripts/BuildCommands/PackageProjectMobile.cs` | MobileClr reverse-flow publish command |
| `Managed/UnrealSharp/UnrealSharp.Plugins/PluginLoadContext_Android.cs` | Android ALC fallback (partial class) |
