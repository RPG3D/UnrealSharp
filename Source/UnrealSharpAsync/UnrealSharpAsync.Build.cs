using UnrealBuildTool;

public class UnrealSharpAsync : ModuleRules
{
    public UnrealSharpAsync(ReadOnlyTargetRules Target) : base(Target)
    {
        // IWYU stays on everywhere else: see UnrealSharpCore.Build.cs.
        PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;
        if (Target.Platform == UnrealTargetPlatform.IOS)
        {
            PrivatePCHHeaderFile = "../UnrealSharpUtilities/Public/UnrealSharpSharedPCH.h";
        }

        PublicDependencyModuleNames.AddRange(
            new string[]
            {
                "Core", 
            }
        );

        PrivateDependencyModuleNames.AddRange(
            new string[]
            {
                "CoreUObject",
                "Engine",
                "Slate",
                "SlateCore",
                "UnrealSharpBinds",
                "UnrealSharpCore"
            }
        );
        
        PublicDefinitions.Add("ForceAsEngineGlue=1");
    }
}