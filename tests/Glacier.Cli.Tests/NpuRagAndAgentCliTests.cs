namespace Glacier.Cli.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Runtime;
using Glacier.Cli.Commands;
using Glacier.Inference.Hardware;
using Glacier.Rag.Embeddings;
using Xunit;

[Collection("ConsoleTests")]
public sealed class NpuRagAndAgentCliTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, CancellationToken, HttpResponseMessage> _handler;

        public ConcurrentQueue<(HttpMethod Method, string Uri, string? Body, string? AuthHeader)> Requests { get; } = new();
        public bool Disposed { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, string?, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
            string? auth = request.Headers.Authorization?.ToString();
            Requests.Enqueue((request.Method, request.RequestUri?.ToString() ?? string.Empty, body, auth));
            return _handler(request, body, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonDisposingCheckHttpClient : HttpClient
    {
        public bool WasDisposed { get; private set; }

        public NonDisposingCheckHttpClient(HttpMessageHandler handler) : base(handler, disposeHandler: false)
        {
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string BuildEmbeddingResponseJson(int dims, float seed = 1.0f)
    {
        var values = new float[dims];
        values[0] = seed;
        if (dims > 1) values[1] = 0.5f;
        return $"{{\"data\":[{{\"embedding\":{JsonSerializer.Serialize(values)}}}]}}";
    }

    private static int GetUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // -----------------------------------------------------------------------
    // Feature #11: RagCommandHandler Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void RagParseOptions_ParsesAllFlagsAndEqualsForms()
    {
        var opts = RagCommandHandler.ParseOptions([
            "--docs=./notes",
            "--query=What is NPU?",
            "-m", "model.gguf",
            "--embed-endpoint=auto-npu",
            "--embed-model", "embeddinggemma",
            "--embed-dims=768",
            "--top-k=5",
            "--hops", "3",
            "-n", "256"
        ]);

        Assert.Equal("./notes", opts.DocsPath);
        Assert.Equal("What is NPU?", opts.Query);
        Assert.Equal("model.gguf", opts.ModelPath);
        Assert.Equal("auto-npu", opts.EmbedEndpoint);
        Assert.Equal("embeddinggemma", opts.EmbedModel);
        Assert.Equal(768, opts.EmbedDims);
        Assert.Equal(5, opts.TopK);
        Assert.Equal(3, opts.MaxHops);
        Assert.Equal(256, opts.MaxTokens);
    }

    [Theory]
    [InlineData(new object[] { new string[] { "--embed-endpoint" } })]
    [InlineData(new object[] { new string[] { "--embed-endpoint", "" } })]
    [InlineData(new object[] { new string[] { "--embed-endpoint", "   " } })]
    [InlineData(new object[] { new string[] { "--embed-model" } })]
    [InlineData(new object[] { new string[] { "--embed-model", "" } })]
    [InlineData(new object[] { new string[] { "--docs", "   " } })]
    [InlineData(new object[] { new string[] { "--query", "" } })]
    [InlineData(new object[] { new string[] { "--model", "   " } })]
    [InlineData(new object[] { new string[] { "--embed-dims" } })]
    [InlineData(new object[] { new string[] { "--embed-endpoint=" } })]
    [InlineData(new object[] { new string[] { "--embed-dims", "0" } })]
    [InlineData(new object[] { new string[] { "--embed-dims", "-16" } })]
    [InlineData(new object[] { new string[] { "--embed-dims", "abc" } })]
    [InlineData(new object[] { new string[] { "--top-k", "0" } })]
    [InlineData(new object[] { new string[] { "--hops", "-1" } })]
    [InlineData(new object[] { new string[] { "--tokens", "0" } })]
    [InlineData(new object[] { new string[] { "--bogus-flag" } })]
    public void RagParseOptions_RejectsInvalidFlagsAndValues(string[] args)
    {
        Assert.Throws<ArgumentException>(() => RagCommandHandler.ParseOptions(args));
    }

    [Fact]
    public async Task RagResolveEmbeddingModel_UnconfiguredUsesFastHashWithoutHttpCalls()
    {
        using var handler = new StubHttpMessageHandler((_, _, _) =>
            throw new InvalidOperationException("No HTTP calls expected when --embed-endpoint is omitted."));
        using var client = new NonDisposingCheckHttpClient(handler);

        var opts = new RagCliOptions();
        var resolution = await RagCommandHandler.ResolveEmbeddingModelAsync(opts, httpClient: client);

        Assert.IsType<FastHashEmbeddingModel>(resolution.Model);
        Assert.Equal(384, resolution.Model.Dimensions);
        Assert.False(resolution.IsFallback);
        Assert.Empty(handler.Requests);
        Assert.False(client.WasDisposed);
    }

    [Theory]
    [InlineData("auto-npu")]
    [InlineData("npu")]
    public async Task RagResolveEmbeddingModel_AutoNpuSelectsEmbedModelProbesDimsAndPreservesClient(string alias)
    {
        using var handler = new StubHttpMessageHandler((req, body, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
            {
                return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"id\":\"qwen3-8b\"},{\"id\":\"embeddinggemma\"}]}");
            }
            if (path == "/v1/embeddings")
            {
                Assert.Contains("\"model\":\"embeddinggemma\"", body);
                Assert.Contains("dimension probe", body);
                return JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(16));
            }
            return JsonResponse(HttpStatusCode.NotFound, "{}");
        });
        using var client = new NonDisposingCheckHttpClient(handler);
        var candidates = new (string, NpuSidecarRuntime)[]
        {
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)
        };

        var opts = new RagCliOptions(EmbedEndpoint: alias);
        var resolution = await RagCommandHandler.ResolveEmbeddingModelAsync(
            opts,
            httpClient: client,
            npuCandidates: candidates,
            probeCacheTtl: TimeSpan.Zero);

        var openAi = Assert.IsType<OpenAiEmbeddingModel>(resolution.Model);
        Assert.Equal(16, openAi.Dimensions);
        Assert.Equal("embeddinggemma", openAi.ModelId);
        Assert.Equal("http://127.0.0.1:52625", openAi.BaseUrl);
        Assert.Equal(NpuSidecarRuntime.FastFlowLM, resolution.Runtime);
        Assert.False(resolution.IsFallback);
        Assert.False(client.WasDisposed);

        openAi.Dispose();
        Assert.False(client.WasDisposed);
    }

    [Fact]
    public async Task RagResolveEmbeddingModel_ExplicitUrlWithDimsSkipsDimensionProbe()
    {
        using var handler = new StubHttpMessageHandler((_, _, _) =>
            throw new InvalidOperationException("No HTTP calls expected when explicit endpoint + --embed-dims are supplied."));
        using var client = new NonDisposingCheckHttpClient(handler);

        var opts = new RagCliOptions(
            EmbedEndpoint: "http://127.0.0.1:8000/v1",
            EmbedModel: "nomic-embed",
            EmbedDims: 16);

        var resolution = await RagCommandHandler.ResolveEmbeddingModelAsync(opts, httpClient: client);

        var openAi = Assert.IsType<OpenAiEmbeddingModel>(resolution.Model);
        Assert.Equal(16, openAi.Dimensions);
        Assert.Equal("nomic-embed", openAi.ModelId);
        Assert.Equal("http://127.0.0.1:8000", openAi.BaseUrl);
        Assert.False(resolution.IsFallback);
        Assert.Empty(handler.Requests);
        Assert.False(client.WasDisposed);
    }

    [Fact]
    public async Task RagResolveEmbeddingModel_UnreachableAutoNpuAndHttpErrorsFallBackToFastHash()
    {
        int closedPort = GetUnusedLoopbackPort();
        var closedCandidates = new (string, NpuSidecarRuntime)[]
        {
            ($"http://127.0.0.1:{closedPort}", NpuSidecarRuntime.FastFlowLM)
        };

        // 1. Real closed loopback port
        var unreachableRes = await RagCommandHandler.ResolveEmbeddingModelAsync(
            new RagCliOptions(EmbedEndpoint: "auto-npu"),
            npuCandidates: closedCandidates,
            probeTimeout: TimeSpan.FromMilliseconds(250),
            probeCacheTtl: TimeSpan.Zero);

        Assert.IsType<FastHashEmbeddingModel>(unreachableRes.Model);
        Assert.Equal(384, unreachableRes.Model.Dimensions);
        Assert.True(unreachableRes.IsFallback);
        Assert.False(string.IsNullOrWhiteSpace(unreachableRes.FallbackReason));

        // 2. HTTP 500 on /v1/embeddings
        using var errorHandler = new StubHttpMessageHandler((req, _, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/models")
                return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"id\":\"embeddinggemma\"}]}");
            return JsonResponse(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
        });
        using var client = new NonDisposingCheckHttpClient(errorHandler);

        var errorRes = await RagCommandHandler.ResolveEmbeddingModelAsync(
            new RagCliOptions(EmbedEndpoint: "http://127.0.0.1:52625"),
            httpClient: client,
            probeCacheTtl: TimeSpan.Zero);

        Assert.IsType<FastHashEmbeddingModel>(errorRes.Model);
        Assert.True(errorRes.IsFallback);
        Assert.False(client.WasDisposed);

        // 3. Invalid non-HTTP URL
        var invalidRes = await RagCommandHandler.ResolveEmbeddingModelAsync(
            new RagCliOptions(EmbedEndpoint: "ftp://invalid-scheme:1234"),
            httpClient: client);

        Assert.IsType<FastHashEmbeddingModel>(invalidRes.Model);
        Assert.True(invalidRes.IsFallback);
        Assert.False(client.WasDisposed);
    }

    [Fact]
    public async Task RagExecuteAsync_EndToEndWithMockNpuAndFallbackSucceeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_rag_cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "npu_notes.txt");
        await File.WriteAllTextAsync(docFile, "AMD XDNA 2 NPU runs EmbeddingGemma locally via FastFlowLM without consuming GPU VRAM.");

        try
        {
            int embedCalls = 0;
            using var handler = new StubHttpMessageHandler((req, _, _) =>
            {
                string path = req.RequestUri!.AbsolutePath;
                if (path == "/v1/models")
                    return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"id\":\"embeddinggemma\"}]}");

                if (path == "/v1/embeddings")
                {
                    int idx = Interlocked.Increment(ref embedCalls);
                    return JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(8, seed: 1.0f + idx * 0.01f));
                }

                return JsonResponse(HttpStatusCode.NotFound, "{}");
            });
            using var client = new NonDisposingCheckHttpClient(handler);
            var candidates = new (string, NpuSidecarRuntime)[]
            {
                ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)
            };

            int exitCode = await RagCommandHandler.ExecuteAsync(
                ["--docs", docFile, "--query", "How does XDNA 2 run EmbeddingGemma?", "--embed-endpoint", "auto-npu"],
                httpClient: client,
                npuCandidates: candidates);

            Assert.Equal(0, exitCode);
            Assert.True(embedCalls >= 2); // probe + chunk + query
            Assert.False(client.WasDisposed);

            // Offline fallback execution also returns 0 cleanly
            int closedPort = GetUnusedLoopbackPort();
            int fallbackExit = await RagCommandHandler.ExecuteAsync(
                ["--docs", docFile, "--query", "What runs locally?", "--embed-endpoint", "auto-npu"],
                httpClient: null,
                npuCandidates: [($"http://127.0.0.1:{closedPort}", NpuSidecarRuntime.FastFlowLM)]);

            Assert.Equal(0, fallbackExit);

            // Invalid flag returns 1
            int invalidExit = await RagCommandHandler.ExecuteAsync(["--unknown-option"]);
            Assert.Equal(1, invalidExit);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RagExecuteAsync_ExplicitDeadPortWithDims4_FallsBackToFastHashAndExitsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_rag_deadport_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "npu_notes.txt");
        await File.WriteAllTextAsync(docFile, "AMD XDNA 2 NPU executes EmbeddingGemma locally without consuming GPU VRAM.");

        try
        {
            int deadPort = GetUnusedLoopbackPort();
            string deadEndpoint = $"http://127.0.0.1:{deadPort}";

            int exitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docFile,
                    "--query", "How does XDNA 2 execute?",
                    "--embed-endpoint", deadEndpoint,
                    "--embed-model", "embeddinggemma",
                    "--embed-dims", "4"
                ]);

            Assert.Equal(0, exitCode);

            using var callerClient = new NonDisposingCheckHttpClient(new SocketsHttpHandler());
            int clientExitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docFile,
                    "--query", "How does XDNA 2 execute?",
                    "--embed-endpoint", deadEndpoint,
                    "--embed-model", "embeddinggemma",
                    "--embed-dims", "4"
                ],
                httpClient: callerClient);

            Assert.Equal(0, clientExitCode);
            Assert.False(callerClient.WasDisposed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RagExecuteAsync_ExplicitDims4Http500_FallsBackToFastHashAndExitsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_rag_500_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "npu_notes.txt");
        await File.WriteAllTextAsync(docFile, "AMD XDNA 2 NPU runs EmbeddingGemma locally via FastFlowLM.");

        try
        {
            int embedCalls = 0;
            using var handler = new StubHttpMessageHandler((req, _, _) =>
            {
                if (req.RequestUri!.AbsolutePath == "/v1/embeddings")
                {
                    Interlocked.Increment(ref embedCalls);
                    return JsonResponse(HttpStatusCode.InternalServerError, "{\"error\":\"fail\"}");
                }
                return JsonResponse(HttpStatusCode.NotFound, "{}");
            });
            using var client = new NonDisposingCheckHttpClient(handler);

            int exitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docFile,
                    "--query", "XDNA2",
                    "--embed-endpoint", "http://127.0.0.1:52625",
                    "--embed-model", "embeddinggemma",
                    "--embed-dims", "4"
                ],
                httpClient: client);

            Assert.Equal(0, exitCode);
            Assert.Equal(1, embedCalls);
            Assert.False(client.WasDisposed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RagExecuteAsync_ExplicitDims4DimensionMismatch8_FallsBackToFastHashAndExitsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_rag_dim8_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "npu_notes.txt");
        await File.WriteAllTextAsync(docFile, "AMD XDNA 2 NPU runs EmbeddingGemma locally via FastFlowLM.");

        try
        {
            int embedCalls = 0;
            using var handler = new StubHttpMessageHandler((req, _, _) =>
            {
                if (req.RequestUri!.AbsolutePath == "/v1/embeddings")
                {
                    Interlocked.Increment(ref embedCalls);
                    return JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(8));
                }
                return JsonResponse(HttpStatusCode.NotFound, "{}");
            });
            using var client = new NonDisposingCheckHttpClient(handler);

            int exitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docFile,
                    "--query", "XDNA2",
                    "--embed-endpoint", "http://127.0.0.1:52625",
                    "--embed-model", "embeddinggemma",
                    "--embed-dims", "4"
                ],
                httpClient: client);

            Assert.Equal(0, exitCode);
            Assert.Equal(1, embedCalls);
            Assert.False(client.WasDisposed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RagExecuteAsync_Probe4ThenIndex8Mismatch_FallsBackToFastHashAndExitsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_rag_probe4_idx8_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "npu_notes.txt");
        await File.WriteAllTextAsync(docFile, "AMD XDNA 2 NPU runs EmbeddingGemma locally via FastFlowLM.");

        try
        {
            int embedCalls = 0;
            using var handler = new StubHttpMessageHandler((req, body, _) =>
            {
                if (req.RequestUri!.AbsolutePath == "/v1/embeddings")
                {
                    Interlocked.Increment(ref embedCalls);
                    int dims = body != null && body.Contains("dimension probe", StringComparison.Ordinal) ? 4 : 8;
                    return JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(dims));
                }
                return JsonResponse(HttpStatusCode.NotFound, "{}");
            });
            using var client = new NonDisposingCheckHttpClient(handler);

            int exitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docFile,
                    "--query", "XDNA2",
                    "--embed-endpoint", "http://127.0.0.1:52625",
                    "--embed-model", "embeddinggemma"
                ],
                httpClient: client);

            Assert.Equal(0, exitCode);
            Assert.Equal(2, embedCalls); // 1: dimension probe (4d), 2: doc chunk (8d mismatch -> fallback)
            Assert.False(client.WasDisposed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RagExecuteAsync_QueryPhaseFailureAfterSuccessfulIndex_FallsBackToFastHashAndExitsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_rag_query_fail_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "npu_notes.txt");
        await File.WriteAllTextAsync(docFile, "AMD XDNA 2 NPU runs EmbeddingGemma locally via FastFlowLM.");

        try
        {
            int embedCalls = 0;
            using var handler = new StubHttpMessageHandler((req, _, _) =>
            {
                if (req.RequestUri!.AbsolutePath == "/v1/embeddings")
                {
                    int call = Interlocked.Increment(ref embedCalls);
                    // 1: dimension probe (4d), 2: doc chunk (4d), 3: query fails with HTTP 500
                    return call <= 2
                        ? JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(4))
                        : JsonResponse(HttpStatusCode.InternalServerError, "{\"error\":\"query failure\"}");
                }
                return JsonResponse(HttpStatusCode.NotFound, "{}");
            });
            using var client = new NonDisposingCheckHttpClient(handler);

            int exitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docFile,
                    "--query", "XDNA2",
                    "--embed-endpoint", "http://127.0.0.1:52625",
                    "--embed-model", "embeddinggemma"
                ],
                httpClient: client);

            Assert.Equal(0, exitCode);
            Assert.Equal(3, embedCalls);
            Assert.False(client.WasDisposed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Feature #12: AgentCommandHandler Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void AgentParseOptions_ParsesNpuEndpointModelTokensAndPrompt()
    {
        var optsNpu = AgentCommandHandler.ParseOptions([
            "--npu",
            "--model", "qwen3-4b",
            "--api-key=secret-demo",
            "--max-steps=4",
            "--max-tokens", "256",
            "Inspect", "available", "devices"
        ]);

        Assert.Equal("auto-npu", optsNpu.Endpoint);
        Assert.Equal("qwen3-4b", optsNpu.Model);
        Assert.Equal("secret-demo", optsNpu.ApiKey);
        Assert.Equal(4, optsNpu.MaxSteps);
        Assert.Equal(256, optsNpu.MaxTokens);
        Assert.Equal("Inspect available devices", optsNpu.Prompt);

        var optsExplicit = AgentCommandHandler.ParseOptions([
            "--endpoint=http://127.0.0.1:8000",
            "-m", "llama-3.2",
            "-n", "128",
            "Run ping"
        ]);
        Assert.Equal("http://127.0.0.1:8000", optsExplicit.Endpoint);
        Assert.Equal("llama-3.2", optsExplicit.Model);
        Assert.Equal(128, optsExplicit.MaxTokens);
        Assert.Equal("Run ping", optsExplicit.Prompt);
    }

    [Theory]
    [InlineData(new object[] { new string[] { "--endpoint" } })]
    [InlineData(new object[] { new string[] { "--endpoint", "" } })]
    [InlineData(new object[] { new string[] { "--endpoint", "   " } })]
    [InlineData(new object[] { new string[] { "--api-key" } })]
    [InlineData(new object[] { new string[] { "--model" } })]
    [InlineData(new object[] { new string[] { "--model", "   " } })]
    [InlineData(new object[] { new string[] { "--max-steps", "0" } })]
    [InlineData(new object[] { new string[] { "--tokens", "-5" } })]
    [InlineData(new object[] { new string[] { "--unknown-flag", "hello" } })]
    public void AgentParseOptions_RejectsInvalidFlagsAndValues(string[] args)
    {
        Assert.Throws<ArgumentException>(() => AgentCommandHandler.ParseOptions(args));
    }

    [Fact]
    public async Task AgentExecuteAsync_WithNpuSidecarRunsToolLoopAndMemExNpuEmbeddings()
    {
        int chatStep = 0;
        int embedCalls = 0;

        using var handler = new StubHttpMessageHandler((req, body, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
            {
                return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"id\":\"embeddinggemma\"},{\"id\":\"qwen3-4b\"}]}");
            }
            if (path == "/v1/embeddings")
            {
                Interlocked.Increment(ref embedCalls);
                return JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(8));
            }
            if (path == "/v1/chat/completions")
            {
                Assert.Contains("\"model\":\"qwen3-4b\"", body);
                int step = Interlocked.Increment(ref chatStep);
                string contentJson = step == 1
                    ? "{\\\"tool\\\":\\\"devices\\\",\\\"arguments\\\":{}}"
                    : "Final report: AMD XDNA 2 NPU detected.";
                return JsonResponse(
                    HttpStatusCode.OK,
                    $"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{contentJson}\"}}}}]}}");
            }
            return JsonResponse(HttpStatusCode.NotFound, "{}");
        });

        using var client = new NonDisposingCheckHttpClient(handler);
        var candidates = new (string, NpuSidecarRuntime)[]
        {
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)
        };
        var mockDevices = new List<DeviceInfo>
        {
            new()
            {
                Id = "amd-xdna2-npu",
                Index = 0,
                Name = "AMD Ryzen AI XDNA 2 NPU",
                Vendor = GpuVendor.Amd,
                DedicatedVramBytes = 0,
                SharedVramBytes = (ulong)(15.5 * 1024 * 1024 * 1024),
                IsDisplayDevice = false,
                IsNpu = true,
                SupportedEngines = [InferenceEngineType.DirectML],
                RecommendedEngine = InferenceEngineType.DirectML,
                SafetyNotes = "NPU sidecar"
            }
        };

        int exitCode = await AgentCommandHandler.ExecuteAsync(
            ["--npu", "--max-steps", "4", "Inspect NPU devices"],
            httpClient: client,
            npuCandidates: candidates,
            deviceProvider: () => mockDevices);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, chatStep);
        Assert.True(embedCalls >= 2); // MemEx dimension probe + trace store embedding
        Assert.False(client.WasDisposed);
    }

    [Fact]
    public async Task AgentExecuteAsync_WithNpuSidecarContinuesWhenEmbeddingsEndpointFails()
    {
        int chatCalls = 0;
        using var handler = new StubHttpMessageHandler((req, _, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
                return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"id\":\"qwen3-4b\"}]}");
            if (path == "/v1/embeddings")
                return JsonResponse(HttpStatusCode.InternalServerError, "{\"error\":\"embeddings unavailable\"}");
            if (path == "/v1/chat/completions")
            {
                Interlocked.Increment(ref chatCalls);
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Completed with hash memory fallback.\"}}]}");
            }
            return JsonResponse(HttpStatusCode.NotFound, "{}");
        });

        using var client = new NonDisposingCheckHttpClient(handler);
        var candidates = new (string, NpuSidecarRuntime)[]
        {
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)
        };

        int exitCode = await AgentCommandHandler.ExecuteAsync(
            ["--endpoint", "auto-npu", "Say hello"],
            httpClient: client,
            npuCandidates: candidates);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, chatCalls);
        Assert.False(client.WasDisposed);
    }

    [Fact]
    public async Task AgentExecuteAsync_ExplicitEndpointWithRemoteModelSendsAuthAndSkipsModelsProbe()
    {
        using var handler = new StubHttpMessageHandler((req, body, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            Assert.Equal("/v1/chat/completions", path);
            Assert.Equal("Bearer test-api-key", req.Headers.Authorization?.ToString());
            Assert.Contains("\"model\":\"remote-qwen3\"", body);
            return JsonResponse(
                HttpStatusCode.OK,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Remote OK\"}}]}");
        });

        using var client = new NonDisposingCheckHttpClient(handler);

        int exitCode = await AgentCommandHandler.ExecuteAsync(
            ["--endpoint", "http://127.0.0.1:19001", "--model", "remote-qwen3", "--api-key", "test-api-key", "Ping remote"],
            httpClient: client);

        Assert.Equal(0, exitCode);
        Assert.Single(handler.Requests);
        Assert.False(client.WasDisposed);
    }

    [Fact]
    public async Task AgentExecuteAsync_OfflineNpuAndTransientFailureFallBackCleanly()
    {
        int closedPort = GetUnusedLoopbackPort();

        // 1. Unreachable --npu falls back cleanly to tool-directive execution
        int npuFallbackExit = await AgentCommandHandler.ExecuteAsync(
            ["--npu", "CALL ping: {}"],
            httpClient: null,
            npuCandidates: [($"http://127.0.0.1:{closedPort}", NpuSidecarRuntime.FastFlowLM)]);
        Assert.Equal(0, npuFallbackExit);

        // 2. Transient 503 during /v1/chat/completions falls back cleanly
        using var unavailableHandler = new StubHttpMessageHandler((_, _, _) =>
            JsonResponse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"busy\"}"));
        using var client = new NonDisposingCheckHttpClient(unavailableHandler);

        int transientExit = await AgentCommandHandler.ExecuteAsync(
            ["--endpoint", "http://127.0.0.1:19002", "--model", "qwen3", "CALL ping: {}"],
            httpClient: client);
        Assert.Equal(0, transientExit);
        Assert.False(client.WasDisposed);

        // 3. Local mode still rejects missing GGUF file
        int missingGgufExit = await AgentCommandHandler.ExecuteAsync(["--model", "does_not_exist_12345.gguf", "hello"]);
        Assert.Equal(1, missingGgufExit);

        // 4. Empty args / missing prompt return 1
        Assert.Equal(1, await AgentCommandHandler.ExecuteAsync([]));
        Assert.Equal(1, await AgentCommandHandler.ExecuteAsync(["--npu"]));
    }

    [Fact]
    public async Task AgentDevicesTool_IncludesIsNpuAndSharedVramGb()
    {
        var mockDevices = new List<DeviceInfo>
        {
            new()
            {
                Id = "amd-xdna2",
                Index = 0,
                Name = "AMD Ryzen AI NPU",
                Vendor = GpuVendor.Amd,
                DedicatedVramBytes = 0,
                SharedVramBytes = (ulong)(15.5 * 1024 * 1024 * 1024),
                IsDisplayDevice = false,
                IsNpu = true,
                SupportedEngines = [InferenceEngineType.DirectML],
                RecommendedEngine = InferenceEngineType.DirectML,
                SafetyNotes = "XDNA 2"
            },
            new()
            {
                Id = "cpu-0",
                Index = 1,
                Name = "Host CPU",
                Vendor = GpuVendor.Cpu,
                DedicatedVramBytes = 0,
                SharedVramBytes = 32UL * 1024 * 1024 * 1024,
                IsDisplayDevice = false,
                IsNpu = false,
                SupportedEngines = [InferenceEngineType.Cpu],
                RecommendedEngine = InferenceEngineType.Cpu,
                SafetyNotes = "CPU"
            }
        };

        using var engine = new AgentExecutionEngine("test-devices-agent");
        AgentCommandHandler.RegisterBuiltInTools(engine, () => mockDevices);

        AgentResponse response = await engine.ExecuteStepAsync(
            new AgentContext("sess-dev", "CALL devices: {}", StepCount: 1));

        Assert.Equal("devices", response.ToolCallName);
        Assert.False(response.IsCompleted);
        using var doc = JsonDocument.Parse(response.Output);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());

        var npu = doc.RootElement[0];
        Assert.Equal("amd-xdna2", npu.GetProperty("id").GetString());
        Assert.Equal("AMD Ryzen AI NPU", npu.GetProperty("name").GetString());
        Assert.Equal("Amd", npu.GetProperty("vendor").GetString());
        Assert.Equal(0.0, npu.GetProperty("dedicatedVramGb").GetDouble(), precision: 3);
        Assert.True(npu.GetProperty("isNpu").GetBoolean());
        Assert.Equal(15.5, npu.GetProperty("sharedVramGb").GetDouble(), precision: 2);

        var cpu = doc.RootElement[1];
        Assert.False(cpu.GetProperty("isNpu").GetBoolean());
        Assert.Equal(32.0, cpu.GetProperty("sharedVramGb").GetDouble(), precision: 2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AgentExecuteAsync_ExplicitEndpointAuthRejection_ReturnsExitCodeOneCleanly(HttpStatusCode status)
    {
        using var handler = new StubHttpMessageHandler((req, _, _) =>
        {
            Assert.Equal("/v1/chat/completions", req.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer wrong-key", req.Headers.Authorization?.ToString());
            return JsonResponse(status, "{\"error\":\"unauthorized\"}");
        });
        using var client = new NonDisposingCheckHttpClient(handler);

        int exitCode = await AgentCommandHandler.ExecuteAsync(
            ["--endpoint", "http://127.0.0.1:19005", "--model", "qwen3.5-9b", "--api-key", "wrong-key", "CALL ping: {}"],
            httpClient: client);

        Assert.Equal(1, exitCode);
        Assert.Single(handler.Requests);
        Assert.False(client.WasDisposed);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task AgentExecuteAsync_ExternalHttpClientWithApiKey_SendsBearerOnModelsAndDoesNotMutateOrDisposeClient()
    {
        NpuSidecarDiscovery.InvalidateCache();
        using var handler = new StubHttpMessageHandler((req, _, _) =>
        {
            Assert.Equal("Bearer secret-npu-key", req.Headers.Authorization?.ToString());
            string path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
            {
                return JsonResponse(HttpStatusCode.OK, "{\"data\":[{\"id\":\"qwen3.5-9b\"}]}");
            }
            if (path == "/v1/embeddings")
            {
                return JsonResponse(HttpStatusCode.OK, BuildEmbeddingResponseJson(8));
            }
            if (path == "/v1/chat/completions")
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Authenticated via external HttpClient.\"}}]}");
            }
            return JsonResponse(HttpStatusCode.NotFound, "{}");
        });

        using var externalClient = new NonDisposingCheckHttpClient(handler);
        Assert.Null(externalClient.DefaultRequestHeaders.Authorization);

        var candidates = new (string, NpuSidecarRuntime)[]
        {
            ("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)
        };

        int exitCode = await AgentCommandHandler.ExecuteAsync(
            ["--npu", "--api-key", "secret-npu-key", "Say hello"],
            httpClient: externalClient,
            npuCandidates: candidates);

        Assert.Equal(0, exitCode);
        Assert.Contains(handler.Requests, r => r.Uri.EndsWith("/v1/models", StringComparison.Ordinal) && r.AuthHeader == "Bearer secret-npu-key");
        Assert.Contains(handler.Requests, r => r.Uri.EndsWith("/v1/chat/completions", StringComparison.Ordinal) && r.AuthHeader == "Bearer secret-npu-key");
        Assert.Null(externalClient.DefaultRequestHeaders.Authorization);
        Assert.False(externalClient.WasDisposed);
    }

    [Fact]
    public async Task AgentExecuteAsync_MalformedApiKey_ReturnsExitCodeOneCleanly()
    {
        using var handler = new StubHttpMessageHandler((_, _, _) =>
            throw new InvalidOperationException("No HTTP call should be made for malformed --api-key."));
        using var client = new NonDisposingCheckHttpClient(handler);

        int exitCode = await AgentCommandHandler.ExecuteAsync(
            ["--endpoint", "http://127.0.0.1:19006", "--model", "qwen3.5-9b", "--api-key", "bad\r\nkey", "hello"],
            httpClient: client);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
        Assert.False(client.WasDisposed);
    }
}
