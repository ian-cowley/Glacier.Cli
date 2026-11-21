namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    public sealed record RunCliOptions(
        string? ModelPath,
        string? SinglePrompt,
        int MaxSeqLen = 2048,
        int MaxTokens = 512,
        float Temperature = 0.7f,
        float TopP = 0.9f,
        string? Device = null,
        string? EngineStr = null,
        string? Split = null,
        KvCachePrecision KvPrecision = KvCachePrecision.Auto,
        int MaxThinkTokens = ReasoningBudgetPolicy.AutoBudgetSentinel,
        IReadOnlyList<LoraAdapterOption>? LoraAdapters = null);

    public static RunCliOptions ParseOptions(string[] args)
    {
        string? modelPath = null;
        string? singlePrompt = null;
        int maxSeqLen = 2048;
        int maxTokens = 512;
        float temperature = 0.7f;
        float topP = 0.9f;
        string? device = null;
        string? engineStr = null;
        string? split = null;
        KvCachePrecision kvPrecision = KvCachePrecision.Auto;
        int maxThinkTokens = ReasoningBudgetPolicy.AutoBudgetSentinel;
        var loraAdapters = new List<LoraAdapterOption>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model")
            {
                modelPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "-c" or "--ctx" or "--context-length")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out maxSeqLen) || maxSeqLen <= 0)
                    throw new ArgumentException($"Invalid {arg} '{val}'. Expected a positive integer.");
            }
            else if (arg is "-n" or "--tokens" or "--max-tokens")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out maxTokens) || maxTokens <= 0)
                    throw new ArgumentException($"Invalid {arg} '{val}'. Expected a positive integer.");
            }
            else if (arg is "--temp" or "--temperature")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature))
                    throw new ArgumentException($"Invalid {arg} '{val}'.");
            }
            else if (arg is "--top-p")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out topP))
                    throw new ArgumentException($"Invalid --top-p '{val}'.");
            }
            else if (arg is "-d" or "--device")
            {
                device = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--engine")
            {
                engineStr = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--split")
            {
                split = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--kv-precision")
            {
                kvPrecision = CliOptionHelpers.ParseKvPrecision(CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg.StartsWith("--kv-precision=", StringComparison.Ordinal))
            {
                kvPrecision = CliOptionHelpers.ParseKvPrecision(arg["--kv-precision=".Length..]);
            }
            else if (arg is "--lora")
            {
                loraAdapters.Add(CliOptionHelpers.ParseLoraSpec(CliOptionHelpers.RequireValue(args, ref i, arg)));
            }
            else if (arg.StartsWith("--lora=", StringComparison.Ordinal))
            {
                loraAdapters.Add(CliOptionHelpers.ParseLoraSpec(arg["--lora=".Length..]));
            }
            else if (arg is "--max-think-tokens")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!ReasoningBudgetPolicy.TryParseSetting(val, out maxThinkTokens))
                    throw new ArgumentException("--max-think-tokens must be 'auto', 'off', '0', or a positive integer.");
            }
            else if (modelPath == null && !arg.StartsWith('-'))
            {
                modelPath = arg;
            }
            else if (singlePrompt == null && !arg.StartsWith('-'))
            {
                singlePrompt = arg;
            }
            else if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return new RunCliOptions(
            modelPath,
            singlePrompt,
            maxSeqLen,
            maxTokens,
            temperature,
            topP,
            device,
            engineStr,
            split,
            kvPrecision,
            maxThinkTokens,
            loraAdapters.Count > 0 ? loraAdapters : null);
    }

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        RunCliOptions opts;
        try
        {
            opts = ParseOptions(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        string? modelPath = opts.ModelPath;
        if (string.IsNullOrEmpty(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Missing model path. Usage: glacier run <model.gguf> [prompt] [options]");
            Console.ResetColor();
            return 1;
        }

        var loras = opts.LoraAdapters ?? [];
        if (loras.Count > 1)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Error: 'glacier run' supports attaching a single --lora adapter at a time; use 'glacier serve' to host multiple named adapters.");
            Console.ResetColor();
            return 1;
        }

        string? resolvedLoraPath = null;
        string? loraName = null;
        if (loras.Count == 1)
        {
            loraName = loras[0].Name;
            resolvedLoraPath = CliOptionHelpers.ResolveLoraFilePath(loras[0].Path);
            if (!File.Exists(resolvedLoraPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: LoRA adapter file not found: '{resolvedLoraPath}'.");
                Console.ResetColor();
                return 1;
            }
        }

        if (!File.Exists(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Model file not found: {modelPath}");
            Console.ResetColor();
            return 1;
        }

        InferenceEngineType engine = InferenceEngineType.Auto;
        if (!string.IsNullOrEmpty(opts.EngineStr))
        {
            if (!Enum.TryParse<InferenceEngineType>(opts.EngineStr, true, out var parsedEngine))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: Unknown --engine '{opts.EngineStr}'. Valid values: auto, cpu, baremetal, vulkan, directml.");
                Console.ResetColor();
                return 1;
            }
            engine = parsedEngine;
        }

        var (device, resolvedEngine) = ResolveLoraTarget(resolvedLoraPath != null, opts.Device, engine);
        engine = resolvedEngine;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[Glacier.Inference] Initializing session for: {Path.GetFileName(modelPath)}");
        Console.ResetColor();

        var loadSw = Stopwatch.StartNew();
        using var session = new InferenceSession(modelPath, opts.MaxSeqLen, device, engine, opts.KvPrecision, opts.Split);
        loadSw.Stop();

        if (resolvedLoraPath != null)
        {
            session.AttachLora(resolvedLoraPath);
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"  ✓ LoRA Adapter: {loraName} ({Path.GetFileName(resolvedLoraPath)}, Rank={session.CpuModel?.LoraWeights?.Rank}, Alpha={session.CpuModel?.LoraWeights?.Alpha})");
            Console.ResetColor();
        }

        Console.WriteLine($"  ✓ Architecture: {session.Architecture} ({session.Weights.BlockCount} layers, {session.Weights.VocabSize:N0} vocab)");
        Console.WriteLine($"  ✓ Compute Device: {session.ActiveDevice}");
        Console.WriteLine($"  ✓ KV Precision: {session.KvPrecision.ToDisplayName()}");
        Console.WriteLine($"  ✓ Cold Load Latency: {loadSw.Elapsed.TotalMilliseconds:F1} ms\n");

        var sampling = new SamplingOptions
        {
            MaxTokens = opts.MaxTokens,
            Temperature = opts.Temperature,
            TopP = opts.TopP,
            MaxReasoningTokens = ReasoningBudgetPolicy.ResolveBudget(opts.MaxThinkTokens, opts.MaxTokens)
        };

        if (!string.IsNullOrEmpty(opts.SinglePrompt))
        {
            await RunSinglePromptAsync(session, opts.SinglePrompt, sampling);
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

    public static (string? Device, InferenceEngineType Engine) ResolveLoraTarget(
        bool hasLora,
        string? device,
        InferenceEngineType engine)
    {
        if (hasLora &&
            (string.IsNullOrWhiteSpace(device) || string.Equals(device.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) &&
            engine == InferenceEngineType.Auto)
        {
            return ("cpu", InferenceEngineType.Cpu);
        }

        return (device, engine);
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
        Console.WriteLine("  --kv-precision <auto|fp16|fp8|q8_0|q4_0|fp32> KV-cache precision (default: auto)");
        Console.WriteLine("  --lora <[name=]path>                 Attach 0x41524F4C LoRA adapter binary/directory (CPU)");
        Console.WriteLine("  --max-think-tokens <auto|off|0|N>    Cap <think> tokens & auto-close (default: auto)");
        Console.WriteLine("  --split <spec>                       Multi-GPU pipeline split");
    }
}
