namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Config;
using Glacier.Inference.Engine;
using Glacier.Inference.Hardware;
using Glacier.Inference.Memory;
using Glacier.Inference.Sampling;

public static class RunCommandHandler
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        string? modelPath = null;
        string? singlePrompt = null;
        int maxSeqLen = 2048;
        int maxTokens = 512;
        float temperature = 0.7f;
        float topP = 0.9f;
        string? device = null;
        string? engineStr = null;
        string? split = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model" && i + 1 < args.Length)
            {
                modelPath = args[++i];
            }
            else if (arg is "-c" or "--ctx" or "--context-length" && i + 1 < args.Length)
            {
                maxSeqLen = int.Parse(args[++i]);
            }
            else if (arg is "-n" or "--tokens" or "--max-tokens" && i + 1 < args.Length)
            {
                maxTokens = int.Parse(args[++i]);
            }
            else if (arg is "--temp" or "--temperature" && i + 1 < args.Length)
            {
                temperature = float.Parse(args[++i]);
            }
            else if (arg is "--top-p" && i + 1 < args.Length)
            {
                topP = float.Parse(args[++i]);
            }
            else if (arg is "--device" && i + 1 < args.Length)
            {
                device = args[++i];
            }
            else if (arg is "--engine" && i + 1 < args.Length)
            {
                engineStr = args[++i];
            }
            else if (arg is "--split" && i + 1 < args.Length)
            {
                split = args[++i];
            }
            else if (modelPath == null && !arg.StartsWith("-"))
            {
                modelPath = arg;
            }
            else if (singlePrompt == null && !arg.StartsWith("-"))
            {
                singlePrompt = arg;
            }
        }

        if (string.IsNullOrEmpty(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Missing model path. Usage: glacier run <model.gguf> [prompt] [options]");
            Console.ResetColor();
            return 1;
        }

        if (!File.Exists(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Model file not found: {modelPath}");
            Console.ResetColor();
            return 1;
        }

        InferenceEngineType engine = InferenceEngineType.Auto;
        if (!string.IsNullOrEmpty(engineStr) && Enum.TryParse<InferenceEngineType>(engineStr, true, out var parsedEngine))
        {
            engine = parsedEngine;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[Glacier.Inference] Initializing session for: {Path.GetFileName(modelPath)}");
        Console.ResetColor();

        var loadSw = Stopwatch.StartNew();
        using var session = new InferenceSession(modelPath, maxSeqLen, device, engine, KvCachePrecision.Auto, split);
        loadSw.Stop();

        Console.WriteLine($"  ✓ Architecture: {session.Architecture} ({session.Weights.BlockCount} layers, {session.Weights.VocabSize:N0} vocab)");
        Console.WriteLine($"  ✓ Compute Device: {session.ActiveDevice}");
        Console.WriteLine($"  ✓ Cold Load Latency: {loadSw.Elapsed.TotalMilliseconds:F1} ms\n");

        var sampling = new SamplingOptions
        {
            MaxTokens = maxTokens,
            Temperature = temperature,
            TopP = topP
        };

        if (!string.IsNullOrEmpty(singlePrompt))
        {
            await RunSinglePromptAsync(session, singlePrompt, sampling);
            return 0;
        }

        await RunReplAsync(session, sampling);
        return 0;
    }

    private static async Task RunSinglePromptAsync(InferenceSession session, string prompt, SamplingOptions options)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Prompt] {prompt}\n");
        Console.ForegroundColor = ConsoleColor.Green;

        var sw = Stopwatch.StartNew();
        int tokenCount = 0;

        var result = await session.GenerateAsync(
            prompt,
            options,
            formatChat: true,
            onToken: token =>
            {
                Console.Write(token);
                tokenCount++;
            });

        sw.Stop();
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n[Metrics] {tokenCount} tokens generated in {sw.Elapsed.TotalSeconds:F2}s ({result.Metrics.GenerationTokensPerSecond:F1} tok/s) | Prefill: {result.Metrics.PromptEvalDuration.TotalMilliseconds:F1} ms");
        Console.ResetColor();
    }

    private static async Task RunReplAsync(InferenceSession session, SamplingOptions options)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("================================================================================");
        Console.WriteLine("   Interactive Chat REPL (Type 'exit' or 'quit' to end, Ctrl+C to abort)        ");
        Console.WriteLine("================================================================================\n");
        Console.ResetColor();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("glacier> ");
            Console.ResetColor();

            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Exiting session.");
                break;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            var sw = Stopwatch.StartNew();
            int tokenCount = 0;

            var result = await session.GenerateAsync(
                input,
                options,
                formatChat: true,
                onToken: token =>
                {
                    Console.Write(token);
                    tokenCount++;
                });

            sw.Stop();
            Console.ResetColor();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{result.Metrics.GenerationTokensPerSecond:F1} tok/s | {tokenCount} tokens]\n");
            Console.ResetColor();
        }
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier run - Streaming inference and interactive REPL");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier run <model.gguf> [prompt] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -c, --ctx <len>                      Context sequence length (default: 2048)");
        Console.WriteLine("  -n, --tokens <count>                 Max tokens to generate (default: 512)");
        Console.WriteLine("  --temp <float>                       Sampling temperature (default: 0.7)");
        Console.WriteLine("  --top-p <float>                      Nucleus top-p sampling (default: 0.9)");
        Console.WriteLine("  --device <id|name>                   Target GPU/CPU (e.g. nvidia-rtx-4060, cpu)");
        Console.WriteLine("  --engine <baremetal|directml|cpu>    Execution engine");
        Console.WriteLine("  --split <spec>                       Multi-GPU pipeline split");
    }
}
