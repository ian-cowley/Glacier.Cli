namespace Glacier.Cli.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using Glacier.Cli;
using Glacier.Cli.Commands;
using Glacier.Rag.Engine;
using Xunit;

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
    public void Diagnostics_InspectCommand_RunsOnExistingModel()
    {
        string modelPath = @"D:\lmstudio\models\lmstudio-community\Meta-Llama-3.1-8B-Instruct-GGUF\Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf";
        if (!File.Exists(modelPath)) return; // Skip if test model not present

        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = DiagnosticsCommandHandlers.RunInspect([modelPath]);
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("Architecture Family:  Llama", output);
            Assert.Contains("Transformer Layers:   32", output);
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
}
