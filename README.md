# UnrealSharp

UnrealSharp is a free, open-source plugin for writing Unreal Engine 5 games in C# on top of .NET 10.

[Workflow Showcase](https://www.youtube.com/watch?v=xR7M2XgCuNU)

## Features

- **Unreal Engine API in C#**: Derive from any `UClass`. Implement Actors, ActorComponents, and more in C# with full access to the Unreal Engine API.
- **Generated bindings**: The C# API is automatically generated from all reflected C++ code. This includes the engine, plugins, and your own project, so any new reflected types or members are immediately available for use in C#.
- **Hot reload**: Recompile and reload C# code without restarting the editor.
- **Full .NET ecosystem**: Pull in any NuGet package you need.
- **MIT licensed**

## Games made with UnrealSharp

- [Ballistic Brews (Steam Page)](https://store.steampowered.com/app/4095000/Ballistic_Brews/)
- [Forged: Reconquest (Steam Page)](https://store.steampowered.com/app/3509080/Forged_Reconquest/)
- [Unsound (Steam Page)](https://store.steampowered.com/app/5249750/Unsound/)
- [Prompt Crisis (Steam Page)](https://store.steampowered.com/app/5203720/Prompt_Crisis/)

Making a game with UnrealSharp? [Open an issue](https://github.com/UnrealSharp/UnrealSharp/issues) or submit a PR to add your project here!

## Sample projects

- [Sample Defense Game](https://github.com/UnrealSharp/UnrealSharp-SampleDefenseGame) built for Mini Jam 174.
- [Slime Guzzler](https://github.com/UnrealSharp/Epic-MegaJam-Project) Epic MegaJam 2025 entry.
- [UnrealSharp-Cropout](https://github.com/UnrealSharp/UnrealSharp-Cropout) Epic's Cropout sample, ported from Blueprints to C#.

## Supported platforms

| Platform | Status |
|---|---|
| Windows | Supported |
| macOS | Supported |
| Linux | Experimental (editor, x86-64) |
| iOS | Experimental, arm64, Mono and CoreCLR |
| Android | Experimental, arm64, Mono and CoreCLR |

## Prerequisites

- Unreal Engine 5.6 – 5.8
- .NET 10.0.5 or newer
- A C++ project (strongly recommended, pure Blueprint projects work, but are harder to provide support for)

### Linux notes

Linux support is experimental. It has been tested with the UE 5.8 prebuilt (installed) engine, the engine's bundled clang toolchain and .NET 10 on x86-64; packaged Linux builds have not been tested yet.

- Install the .NET 10 SDK, for example with `dotnet-install.sh` into `~/.dotnet` or from your distribution's packages. The editor looks for it in `DOTNET_ROOT` first (e.g. `DOTNET_ROOT=~/.dotnet ./UnrealEditor MyGame.uproject`), then follows the `dotnet` executable on `PATH` to the real SDK directory, so `/usr/bin/dotnet` symlinks work. Version-manager shims (mise, asdf) are skipped because they have no `sdk/` folder next to them.
- Opening the C# solution from the editor uses `xdg-open`, so associate `.sln` files with your IDE (e.g. Rider).

### Mobile packaging

Populate `Source/ThirdParty/DotNetRuntimeSDK` at the pinned submodule revision and provide the matching arm64 payload for your target and runtime. Existing local payloads can be reused. The SDK supplies the .NET 10 Mono or .NET 11 CoreCLR runtime and BCL. The managed projects target `net10.0` for both.

Packaged Android and iOS targets default to Mono. Set `DotNetRuntime=CoreCLR` in the `[UnrealSharp]` section of `Config/Android/AndroidEngine.ini` or `Config/IOS/IOSEngine.ini` to select CoreCLR for a platform. The editor uses its own runtime selection and defaults to CoreCLR.

Build the native Game target to generate its bindings, then publish managed assemblies for the same platform and configuration before building and staging the package. Run this Unreal AutomationTool command with the paths for your project.

```text
RunUAT PackageProjectMobile -ScriptDir=<UnrealSharp>/Build/Scripts -Project=<Game.uproject> -TargetPlatform=Android -UEBuildConfig=Development
```

Use `-TargetPlatform=IOS` for iOS and add `-TargetArchitecture=IOSSimulator` when preparing simulator assemblies. Run a native build after each managed publish so UBT refreshes the staging receipt and restores the selected SDK's BCL copy outputs. `BuildCookRun -Build -Cook -Pak -Stage` performs that build. A later `-skipbuild` package step can reuse this refreshed native build. Repeat the publish after changing the target runtime or the managed projects. The SDK stages the selected runtime and the managed output from `Content/Managed/<Platform>`.

Mobile publishing selects framework-dependent `android-arm64`, `ios-arm64` or `iossimulator-arm64` assets. Native NuGet files require UE native-library staging and iOS signing. After a UE module supplies those files, use `ExcludeAssets="native"` on the corresponding `PackageReference`. The publish command reports unintegrated native assets before replacing a working managed output.

### Mobile validation

The port was rebuilt from upstream `main` at `947b6ae5`, with `mobile20261021` used as a reference. DotNetRuntimeSDK uses `86976ca8`, a staging-only successor to `66a9523c` that unifies Mono simulator managed content under `IOS`. Runtime payloads come from the existing local SDK cache.

Validation with a separate SharpDemo project copy and the local UE 5.9 engine completed the following checks.

| Target | Runtime | Native build | Managed publish | Signed package | Device startup |
|---|---|---|---|---|---|
| Android arm64 | Mono | Passed | Passed | Passed | Passed |
| Android arm64 | CoreCLR | Passed | Passed | Passed | Passed |
| iOS arm64 | Mono | Passed | Passed | Passed | Passed |
| iOS arm64 | CoreCLR | Passed | Passed | Passed | Passed |

All 29 source-generator tests passed, including source and external-metadata struct layout regressions. Offline fixtures also covered runtime bootstrap, explicit assembly paths, layout marshalling, cache paths, concurrent builds, RID selection, reference resolution and interrupted-publish recovery. The mobile review below records the scope and environment of these checks.

Both runtimes reached PAL3 Gameplay and emitted SMOKE PASS on a Xiaomi 24122RKC7C Android phone and an iPad Air 7 with iPadOS 27.2.1. After isolating the mobile implementation, all four combinations were republished, rebuilt, signed and installed again, reusing the existing cooked game assets. The Android CoreCLR run also advanced from the menu into the opening cutscene and video. The existing Mono iOS CoreLib AOT object carries an iOS 26.5 deployment marker, which produces a warning when linking the project's iOS 17.0 target. Compatibility with older devices requires a device check.

The device run exposed native parameter-buffer alignment requirements. Mono interpreter `stackalloc` only guarantees 8-byte alignment, while a reflected `FTransform` parameter requires 16. Native UFunctions can report `MinAlignment=1`, so the bridge computes the maximum alignment of the actual parameters. Both generators and native property/struct temporaries reserve alignment padding and align the usable pointer before initialization.

The verification project supplied two optional UGP callback stubs for this local engine checkout and used file-based cooking after its Zen store connection failed.

The current implementation map, patch rationale and validation boundaries are recorded in [the mobile review](MOBILE_REVIEW.txt). For future ports and upstream updates, see [the mobile porting guide](MOBILE_PORTING_GUIDE.md) (Chinese).

### Keeping upstream merges small

Mobile build synchronization, publishing, managed entry points, assembly resolution and interpreter ABI generation live in `BuildSolution_Mobile.cs`, `PackageProject_Mobile.cs`, `Main_Mobile.cs`, `PluginLoadContext_Mobile.cs` and `NativeCallbacksWrapperGenerator_Mobile.cs`. These are partial classes of the original types, so command names and managed entry-point signatures stay unchanged. `UnrealSharp.Mobile.targets` contains the published-reference override. The original files retain the calls needed to connect these implementations.

Each native runtime implements its own startup and shutdown in `CSDotNetRuntimeHost_Mono.cpp` or `CSDotNetRuntimeHost_MobileCoreClr.cpp`. The desktop host retains the upstream implementation behind a compilation guard. Shared alignment, container ABI and engine compatibility fixes remain at their responsible call sites. When updating upstream, review the small hooks and these shared fixes as well as changes to the methods used by the partial files.


## Getting started

Visit the website's [Get Started](https://www.unrealsharp.com/getting-started/quickstart) page!

If you want to contribute to the documentation, check out the [docs repository](https://github.com/UnrealSharp/unrealsharp.github.io)!

## Code example

A networked, interactable resource pickup written entirely in C#:

```csharp
using UnrealSharp;
using UnrealSharp.Attributes;
using UnrealSharp.Engine;
using UnrealSharp.Niagara;

namespace ManagedSharpProject;

public delegate void OnIsPickedUp(bool bIsPickedUp);

[UClass]
public partial class AResourceBase : AActor, IInteractable
{
    public AResourceBase()
    {
        Replicates = true;
        RespawnTime = 500.0f;
    }

    [UProperty(DefaultComponent = true, RootComponent = true)]
    public partial UStaticMeshComponent Mesh { get; set; }

    [UProperty(DefaultComponent = true)]
    public partial UHealthComponent HealthComponent { get; set; }

    [UProperty(PropertyFlags.EditDefaultsOnly)]
    public partial int PickUpAmount { get; set; }

    [UProperty(PropertyFlags.EditDefaultsOnly | PropertyFlags.BlueprintReadOnly)]
    protected partial float RespawnTime { get; set; }

    [UProperty(PropertyFlags.BlueprintReadOnly, ReplicatedUsing = nameof(OnRep_IsPickedUp))]
    protected partial bool bIsPickedUp { get; set; }

    [UProperty(PropertyFlags.EditDefaultsOnly)]
    public partial TSoftObjectPtr<UNiagaraSystem>? PickUpEffect { get; set; }

    [UProperty(PropertyFlags.BlueprintAssignable)]
    public partial TMulticastDelegate<OnIsPickedUp> OnIsPickedUp { get; set; }

    public override void BeginPlay()
    {
        HealthComponent.OnDeath += OnDeath;
        base.BeginPlay();
    }

    [UFunction]
    protected virtual void OnDeath(APlayer player) {}

    public void OnInteract(APlayer player)
    {
        GatherResource(player);
    }

    [UFunction(FunctionFlags.BlueprintCallable)]
    protected void GatherResource(APlayer player)
    {
        if (bIsPickedUp)
        {
            return;
        }

        if (!player.Inventory.AddItem(this, PickUpAmount))
        {
            return;
        }

        UExperienceComponent experienceComponent = UExperienceComponent.Get(player.PlayerState);
        experienceComponent.AddExperience(PickUpAmount);

        SystemLibrary.SetTimer(OnRespawned, RespawnTime, false);

        bIsPickedUp = true;
        OnRep_IsPickedUp();
    }

    [UFunction]
    public void OnRespawned()
    {
        bIsPickedUp = false;
        OnRep_IsPickedUp();
    }

    [UFunction]
    public void OnRep_IsPickedUp()
    {
        if (PickUpEffect is not null)
        {
            UNiagaraFunctionLibrary.SpawnSystemAtLocation(this, PickUpEffect, GetActorLocation(), GetActorRotation());
        }

        OnIsPickedUpChanged(bIsPickedUp);
        OnIsPickedUp.Invoke(bIsPickedUp);
    }

    // Overridable from Blueprints
    [UFunction(FunctionFlags.BlueprintEvent)]
    public partial void OnIsPickedUpChanged(bool bIsPickedUp);
    public partial void OnIsPickedUpChanged_Implementation(bool bIsPickedUp)
    {
        SetActorHiddenInGame(bIsPickedUp);
    }
}
