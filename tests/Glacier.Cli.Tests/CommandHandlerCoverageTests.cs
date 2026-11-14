namespace Glacier.Cli.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using Glacier.Cli;
using Glacier.Cli.Commands;
using Xunit;

[Collection("ConsoleTests")]
public class CommandHandlerCoverageTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("rag")]
    [InlineData("agent")]
    [InlineData("config")]
    [InlineData("unknown_subcmd")]
    public async Task Program_HelpSubcommandSyntax_ReturnsZero(string subcommand)
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main(["help", subcommand]);
            Assert.Equal(0, code);
            Assert.NotEmpty(sw.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task AgentCommand_EmptyArgsAndMissingPromptAndMissingModel_ReturnError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            Assert.Equal(1, await AgentCommandHandler.ExecuteAsync([]));
            Assert.Equal(1, await AgentCommandHandler.ExecuteAsync(["--model", "nonexistent.gguf"]));
            Assert.Equal(1, await AgentCommandHandler.ExecuteAsync(["--model", "nonexistent.gguf", "hello"]));
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task AgentCommand_OfflineMockExecution_ReturnsZero()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await AgentCommandHandler.ExecuteAsync(["--max-steps", "2", "-n", "32", "summarize status"]);
            Assert.Equal(0, code);
            Assert.Contains("[Agent Output]", sw.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task RagCommand_MissingModel_ReturnsError()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await RagCommandHandler.ExecuteAsync(["--model", "missing_model.gguf", "--query", "test"]);
            Assert.Equal(1, code);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task RagCommand_SingleFileAndDirectoryDocs_SingleShotQuery_ReturnsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_cli_rag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string docFile = Path.Combine(tempDir, "notes.txt");
        await File.WriteAllTextAsync(docFile, "Glacier GraphRAG uses CSR graphs and SIMD vector search.");

        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int fileCode = await RagCommandHandler.ExecuteAsync(["--docs", docFile, "--query", "Glacier", "-k", "2", "--hops", "1"]);
            Assert.Equal(0, fileCode);

            int dirCode = await RagCommandHandler.ExecuteAsync(["--docs", tempDir, "--query", "SIMD"]);
            Assert.Equal(0, dirCode);
        }
        finally
        {
            Console.SetOut(origOut);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DiagnosticsCommands_InspectBenchConfig_ErrorAndReadOnlyPaths()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            Assert.Equal(1, DiagnosticsCommandHandlers.RunInspect([]));
            Assert.Equal(1, DiagnosticsCommandHandlers.RunInspect(["missing.gguf"]));
            Assert.Equal(1, await DiagnosticsCommandHandlers.RunBenchAsync([]));
            Assert.Equal(1, await DiagnosticsCommandHandlers.RunBenchAsync(["missing.gguf"]));

            Assert.Equal(0, DiagnosticsCommandHandlers.RunConfig([]));
            Assert.Contains("GLACIER PERSISTENT SETTINGS", sw.ToString());
            Assert.Equal(1, DiagnosticsCommandHandlers.RunConfig(["--unknown-flag"]));
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }
}
