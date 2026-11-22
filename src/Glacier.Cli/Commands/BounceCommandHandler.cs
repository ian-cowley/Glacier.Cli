namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Hardware;
using Glacier.Serve.Inference.Bounce;
using Glacier.Serve.Inference.Http;
using Glacier.Serve.Server;

public static class BounceCommandHandler
{
    public sealed record BounceCliOptions(
        string PrimaryEndpoint = "http://127.0.0.1:11434",
        string? CriticEndpoint = null,
        string Host = "127.0.0.1",
        int Port = 18090,
        string? HarnessPath = null,
        int MaxRepairs = 2,
        bool CompilerBounceEnabled = true,
        string ModelAlias = "Glacier-3Machine-Bounce");

    public static BounceCliOptions ParseOptions(string[] args)
    {
        string primaryEndpoint = "http://127.0.0.1:11434";
        string? criticEndpoint = null;
        string host = "127.0.0.1";
        int port = 18090;
        string? harnessPath = null;
        int maxRepairs = 2;
        bool compilerBounceEnabled = true;
        string modelAlias = "Glacier-3Machine-Bounce";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--primary":
                    primaryEndpoint = CliOptionHelpers.RequireValue(args, ref i, arg);
                    break;
                case "--critic":
                case "--peer-critic":
                    criticEndpoint = CliOptionHelpers.RequireValue(args, ref i, arg);
                    break;
                case "--npu-critic":
                    criticEndpoint = "auto-npu";
                    break;
                case "--host":
                    host = CliOptionHelpers.RequireValue(args, ref i, arg);
                    break;
                case "-p":
                case "--port":
                    string portStr = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!int.TryParse(portStr, out port) || port < 1 || port > 65535)
                        throw new ArgumentException($"Invalid {arg} '{portStr}'. Expected 1..65535.");
                    break;
                case "--harness":
                case "--bounce-harness":
                    harnessPath = CliOptionHelpers.RequireValue(args, ref i, arg);
                    break;
                case "--max-repairs":
                case "--bounce-max-repairs":
                    string mrStr = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!int.TryParse(mrStr, out maxRepairs) || maxRepairs < 0)
                        throw new ArgumentException($"Invalid {arg} '{mrStr}'. Expected a non-negative integer.");
                    break;
                case "--model-alias":
                    modelAlias = CliOptionHelpers.RequireValue(args, ref i, arg);
                    break;
                case "--compiler-bounce":
                    compilerBounceEnabled = true;
                    break;
                case "--no-compiler-bounce":
                    compilerBounceEnabled = false;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return new BounceCliOptions(
            primaryEndpoint,
            criticEndpoint,
            host,
            port,
            harnessPath,
            maxRepairs,
            compilerBounceEnabled,
            modelAlias);
    }

    public static string? ResolveDefaultHarnessPath(string? currentDirectory = null, string? baseDirectory = null)
        => SandboxedProcessBounceVerifier.ResolveDefaultHarnessPath(currentDirectory, baseDirectory);

    public static void RegisterEndpoints(GlacierServeApp app, PeerBounceService bounceService, string modelAlias = "Glacier-3Machine-Bounce")
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(bounceService);
        app.MapPeerBounce(bounceService, mapOpenAiAndHealthRoutes: true, modelAlias: modelAlias);
    }

    /// <summary>
    /// Resolves <c>--critic</c>, automatically probing local FastFlowLM / Lemonade NPU sidecars
    /// when <paramref name="criticOption"/> is <c>"auto-npu"</c> or <c>"npu"</c>.
    /// </summary>
    public static async Task<(string? Endpoint, NpuSidecarEndpoint? DiscoveredNpu)> ResolveCriticEndpointAsync(
        string? criticOption,
        HttpClient httpClient,
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? npuCandidates = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(criticOption))
            return (null, null);

        if (criticOption.Equals("auto-npu", StringComparison.OrdinalIgnoreCase) ||
            criticOption.Equals("npu", StringComparison.OrdinalIgnoreCase))
        {
            var found = await NpuSidecarDiscovery.ProbeAsync(
                httpClient,
                candidates: npuCandidates,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return (found?.BaseUrl, found);
        }

        return (criticOption, null);
    }

    public static async Task<int> ExecuteAsync(string[] args)
    {
        BounceCliOptions opts;
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

        string? harnessPath = opts.HarnessPath;
        if (string.IsNullOrWhiteSpace(harnessPath))
        {
            harnessPath = ResolveDefaultHarnessPath();
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
        var (resolvedCritic, discoveredNpu) = await ResolveCriticEndpointAsync(opts.CriticEndpoint, httpClient);
        var verifier = new SandboxedProcessBounceVerifier(harnessPath);
        var bounceService = new PeerBounceService(
            httpClient,
            verifier,
            opts.PrimaryEndpoint,
            resolvedCritic,
            compilerBounceEnabled: opts.CompilerBounceEnabled,
            maxRepairs: opts.MaxRepairs);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           GLACIER.SERVE - Peer Critic + Sandboxed Compiler Bounce             ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine($"  ✓ Listen:   http://{opts.Host}:{opts.Port}");
        Console.WriteLine($"  ✓ Primary:  {opts.PrimaryEndpoint}");
        if (discoveredNpu != null)
        {
            Console.WriteLine($"  ✓ Critic:   {discoveredNpu.BaseUrl} (NPU Sidecar: {discoveredNpu.Runtime}, model={discoveredNpu.PrimaryModel ?? "auto"})");
        }
        else if (opts.CriticEndpoint is not null &&
                 (opts.CriticEndpoint.Equals("auto-npu", StringComparison.OrdinalIgnoreCase) ||
                  opts.CriticEndpoint.Equals("npu", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("  ! Critic:   (no live local NPU sidecar detected on FastFlowLM :52625 or Lemonade :8000/:13305 — critic disabled)");
        }
        else
        {
            Console.WriteLine($"  ✓ Critic:   {resolvedCritic ?? "(disabled)"}");
        }
        Console.WriteLine($"  ✓ Verifier: {(opts.CompilerBounceEnabled ? $"ENABLED (maxRepairs={opts.MaxRepairs}, sandbox=OsProcessSandbox)" : "DISABLED")}");
        Console.WriteLine();

        using var app = GlacierServeApp.CreateBuilder()
            .UseHost(opts.Host)
            .UsePort(opts.Port)
            .Build();
        RegisterEndpoints(app, bounceService, opts.ModelAlias);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await app.StartAsync(cts.Token);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
        }

        return 0;
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier bounce - Peer Critic + OS-Sandboxed Compiler Bounce Proxy");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier bounce [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --primary <url>                      Primary OpenAI/Glacier endpoint (default: http://127.0.0.1:11434)");
        Console.WriteLine("  --critic, --peer-critic <url|auto-npu> Optional peer critic endpoint (or 'auto-npu' for FastFlowLM/Lemonade NPU)");
        Console.WriteLine("  --npu-critic                         Shorthand for --critic auto-npu (zero-VRAM NPU critic offload)");
        Console.WriteLine("  --host <string>                      Listening host (default: 127.0.0.1)");
        Console.WriteLine("  -p, --port <int>                     Listening port (default: 18090)");
        Console.WriteLine("  --harness <path>                     Optional EvaluationHarness executable");
        Console.WriteLine("  --max-repairs <int>                  Max repair passes (0-3, default: 2)");
        Console.WriteLine("  --compiler-bounce / --no-compiler-bounce Enable/disable sandboxed compiler verifier");
        Console.WriteLine("  --model-alias <name>                 Model ID exposed on /v1/models");
    }
}
