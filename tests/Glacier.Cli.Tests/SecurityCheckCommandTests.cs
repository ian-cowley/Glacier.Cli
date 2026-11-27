namespace Glacier.Cli.Tests;

using System;
using System.IO;
using System.Text;
using Glacier.Cli.Commands;
using Xunit;

public sealed class SecurityCheckCommandTests
{
    [Fact]
    public void ScanPath_DetectsProviderKeysConnectionStringsRawHexAndGithubWorkflows()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string workflowDir = Path.Combine(tempDir, ".github", "workflows");
        Directory.CreateDirectory(workflowDir);

        try
        {
            string googleFake = string.Concat("AIza", "Sy", new string('A', 33));
            string workflowFile = Path.Combine(workflowDir, "ci.yml");
            File.WriteAllText(workflowFile, $"env:\n  GEMINI_KEY: \"{googleFake}\"\n");

            string appConfig = Path.Combine(tempDir, "config.cs");
            string connPass = string.Concat("Pass", "word=", "SuperSecret", "DbPass99;");
            string rawPrivKey = string.Concat("priv", "ate_key = \"0x", new string('a', 64), "\"");
            File.WriteAllText(appConfig, $"{connPass}\n{rawPrivKey}\n");

            // UTF-16 LE encoded config with an OpenAI-shaped test fixture
            string openAiFake = string.Concat("sk-", "proj", new string('B', 24));
            string utf16File = Path.Combine(tempDir, "unicode.env");
            File.WriteAllText(utf16File, $"OPENAI_KEY={openAiFake}\n", Encoding.Unicode);

            var summary = SecurityCheckCommandHandler.ScanPath(tempDir);
            Assert.True(summary.FilesScanned >= 3);
            Assert.Contains(summary.Findings, f => f.RelativePath.Contains("ci.yml") && f.SecretType.Contains("Google AI"));
            Assert.Contains(summary.Findings, f => f.RelativePath.Contains("config.cs") && f.SecretType.Contains("Connection String"));
            Assert.Contains(summary.Findings, f => f.RelativePath.Contains("config.cs") && f.SecretType.Contains("Raw Hex"));
            Assert.Contains(summary.Findings, f => f.RelativePath.Contains("unicode.env") && f.SecretType.Contains("OpenAI"));

            int exitCode = SecurityCheckCommandHandler.Execute([tempDir]);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ScanPath_AllowsExplicitPlaceholdersAndCleanCode()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_clean_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string csFile = Path.Combine(tempDir, "Safe.cs");
            string content =
                "var a = Environment.GetEnvironmentVariable(\"GEMINI_API_KEY\");\n" +
                "string api_key = \"YOUR_API_KEY_PLACEHOLDER\";\n" +
                "ReadOnlySpan<byte> password = \"UserMasterPassword\"u8;\n";
            File.WriteAllText(csFile, content);

            int exitCode = SecurityCheckCommandHandler.Execute([tempDir]);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Execute_InvalidPath_ReturnsExitCodeTwo()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}");
        int exitCode = SecurityCheckCommandHandler.Execute([missing]);
        Assert.Equal(2, exitCode);
    }
}
