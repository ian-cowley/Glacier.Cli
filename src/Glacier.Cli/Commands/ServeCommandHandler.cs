namespace Glacier.Cli.Commands;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Serve.Inference.Batching;
using Glacier.Serve.Inference.Http;
using Glacier.Serve.Server;

public static class ServeCommandHandler
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        string? modelPath = null;
        int port = 11434;
        string host = "127.0.0.1";
        int totalBlocks = 1024;
        int maxBatch = 16;
        string? modelName = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model" && i + 1 < args.Length)
            {
                modelPath = args[++i];
            }
            else if (arg is "-p" or "--port" && i + 1 < args.Length)
            {
                port = int.Parse(args[++i]);
            }
            else if (arg is "--host" && i + 1 < args.Length)
            {
                host = args[++i];
            }
            else if (arg is "--blocks" or "--kv-blocks" && i + 1 < args.Length)
            {
                totalBlocks = int.Parse(args[++i]);
            }
            else if (arg is "-b" or "--batch-size" && i + 1 < args.Length)
            {
                maxBatch = int.Parse(args[++i]);
            }
            else if (arg is "--name" or "--model-name" && i + 1 < args.Length)
            {
                modelName = args[++i];
            }
            else if (modelPath == null && !arg.StartsWith("-"))
            {
                modelPath = arg;
            }
        }

        if (string.IsNullOrEmpty(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Missing model path. Usage: glacier serve <model.gguf> [options]");
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

        modelName ??= Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();

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
        Console.WriteLine($"  ✓ Listening Address:  http://{host}:{port}\n");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Active Endpoints:");
        Console.ResetColor();
        Console.WriteLine($"  • POST  http://{host}:{port}/v1/chat/completions (OpenAI SSE streaming)");
        Console.WriteLine($"  • POST  http://{host}:{port}/api/chat            (Ollama NDJSON streaming)");
        Console.WriteLine($"  • POST  http://{host}:{port}/api/generate        (Ollama raw completion)");
        Console.WriteLine($"  • GET   http://{host}:{port}/v1/models           (OpenAI models)");
        Console.WriteLine($"  • GET   http://{host}:{port}/api/tags            (Ollama tags)");
        Console.WriteLine($"  • GET   http://{host}:{port}/health              (Health JSON)");
        Console.WriteLine($"  • GET   http://{host}:{port}/metrics             (Prometheus metrics)\n");

        using var engine = new ContinuousBatchEngine(modelPath, totalKvBlocks: totalBlocks, maxBatchSize: maxBatch);
        using var app = GlacierServeApp.CreateBuilder()
            .UsePort(port)
            .Build();

        app.MapInference(engine, modelName);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down Glacier.Serve...");
            cts.Cancel();
        };

        await app.StartAsync(cts.Token);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Ready] Glacier.Serve listening on http://{host}:{port} (Press Ctrl+C to terminate)");
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
    }
}
