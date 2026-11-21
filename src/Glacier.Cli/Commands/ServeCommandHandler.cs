namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Engine;
using Glacier.Inference.Memory;
using Glacier.Inference.Sampling;
using Glacier.Serve.Daemons;
using Glacier.Serve.Inference.Batching;
using Glacier.Serve.Inference.Bounce;
using Glacier.Serve.Inference.Http;
using Glacier.Serve.Server;

public static class ServeCommandHandler
{
    public sealed record ServeCliOptions(
        string? ModelPath,
        string Host,
        int Port,
        int TotalBlocks,
        int MaxBatch,
        string? ModelName,
        int SleepIdleSeconds = 300,
        int SleepMinDreams = 1,
        string? SleepCheckpointPath = null,
        KvCachePrecision KvPrecision = KvCachePrecision.Auto,
        IReadOnlyList<LoraAdapterOption>? LoraAdapters = null,
        int SleepBudget = 400,
        bool EnablePeerBounce = true,
        string? BouncePrimaryEndpoint = null,
        string? BounceCriticEndpoint = null,
        string? BounceHarnessPath = null,
        int BounceMaxRepairs = 2,
        bool CompilerBounceEnabled = true,
        int MaxThinkTokens = ReasoningBudgetPolicy.AutoBudgetSentinel);

