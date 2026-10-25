# Remote Debugging Managed (C#) Code on Android (CoreCLR)

UnrealSharp runs **CoreCLR** on Android via the raw `coreclr_initialize` API. This guide
enables attaching a **managed debugger** (breakpoints, variable inspection, call stacks)
to the C# scripts running on an arm64 device.

## TL;DR flow

```
device:  app launches with -waitformanageddebugger  (or ini flag)
         -> CoreCLR exposes a Unix-domain-socket diagnostic port and PAUSES at startup
PC:      adb forward tcp:9000 localfilesystem:<socket path from logcat>
         dotnet-dsrouter  (bridges tcp:9000 -> local IPC server)
         Visual Studio .NET debugger attaches to the IPC server
         -> runtime resumes -> breakpoints hit
```

## ⚠️ Important: use the .NET debugger, NOT the Unity debugger

Visual Studio's **"Unity" debugger** (Visual Studio Tools for Unity) speaks the
**Mono Soft Debugger (SDB)** protocol. It attaches to a **Mono** runtime - it **cannot**
attach to **CoreCLR**. This project runs CoreCLR on Android, so use **Visual Studio's
regular .NET managed debugger** (the same one used for desktop .NET apps), via the
**diagnostic port** bridge below. This is the same mechanism .NET MAUI uses for CoreCLR
debugging on Android.

