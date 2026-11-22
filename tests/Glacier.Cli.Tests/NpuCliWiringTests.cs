namespace Glacier.Cli.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Cli.Commands;
using Glacier.Inference.Hardware;
using Xunit;

public sealed class NpuCliWiringTests
{
    private sealed class StubNpuModelsHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubNpuModelsHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public void BounceParseOptions_NpuCriticFlag_SetsAutoNpuSentinel()
    {
        var opts = BounceCommandHandler.ParseOptions(["--primary", "http://127.0.0.1:11434", "--npu-critic"]);
        Assert.Equal("auto-npu", opts.CriticEndpoint);
    }

    [Fact]
    public async Task ResolveCriticEndpointAsync_AutoNpu_DiscoversLoopbackSidecar()
    {
        using var handler = new StubNpuModelsHandler(req =>
        {
            if (req.Method == HttpMethod.Get &&
                string.Equals(req.RequestUri?.ToString(), "http://127.0.0.1:52625/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                const string json = """{"object":"list","data":[{"id":"qwen3-npu"}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);

        var (endpoint, discovered) = await BounceCommandHandler.ResolveCriticEndpointAsync(
            "auto-npu",
            http);

        Assert.Equal("http://127.0.0.1:52625", endpoint);
        Assert.NotNull(discovered);
        Assert.Equal("http://127.0.0.1:52625", discovered.BaseUrl);
        Assert.Equal(NpuSidecarRuntime.FastFlowLM, discovered.Runtime);
        Assert.Equal("qwen3-npu", discovered.PrimaryModel);
    }

    [Fact]
    public async Task ResolveCriticEndpointAsync_ExplicitUrl_PassesThroughWithoutProbing()
    {
        using var handler = new StubNpuModelsHandler(_ =>
            throw new InvalidOperationException("Explicit critic URL must not trigger HTTP discovery probing."));
        using var http = new HttpClient(handler);

        var (endpoint, discovered) = await BounceCommandHandler.ResolveCriticEndpointAsync(
            "http://192.168.1.50:11434",
            http);

        Assert.Equal("http://192.168.1.50:11434", endpoint);
        Assert.Null(discovered);
    }
}