    public static ServeCliOptions ParseOptions(string[] args)
    {
        string? modelPath = null;
        int port = 11434;
        string host = "127.0.0.1";
        int totalBlocks = 1024;
        int maxBatch = 16;
        string? modelName = null;
        int sleepIdleSeconds = 300;
        int sleepMinDreams = 1;
        string? sleepCheckpointPath = null;
        KvCachePrecision kvPrecision = KvCachePrecision.Auto;
        var loraAdapters = new List<LoraAdapterOption>();
        int sleepBudget = 400;
        bool enablePeerBounce = true;
        string? bouncePrimaryEndpoint = null;
        string? bounceCriticEndpoint = null;
        string? bounceHarnessPath = null;
        int bounceMaxRepairs = 2;
        bool compilerBounceEnabled = true;
        int maxThinkTokens = ReasoningBudgetPolicy.AutoBudgetSentinel;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model")
            {
                modelPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "-p" or "--port")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out port) || port < 1 || port > 65535)
                    throw new ArgumentException($"Invalid --port '{val}'. Expected 1..65535.");
            }
            else if (arg is "--host")
            {
                host = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--blocks" or "--kv-blocks")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out totalBlocks) || totalBlocks <= 0)
                    throw new ArgumentException($"Invalid {arg} '{val}'. Expected a positive integer.");
            }
            else if (arg is "-b" or "--batch-size")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out maxBatch) || maxBatch <= 0)
                    throw new ArgumentException($"Invalid {arg} '{val}'. Expected a positive integer.");
            }
            else if (arg is "--name" or "--model-name")
            {
                modelName = CliOptionHelpers.RequireValue(args, ref i, arg);
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
            else if (arg is "--sleep-memory")
            {
                if (sleepIdleSeconds <= 0)
                    sleepIdleSeconds = 300;
            }
            else if (arg is "--no-sleep-memory")
            {
                sleepIdleSeconds = 0;
            }
            else if (arg is "--sleep-budget")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out sleepBudget) || sleepBudget <= 0)
                    throw new ArgumentException($"Invalid --sleep-budget '{val}'. Expected a positive integer.");
                if (sleepIdleSeconds <= 0)
                    sleepIdleSeconds = 300;
            }
            else if (arg.StartsWith("--sleep-budget=", StringComparison.Ordinal))
            {
                string val = arg["--sleep-budget=".Length..];
                if (!int.TryParse(val, out sleepBudget) || sleepBudget <= 0)
                    throw new ArgumentException($"Invalid --sleep-budget '{val}'. Expected a positive integer.");
                if (sleepIdleSeconds <= 0)
                    sleepIdleSeconds = 300;
            }
            else if (arg is "--sleep-idle-seconds")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out sleepIdleSeconds) || sleepIdleSeconds < 0)
                    throw new ArgumentException($"Invalid --sleep-idle-seconds '{val}'. Expected 0 or a positive integer.");
            }
            else if (arg is "--sleep-min-dreams")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out sleepMinDreams) || sleepMinDreams <= 0)
                    throw new ArgumentException($"Invalid --sleep-min-dreams '{val}'. Expected a positive integer.");
            }
            else if (arg.StartsWith("--sleep-min-dreams=", StringComparison.Ordinal))
            {
                string val = arg["--sleep-min-dreams=".Length..];
                if (!int.TryParse(val, out sleepMinDreams) || sleepMinDreams <= 0)
                    throw new ArgumentException($"Invalid --sleep-min-dreams '{val}'. Expected a positive integer.");
            }
            else if (arg is "--sleep-checkpoint")
            {
                sleepCheckpointPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg.StartsWith("--sleep-checkpoint=", StringComparison.Ordinal))
            {
                sleepCheckpointPath = arg["--sleep-checkpoint=".Length..];
            }
            else if (arg is "--bounce")
            {
                enablePeerBounce = true;
            }
            else if (arg is "--no-bounce")
            {
                enablePeerBounce = false;
            }
            else if (arg is "--bounce-primary")
            {
                bouncePrimaryEndpoint = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--critic" or "--peer-critic")
            {
                bounceCriticEndpoint = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--bounce-harness" or "--harness")
            {
                bounceHarnessPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--bounce-max-repairs" or "--max-repairs")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(val, out bounceMaxRepairs) || bounceMaxRepairs < 0)
                    throw new ArgumentException($"Invalid {arg} '{val}'. Expected a non-negative integer.");
            }
            else if (arg is "--compiler-bounce")
            {
                compilerBounceEnabled = true;
            }
            else if (arg is "--no-compiler-bounce")
            {
                compilerBounceEnabled = false;
            }
            else if (arg is "--max-think-tokens")
            {
                string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!TryParseCliThinkBudget(val, out maxThinkTokens))
                    throw new ArgumentException("Invalid --max-think-tokens. Expected 'auto', 'off', '-1', '0', or a positive integer.");
            }
            else if (arg.StartsWith("--max-think-tokens=", StringComparison.Ordinal))
            {
                string val = arg["--max-think-tokens=".Length..];
                if (!TryParseCliThinkBudget(val, out maxThinkTokens))
                    throw new ArgumentException("Invalid --max-think-tokens. Expected 'auto', 'off', '-1', '0', or a positive integer.");
            }
            else if (modelPath == null && !arg.StartsWith('-'))
            {
                modelPath = arg;
            }
            else
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return new ServeCliOptions(
            modelPath,
            host,
            port,
            totalBlocks,
            maxBatch,
            modelName,
            sleepIdleSeconds,
            sleepMinDreams,
            sleepCheckpointPath,
            kvPrecision,
            loraAdapters.Count > 0 ? loraAdapters : null,
            sleepBudget,
            enablePeerBounce,
            bouncePrimaryEndpoint,
            bounceCriticEndpoint,
            bounceHarnessPath,
            bounceMaxRepairs,
            compilerBounceEnabled,
            maxThinkTokens);
    }

    private static bool TryParseCliThinkBudget(string? raw, out int parsed)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = 0;
            return false;
        }
        if (int.TryParse(raw.Trim(), out int intVal) && intVal == ReasoningBudgetPolicy.AutoBudgetSentinel)
        {
            parsed = ReasoningBudgetPolicy.AutoBudgetSentinel;
            return true;
        }
        return ReasoningBudgetPolicy.TryParseSetting(raw, out parsed);
    }

    public static SleepMemoryService CreateSleepMemoryService(ServeCliOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        return new SleepMemoryService(
            tokenBudget: opts.SleepBudget,
            idleThreshold: TimeSpan.FromSeconds(opts.SleepIdleSeconds),
            pollingInterval: TimeSpan.FromSeconds(1),
            minDreamsToTriggerSleep: opts.SleepMinDreams);
    }

    public static void RegisterEndpoints(
        GlacierServeApp app,
        ContinuousBatchEngine engine,
        string modelName,
        SleepMemoryService? sleepMemory = null,
        PeerBounceService? bounceService = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(engine);

        app.MapInference(engine, modelName, sleepMemory);
        if (bounceService != null)
        {
            app.MapPeerBounce(bounceService, mapOpenAiAndHealthRoutes: false, modelAlias: modelName);
        }
    }

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        ServeCliOptions opts;
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
        string host = opts.Host;
        int port = opts.Port;
        int totalBlocks = opts.TotalBlocks;
        int maxBatch = opts.MaxBatch;

        if (string.IsNullOrEmpty(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Missing model path. Usage: glacier serve <model.gguf> [options]");
            Console.ResetColor();
            return 1;
        }

        var loras = opts.LoraAdapters ?? [];
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lora in loras)
        {
            if (!seenNames.Add(lora.Name))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: Duplicate --lora adapter name '{lora.Name}'. Use '<name>=<path>' to assign distinct names.");
                Console.ResetColor();
                return 1;
            }

            string resolvedLoraPath = CliOptionHelpers.ResolveLoraFilePath(lora.Path);
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

        string modelName = opts.ModelName ?? Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            GLACIER.SERVE - PagedAttention Continuous Batching Server          ║");
        Console.WriteLine("║                 Drop-In Replacement for Ollama & OpenAI APIs                  ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"  ✓ Model:              {modelPath}");
        Console.WriteLine($"  ✓ Model Alias:        {modelName}");
        Console.WriteLine($"  ✓ PagedAttention:     {totalBlocks:N0} blocks ({totalBlocks * 16:N0} max tokens capacity)");
        Console.WriteLine($"  ✓ Continuous Batch:   Max {maxBatch} parallel concurrent sequences");
        Console.WriteLine($"  ✓ KV Precision:       {opts.KvPrecision.ToDisplayName()}");
        if (loras.Count > 0)
            Console.WriteLine($"  ✓ LoRA Adapters:      {string.Join(", ", loras.Select(l => l.Name))}");
        Console.WriteLine($"  ✓ Listening Address:  http://{host}:{port}\n");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Active Endpoints:");
        Console.ResetColor();
        Console.WriteLine($"  • POST  http://{host}:{port}/v1/chat/completions (OpenAI SSE streaming)");
        Console.WriteLine($"  • POST  http://{host}:{port}/v1/completions      (OpenAI text completions)");
        Console.WriteLine($"  • POST  http://{host}:{port}/api/chat            (Ollama NDJSON streaming)");
        Console.WriteLine($"  • POST  http://{host}:{port}/api/generate        (Ollama raw completion)");
        Console.WriteLine($"  • GET   http://{host}:{port}/v1/models           (OpenAI models & LoRA adapters)");
        Console.WriteLine($"  • GET   http://{host}:{port}/api/tags            (Ollama tags)");
        Console.WriteLine($"  • GET   http://{host}:{port}/health              (Health JSON)");
        Console.WriteLine($"  • GET   http://{host}:{port}/metrics             (Prometheus metrics)");
        if (opts.SleepIdleSeconds > 0)
        {
            Console.WriteLine($"  • GET   http://{host}:{port}/api/sleep/status    (HOPE+MemEx Sleep Memory status)");
            Console.WriteLine($"  • POST  http://{host}:{port}/api/sleep/trigger   (Trigger Sleep consolidation)");
            Console.WriteLine($"  • GET   http://{host}:{port}/v1/sleep/status     (Sleep Memory daemon state)");
            Console.WriteLine($"  • POST  http://{host}:{port}/v1/sleep/trigger    (Trigger Sleep daemon)");
        }
        if (opts.EnablePeerBounce)
        {
            Console.WriteLine($"  • GET   http://{host}:{port}/api/bounce/status   (PeerBounce verifier telemetry)");
            Console.WriteLine($"  • POST  http://{host}:{port}/api/bounce/chat/completions (Sandboxed PeerBounce route)");
        }
        Console.WriteLine();

        await using var sleepMemory = opts.SleepIdleSeconds > 0
            ? CreateSleepMemoryService(opts)
            : null;

        using var engine = new ContinuousBatchEngine(
            modelPath,
            totalKvBlocks: totalBlocks,
            maxBatchSize: maxBatch,
            defaultMaxReasoningTokens: opts.MaxThinkTokens,
            kvCachePrecision: opts.KvPrecision);

        foreach (var lora in loras)
        {
            engine.LoadLoraAdapter(lora.Name, lora.Path);
        }

        if (sleepMemory != null)
        {
            if (!string.IsNullOrWhiteSpace(opts.SleepCheckpointPath))
            {
                string ckptPath = opts.SleepCheckpointPath!;
                sleepMemory.Daemon.ConsolidationCompleted += _ => sleepMemory.ExpansionBank.Save(ckptPath);
            }
            engine.AttachSleepMemory(sleepMemory, attachDaemonAndExpansion: true);
            Console.WriteLine($"  ✓ Sleep Memory:       Enabled (budget={opts.SleepBudget}, idle={opts.SleepIdleSeconds}s{(string.IsNullOrWhiteSpace(opts.SleepCheckpointPath) ? "" : $", checkpoint={opts.SleepCheckpointPath}")})\n");
        }

        using var bounceHttp = opts.EnablePeerBounce ? new HttpClient { Timeout = TimeSpan.FromSeconds(600) } : null;
        PeerBounceService? bounceService = null;
        if (opts.EnablePeerBounce)
        {
            string loopbackHost = host is "0.0.0.0" or "::" or "*" ? "127.0.0.1" : host;
            string primaryEndpoint = string.IsNullOrWhiteSpace(opts.BouncePrimaryEndpoint)
                ? $"http://{loopbackHost}:{port}"
                : opts.BouncePrimaryEndpoint!;
            string? bounceHarnessPath = string.IsNullOrWhiteSpace(opts.BounceHarnessPath)
                ? SandboxedProcessBounceVerifier.ResolveDefaultHarnessPath()
                : opts.BounceHarnessPath;
            var verifier = new SandboxedProcessBounceVerifier(bounceHarnessPath);
            bounceService = new PeerBounceService(
                bounceHttp!,
                verifier,
                primaryEndpoint,
                opts.BounceCriticEndpoint,
                compilerBounceEnabled: opts.CompilerBounceEnabled,
                maxRepairs: opts.BounceMaxRepairs);
        }

        using var app = GlacierServeApp.CreateBuilder()
            .UseHost(host)
            .UsePort(port)
            .Build();

        RegisterEndpoints(app, engine, modelName, sleepMemory, bounceService);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down Glacier.Serve...");
            cts.Cancel();
        };

        await app.StartAsync(cts.Token);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Ready] Glacier.Serve listening on http://{host}:{port} (Backend={engine.ActiveBackend}, KV={engine.KvPrecision.ToDisplayName()}) (Press Ctrl+C to terminate)");
        Console.ResetColor();

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
        }

        Console.WriteLine("Server stopped cleanly.");
        return 0;
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier serve - High-concurrency PagedAttention HTTP inference server");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier serve <model.gguf> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -p, --port <int>                     Port to listen on (default: 11434)");
        Console.WriteLine("  --host <string>                      Host address (default: 127.0.0.1)");
        Console.WriteLine("  --blocks <int>                       PagedAttention KV blocks (default: 1024)");
        Console.WriteLine("  -b, --batch-size <int>               Max concurrent sequences (default: 16)");
        Console.WriteLine("  --name <string>                      Model tag alias (default: filename without extension)");
        Console.WriteLine("  --kv-precision <auto|fp16|fp8|q8_0|q4_0|fp32> KV-cache precision (default: auto)");
        Console.WriteLine("  --lora <[name=]path>                 Hot-load 0x41524F4C LoRA adapter (repeatable)");
        Console.WriteLine("  --max-think-tokens <auto|off|0|N>    Default reasoning <think> token budget (default: auto)");
        Console.WriteLine("  --sleep-memory / --no-sleep-memory   Enable/disable HOPE+MemEx sleep consolidation (default: on)");
        Console.WriteLine("  --sleep-budget <tokens>              Sleep memory context budget in tokens (default: 400)");
        Console.WriteLine("  --sleep-idle-seconds <int>           Idle seconds before Sleep consolidation (default: 300, 0=off)");
        Console.WriteLine("  --sleep-min-dreams <int>             Minimum dreams before auto-sleep triggers (default: 1)");
        Console.WriteLine("  --sleep-checkpoint <path>            Persist Sleep MoE expert weights on consolidation");
        Console.WriteLine("  --bounce / --no-bounce               Enable/disable co-hosted PeerBounce routes (default: on)");
        Console.WriteLine("  --bounce-primary <url>               Override PeerBounce primary endpoint (default: loopback)");
        Console.WriteLine("  --critic, --peer-critic <url>        Optional PeerBounce critic endpoint");
        Console.WriteLine("  --bounce-harness, --harness <path>   Optional EvaluationHarness executable for PeerBounce");
        Console.WriteLine("  --bounce-max-repairs <int>           Max PeerBounce repair passes (0-3, default: 2)");
        Console.WriteLine("  --compiler-bounce / --no-compiler-bounce Enable/disable sandboxed compiler verifier");
    }
}
