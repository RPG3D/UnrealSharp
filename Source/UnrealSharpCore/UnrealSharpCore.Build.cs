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
					"Json",
			});

        PublicIncludePaths.AddRange(new string[] { ModuleDirectory });
        PublicDefinitions.Add("ForceAsEngineGlue=1");

        // MonoSDK.Build.cs defines UNREALSHARP_MONO=1 (or =0) and links the Mono runtime.
        // When Mono is active, the CoreCLR/hostfxr headers in DotNetRuntime/inc/ are not needed.
        bool bUseMono = false;
        ConfigHierarchy EngineIni = ConfigCache.ReadHierarchy(ConfigHierarchyType.Engine,
            DirectoryReference.FromFile(Target.ProjectFile), Target.Platform);
        EngineIni.GetBool("UnrealSharp", "bUseMono", out bUseMono);
        if (!bUseMono || Target.bBuildEditor)
        {
            // Editor always needs CoreCLR/hostfxr headers (editor uses hostfxr, not Mono)
            PublicSystemIncludePaths.Add(Path.Combine(PluginDirectory, "Managed", "DotNetRuntime", "inc"));
        }
        // Pass USE_MONO_RUNTIME to UHT plugin so it can inject -p:UseMonoRuntime=true when building C# bindings.
        // Editor always uses CoreCLR — only inject Mono for non-editor (game/client) target builds.
        PublicDefinitions.Add("USE_MONO_RUNTIME=" + (bUseMono && !Target.bBuildEditor ? 1 : 0));

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


