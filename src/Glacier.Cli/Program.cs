namespace Glacier.Cli;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Glacier.Cli.Commands;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?" or "-?")
        {
            if (args.Length > 1 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                PrintCommandHelp(args[1]);
                return 0;
            }
            PrintBanner();
            PrintHelp();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] cmdArgs = args.Length > 1 ? args[1..] : [];

        if (HasHelpFlag(cmdArgs))
        {
            PrintCommandHelp(command);
            return 0;
        }

        try
        {
            return command switch
            {
                "run" => await RunCommandHandler.ExecuteAsync(cmdArgs),
                "tune" => await TuneCommandHandler.ExecuteAsync(cmdArgs),
                "merge" => MergeCommandHandler.Execute(cmdArgs),
                "rag" => await RagCommandHandler.ExecuteAsync(cmdArgs),
                "serve" => await ServeCommandHandler.ExecuteAsync(cmdArgs),
                "pull" => await PullCommandHandler.ExecuteAsync(cmdArgs),
                "inspect" => DiagnosticsCommandHandlers.RunInspect(cmdArgs),
                "devices" => DiagnosticsCommandHandlers.RunDevices(cmdArgs),
                "bench" => await DiagnosticsCommandHandlers.RunBenchAsync(cmdArgs),
                "config" => DiagnosticsCommandHandlers.RunConfig(cmdArgs),
                _ => HandleUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[Glacier Error] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static bool HasHelpFlag(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "-h" or "--help" or "help" or "/?" or "-?")
                return true;
        }
        return false;
    }

    public static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                 GLACIER - Unified Pure C# .NET 10 LLM Stack                   ║");
        Console.WriteLine("║   <15ms Cold Start | Zero Python/C++ | SIMD AVX-512 | PagedAttention | RAG    ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Usage: glacier <command> [arguments] [options]");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Core Execution Commands:");
        Console.ResetColor();
        Console.WriteLine("  run     <model.gguf> [prompt]        Execute streaming inference or interactive REPL");
        Console.WriteLine("  tune    <base.gguf> --data <data>    Fine-tune model in minutes with in-process LoRA");
        Console.WriteLine("  merge   <base> <adapter> <out>       Fuse LoRA delta adapter directly into base GGUF");
        Console.WriteLine("  rag     --model <gguf> --docs <dir>  In-process SIMD Vector + CSR GraphRAG query");
        Console.WriteLine("  serve   <model.gguf> [options]       Start drop-in Ollama/OpenAI PagedAttention server");
        Console.WriteLine("  pull    <repo-or-url> [options]      High-speed chunked GGUF download with live progress");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Diagnostics & Benchmarks:");
        Console.ResetColor();
        Console.WriteLine("  devices                              Detect and audit GPUs, VRAM, and SIMD hardware");
        Console.WriteLine("  inspect <model.gguf>                 Display GGUF architecture, metadata & tensors");
        Console.WriteLine("  bench   <model.gguf> [options]       Measure cold start, prefill tok/s & decode tok/s");
        Console.WriteLine("  config  [options]                    Manage persistent acceleration preferences");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Run 'glacier <command> --help' or 'glacier help <command>' for command-specific options.");
        Console.ResetColor();
    }

    private static void PrintCommandHelp(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "run":
                RunCommandHandler.PrintHelp();
                break;
            case "tune":
                TuneCommandHandler.PrintHelp();
                break;
            case "merge":
                MergeCommandHandler.PrintHelp();
                break;
            case "rag":
                RagCommandHandler.PrintHelp();
                break;
            case "serve":
                ServeCommandHandler.PrintHelp();
                break;
            case "pull":
                PullCommandHandler.PrintHelp();
                break;
            case "devices":
                DiagnosticsCommandHandlers.PrintDevicesHelp();
                break;
            case "inspect":
                DiagnosticsCommandHandlers.PrintInspectHelp();
                break;
            case "bench":
                DiagnosticsCommandHandlers.PrintBenchHelp();
                break;
            case "config":
                DiagnosticsCommandHandlers.PrintConfigHelp();
                break;
            default:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Unknown command: '{command}'\n");
                Console.ResetColor();
                PrintHelp();
                break;
        }
    }

    private static int HandleUnknownCommand(string command)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown command: '{command}'");
        Console.ResetColor();
        Console.WriteLine("Run 'glacier --help' to view all available commands.");
        return 1;
    }
}
