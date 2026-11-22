namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Glacier.Inference.Engine;
using Glacier.Inference.Hardware;
using Glacier.Inference.Sampling;
using Glacier.MemEx.Models;

public sealed record AgentCliOptions(
    string? Model = null,
    string? Endpoint = null,
    string? ApiKey = null,
    int MaxSteps = 8,
    int MaxTokens = 512,
    string Prompt = "");

internal sealed record AgentRemoteResolution(
    string? BaseUrl,
    string? ModelId,
    NpuSidecarRuntime? Runtime,
    bool IsNpuActive,
    string? FallbackReason);

public static class AgentCommandHandler
{
    public static AgentCliOptions ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? model = null;
        string? endpoint = null;
        string? apiKey = null;
        int maxSteps = 8;
        int maxTokens = 512;
        var promptParts = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (TrySplitEqualsFlag(arg, out string eqFlag, out string eqValue))
            {
                switch (eqFlag)
                {
                    case "-m":
                    case "--model":
                        model = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "--endpoint":
                        endpoint = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "--api-key":
                        apiKey = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "--max-steps":
                        maxSteps = ParsePositiveInt(eqFlag, RequireNonEmptyOptionValue(eqFlag, eqValue));
                        break;
                    case "-n":
                    case "--tokens":
                    case "--max-tokens":
                        maxTokens = ParsePositiveInt(eqFlag, RequireNonEmptyOptionValue(eqFlag, eqValue));
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{arg}'.");
                }
                continue;
            }

