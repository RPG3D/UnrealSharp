using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using EpicGames.UHT.Tables;
using EpicGames.UHT.Utils;
using UnrealSharpManagedGlue;
using UnrealSharpManagedGlue.Utilities;

[UnrealHeaderTool]
public static class Program
{
    static Program()
    {
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string? baseDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            
            if (baseDirectory == null)
            {
                return null;
            }
            
            string assemblyPath = Path.Combine(baseDirectory, $"{assemblyName.Name}.dll");

            Assembly newAssembly;
            using FileStream assemblyFile = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        
            if (File.Exists(pdbPath))
            {
                using FileStream pdbFile = File.Open(pdbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                newAssembly = AssemblyLoadContext.Default.LoadFromStream(assemblyFile, pdbFile);
            }
            else
            {
                newAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            }
            
            return newAssembly;
        };
    }
    
    [UhtExporter(Name = "UnrealSharpCore", Description = "Exports C++ to C# code", Options = UhtExporterOptions.Default, ModuleName = "UnrealSharpCore")]
    private static void Main(IUhtExportFactory factory)
    {
        string logDirectory = Path.Combine(factory.Session.ProjectDirectory ?? ".", "Saved", "Logs");
        Directory.CreateDirectory(logDirectory);
        string traceFile = Path.Combine(logDirectory, "unrealsharp-glue.log");

        void Trace(string message)
        {
            File.AppendAllText(traceFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }

        try
        {
            Trace("Initialize");
            ConsoleUtilities.Log("Initializing UnrealSharp C# bindings generator...");

            GeneratorStatics.Initialize(factory);
            Trace("ExportBindings");
            ExportBindings();

            Trace("CleanOldExportedFiles");
            FileExporter.CleanOldExportedFiles();
            Trace("BuildBindings");
            BuildUtilities.BuildBindings();

            Trace("SyncModuleProjects");
            ModuleFactory.SyncModuleProjects();
            Trace("GenerateUserSolution");
            BuildUtilities.GenerateUserSolution();
            Trace("Done");
        }
        catch (Exception ex)
        {
            Trace("EXCEPTION: " + ex);
            ConsoleUtilities.Log("Critical failure during glue generation:");
            ConsoleUtilities.Log(ex.ToString());
            throw;
        }
    }

    private static void ExportBindings()
    {
        ConsoleUtilities.Log("Starting C# bindings export...");

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            
            GlueGenerator.GenerateBindings();
            TaskManager.WaitForTasks();

            stopwatch.Stop();
            
            string timeString = stopwatch.Elapsed.TotalSeconds < 1 ? $"{stopwatch.Elapsed.TotalMilliseconds:F0}ms" : $"{stopwatch.Elapsed.TotalSeconds:F2}s";
            ConsoleUtilities.Log($"Finished exporting C# bindings in {timeString}");
        }
        catch (Exception ex)
        {
            ConsoleUtilities.Log("Critical failure during export process:");
            ConsoleUtilities.Log(ex.ToString());
        }
    }
}