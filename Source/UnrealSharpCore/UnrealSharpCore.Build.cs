using System.IO;
using EpicGames.Core;
using UnrealBuildTool;

public class UnrealSharpCore : ModuleRules
{
	public UnrealSharpCore(ReadOnlyTargetRules Target) : base(Target)
	{
		// IWYU stays on everywhere else: only the iOS unity grouping starts with files that
		// rely on headers the shared PCH happens to include on the other platforms.
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		if (Target.Platform == UnrealTargetPlatform.IOS)
		{
			PrivatePCHHeaderFile = "../UnrealSharpUtilities/Public/UnrealSharpSharedPCH.h";
		}
		
		PublicDefinitions.Add("PLUGIN_PATH=" + PluginDirectory.Replace("\\","/"));
		PublicDefinitions.Add("TARGET_TYPE=" + (int)Target.Type);
		PublicDefinitions.Add("TARGET_CONFIGURATION=" + (int)Target.Configuration);
		
		PublicDependencyModuleNames.AddRange(
			new string[]
			{
				"Core",
				"GameplayTags",
				"UnrealSharpUtilities",
				// External ThirdParty module (Source/ThirdParty/MonoSDK/): UNREALSHARP_MONO define +
				// Mono runtime linkage + public include paths for the Mono embedding headers.
				// Public so the define propagates: MonoSDK -> UnrealSharpCore -> UnrealSharpEditor.
				"MonoSDK",
				// External ThirdParty module (Source/ThirdParty/CoreClrSDK/): raw-CoreCLR mobile
				// host support. The module itself activates only for Android/iOS when the
				// resolved backend is CoreCLR (see the backend gate inside CoreClrSDK.Build.cs);
				// it is a no-op everywhere else (Mono backend, desktop hostfxr platforms).
				"CoreClrSDK",
			}
			);
		
		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				"CoreUObject",
				"Engine",
				"Slate",
				"SlateCore", 
				"Boost", 
				"Projects",
				"UMG", 
				"DeveloperSettings", 
				"UnrealSharpUtilities", 
				"EnhancedInput", 
				"UnrealSharpUtilities",
				"GameplayTags", 
				"AIModule",
				"UnrealSharpBinds",
				"FieldNotification",
				"InputCore",
				"Json"
			});

        PublicIncludePaths.AddRange(new string[] { ModuleDirectory });
        PublicDefinitions.Add("ForceAsEngineGlue=1");
        // MonoSDK (External module) defines UNREALSHARP_MONO=1/0 via the shared
        // UnrealSharpRuntimeSelection resolver (DotNetRuntime / DotNetRuntimeEditor /
        // platform defaults). When Mono is active on a non-editor target, the
        // CoreCLR/hostfxr headers are unused, so skip the include path.
        bool bUseMonoRuntime = UnrealSharpRuntimeSelection.ShouldUseMono(Target);
        if (!bUseMonoRuntime || Target.bBuildEditor)
        {
            PublicSystemIncludePaths.Add(Path.Combine(PluginDirectory, "Managed", "DotNetRuntime", "inc"));
        }

		if (Target.bBuildEditor)
		{
			PrivateDependencyModuleNames.AddRange(new string[]
			{
				"UnrealEd", 
				"EditorSubsystem",
				"BlueprintGraph",
				"BlueprintEditorLibrary"
			});
		}
	}
}