            switch (arg)
            {
                case "-m":
                case "--model":
                    model = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--endpoint":
                    endpoint = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--npu":
                    endpoint = "auto-npu";
                    break;
                case "--api-key":
                    apiKey = CliOptionHelpers.RequireValue(args, ref i, arg);
                    break;
                case "--max-steps":
                    maxSteps = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "-n":
                case "--tokens":
                case "--max-tokens":
                    maxTokens = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    promptParts.Add(arg);
                    break;
            }
        }

        return new AgentCliOptions(
            Model: model,
            Endpoint: endpoint,
            ApiKey: apiKey,
            MaxSteps: maxSteps,
            MaxTokens: maxTokens,
            Prompt: string.Join(" ", promptParts).Trim());
    }

    public static Task<int> ExecuteAsync(string[] args)
        => ExecuteAsync(args, httpClient: null, npuCandidates: null, deviceProvider: null, cancellationToken: default);

    public static async Task<int> ExecuteAsync(
        string[] args,
        HttpClient? httpClient,
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? npuCandidates = null,
        Func<IReadOnlyList<DeviceInfo>>? deviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        AgentCliOptions opts;
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

        if (string.IsNullOrWhiteSpace(opts.Prompt))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Error: Missing agent task prompt. Usage: glacier agent [--model <path|id>] [--endpoint <url|auto-npu>] [--npu] <prompt>");
            Console.ResetColor();
            return 1;
        }

        bool hasEndpointOption = !string.IsNullOrWhiteSpace(opts.Endpoint);
        if (!hasEndpointOption && !string.IsNullOrEmpty(opts.Model) && !File.Exists(opts.Model))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Model file not found: {opts.Model}");
            Console.ResetColor();
            return 1;
        }

        if (hasEndpointOption && !IsAutoNpuAlias(opts.Endpoint) && !IsValidHttpEndpoint(opts.Endpoint!.Trim(), httpClient))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: Invalid endpoint URL '{opts.Endpoint}'.");
            Console.ResetColor();
            return 1;
        }

        string? normalizedApiKey = string.IsNullOrWhiteSpace(opts.ApiKey) ? null : opts.ApiKey.Trim();
        AuthenticationHeaderValue? bearerAuth = null;
        if (normalizedApiKey != null)
        {
            try
            {
                bearerAuth = new AuthenticationHeaderValue("Bearer", normalizedApiKey);
                using var headerValidationReq = new HttpRequestMessage();
                headerValidationReq.Headers.Authorization = bearerAuth;
            }
            catch (FormatException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: Invalid --api-key format: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                 GLACIER.AGENT - Autonomous Agent Runtime                      ║");
        Console.WriteLine("║            High-Speed Actor Engine | In-Memory MCP | SIMD Tokenizer           ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"\n  Task: {opts.Prompt}\n");

        var sw = Stopwatch.StartNew();
        string sessionId = Guid.NewGuid().ToString("N")[..8];

        bool ownsHttpClient = httpClient == null;
        HttpClient effectiveClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        HttpClient? probeClient = null;
        if (bearerAuth != null)
        {
            if (ownsHttpClient)
            {
                effectiveClient.DefaultRequestHeaders.Authorization = bearerAuth;
            }
            else
            {
                probeClient = new HttpClient(new BearerAuthForwardingHandler(effectiveClient, bearerAuth), disposeHandler: true)
                {
                    BaseAddress = effectiveClient.BaseAddress,
                    Timeout = effectiveClient.Timeout
                };
            }
        }

        try
        {
            AgentRemoteResolution remote = await ResolveRemoteBackendAsync(
                opts,
                probeClient ?? effectiveClient,
                npuCandidates,
                cancellationToken).ConfigureAwait(false);

            AgentResponse response;
            if (remote.BaseUrl != null && remote.ModelId != null)
            {
                string runtimeLabel = remote.Runtime is { } rt && rt != NpuSidecarRuntime.Unknown
                    ? $", runtime={rt}"
                    : string.Empty;
                Console.WriteLine($"  ✓ LLM Backend: OpenAI/NPU ({remote.BaseUrl}, model={remote.ModelId}{runtimeLabel})");

                var openAiService = new OpenAiService(remote.BaseUrl, normalizedApiKey, httpClient: effectiveClient);
                var chatModel = new LlmServiceChatModel(
                    openAiService,
                    remote.ModelId,
                    temperature: 0.1f,
                    maxTokens: opts.MaxTokens);

                using var engine = new AgentExecutionEngine("glacier-cli-agent", chatModel);
                RegisterBuiltInTools(engine, deviceProvider);

                var memExOptions = remote.IsNpuActive
                    ? new MemExOptions
                    {
                        EnableNpuEmbeddings = true,
                        EmbeddingEndpoint = remote.BaseUrl,
                        EmbeddingApiKey = normalizedApiKey,
                        HttpClient = probeClient ?? effectiveClient,
                        ProbeTimeout = TimeSpan.FromMilliseconds(500),
                        EmbeddingTimeout = TimeSpan.FromSeconds(10)
                    }
                    : new MemExOptions { EnableNpuEmbeddings = false };

                using var episodicMemory = new Glacier.MemEx.MemExEngine(memExOptions);
                if (remote.IsNpuActive)
                {
                    bool npuEmbeddingsReady = await episodicMemory
                        .InitializeEmbeddingsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (npuEmbeddingsReady)
                    {
                        Console.WriteLine(
                            $"  ✓ Episodic Memory: NPU semantic embeddings ({episodicMemory.ActiveEmbeddingModelId}, {episodicMemory.ActiveEmbeddingDimensions}d)");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("  ! Episodic Memory: NPU embedding probe failed — using local FastHashEmbeddingModel (384d)");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.WriteLine("  ✓ Episodic Memory: FastHashEmbeddingModel (384d, local)");
                }

                engine.AttachEpisodicMemory(episodicMemory);

                using var conformalRouter = new Glacier.Clavier.Engine.ClavierSession(
                    embeddingDim: 16,
                    hiddenDim: 16,
                    numActions: Math.Max(2, engine.Tools.Tools.Count));
                engine.AttachConformalRouter(conformalRouter);
                AttachConsoleLogger(engine);

                try
                {
                    response = await engine.ExecuteLoopAsync(sessionId, opts.Prompt, opts.MaxSteps, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(
                        $"Error: Remote endpoint rejected credentials ({(int)ex.StatusCode.Value} {ex.StatusCode}). Check --api-key.");
                    Console.ResetColor();
                    return 1;
                }
                catch (HttpRequestException ex) when (IsTransientRemoteFailure(ex))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ! Remote endpoint unavailable ({ex.Message}) — falling back to local execution.");
                    Console.ResetColor();

                    response = await RunLocalOrDirectiveFallbackAsync(
                        opts,
                        sessionId,
                        deviceProvider,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    string statusDesc = ex.StatusCode.HasValue
                        ? $"{(int)ex.StatusCode.Value} {ex.StatusCode.Value}"
                        : "HTTP error";
                    Console.Error.WriteLine($"Error: Remote endpoint request failed ({statusDesc}): {ex.Message}");
                    Console.ResetColor();
                    return 1;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  ! Remote endpoint timed out — falling back to local execution.");
                    Console.ResetColor();

                    response = await RunLocalOrDirectiveFallbackAsync(
                        opts,
                        sessionId,
                        deviceProvider,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(remote.FallbackReason))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ! Remote/NPU backend: {remote.FallbackReason} Falling back to local execution.");
                    Console.ResetColor();
                }

                response = await RunLocalOrDirectiveFallbackAsync(
                    opts,
                    sessionId,
                    deviceProvider,
                    cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[Agent Output] {response.Output}");
            Console.WriteLine($"✓ Completed in {sw.Elapsed.TotalMilliseconds:F1} ms (IsCompleted: {response.IsCompleted})");
            Console.ResetColor();

            return 0;
        }
        finally
        {
            probeClient?.Dispose();
            if (ownsHttpClient)
                effectiveClient.Dispose();
        }
    }

    internal static async Task<AgentRemoteResolution> ResolveRemoteBackendAsync(
        AgentCliOptions opts,
        HttpClient httpClient,
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? npuCandidates = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(opts.Endpoint))
            return new AgentRemoteResolution(null, null, null, IsNpuActive: false, FallbackReason: null);

        string rawEndpoint = opts.Endpoint.Trim();
        if (IsAutoNpuAlias(rawEndpoint))
        {
            var discovered = await NpuSidecarDiscovery.ProbeCachedAsync(
                httpClient: httpClient,
                candidates: npuCandidates,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (discovered is null)
            {
                return new AgentRemoteResolution(
                    null,
                    null,
                    null,
                    IsNpuActive: false,
                    FallbackReason: "No local NPU sidecar detected on FastFlowLM :52625 or Lemonade :8000/:13305.");
            }

            string modelId = !string.IsNullOrWhiteSpace(opts.Model)
                ? opts.Model.Trim()
                : SelectChatModelId(discovered.Models) ?? "default";

            return new AgentRemoteResolution(
                discovered.BaseUrl,
                modelId,
                discovered.Runtime,
                IsNpuActive: true,
                FallbackReason: null);
        }

        if (!string.IsNullOrWhiteSpace(opts.Model))
        {
            return new AgentRemoteResolution(
                rawEndpoint,
                opts.Model.Trim(),
                Runtime: null,
                IsNpuActive: false,
                FallbackReason: null);
        }

        var probed = await NpuSidecarDiscovery.ProbeSingleAsync(
            httpClient,
            rawEndpoint,
            NpuSidecarRuntime.Unknown,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (probed is null)
        {
            return new AgentRemoteResolution(
                null,
                null,
                null,
                IsNpuActive: false,
                FallbackReason: $"Endpoint '{rawEndpoint}' is unreachable or returned no models.");
        }

        string discoveredModelId = SelectChatModelId(probed.Models) ?? "default";
        bool isNpu = probed.Runtime is NpuSidecarRuntime.FastFlowLM or NpuSidecarRuntime.LemonadeServer;

        return new AgentRemoteResolution(
            probed.BaseUrl,
            discoveredModelId,
            probed.Runtime,
            IsNpuActive: isNpu,
            FallbackReason: null);
    }

    private static async Task<AgentResponse> RunLocalOrDirectiveFallbackAsync(
        AgentCliOptions opts,
        string sessionId,
        Func<IReadOnlyList<DeviceInfo>>? deviceProvider,
        CancellationToken cancellationToken)
    {
        bool hasLocalGguf = !string.IsNullOrEmpty(opts.Model) && File.Exists(opts.Model);
        using InferenceSession? session = hasLocalGguf
            ? new InferenceSession(opts.Model!, maxSeqLen: 4096)
            : null;

        using AgentExecutionEngine engine = session != null
            ? new AgentExecutionEngine("glacier-cli-agent", new InferenceSessionChatModel(session, opts.MaxTokens))
            : new AgentExecutionEngine("glacier-cli-agent");

        RegisterBuiltInTools(engine, deviceProvider);

        using var episodicMemory = new Glacier.MemEx.MemExEngine(new MemExOptions
        {
            EnableNpuEmbeddings = false
        });
        engine.AttachEpisodicMemory(episodicMemory);

        using var conformalRouter = new Glacier.Clavier.Engine.ClavierSession(
            embeddingDim: 16,
            hiddenDim: 16,
            numActions: Math.Max(2, engine.Tools.Tools.Count));
        engine.AttachConformalRouter(conformalRouter);
        AttachConsoleLogger(engine);

        return session != null
            ? await engine.ExecuteLoopAsync(sessionId, opts.Prompt, opts.MaxSteps, cancellationToken).ConfigureAwait(false)
            : await engine.ExecuteStepAsync(new AgentContext(sessionId, opts.Prompt, StepCount: 1), cancellationToken).ConfigureAwait(false);
    }

    private static void AttachConsoleLogger(AgentExecutionEngine engine)
    {
        engine.EventPublished += evt =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] ({evt.SourceAgent}) {evt.EventType}: {evt.Payload}");
            Console.ResetColor();
        };
    }

    public static void RegisterBuiltInTools(AgentExecutionEngine engine)
        => RegisterBuiltInTools(engine, deviceProvider: null);

    public static void RegisterBuiltInTools(AgentExecutionEngine engine, Func<IReadOnlyList<DeviceInfo>>? deviceProvider)
    {
        ArgumentNullException.ThrowIfNull(engine);

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
            description: "Enumerates available CPU/GPU/NPU compute accelerators detected by Glacier.",
            parameterJsonSchema: "{\"type\":\"object\",\"properties\":{}}",
            handler: _ =>
            {
                var devices = (deviceProvider ?? DeviceManager.GetDevices)();
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
                        writer.WriteBoolean("isNpu", d.IsNpu);
                        writer.WriteNumber("sharedVramGb", d.SharedVramGb);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
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
        Console.WriteLine("  -m, --model <path|id>                Local GGUF path or remote/NPU model ID");
        Console.WriteLine("  --endpoint <url|auto-npu>            OpenAI-compatible endpoint (or 'auto-npu' for FastFlowLM/Lemonade)");
        Console.WriteLine("  --npu                                Shorthand for --endpoint auto-npu (zero-VRAM NPU execution)");
        Console.WriteLine("  --api-key <key>                      Optional bearer token for remote endpoints");
        Console.WriteLine("  --max-steps <int>                    Maximum reasoning/tool steps (default: 8)");
        Console.WriteLine("  -n, --tokens, --max-tokens <int>     Max tokens per LLM step (default: 512)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  glacier agent --model model.gguf \"Inspect available GPU accelerators\"");
        Console.WriteLine("  glacier agent --npu \"Inspect available hardware and run ping\"");
        Console.WriteLine("  glacier agent --endpoint auto-npu --model qwen3-4b \"Summarize workspace status\"");
        Console.WriteLine("  glacier agent \"CALL ping: {}\"");
        Console.WriteLine("  glacier agent \"CALL devices: {}\"");
    }

    internal static bool IsAutoNpuAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        return trimmed.Equals("auto-npu", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("npu", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? SelectChatModelId(IReadOnlyList<string>? models)
    {
        if (models is null || models.Count == 0)
            return null;

        string? firstNonEmpty = null;
        foreach (string candidate in models)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string trimmed = candidate.Trim();
            firstNonEmpty ??= trimmed;
            if (!trimmed.Contains("embed", StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        return firstNonEmpty;
    }

    private static bool IsValidHttpEndpoint(string endpoint, HttpClient? httpClient)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? absoluteUri))
        {
            return absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        return httpClient?.BaseAddress != null && Uri.TryCreate(endpoint, UriKind.Relative, out _);
    }

    private static bool IsTransientRemoteFailure(HttpRequestException ex)
        => ex.StatusCode is null
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool TrySplitEqualsFlag(string arg, out string flag, out string value)
    {
        if (arg.StartsWith('-'))
        {
            int eqIdx = arg.IndexOf('=');
            if (eqIdx > 0)
            {
                flag = arg[..eqIdx];
                value = arg[(eqIdx + 1)..];
                return true;
            }
        }

        flag = string.Empty;
        value = string.Empty;
        return false;
    }

    private static string RequireNonEmptyOptionValue(string flag, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Option '{flag}' requires a value.");

        return value;
    }

    private static int ParsePositiveInt(string flag, string rawValue)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"Invalid {flag} '{rawValue}'. Expected a positive integer.");

        return parsed;
    }

    private sealed class BearerAuthForwardingHandler(HttpClient innerClient, AuthenticationHeaderValue bearerAuth)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
                Content = request.Content
            };

            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            forwarded.Headers.Authorization = bearerAuth;
            return innerClient.SendAsync(forwarded, cancellationToken);
        }
    }
}
