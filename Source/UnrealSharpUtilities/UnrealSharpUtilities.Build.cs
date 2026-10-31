using UnrealBuildTool;

public class UnrealSharpUtilities : ModuleRules
{
    public UnrealSharpUtilities(ReadOnlyTargetRules Target) : base(Target)
    {
        // IWYU stays on everywhere else: see UnrealSharpCore.Build.cs.
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
        if (Target.Platform == UnrealTargetPlatform.IOS)
        {
            PrivatePCHHeaderFile = "Public/UnrealSharpSharedPCH.h";
        }

        PublicDependencyModuleNames.AddRange(
            new string[]
            {
                "Core",
                "Json",
                "Projects",
                // External ThirdParty module (Source/ThirdParty/DotNetRuntimeSDK/): defines
                // UNREALSHARP_MONO=1/0 (read from the layered DotNetRuntime ini keys) and
                // links the selected runtime (Mono or CoreCLR). Public dependency so the
                // define propagates through UnrealSharpCore -> UnrealSharpEditor.
                "DotNetRuntimeSDK",
            }
        );

        PrivateDependencyModuleNames.AddRange(
            new string[]
            {
                "CoreUObject",
                "Engine",
                "Slate",
                "SlateCore",
                "DeveloperSettings",
                "Projects", 
            }
        );

        if (Target.bBuildEditor)
        {
            PublicDependencyModuleNames.AddRange(
                new string[]
                {
                    "UATHelper",
                    "UnrealEd",
                }
            );
        }
        
        PublicDefinitions.Add("ForceAsEngineGlue=1");
    }
}