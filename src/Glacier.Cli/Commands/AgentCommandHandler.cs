namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Runtime;
using Glacier.Inference.Engine;
using Glacier.Inference.Hardware;
using Glacier.Inference.Sampling;

public static class AgentCommandHandler
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        string? modelPath = null;
        int maxSteps = 8;
        int maxTokens = 512;
        var promptParts = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model" && i + 1 < args.Length)
            {
                modelPath = args[++i];
            }
            else if (arg is "--max-steps" && i + 1 < args.Length)
            {
                maxSteps = int.Parse(args[++i]);
            }
            else if (arg is "-n" or "--tokens" && i + 1 < args.Length)
            {
                maxTokens = int.Parse(args[++i]);
            }
            else
            {
                promptParts.Add(arg);
            }
        }

        string prompt = string.Join(" ", promptParts).Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Missing agent task prompt. Usage: glacier agent [--model <model.gguf>] <prompt>");
            Console.ResetColor();
            return 1;
        }

        if (!string.IsNullOrEmpty(modelPath) && !File.Exists(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Model file not found: {modelPath}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                 GLACIER.AGENT - Autonomous Agent Runtime                      ║");
        Console.WriteLine("║            High-Speed Actor Engine | In-Memory MCP | SIMD Tokenizer           ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"\n  Task: {prompt}\n");

        var sw = Stopwatch.StartNew();
        using InferenceSession? session = !string.IsNullOrEmpty(modelPath)
            ? new InferenceSession(modelPath, maxSeqLen: 4096)
            : null;

        using AgentExecutionEngine engine = session != null
            ? new AgentExecutionEngine("glacier-cli-agent", new InferenceSessionChatModel(session, maxTokens))
            : new AgentExecutionEngine("glacier-cli-agent");

        RegisterBuiltInTools(engine);

        engine.EventPublished += evt =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] ({evt.SourceAgent}) {evt.EventType}: {evt.Payload}");
            Console.ResetColor();
        };

        string sessionId = Guid.NewGuid().ToString("N")[..8];
        AgentResponse response = session != null
            ? await engine.ExecuteLoopAsync(sessionId, prompt, maxSteps)
            : await engine.ExecuteStepAsync(new AgentContext(sessionId, prompt, StepCount: 1));

        sw.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[Agent Output] {response.Output}");
        Console.WriteLine($"✓ Completed in {sw.Elapsed.TotalMilliseconds:F1} ms (IsCompleted: {response.IsCompleted})");
        Console.ResetColor();

        return 0;
    }

    public static void RegisterBuiltInTools(AgentExecutionEngine engine)
    {
        engine.RegisterTool(DelegateAgentTool.Create(
            name: "ping",
            description: "Health check tool returning runtime status.",
            parameterJsonSchema: "{\"type\":\"object\",\"properties\":{}}",
            handler: _ => "{\"status\":\"pong\",\"runtime\":\"Glacier.Agent\"}"));

        engine.RegisterTool(DelegateAgentTool.Create(
            name: "clock",
            description: "Returns the current UTC timestamp in ISO-8601 format.",
            parameterJsonSchema: "{\"type\":\"object\",\"properties\":{}}",
            handler: _ => $"{{\"utc\":\"{DateTimeOffset.UtcNow:O}\"}}"));

        engine.RegisterTool(DelegateAgentTool.Create(
            name: "devices",
            description: "Enumerates available CPU/GPU compute accelerators detected by Glacier.",
            parameterJsonSchema: "{\"type\":\"object\",\"properties\":{}}",
            handler: _ =>
            {
                var devices = DeviceManager.GetDevices();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var d in devices)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", d.Id);
                        writer.WriteString("name", d.Name);
                        writer.WriteString("vendor", d.Vendor.ToString());
                        writer.WriteNumber("dedicatedVramGb", d.DedicatedVramGb);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }));
    }

    private sealed class InferenceSessionChatModel(InferenceSession session, int maxTokens) : IAgentChatModel
    {
        public async ValueTask<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            string combined = $"{systemPrompt}\n\nUser: {userPrompt}\nAssistant:";
            var sampling = new SamplingOptions { MaxTokens = maxTokens, Temperature = 0.1f };
            var result = await session.GenerateAsync(combined, sampling, formatChat: true, ct: ct);
            return result.Text;
        }
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier agent - High-speed pure C# autonomous agent execution");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier agent [options] <prompt / command>");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -m, --model <model.gguf>             GGUF model for multi-step LLM tool planning");
        Console.WriteLine("  --max-steps <int>                    Maximum reasoning/tool steps (default: 8)");
        Console.WriteLine("  -n, --tokens <int>                   Max tokens per LLM step (default: 512)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  glacier agent --model model.gguf \"Inspect available GPU accelerators\"");
        Console.WriteLine("  glacier agent \"CALL ping: {}\"");
        Console.WriteLine("  glacier agent \"CALL devices: {}\"");
    }
}
