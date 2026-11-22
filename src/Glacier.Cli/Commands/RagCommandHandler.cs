namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Engine;
using Glacier.Inference.Hardware;
using Glacier.Inference.Sampling;
using Glacier.Rag.Embeddings;
using Glacier.Rag.Engine;

public sealed record RagCliOptions(
    string? ModelPath = null,
    string? DocsPath = null,
    string? Query = null,
    int TopK = 3,
    int MaxHops = 2,
    int MaxTokens = 512,
    string? EmbedEndpoint = null,
    string? EmbedModel = null,
    int? EmbedDims = null,
    string? EmbedApiKey = null);

public sealed record RagEmbeddingResolution(
    IEmbeddingModel Model,
    string? Endpoint,
    string? ModelId,
    NpuSidecarRuntime? Runtime,
    bool IsFallback,
    string? FallbackReason);

public static class RagCommandHandler
{
    private const string DefaultEmbeddingModelId = "embeddinggemma";
    private static readonly TimeSpan DefaultDimensionProbeTimeout = TimeSpan.FromSeconds(5);

    public static RagCliOptions ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? modelPath = null;
        string? docsPath = null;
        string? query = null;
        int topK = 3;
        int maxHops = 2;
        int maxTokens = 512;
        string? embedEndpoint = null;
        string? embedModel = null;
        int? embedDims = null;
        string? embedApiKey = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (TrySplitEqualsFlag(arg, out string eqFlag, out string eqValue))
            {
                switch (eqFlag)
                {
                    case "-m":
                    case "--model":
                        modelPath = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "-d":
                    case "--docs":
                    case "--data":
                        docsPath = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "-q":
                    case "--query":
                        query = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "-k":
                    case "--top-k":
                        topK = ParsePositiveInt(eqFlag, RequireNonEmptyOptionValue(eqFlag, eqValue));
                        break;
                    case "--hops":
                        maxHops = ParseNonNegativeInt(eqFlag, RequireNonEmptyOptionValue(eqFlag, eqValue));
                        break;
                    case "-n":
                    case "--tokens":
                        maxTokens = ParsePositiveInt(eqFlag, RequireNonEmptyOptionValue(eqFlag, eqValue));
                        break;
                    case "--embed-endpoint":
                        embedEndpoint = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "--embed-model":
                        embedModel = RequireNonEmptyOptionValue(eqFlag, eqValue);
                        break;
                    case "--embed-dims":
                        embedDims = ParsePositiveInt(eqFlag, RequireNonEmptyOptionValue(eqFlag, eqValue));
                        break;
                    case "--api-key":
                    case "--embed-api-key":
                        embedApiKey = RequireNonEmptyOptionValue(eqFlag, eqValue);
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
                    modelPath = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "-d":
                case "--docs":
                case "--data":
                    docsPath = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "-q":
                case "--query":
                    query = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "-k":
                case "--top-k":
                    topK = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--hops":
                    maxHops = ParseNonNegativeInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "-n":
                case "--tokens":
                    maxTokens = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--embed-endpoint":
                    embedEndpoint = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--embed-model":
                    embedModel = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--embed-dims":
                    embedDims = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                case "--api-key":
                case "--embed-api-key":
                    embedApiKey = RequireNonEmptyOptionValue(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return new RagCliOptions(
            ModelPath: modelPath,
            DocsPath: docsPath,
            Query: query,
            TopK: topK,
            MaxHops: maxHops,
            MaxTokens: maxTokens,
            EmbedEndpoint: embedEndpoint,
            EmbedModel: embedModel,
            EmbedDims: embedDims,
            EmbedApiKey: embedApiKey);
    }

    public static async Task<RagEmbeddingResolution> ResolveEmbeddingModelAsync(
        RagCliOptions options,
        HttpClient? httpClient = null,
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? npuCandidates = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? probeCacheTtl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.EmbedEndpoint))
        {
            return new RagEmbeddingResolution(
                new FastHashEmbeddingModel(384),
                Endpoint: null,
                ModelId: "fast-hash",
                Runtime: null,
                IsFallback: false,
                FallbackReason: null);
        }

        string rawEndpoint = options.EmbedEndpoint.Trim();
        string? apiKey = string.IsNullOrWhiteSpace(options.EmbedApiKey) ? null : options.EmbedApiKey.Trim();
        HttpClient? authProbeClient = null;
        if (apiKey != null && httpClient != null)
        {
            var bearerAuth = new AuthenticationHeaderValue("Bearer", apiKey);
            authProbeClient = new HttpClient(new BearerAuthForwardingHandler(httpClient, bearerAuth), disposeHandler: true)
            {
                BaseAddress = httpClient.BaseAddress,
                Timeout = httpClient.Timeout
            };
        }

        try
        {
            string resolvedEndpoint;
            NpuSidecarRuntime? runtime;
            string modelId;
            HttpClient? effectiveProbeClient = authProbeClient ?? httpClient;

            if (IsAutoNpuAlias(rawEndpoint))
            {
                var discovered = await NpuSidecarDiscovery.ProbeCachedAsync(
                    cacheTtl: probeCacheTtl,
                    httpClient: effectiveProbeClient,
                    candidates: npuCandidates,
                    perEndpointTimeout: probeTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (discovered is null)
                {
                    return CreateFallbackResolution(
                        "No local NPU sidecar detected on FastFlowLM :52625 or Lemonade :8000/:13305.");
                }

                resolvedEndpoint = discovered.BaseUrl;
                runtime = discovered.Runtime;
                modelId = !string.IsNullOrWhiteSpace(options.EmbedModel)
                    ? options.EmbedModel.Trim()
                    : SelectEmbeddingModelId(discovered.Models) ?? DefaultEmbeddingModelId;
            }
            else
            {
                if (!IsValidHttpEndpoint(rawEndpoint, httpClient))
                {
                    return CreateFallbackResolution($"Invalid embedding endpoint '{rawEndpoint}'.");
                }

                resolvedEndpoint = rawEndpoint;
                if (!string.IsNullOrWhiteSpace(options.EmbedModel))
                {
                    modelId = options.EmbedModel.Trim();
                    runtime = null;
                }
                else
                {
                    var discovered = await NpuSidecarDiscovery.ProbeAsync(
                        effectiveProbeClient,
                        candidates: [(rawEndpoint, NpuSidecarRuntime.Unknown)],
                        perEndpointTimeout: probeTimeout,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    runtime = discovered?.Runtime;
                    modelId = SelectEmbeddingModelId(discovered?.Models) ?? DefaultEmbeddingModelId;
                }
            }

            int? dims = options.EmbedDims;
            if (dims is null)
            {
                dims = await OpenAiEmbeddingModel.ProbeDimensionsAsync(
                    resolvedEndpoint,
                    modelId,
                    apiKey: apiKey,
                    httpClient: httpClient,
                    timeout: probeTimeout ?? DefaultDimensionProbeTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var model = await OpenAiEmbeddingModel.CreateAutoAsync(
                resolvedEndpoint,
                modelId,
                apiKey: apiKey,
                httpClient: httpClient,
                timeout: null,
                knownDimensions: dims,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new RagEmbeddingResolution(
                model,
                model.BaseUrl,
                model.ModelId,
                runtime,
                IsFallback: false,
                FallbackReason: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableEmbeddingException(ex))
        {
            return CreateFallbackResolution(ex.Message);
        }
        finally
        {
            authProbeClient?.Dispose();
        }
    }

    public static Task<int> ExecuteAsync(string[] args)
        => ExecuteAsync(args, httpClient: null, npuCandidates: null, cancellationToken: default);

    public static async Task<int> ExecuteAsync(
        string[] args,
        HttpClient? httpClient,
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? npuCandidates = null,
        CancellationToken cancellationToken = default)
    {
        RagCliOptions opts;
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

        if (!string.IsNullOrEmpty(opts.ModelPath) && !File.Exists(opts.ModelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Model file not found: {opts.ModelPath}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            GLACIER.RAG - In-Process SIMD Vector + CSR GraphRAG                ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        var embeddingResolution = await ResolveEmbeddingModelAsync(
            opts,
            httpClient: httpClient,
            npuCandidates: npuCandidates,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        using var state = new RagExecutionState(embeddingResolution.Model);

        if (state.EmbeddingModel is OpenAiEmbeddingModel openAiEmbedding)
        {
            string runtimeLabel = embeddingResolution.Runtime is { } rt && rt != NpuSidecarRuntime.Unknown
                ? $", runtime={rt}"
                : string.Empty;
            Console.WriteLine($"  ✓ Embeddings: OpenAI/NPU ({openAiEmbedding.BaseUrl}, model={openAiEmbedding.ModelId}, dims={openAiEmbedding.Dimensions}{runtimeLabel})");
        }
        else if (embeddingResolution.IsFallback)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ! Embeddings: Falling back to FastHashEmbeddingModel (384d) — {embeddingResolution.FallbackReason}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine($"  ✓ Embeddings: FastHashEmbeddingModel ({state.EmbeddingModel.Dimensions}d, local offline)");
        }

        // 1. Ingest Documents (falling back cleanly if OpenAI/NPU embeddings fail at runtime)
        try
        {
            await IngestDocumentsAsync(state.Engine, opts.DocsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (state.IsOpenAiEmbedding
            && !cancellationToken.IsCancellationRequested
            && IsRecoverableEmbeddingException(ex))
        {
            state.SwitchToFallback(ex);
            await IngestDocumentsAsync(state.Engine, opts.DocsPath, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"  ✓ Graph Knowledge Nodes: {state.Engine.GraphNodeCount:N0} nodes | {state.Engine.GraphEdgeCount:N0} edges");
        Console.WriteLine($"  ✓ Vector Index:          {state.Engine.IndexedChunksCount:N0} semantic embeddings\n");

        // Hoist InferenceSession outside the interactive REPL loop so model weights & KV cache stay warm
        using InferenceSession? session = !string.IsNullOrEmpty(opts.ModelPath)
            ? new InferenceSession(opts.ModelPath, maxSeqLen: 4096)
            : null;

        // 2. Query Loop or Single-Shot (with runtime fallback if OpenAI/NPU embeddings fail during retrieval)
        if (!string.IsNullOrEmpty(opts.Query))
        {
            await ProcessQueryWithFallbackAsync(state, session, opts, opts.Query, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("================================================================================");
        Console.WriteLine("   Interactive GraphRAG Console (Type 'exit' to quit)                           ");
        Console.WriteLine("================================================================================\n");
        Console.ResetColor();

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("rag> ");
            Console.ResetColor();

            string? line = Console.ReadLine();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            await ProcessQueryWithFallbackAsync(state, session, opts, line, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task IngestDocumentsAsync(
        GraphRagEngine ragEngine,
        string? docsPath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(docsPath))
        {
            if (File.Exists(docsPath))
            {
                Console.WriteLine($"[1/3] Ingesting document: {Path.GetFileName(docsPath)}...");
                string content = await File.ReadAllTextAsync(docsPath, cancellationToken).ConfigureAwait(false);
                int chunks = await ragEngine.IndexDocumentAsync(Path.GetFileName(docsPath), content, cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"  ✓ Indexed {chunks} semantic chunks");
            }
            else if (Directory.Exists(docsPath))
            {
                Console.WriteLine($"[1/3] Scanning and ingesting directory: {docsPath}...");
                var files = Directory.GetFiles(docsPath, "*.*", SearchOption.AllDirectories);
                int totalDocs = 0;
                int totalChunks = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".md" or ".json" or ".cs" or ".csv")
                    {
                        string content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                        totalChunks += await ragEngine.IndexDocumentAsync(Path.GetFileName(file), content, cancellationToken: cancellationToken).ConfigureAwait(false);
                        totalDocs++;
                    }
                }
                Console.WriteLine($"  ✓ Ingested {totalDocs} files into {totalChunks} vector & graph chunks");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Document path '{docsPath}' not found.");
                Console.ResetColor();
            }
        }
        else
        {
            Console.WriteLine("[1/3] No documents supplied via --docs; using empty knowledge index.");
        }
    }

    private static async Task ProcessQueryWithFallbackAsync(
        RagExecutionState state,
        InferenceSession? session,
        RagCliOptions opts,
        string query,
        CancellationToken cancellationToken)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[Query] {query}");
        Console.ResetColor();

        var sw = Stopwatch.StartNew();
        HybridRetrievalResult retrieval;
        try
        {
            retrieval = await state.Engine.RetrieveAsync(
                query,
                new RagOptions { TopK = opts.TopK, MaxGraphHops = opts.MaxHops },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (state.IsOpenAiEmbedding
            && !cancellationToken.IsCancellationRequested
            && IsRecoverableEmbeddingException(ex))
        {
            state.SwitchToFallback(ex);
            await IngestDocumentsAsync(state.Engine, opts.DocsPath, cancellationToken).ConfigureAwait(false);
            sw.Restart();
            retrieval = await state.Engine.RetrieveAsync(
                query,
                new RagOptions { TopK = opts.TopK, MaxGraphHops = opts.MaxHops },
                cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();

        Console.WriteLine($"  ✓ Hybrid Retrieval: {sw.Elapsed.TotalMilliseconds:F2} ms (Vector: {retrieval.VectorSearchLatencyMs:F2} ms, Graph: {retrieval.GraphTraversalLatencyMs:F2} ms)");
        Console.WriteLine($"  ✓ Discovered Entities: [{string.Join(", ", retrieval.DiscoveredEntities)}]");
        Console.WriteLine($"  ✓ Graph Relations:    {retrieval.GraphRelations.Count} structural hops traversed");
        Console.WriteLine($"  ✓ Top Vector Matches:   {retrieval.VectorMatches.Count} excerpts\n");

        if (session is null)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("=== SYNTHESIZED HYBRID CONTEXT (Pass --model <gguf> to stream LLM answer) ===");
            Console.WriteLine(retrieval.SynthesizedContext);
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=== STREAMING GROUNDED LLM GENERATION ===");
        string prompt = state.Engine.BuildAugmentedPrompt(query, retrieval);

        var sampling = new SamplingOptions { MaxTokens = opts.MaxTokens, Temperature = 0.2f };
        var genResult = await session.GenerateAsync(
            prompt,
            sampling,
            formatChat: false,
            onToken: token => Console.Write(token),
            ct: cancellationToken);

        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n[Metrics] {genResult.Metrics.GenerationTokensPerSecond:F1} tok/s | Total: {genResult.Metrics.TotalDuration.TotalMilliseconds:F0} ms\n");
        Console.ResetColor();
    }

    private sealed class RagExecutionState : IDisposable
    {
        public IEmbeddingModel EmbeddingModel { get; private set; }
        public GraphRagEngine Engine { get; private set; }

        public RagExecutionState(IEmbeddingModel embeddingModel)
        {
            EmbeddingModel = embeddingModel;
            Engine = new GraphRagEngine(embeddingModel);
        }

        public bool IsOpenAiEmbedding => EmbeddingModel is OpenAiEmbeddingModel;

        public void SwitchToFallback(Exception failure)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"Warning: Embedding endpoint failed during RAG execution ({failure.Message}). Falling back to FastHashEmbeddingModel (384d).");
            Console.ResetColor();

            Engine.Dispose();
            EmbeddingModel.Dispose();

            var fallbackModel = new FastHashEmbeddingModel(384);
            EmbeddingModel = fallbackModel;
            Engine = new GraphRagEngine(fallbackModel);
        }

        public void Dispose()
        {
            Engine.Dispose();
            EmbeddingModel.Dispose();
        }
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier rag - In-process SIMD Vector + CSR GraphRAG query engine");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier rag [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -d, --docs <path>                    Document file or folder to ingest");
        Console.WriteLine("  -q, --query <text>                   Single question to answer (launches REPL if omitted)");
        Console.WriteLine("  -m, --model <model.gguf>             GGUF model to generate answers from");
        Console.WriteLine("  --embed-endpoint <url|auto-npu>      OpenAI/NPU embedding endpoint (or 'auto-npu' for FastFlowLM/Lemonade)");
        Console.WriteLine("  --embed-model <id>                   Embedding model ID served by endpoint (default: auto / embeddinggemma)");
        Console.WriteLine("  --embed-dims <int>                   Override embedding vector dimensions (auto-probed if omitted)");
        Console.WriteLine("  --api-key, --embed-api-key <key>     Optional bearer token for remote embedding endpoints");
        Console.WriteLine("  -k, --top-k <int>                    Number of vector matches (default: 3)");
        Console.WriteLine("  --hops <int>                         Max graph hops for CSR traversal (default: 2)");
        Console.WriteLine("  -n, --tokens <int>                   Max tokens for LLM generation (default: 512)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  glacier rag --docs ./docs --query \"How does PagedAttention work?\"");
        Console.WriteLine("  glacier rag --docs ./docs --embed-endpoint auto-npu --query \"Summarize NPU architecture\"");
        Console.WriteLine("  glacier rag --docs ./docs --embed-endpoint http://127.0.0.1:52625 --embed-model embeddinggemma -q \"...\"");
    }

    internal static bool IsAutoNpuAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        return trimmed.Equals("auto-npu", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("npu", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? SelectEmbeddingModelId(IReadOnlyList<string>? models)
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
            if (trimmed.Contains("embed", StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        return firstNonEmpty;
    }

    private static RagEmbeddingResolution CreateFallbackResolution(string reason)
        => new(
            new FastHashEmbeddingModel(384),
            Endpoint: null,
            ModelId: "fast-hash",
            Runtime: null,
            IsFallback: true,
            FallbackReason: reason);

    private static bool IsValidHttpEndpoint(string endpoint, HttpClient? httpClient)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? absoluteUri))
        {
            return absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        return httpClient?.BaseAddress != null && Uri.TryCreate(endpoint, UriKind.Relative, out _);
    }

    private static bool IsRecoverableEmbeddingException(Exception ex)
        => ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or JsonException
            or InvalidOperationException
            or UriFormatException
            or NotSupportedException
            or ArgumentException;

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

    private static int ParseNonNegativeInt(string flag, string rawValue)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
            throw new ArgumentException($"Invalid {flag} '{rawValue}'. Expected a non-negative integer.");

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
