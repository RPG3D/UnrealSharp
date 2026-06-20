# CoreClrSDK — Android CoreCLR runtime + BCL

Prebuilt CoreCLR native runtime and BCL managed assemblies for **Android (arm64-v8a)**,
used by the raw `coreclr_initialize` host path (`#if PLATFORM_ANDROID` in
`UnrealSharpCore`).

This mirrors the `MonoSDK` layout (scripts/headers + UBT integration live here;
platform binaries are produced separately). `CoreClrSDK.Build.cs` reads from
`Android/lib/` and `Android/runtime/` and registers them as `RuntimeDependencies`,
and `CoreClrSDK_APL.xml` copies the native `.so` into the APK `lib/arm64-v8a/`.

## Directory layout

```
Source/ThirdParty/CoreClrSDK/
├── CoreClrSDK.Build.cs        ← UBT External module: RuntimeDependencies + APL wiring
├── CoreClrSDK_APL.xml         ← Android Plugin Language: copies .so into APK lib/<abi>/
├── README.md                  ← this file
└── Android/
    ├── lib/                   ← native .so  (libcoreclr.so, libclrjit.so, libSystem.*.so)
    └── runtime/               ← BCL managed .dll (System.*.dll, System.Private.CoreLib.dll, …)
```

## Populating the binaries

Produce everything with a single self-contained publish for `android-arm64`:

```bash
dotnet publish <ManagedProject>.csproj \
  -c Release -r android-arm64 --self-contained true \
  -o ./android-publish/
```

Then distribute the output:

| Publish artifact            | Destination                         |
|-----------------------------|-------------------------------------|
| `libcoreclr.so`             | `Android/lib/`                      |
| `libclrjit.so`              | `Android/lib/`                      |
| `libSystem.*.so`            | `Android/lib/`                      |
| `System.*.dll`, `*.dll` (BCL)| `Android/runtime/`                 |

> Do **not** copy project/user DLLs here — those are published to the game
> project's `Content/Managed/Android/` and staged into the PAK (UFS) for
> hot-update. Only the runtime-version-bound BCL belongs in `Android/runtime/`.
> `PackageProjectAndroidCoreClr` deduplicates BCL between the publish output and
> this directory (see `DeduplicateBclDlls`).

## Enabling

Nothing to enable. `CoreClrSDK.Build.cs` activates unconditionally for
`Target.Platform == Android` (Android is a first-class supported platform — no opt-in
switch, no macro). It is a no-op on every other platform.
