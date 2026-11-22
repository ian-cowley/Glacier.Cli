namespace Glacier.Cli.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Cli.Commands;
using Xunit;

[Collection("ConsoleTests")]
public sealed class Tier5M7Phase2HardeningTests
{
    private sealed class StubCliHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> _responder;
        public ConcurrentQueue<(string Url, string? Auth)> Requests { get; } = new();

        public StubCliHandler(Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
            Requests.Enqueue((request.RequestUri?.ToString() ?? "", request.Headers.Authorization?.ToString()));
            return await _responder(request, body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task RagCommandHandler_EmbedEndpointWithQueryString_AndAuthenticatedApiKey_PreservesQueryAndForwardsBearerWithoutMutatingSharedClient()
    {
        string docsDir = Directory.CreateTempSubdirectory("tier5_rag_cli_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(docsDir, "guide.md"), "Glacier RAG endpoint normalization and bearer auth test.");

            using var handler = new StubCliHandler((req, _, _) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embed-q"}]}"""));
                if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(req.Headers.Authorization?.ToString(), "Bearer cli-secret-key", StringComparison.Ordinal))
                        return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"error":"missing key"}"""));
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[0.1,0.2,0.3,0.4]}]}"""));
                }
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            });
            using var sharedHttp = new HttpClient(handler);

            int exitCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docsDir,
                    "--embed-endpoint", "http://127.0.0.1:52625/v1?k=1",
                    "--embed-model", "embed-q",
                    "--api-key", "cli-secret-key",
                    "--query", "auth test"
                ],
                httpClient: sharedHttp);

            Assert.Equal(0, exitCode);
            Assert.Null(sharedHttp.DefaultRequestHeaders.Authorization);

            var embedReqs = handler.Requests.Where(r => r.Url.Contains("/embeddings", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(embedReqs);
            Assert.All(embedReqs, r =>
            {
                Assert.Equal("http://127.0.0.1:52625/v1/embeddings?k=1", r.Url);
                Assert.Equal("Bearer cli-secret-key", r.Auth);
            });
        }
        finally
        {
            if (Directory.Exists(docsDir)) Directory.Delete(docsDir, recursive: true);
        }
    }

    [Fact]
    public async Task RagCommandHandler_CancellationMidIndexing_AbortsCleanlyAndForwardsToken()
    {
        string docsDir = Directory.CreateTempSubdirectory("tier5_rag_cancel_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(docsDir, "doc.md"), "Slow embedding document for cancellation verification.");

            var embedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubCliHandler(async (req, _, ct) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.OK, """{"data":[{"id":"embed-q"}]}""");
                if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    embedEntered.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[0.1,0.2]}]}""");
                }
                return Json(HttpStatusCode.NotFound, "{}");
            });
            using var http = new HttpClient(handler);
            using var cts = new CancellationTokenSource();

            Task<int> execTask = RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docsDir,
                    "--embed-endpoint", "http://127.0.0.1:52625/v1",
                    "--embed-model", "embed-q",
                    "--embed-dims", "2",
                    "--query", "cancel check"
                ],
                httpClient: http,
                cancellationToken: cts.Token);

            await embedEntered.Task;
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execTask.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            if (Directory.Exists(docsDir)) Directory.Delete(docsDir, recursive: true);
        }
    }

    [Fact]
    public async Task AgentCommandHandler_Persistent429And503_DeterministicExitCodes_AndConcurrentApiKeysDoNotMutateHttpClient()
    {
        // Persistent 429 / 500 -> clean exit code 1 (no unhandled HttpRequestException)
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            using var errH = new StubCliHandler((_, _, _) =>
                Task.FromResult(Json(status, """{"error":"overloaded"}""")));
            using var errClient = new HttpClient(errH);
            int code = await AgentCommandHandler.ExecuteAsync(
                ["--endpoint", "http://127.0.0.1:52625/v1", "--model", "qwen3-4b", "hello"],
                httpClient: errClient);
            Assert.Equal(1, code);
        }

        // Transient 503 -> falls back cleanly to local/directive execution (exit 0) without unhandled exception
        using var transientH = new StubCliHandler((_, _, _) =>
            Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""")));
        using var transientClient = new HttpClient(transientH);
        int transientCode = await AgentCommandHandler.ExecuteAsync(
            ["--endpoint", "http://127.0.0.1:52625/v1", "--model", "qwen3-4b", "CALL ping: {}"],
            httpClient: transientClient);
        Assert.Equal(0, transientCode);

        // Concurrent parallel AgentCommandHandler runs with distinct --api-key headers on one shared HttpClient
        using var authHandler = new StubCliHandler((_, _, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"FINAL: ok"}}]}""")));
        using var sharedHttp = new HttpClient(authHandler);

        int[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            AgentCommandHandler.ExecuteAsync(
                ["--endpoint", "http://127.0.0.1:52625/v1", "--model", "qwen3-4b", "--api-key", $"key-{i}", $"prompt {i}"],
                httpClient: sharedHttp)));

        Assert.All(results, c => Assert.Equal(0, c));
        Assert.Null(sharedHttp.DefaultRequestHeaders.Authorization);
        var distinctAuths = authHandler.Requests.Select(r => r.Auth).Where(a => a != null).Distinct().ToArray();
        Assert.Equal(8, distinctAuths.Length);
    }

    [Fact]
    public async Task RagAndAgentHandlers_QueryStringEndpointWithoutExplicitModel_DiscoverServedModel()
    {
        string docsDir = Directory.CreateTempSubdirectory("tier5_qs_discover_").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(docsDir, "guide.md"), "NPU sidecar query string discovery verification.");

            var embedModelSeen = new ConcurrentQueue<string?>();
            var chatModelSeen = new ConcurrentQueue<string?>();

            using var handler = new StubCliHandler((req, body, _) =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embed-q"},{"id":"qwen3-npu"}]}"""));
                }
                if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(body ?? "{}");
                    embedModelSeen.Enqueue(doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null);
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[0.2,0.4,0.6,0.8]}]}"""));
                }
                if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(body ?? "{}");
                    chatModelSeen.Enqueue(doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null);
                    return Task.FromResult(Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"FINAL: discovered-ok"}}]}"""));
                }
                return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
            });
            using var http = new HttpClient(handler);

            int ragCode = await RagCommandHandler.ExecuteAsync(
                [
                    "--docs", docsDir,
                    "--embed-endpoint", "http://127.0.0.1:52625/v1?api-version=1",
                    "--query", "discovery"
                ],
                httpClient: http);
            Assert.Equal(0, ragCode);
            Assert.Contains("http://127.0.0.1:52625/v1/models?api-version=1", handler.Requests.Select(r => r.Url));
            Assert.Contains("http://127.0.0.1:52625/v1/embeddings?api-version=1", handler.Requests.Select(r => r.Url));
            Assert.Contains("embed-q", embedModelSeen);

            int agentCode = await AgentCommandHandler.ExecuteAsync(
                [
                    "--endpoint", "http://127.0.0.1:52625/v1?api-version=1",
                    "hello"
                ],
                httpClient: http);
            Assert.Equal(0, agentCode);
            Assert.Contains("http://127.0.0.1:52625/v1/chat/completions?api-version=1", handler.Requests.Select(r => r.Url));
            Assert.Contains("qwen3-npu", chatModelSeen);
        }
        finally
        {
            if (Directory.Exists(docsDir)) Directory.Delete(docsDir, recursive: true);
        }
    }
}
