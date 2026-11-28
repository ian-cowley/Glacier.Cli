using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Clavier.Engine;
using Glacier.Cli.Commands;
using Xunit;

namespace Glacier.Cli.Tests;

public class CliM4HardeningTests
{
    [Fact]
    public void AgentCommand_ClavierRouter_DefaultsNullAndLoadsValidCheckpoint()
    {
        AgentCliOptions defaultOpts = AgentCommandHandler.ParseOptions(["hello"]);
        Assert.Null(defaultOpts.ClavierModel);
        Assert.Null(AgentCommandHandler.LoadOptionalClavierSession(null));

        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_clvr_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string ckptPath = Path.Combine(tempDir, "router.clvr");
            using (var session = new ClavierSession(embeddingDim: 8, hiddenDim: 8, numActions: 3))
            using (var fs = File.Create(ckptPath))
            {
                session.SaveCheckpoint(fs);
            }

            AgentCliOptions parsed = AgentCommandHandler.ParseOptions(["--clavier-model", ckptPath, "run task"]);
            Assert.Equal(ckptPath, parsed.ClavierModel);

            using ClavierSession? loaded = AgentCommandHandler.LoadOptionalClavierSession(parsed.ClavierModel);
            Assert.NotNull(loaded);
            Assert.Equal(8, loaded!.EmbeddingDim);
            Assert.Equal(8, loaded.HiddenDim);
            Assert.Equal(3, loaded.NumActions);

            string missingPath = Path.Combine(tempDir, "missing.clvr");
            Assert.Throws<FileNotFoundException>(() => AgentCommandHandler.LoadOptionalClavierSession(missingPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PullCommand_ParallelRangeDownloadAndSha256_VerifiesAndRejectsTamperedPayload()
    {
        byte[] payload = new byte[4096];
        "GGUF"u8.CopyTo(payload);
        for (int i = 4; i < payload.Length; i++)
            payload[i] = (byte)(i & 0xFF);

        string expectedHex = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string wrongHex = new('0', 64);

        int rangeGetsObserved = 0;
        using var mockHandler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK);
                head.Headers.AcceptRanges.Add("bytes");
                head.Content = new ByteArrayContent([]);
                head.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(head);
            }

            if (req.Headers.Range is { Ranges.Count: 1 } range)
            {
                Interlocked.Increment(ref rangeGetsObserved);
                var item = System.Linq.Enumerable.First(range.Ranges);
                long from = item.From ?? 0;
                long to = item.To ?? (payload.Length - 1);
                int len = (int)(to - from + 1);
                var slice = payload.AsSpan((int)from, len).ToArray();
                var res = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                res.Content.Headers.ContentLength = slice.Length;
                res.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
                return Task.FromResult(res);
            }

            var fullRes = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            fullRes.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(fullRes);
        });

        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_pull_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Valid SHA-256 + parallel Range download
            int okExit = await PullCommandHandler.ExecuteAsync(
                ["https://example.invalid/test.gguf", "--out", tempDir, "--file", "ok.gguf", "--parallel", "2", "--sha256", expectedHex],
                mockHandler,
                CancellationToken.None,
                parallelThresholdBytes: 1024);

            Assert.Equal(0, okExit);
            Assert.True(rangeGetsObserved >= 2);
            string okPath = Path.Combine(tempDir, "ok.gguf");
            Assert.True(File.Exists(okPath));
            Assert.Equal(payload, await File.ReadAllBytesAsync(okPath));

            // 2. SHA-256 mismatch must fail and clean up .part
            int badExit = await PullCommandHandler.ExecuteAsync(
                ["https://example.invalid/test.gguf", "--out", tempDir, "--file", "bad.gguf", "--sha256", wrongHex],
                mockHandler,
                CancellationToken.None,
                parallelThresholdBytes: 1024);

            Assert.Equal(1, badExit);
            Assert.False(File.Exists(Path.Combine(tempDir, "bad.gguf")));
            Assert.False(File.Exists(Path.Combine(tempDir, "bad.gguf.part")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TuneAndMergeHandlers_InvalidNumericOrMissingValues_ReturnExit1Cleanly()
    {
        Assert.Throws<ArgumentException>(() => TuneCommandHandler.ParseOptions(["--rank", "notanumber"]));
        Assert.Throws<ArgumentException>(() => TuneCommandHandler.ParseOptions(["--lr", "-0.1"]));
        Assert.Throws<ArgumentException>(() => MergeCommandHandler.ParseOptions(["a.gguf", "b.bin", "c.gguf", "--threads", "0"]));

        int tuneExit = await TuneCommandHandler.ExecuteAsync(["--rank", "abc"]);
        int mergeExit = MergeCommandHandler.Execute(["a.gguf", "b.bin", "c.gguf", "--threads", "abc"]);

        Assert.Equal(1, tuneExit);
        Assert.Equal(1, mergeExit);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
