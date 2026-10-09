namespace Glacier.Cli.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using Glacier.Cli;
using Xunit;

[Collection("ConsoleTests")]
public class CliArgumentParsingTests
{
    [Fact]
    public async Task Program_NoArgs_ReturnsSuccessWithUsage()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main([]);
            Assert.Equal(0, code);
            Assert.Contains("GLACIER", sw.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task Program_UnknownCommand_ReturnsErrorCode()
    {
        var sw = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(sw);

        try
        {
            int code = await Program.Main(["non_existent_command_xyz"]);
            Assert.NotEqual(0, code);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Program_Run_WithoutModel_ReturnsErrorCode()
    {
        var sw = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(sw);

        try
        {
            int code = await Program.Main(["run"]);
            Assert.NotEqual(0, code);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Program_Tune_WithoutArgs_ReturnsErrorCode()
    {
        var sw = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(sw);

        try
        {
            int code = await Program.Main(["tune"]);
            Assert.NotEqual(0, code);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Program_Merge_WithoutArgs_ReturnsErrorCode()
    {
        var sw = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(sw);

        try
        {
            int code = await Program.Main(["merge"]);
            Assert.NotEqual(0, code);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Program_Pull_WithoutRepo_ReturnsErrorCode()
    {
        var sw = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(sw);

        try
        {
            int code = await Program.Main(["pull"]);
            Assert.NotEqual(0, code);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Program_DevicesCommand_ListsHardware()
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            int code = await Program.Main(["devices"]);
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("GLACIER DETECTED ACCELERATORS", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }
}