(If you ever switch this project's Android runtime to Mono via `bUseMono`, then the
Unity SDB debugger applies instead - that's a different setup, not covered here.)

## Prerequisites

- **Development (debuggable) build** of the APK. Shipping builds compile out the
  debug-port code (`#if !(UE_BUILD_SHIPPING)`), and a non-debuggable APK blocks
  `adb forward` to the app's private socket.
- **PDBs shipped**: `CoreClrSDK.Build.cs` stages `Content/Managed/Android/*.pdb` as UFS,
  and `EnsureRuntimeDllsExtracted` extracts them to the runtime dir alongside the DLLs.
  The default managed publish (no `DebugType` override) emits portable PDBs in both
  Debug and Release. For the richest experience, publish with `-c Debug` for the
  debug session; Release+PDB also works (breakpoints bind, code may be JIT-optimized).
- **.NET 10 SDK** on the PC (for `dotnet-dsrouter`).

## 1. Build & deploy

Build and install the debuggable Android Development APK per `README_AndroidClr.md`
(sections "Packaging flow" and "Install & run"). PDBs come along automatically.

## 2. Launch on device with the debug trigger

The runtime must be told to expose the diagnostic port and pause. Two triggers (either):

**A. Command-line flag (per-launch, non-persistent - preferred):**
```bash
adb shell am start -n <pkg>/<activity> -e cmdline "-waitformanageddebugger"
```
- `<pkg>` / `<activity>`: your game's package and launcher activity
  (UE5: commonly `com.epicgames.ue5.GameActivity`, or your project's `SplashActivity`).
- The `-e cmdline "..."` extra is read by UE's Android command-line plumbing into
  `FCommandLine`, matching the desktop `-waitformanageddebugger` flag.

**B. INI flag (persistent fallback - handy when the intent-extra plumbing is awkward):**
In the project's `Config/DefaultEngine.ini`:
```ini
[UnrealSharp]
bWaitForManagedDebuggerAndroid=true
```
Redeploy. ⚠️ **Remember to remove this when done** - while set, **every** launch pauses
at startup waiting for a debugger. (Shipping builds ignore it regardless.)

## 3. Confirm the pause in logcat

```bash
adb logcat -s LogUnrealSharp:W
```
Expect:
```
[CoreClr-Android] Managed debugger: runtime will PAUSE at startup until a debugger attaches. Diagnostic socket: /data/data/<pkg>/files/Saved/Managed/Android/debugger.sock
[CoreClr-Android] On PC run: adb forward tcp:9000 localfilesystem:/data/.../debugger.sock, then dotnet-dsrouter (TCP->IPC), then attach the VS .NET debugger.
```
The app is now **paused** at CoreCLR init - no managed code runs until you attach and resume.

## 4. Forward the device socket to a PC TCP port

```bash
adb forward tcp:9000 localfilesystem:/data/data/<pkg>/files/Saved/Managed/Android/debugger.sock
```
(Use the exact path printed by logcat in step 3.)

> If `adb forward` can't reach the socket (permission denied on a non-debuggable build),
> the APK isn't debuggable - rebuild as Development. The app-private path is reachable by
> `adb forward localfilesystem:` only for debuggable apps.

## 5. Run dotnet-dsrouter on the PC (TCP -> IPC bridge)

`dotnet-dsrouter` bridges the device's diagnostic port (now at `127.0.0.1:9000` via the
forward) to a local IPC server the IDE attaches to.

```bash
# one-time install (global .NET tool)
dotnet tool install -g dotnet-dsrouter

# bridge: connect to the forwarded diagnostic port (TCP) and expose a local IPC server
dotnet-dsrouter client-server --ipc-server UnrealSharpAndroidDiag --tcp-client 127.0.0.1:9000
```

⚠️ The dsrouter verb/flag names changed across versions. **Run `dotnet-dsrouter --help`**
to confirm the exact syntax for your installed version. The shape above
(`client-server` mode: TCP client in, IPC server out) is the intended one; if your
version uses different flags (e.g. `--ipcs` / `--tcpc`), use those equivalents.

Leave dsrouter running in its own terminal.

## 6. Attach Visual Studio's .NET debugger

1. In Visual Studio (2022/2026), open your C# script project (the source on the PC -
   the device's PDBs map back to this layout).
2. Set breakpoints.
3. Attach the **.NET managed debugger** to the dsrouter IPC server:
   - `Debug > Attach to Process...`
   - Transport: **Default**, or the diagnostic-port transport if VS exposes it.
   - Connect to the IPC server name `UnrealSharpAndroidDiag` (from step 5).
4. On attach, dsrouter forwards the `ResumeStartup` command - the paused runtime
   **resumes** and your breakpoints hit as the C# entry point / scripts execute.

> The exact VS attach UX for a diagnostic port is less surfaced than VS Code's. If VS
> attach is awkward, **VS Code with the C# Dev Kit** has the most explicit diagnostic-port
> attach support (`dotnet` attach, then pick the dsrouter IPC server) and is the
> documented fallback.

## 7. Stop / clean up

- Detach the debugger / stop dsrouter (`Ctrl-C` in its terminal).
- Remove `adb forward tcp:9000 localfilesystem:...` if desired: `adb forward --remove tcp:9000`.
- Remove the ini flag (trigger B) so normal launches don't pause.

## How it works (device side)

`CSDotNetRuntimeHost_Android.cpp` `InitializeManagedRuntimeAndroid()`, guarded by
`#if !(UE_BUILD_SHIPPING)` and a trigger check, sets two environment variables before
`coreclr_initialize`:

| Env var | Value | Effect |
|---------|-------|--------|
| `DOTNET_DiagnosticPorts` | `<runtimeDir>/debugger.sock,listen` | CoreCLR binds a Unix-domain-socket diagnostic port at this path |
| `DOTNET_DefaultDiagnosticPortSuspend` | `1` | Runtime pauses at startup until a debugger sends `ResumeStartup` |

Verified on Win64 CoreCLR (Android uses the same diagnostic-server code): with both set,
the runtime prints `"...configured to pause during startup and is awaiting a Diagnostics
IPC ResumeStartup command"` and does not run `Main` until resumed.

> The per-port `suspendruntime` keyword (`<path>,listen,suspendruntime`) was tested and
> does **not** pause the runtime - hence the separate `DOTNET_DefaultDiagnosticPortSuspend`
> env var is used instead.

## Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| App launches and runs normally (no pause) | Trigger not active. Verify the flag/ini; check logcat for the "PAUSE" line. Shipping build ignores the trigger by design. |
| App hangs forever after "PAUSE" log | No debugger attached/resumed. Attach via dsrouter + VS (steps 5-6). If you didn't mean to debug, remove the trigger and relaunch. |
| `adb forward` → permission denied | APK not debuggable. Rebuild as Development. |
| `dotnet-dsrouter` can't connect | `adb forward` not set, or wrong socket path, or dsrouter flag syntax differs - check `--help`. |
| Breakpoints don't bind | PDBs missing from the deploy (re-publish with PDBs), or source layout mismatch between PC and the PDB's recorded paths. |
