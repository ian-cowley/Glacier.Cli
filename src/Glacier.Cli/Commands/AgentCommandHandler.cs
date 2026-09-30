namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Runtime;

public static class AgentCommandHandler
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        string prompt = string.Join(" ", args);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                 GLACIER.AGENT - Autonomous Agent Runtime                      ║");
        Console.WriteLine("║            High-Speed Actor Engine | In-Memory MCP | SIMD Tokenizer           ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"\n  Task: {prompt}\n");

        var sw = Stopwatch.StartNew();
        using var engine = new AgentExecutionEngine("glacier-cli-agent");
        engine.EventPublished += evt =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] ({evt.SourceAgent}) {evt.EventType}: {evt.Payload}");
            Console.ResetColor();
        };

        var context = new AgentContext(
            SessionId: Guid.NewGuid().ToString("N")[..8],
            Prompt: prompt,
            StepCount: 1);

        var response = await engine.ExecuteStepAsync(context);
        sw.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[Agent Output] {response.Output}");
        Console.WriteLine($"✓ Completed in {sw.Elapsed.TotalMilliseconds:F1} ms (IsCompleted: {response.IsCompleted})");
        Console.ResetColor();

        return 0;
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier agent - High-speed pure C# autonomous agent execution");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier agent <prompt / command>");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  glacier agent \"Analyze data performance across all nodes\"");
        Console.WriteLine("  glacier agent \"CALL ping: {}\"");
    }
}
