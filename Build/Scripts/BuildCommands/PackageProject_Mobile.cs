using System.Collections.Generic;
using System.IO;
using UnrealSharp.Automation.Utilities;
using UnrealSharp.Shared;

namespace UnrealSharp.Automation.BuildCommands;

// SDK-hosted runtimes publish managed assemblies independently of the native BCL.
public partial class PackageProject
{
    protected void PublishMobileManagedCode(PackagingOptions options, string publishFolder, string runtimeIdentifier)
    {
        IList<string> Arguments = BuildBaseArguments(runtimeIdentifier, options, publishFolder, selfContained: false);
        string BindingsOutput = publishFolder + ".bindings";
        string OutputArgument = $"-p:BaseOutputPath={BindingsOutput}{Path.DirectorySeparatorChar}";
        string BindingsPath = Path.Combine(this.GetUnrealSharpRootFolder(), ManagedFolderName, BindingsProjectFolder);
        BuildCommands.BuildSolution.RunBuild(
            [Path.Combine(BindingsPath, "UnrealSharp.GlueGenerator", "UnrealSharp.GlueGenerator"),
             Path.Combine(BindingsPath, "UnrealSharp.CodeFixer"),
             Path.Combine(BindingsPath, "UnrealSharp.SourceGenerators")],
            options.BuildConfiguration, publish: false, ["-p:UseDefaultOutputPath=true", OutputArgument], this.GetUnrealSharpRootFolder());
        BuildBindingsSolution([.. Arguments, OutputArgument], options.BuildConfiguration);

        string Analyzers = Path.Combine(BindingsOutput, options.BuildConfiguration.GetDotNetBuildConfiguration(), "netstandard2.0");
        Arguments.Add($"-p:UnrealSharpAnalyzerDirectory={Analyzers}");
        Arguments.Add($"-p:UnrealSharpPublishedReferenceDirectory={publishFolder}");
        BuildUserBindings(publishFolder, options, Arguments);

        string Manifest = Path.Combine(publishFolder, AssemblyUtilities.MakeLoadOrderFileName(LoadOrderUtilities.GlueLoadOrderName));
        if (File.Exists(Manifest))
        {
            string GlueAssemblies = string.Join("|", AssemblyUtilities.ReadLoadOrder(Manifest));
            Arguments.Add($"-p:UnrealSharpPublishedGlueAssemblies={GlueAssemblies}");
        }

        // Shared user projects can be referenced by independent plugin checkouts.
        Arguments.Add("--artifacts-path");
        Arguments.Add(publishFolder + ".user");
        BuildUserSolution(publishFolder, Arguments, options);
        EmitInstalledFlagFile(publishFolder);
    }
}
