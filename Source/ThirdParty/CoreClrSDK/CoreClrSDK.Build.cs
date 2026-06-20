// CoreClrSDK.Build.cs
// UBT External-module wrapper for the Android CoreCLR runtime SDK.
//
// Android is a first-class supported platform (no opt-in switch). On Android this module:
//   • stages BCL managed DLLs as NonUFS (outside PAK, runtime-version-bound)
//   • stages project managed DLLs (*.dll/*.pdb/*.json) from Content/Managed/Android/
//     as UFS (inside PAK, hot-update)
//   • wires CoreClrSDK_APL.xml so native .so land in APK lib/arm64-v8a/
// On all other platforms it is a no-op (the hostfxr path handles Win64/Mac).
//
// The platform dispatch in CSDotNetRuntimeHost uses a plain #if PLATFORM_ANDROID
// (no macro), consistent with how Win64/Mac are handled.
//
// SDK files and this Build.cs live together in Source/ThirdParty/CoreClrSDK/
// (standard UE5 plugin ThirdParty layout).
//
// SDK directory layout (Source/ThirdParty/CoreClrSDK/):
//   Android/lib/      native .so  (libcoreclr.so, libclrjit.so, libSystem.*.so)
//   Android/runtime/  BCL managed .dll
// CoreClrSDK_APL.xml lives alongside this file.

using System.IO;
using UnrealBuildTool;

public class CoreClrSDK : ModuleRules
{
    public CoreClrSDK(ReadOnlyTargetRules Target) : base(Target)
    {
        Type = ModuleType.External;

        // No-op on non-Android platforms (Win64/Mac use the hostfxr path).
        if (Target.Platform != UnrealTargetPlatform.Android)
        {
            return;
        }

        // ModuleDirectory IS the SDK root (Source/ThirdParty/CoreClrSDK/).
        string sdkRoot = ModuleDirectory;
        string nativeLibDir = Path.Combine(sdkRoot, "Android", "lib");
        string bclRuntimeDir = Path.Combine(sdkRoot, "Android", "runtime");

        // Expose coreclrhost.h (raw CoreCLR C API) so the host can include it.
        string includeDir = Path.Combine(sdkRoot, "include");
        if (Directory.Exists(includeDir))
        {
            PublicIncludePaths.Add(includeDir);
        }

        // ── Compile-time link against libcoreclr.so ──────────────────────────
        // Linking libcoreclr.so at compile time (NDK linker -l) lets the C++ host
        // call coreclr_initialize / coreclr_create_delegate / coreclr_set_error_writer /
        // coreclr_shutdown directly via the coreclrhost.h prototypes — no dlopen, no
        // GetDllExport string lookup (which is spelling-error-prone). The .so is placed
        // into APK lib/arm64-v8a/ by CoreClrSDK_APL.xml so the OS linker resolves it at
        // load time. Mirrors how MonoSDK.Build.cs links libmonosgen-2.0.so.
        string coreclrLibPath = Path.Combine(nativeLibDir, "libcoreclr.so");
        if (File.Exists(coreclrLibPath))
        {
            PublicAdditionalLibraries.Add(coreclrLibPath);
        }

        // ── BCL managed DLLs — staged as NonUFS (outside PAK) ──────────────
        // BCL is tied to the CoreCLR runtime version and should not be hot-updated
        // via PAK. CoreCLR loads these from real filesystem paths (extracted to the
        // writable runtime dir at launch — see EnsureRuntimeDllsExtracted).
        if (Directory.Exists(bclRuntimeDir))
        {
            RuntimeDependencies.Add(Path.Combine(bclRuntimeDir, "...*.dll"), StagedFileType.NonUFS);
        }

        // ── Native .so — staged into APK lib/arm64-v8a/ via APL ────────────
        // CoreClrSDK_APL.xml <copyDir>s all of Android/lib/ to $S(BuildDir)/libs/arm64-v8a/
        // so the OS linker loads them at install. libcoreclr.so is also compile-time
        // linked above; the rest (libclrjit.so, libSystem.*.so) are loaded by CoreCLR at
        // runtime via dlopen, so they only need to be staged (APL), not linked.
        string aplPath = Path.Combine(ModuleDirectory, "CoreClrSDK_APL.xml");
        if (File.Exists(aplPath))
        {
            AdditionalPropertiesForReceipt.Add("AndroidPlugin", aplPath);
        }

        // ── Project managed DLLs staging — UFS (inside PAK) ────────────────
        // Register Content/Managed/Android/ as UFS so project DLLs + LoadOrder.json
        // get cooked into the PAK (hot-updatable). This is in CoreClrSDK.Build.cs so
        // game projects need no Build.cs changes.
        if (Target.ProjectFile != null)
        {
            string projectDir = Path.GetDirectoryName(Target.ProjectFile.FullName)!;
            string managedContentDir = Path.Combine(projectDir, "Content", "Managed", "Android");
            if (Directory.Exists(managedContentDir))
            {
                RuntimeDependencies.Add(Path.Combine(managedContentDir, "*.dll"), StagedFileType.UFS);
                RuntimeDependencies.Add(Path.Combine(managedContentDir, "*.pdb"), StagedFileType.UFS);
                RuntimeDependencies.Add(Path.Combine(managedContentDir, "*.json"), StagedFileType.UFS);
            }
        }
    }
}
