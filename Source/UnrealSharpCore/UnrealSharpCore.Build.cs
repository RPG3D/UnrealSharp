using System.IO;
using EpicGames.Core;
using UnrealBuildTool;

public class UnrealSharpCore : ModuleRules
{
	public UnrealSharpCore(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		
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
        // MonoSDK (External module) defines UNREALSHARP_MONO=1/0 and links the Mono runtime
        // when bUseMono=true in DefaultEngine.ini. When Mono is active on a non-editor target,
        // the CoreCLR/hostfxr headers are unused, so skip the include path.
        bool bUseMonoRuntime = false;
        ConfigHierarchy EngineIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine,
            DirectoryReference.FromFile(Target.ProjectFile), Target.Platform);
        EngineIni.GetBool("UnrealSharp", "bUseMono", out bUseMonoRuntime);
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


