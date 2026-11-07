namespace Glacier.Cli.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Cli;
using Glacier.Cli.Commands;
using Glacier.Rag.Engine;
using Xunit;

[Collection("ConsoleTests")]
public class CliIntegrationTests
{
    [Fact]
    public async Task Program_Help_DisplaysBannerAndAllCommands()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main(["--help"]);
            Assert.Equal(0, code);

            string output = sw.ToString();
            Assert.Contains("GLACIER - Unified Pure C# .NET 10 LLM Stack", output);
            Assert.Contains("run", output);
            Assert.Contains("tune", output);
            Assert.Contains("merge", output);
            Assert.Contains("rag", output);
            Assert.Contains("serve", output);
            Assert.Contains("pull", output);
            Assert.Contains("agent", output);
            Assert.Contains("devices", output);
            Assert.Contains("inspect", output);
            Assert.Contains("bench", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("tune")]
    [InlineData("merge")]
    [InlineData("rag")]
    [InlineData("serve")]
    [InlineData("pull")]
    [InlineData("agent")]
    [InlineData("devices")]
    [InlineData("inspect")]
    [InlineData("bench")]
    [InlineData("config")]
    public async Task Program_SubcommandHelp_OutputsHelpCleanly(string cmd)
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main([cmd, "--help"]);
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.NotEmpty(output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task Program_UnknownCommand_ReturnsError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main(["nonexistent-command"]);
            Assert.Equal(1, code);
            Assert.Contains("Unknown command", sw.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task RunCommand_MissingModel_ReturnsError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await RunCommandHandler.ExecuteAsync([]);
            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task TuneCommand_MissingDataset_ReturnsError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await TuneCommandHandler.ExecuteAsync(["dummy.gguf", "--data", "nonexistent.jsonl"]);
            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void MergeCommand_MissingFiles_ReturnsError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = MergeCommandHandler.Execute(["nonexistent_base.gguf", "nonexistent_adapter.bin", "out.gguf"]);
            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task ServeCommand_MissingModel_ReturnsError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await ServeCommandHandler.ExecuteAsync(["nonexistent.gguf"]);
            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void ServeCommand_ParseOptions_CapturesHostPortAndCapacity()
    {
        var opts = ServeCommandHandler.ParseOptions([
            "model.gguf",
            "--host", "0.0.0.0",
            "--port", "8088",
            "--blocks", "512",
            "-b", "8",
            "--name", "custom-llama"
        ]);

        Assert.Equal("model.gguf", opts.ModelPath);
        Assert.Equal("0.0.0.0", opts.Host);
        Assert.Equal(8088, opts.Port);
        Assert.Equal(512, opts.TotalBlocks);
        Assert.Equal(8, opts.MaxBatch);
        Assert.Equal("custom-llama", opts.ModelName);
    }

    [Fact]
    public void PullCommand_ResolveSafeDestinationPath_BlocksPathTraversal()
    {
        string tempOut = Path.Combine(Path.GetTempPath(), "glacier_pull_test_" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => PullCommandHandler.ResolveSafeDestinationPath(tempOut, "../evil.gguf"));
        Assert.Throws<ArgumentException>(() => PullCommandHandler.ResolveSafeDestinationPath(tempOut, "..\\..\\evil.gguf"));

        string validPath = PullCommandHandler.ResolveSafeDestinationPath(tempOut, "sub/qwen-q4_k_m.gguf");
        Assert.Equal(Path.Combine(Path.GetFullPath(tempOut), "qwen-q4_k_m.gguf"), validPath);
    }

    [Fact]
    public async Task PullCommand_InvalidGgufMagic_FailsClosedAndDeletesTempFile()
    {
        string tempOut = Path.Combine(Path.GetTempPath(), "glacier_pull_bad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempOut);

        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            using var handler = new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.ASCII.GetBytes("HTML_NOT_GGUF_PAYLOAD"))
                });

            int exitCode = await PullCommandHandler.ExecuteAsync(
                ["https://example.invalid/bad.gguf", "--out", tempOut],
                handler);

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(Path.Combine(tempOut, "bad.gguf")));
            Assert.False(File.Exists(Path.Combine(tempOut, "bad.gguf.part")));
        }
        finally
        {
            Console.SetOut(origOut);
            try { Directory.Delete(tempOut, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PullCommand_ValidGgufAndPartialResume_VerifiesHeaderAndMovesAtomically()
    {
        string tempOut = Path.Combine(Path.GetTempPath(), "glacier_pull_ok_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempOut);

        string partFile = Path.Combine(tempOut, "valid.gguf.part");
        await File.WriteAllBytesAsync(partFile, Encoding.ASCII.GetBytes("GG"));

        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            long? observedRangeFrom = null;
            using var handler = new StubHttpMessageHandler(req =>
            {
                observedRangeFrom = req.Headers.Range?.Ranges.Count > 0
                    ? req.Headers.Range.Ranges.GetEnumerator() is var e && e.MoveNext() ? e.Current.From : null
                    : null;
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(Encoding.ASCII.GetBytes("UF\x03\x00\x00\x00"))
                };
            });

            int exitCode = await PullCommandHandler.ExecuteAsync(
                ["https://example.invalid/valid.gguf", "--out", tempOut],
                handler);

            Assert.Equal(0, exitCode);
            Assert.Equal(2L, observedRangeFrom);
            string finalFile = Path.Combine(tempOut, "valid.gguf");
            Assert.True(File.Exists(finalFile));
            Assert.False(File.Exists(partFile));
            byte[] finalBytes = await File.ReadAllBytesAsync(finalFile);
            Assert.Equal("GGUF", Encoding.ASCII.GetString(finalBytes, 0, 4));
        }
        finally
        {
            Console.SetOut(origOut);
            try { Directory.Delete(tempOut, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Diagnostics_DevicesAudit_CompletesCleanly()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = DiagnosticsCommandHandlers.RunDevices([]);
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("GLACIER DETECTED ACCELERATORS & ENGINES", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void Diagnostics_InspectCommand_RunsOnEnvConfiguredModel()
    {
        string? modelPath = Environment.GetEnvironmentVariable("GLACIER_TEST_GGUF_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return;

        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = DiagnosticsCommandHandlers.RunInspect([modelPath]);
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("Architecture Family:", output);
            Assert.Contains("Transformer Layers:", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void Rag_InProcessEngine_IndexesAndRetrievesCorrectly()
    {
        using var rag = new GraphRagEngine();
        string text = "Glacier.Serve is a .NET 10 continuous batching engine. It delivers PagedAttention with zero fragmentation.";
        int chunks = rag.IndexDocument("doc1.txt", text);

        Assert.True(chunks > 0);
        Assert.True(rag.GraphNodeCount > 0);
        Assert.True(rag.IndexedChunksCount > 0);

        var result = rag.Retrieve("What is Glacier.Serve?");
        Assert.NotNull(result);
        Assert.NotEmpty(result.DiscoveredEntities);
        Assert.NotEmpty(result.VectorMatches);
        Assert.Contains("PagedAttention", result.SynthesizedContext);
    }

    [Fact]
    public async Task AgentCommand_ExecutesAutonomousPromptAndBuiltInPingTool()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main(["agent", "CALL ping: {}"]);
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("GLACIER.AGENT - Autonomous Agent Runtime", output);
            Assert.Contains("\"status\":\"pong\"", output);
            Assert.Contains("Completed in", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
