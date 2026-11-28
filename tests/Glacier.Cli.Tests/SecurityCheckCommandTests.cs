namespace Glacier.Cli.Tests;

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Glacier.Cli.Commands;
using Xunit;

[Collection("ConsoleTests")]
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

    [Fact]
    public void ScanPath_DetectsNuGetApiKey_ConstructedDynamically()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_nuget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string nugetKey = string.Concat("oy2", new string('a', 20), new string('7', 23));
            File.WriteAllText(Path.Combine(tempDir, "publish.yml"), $"NUGET_KEY={nugetKey}\n");

            var summary = SecurityCheckCommandHandler.ScanPath(tempDir);
            Assert.Contains(summary.Findings, f => f.SecretType == "NuGet API Key" && f.RelativePath == "publish.yml");
            Assert.Equal(1, SecurityCheckCommandHandler.Execute([tempDir]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ScanPath_EliminatesFalsePositives_WhileCatchingRealLeaks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_fp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Code assignments, interpolated env names, localhost benchmark fixtures, and CI shell expansions
            string safeCs = string.Join('\n',
                "Span<byte> password = stackalloc byte[32];",
                "byte[] password2 = Encoding.UTF8.GetBytes(\"password\");",
                "byte[] pwd = System.Text.Encoding.UTF8.GetBytes(\"secure_password\");",
                "string cs = $\"DATABASE_PASSWORD={dbPass}\\n\";",
                "string bench = \"Host=localhost;Port=5432;Database=twitter_bench;Username=postgres;Password=postgres;Pooling=true\";");
            File.WriteAllText(Path.Combine(tempDir, "SafeCode.cs"), safeCs);

            string safePy = string.Join('\n',
                "password = str(jb.get('password', ''))",
                "password = form.get('password', [''])[0]",
                "password = sys.argv[2]",
                "password = getpass.getpass(\"Enter: \")");
            File.WriteAllText(Path.Combine(tempDir, "safe_auth.py"), safePy);

            string safeSh = "PGPASSWORD=benchpass psql -h localhost -U benchuser -d twitter_bench\n" +
                            "export DB_CONNECTION_STRING=\"Host=127.0.0.1;Port=5432;Username=benchuser;Password=benchpass;Pooling=true\"\n";
            File.WriteAllText(Path.Combine(tempDir, "bench.sh"), safeSh);

            string safeWorkflowDir = Path.Combine(tempDir, ".github", "workflows");
            Directory.CreateDirectory(safeWorkflowDir);
            File.WriteAllText(
                Path.Combine(safeWorkflowDir, "publish.yml"),
                "env:\n  API_KEY: \"${OIDC_KEY:-$FALLBACK_KEY}\"\n  GH_TOK: \"${{ secrets.GITHUB_TOKEN }}\"\n");

            File.WriteAllText(
                Path.Combine(tempDir, ".env.example"),
                "API_KEY=${OIDC_KEY:-$FALLBACK_KEY}\nRUNTIME_KEY=${RUNTIME_ENV_SECRET}\nCI_KEY=${{ secrets.FOO }}\n");

            string templateConn = "Host=prod.db;Password={dbPass};\n";
            File.WriteAllText(Path.Combine(tempDir, "Template.cs"), templateConn);

            var cleanSummary = SecurityCheckCommandHandler.ScanPath(tempDir);
            Assert.Empty(cleanSummary.Findings);
            Assert.Equal(0, SecurityCheckCommandHandler.Execute([tempDir]));

            // Real leaks that MUST still be caught:
            string leakDir = Path.Combine(tempDir, "real_leaks");
            Directory.CreateDirectory(leakDir);
            string prodConn = string.Concat("Host=prod.internal;Username=postgres;Pass", "word=", "postgres;");
            string localhostRealPass = string.Concat("Host=localhost;Username=postgres;Pass", "word=", "RealProd", "Secret99;");
            string localhostDualBypass = string.Concat("Host=localhost;Pass", "word=", "RealSecret99;", "Pw", "d=postgres");
            string localhostTrailingReal = string.Concat("Host=localhost;", "Pw", "d=postgres;Pass", "word=", "RealSecret99;");
            string dollarBraceConn = string.Concat("Host=prod.db;Pass", "word=", "My$ecret{Pa}99;");
            string dollarBraceUri = string.Concat("postgres://user:", "My$ecret{Pa}99", "@prod.db:5432/db");
            string shellHardcodedFallback = string.Concat("API_KEY=\"${OIDC_KEY:-", "RealHardcoded", "Secret99}\"");
            File.WriteAllText(
                Path.Combine(leakDir, "leaks.txt"),
                string.Join('\n',
                    prodConn,
                    localhostRealPass,
                    localhostDualBypass,
                    localhostTrailingReal,
                    dollarBraceConn,
                    dollarBraceUri,
                    shellHardcodedFallback) + "\n");

            string unquotedEnvFallbackLeak = string.Concat("API_KEY=${OIDC_KEY:-", "RealHardcoded", "Secret99}\n");
            File.WriteAllText(Path.Combine(leakDir, ".env"), unquotedEnvFallbackLeak);

            var leakSummary = SecurityCheckCommandHandler.ScanPath(leakDir);
            Assert.Equal(9, leakSummary.Findings.Count);
            for (int lineNo = 1; lineNo <= 7; lineNo++)
            {
                Assert.Contains(leakSummary.Findings, f => f.RelativePath == "leaks.txt" && f.LineNumber == lineNo);
            }
            Assert.Contains(leakSummary.Findings, f => f.RelativePath == ".env" && f.LineNumber == 1);
            Assert.Equal(1, SecurityCheckCommandHandler.Execute([leakDir]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ScanPath_DetectsShellFallbacksAndPassKeys_WhileAllowingEmptyDefaultsAndSpacedLocalhost()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_r3_{Guid.NewGuid():N}");
        string safeDir = Path.Combine(tempDir, "safe");
        string leakDir = Path.Combine(tempDir, "leaks");
        Directory.CreateDirectory(safeDir);
        Directory.CreateDirectory(leakDir);

        try
        {
            string safeCs = string.Join('\n',
                "string spacedLocal = \"Host=localhost;Database=x; Pwd = postgres ;\";",
                "string envRefConn = \"Host=prod.db;Password=${DB_PASS};\";",
                "string envFallbackVarConn = \"Host=prod.db;Password=${DB_PASS:-$FALLBACK};\";",
                "string placeholderConn = \"Host=prod.db;Password={dbPass};\";");
            File.WriteAllText(Path.Combine(safeDir, "SafeConn.cs"), safeCs + "\n");

            string safeEnv = string.Join('\n',
                "API_KEY=${OIDC_KEY:-}",
                "API_KEY=${OIDC_KEY:-\"\"}",
                "DB_PASS=${PASS:-$FALLBACK}");
            File.WriteAllText(Path.Combine(safeDir, ".env"), safeEnv + "\n");

            var cleanSummary = SecurityCheckCommandHandler.ScanPath(safeDir);
            Assert.Empty(cleanSummary.Findings);
            Assert.Equal(0, SecurityCheckCommandHandler.Execute([safeDir]));

            string csShellFallback = string.Concat("string cs1 = \"Host=prod.db;Pass", "word=${DB_PASS:-", "RealSecret", "99};\";");
            string uriShellFallback = string.Concat("string u1 = \"postgres://user:${DB_PASS:-", "RealSecret", "99}@prod.db:5432/db\";");
            string dbPassQuoted = string.Concat("DB_PA", "SS=\"", "AnotherReal", "Secret12\"");
            string spacedLocalReal = string.Concat("string cs2 = \"Host=localhost;Database=x; Pw", "d = RealSecret", "99 ;\";");
            File.WriteAllText(
                Path.Combine(leakDir, "Leaks.cs"),
                string.Join('\n', csShellFallback, uriShellFallback, dbPassQuoted, spacedLocalReal) + "\n");

            string dbPassEnvFallback = string.Concat("DB_PA", "SS=${PASS:-", "AnotherReal", "Secret12}");
            string dbPassEnvPlain = string.Concat("DB_PA", "SS=", "AnotherReal", "Secret12");
            string quotedShellFallback = string.Concat("API_KEY=${K:-\"", "RealSecret", "99\"}");
            File.WriteAllText(
                Path.Combine(leakDir, ".env"),
                string.Join('\n', dbPassEnvFallback, dbPassEnvPlain, quotedShellFallback) + "\n");

            var leakSummary = SecurityCheckCommandHandler.ScanPath(leakDir);
            Assert.Equal(7, leakSummary.Findings.Count);
            for (int lineNo = 1; lineNo <= 4; lineNo++)
            {
                Assert.Contains(leakSummary.Findings, f => f.RelativePath == "Leaks.cs" && f.LineNumber == lineNo);
            }
            for (int lineNo = 1; lineNo <= 3; lineNo++)
            {
                Assert.Contains(leakSummary.Findings, f => f.RelativePath == ".env" && f.LineNumber == lineNo);
            }
            Assert.Equal(1, SecurityCheckCommandHandler.Execute([leakDir]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ScanPath_AllowsNonCredentialPassTerms_WhileCatchingSuffixAfterGithubExprAndLeadingSpacedConnPass()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_r4_{Guid.NewGuid():N}");
        string safeDir = Path.Combine(tempDir, "safe");
        string leakDir = Path.Combine(tempDir, "leaks");
        Directory.CreateDirectory(safeDir);
        Directory.CreateDirectory(leakDir);

        try
        {
            string safePy = string.Join('\n',
                "forward_pass = \"something_else\"",
                "backward_pass = \"something_else\"",
                "render_pass = \"something_else\"",
                "compile_pass = \"something_else\"",
                "first_pass = \"something_else\"",
                "validation_pass = \"something_else\"",
                "pass = \"something_else\"");
            File.WriteAllText(Path.Combine(safeDir, "ml_render.py"), safePy + "\n");

            string safeEnv = string.Join('\n',
                "forward_pass=something_else_here",
                "render_pass=deferred_shadow_pass",
                "API_KEY=${{ secrets.FOO }}");
            File.WriteAllText(Path.Combine(safeDir, ".env"), safeEnv + "\n");

            var cleanSummary = SecurityCheckCommandHandler.ScanPath(safeDir);
            Assert.Empty(cleanSummary.Findings);
            Assert.Equal(0, SecurityCheckCommandHandler.Execute([safeDir]));

            string leadingSpacedConn1 = string.Concat("var cs1 = \"Pass", "word = RealSecret", "99;Host=prod.db;\";");
            string leadingSpacedConn2 = string.Concat("var cs2 = \"Pass", "word = RealSecret", "99; Server=prod.db;\";");
            string redisPassQuoted = string.Concat("REDIS_PA", "SS = \"", "AnotherReal", "Secret12\"");
            string myAppPassQuoted = string.Concat("MY_APP_PA", "SS = \"", "AnotherReal", "Secret12\"");
            File.WriteAllText(
                Path.Combine(leakDir, "Leaks.cs"),
                string.Join('\n', leadingSpacedConn1, leadingSpacedConn2, redisPassQuoted, myAppPassQuoted) + "\n");

            string ghExprSuffix = string.Concat("API_KEY=${{ secrets.FOO }}", "RealSecret", "99");
            string smtpPassEnv = string.Concat("SMTP_PA", "SS=", "AnotherReal", "Secret12");
            string adminPassEnv = string.Concat("ADMIN_PA", "SS=", "AnotherReal", "Secret12");
            File.WriteAllText(
                Path.Combine(leakDir, ".env"),
                string.Join('\n', ghExprSuffix, smtpPassEnv, adminPassEnv) + "\n");

            var leakSummary = SecurityCheckCommandHandler.ScanPath(leakDir);
            Assert.Equal(7, leakSummary.Findings.Count);
            for (int lineNo = 1; lineNo <= 4; lineNo++)
            {
                Assert.Contains(leakSummary.Findings, f => f.RelativePath == "Leaks.cs" && f.LineNumber == lineNo);
            }
            for (int lineNo = 1; lineNo <= 3; lineNo++)
            {
                Assert.Contains(leakSummary.Findings, f => f.RelativePath == ".env" && f.LineNumber == lineNo);
            }
            Assert.Equal(1, SecurityCheckCommandHandler.Execute([leakDir]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ScanPath_IncludeScratchFlag_ControlsScratchDirectoryScanning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_scratch_{Guid.NewGuid():N}");
        string scratchDir = Path.Combine(tempDir, "scratch");
        string nodeModulesDir = Path.Combine(tempDir, "node_modules");
        Directory.CreateDirectory(scratchDir);
        Directory.CreateDirectory(nodeModulesDir);

        try
        {
            string fakeKey = string.Concat("AIza", "Sy", new string('X', 33));
            File.WriteAllText(Path.Combine(scratchDir, "probe.txt"), $"KEY={fakeKey}\n");
            File.WriteAllText(Path.Combine(nodeModulesDir, "ignored.txt"), $"KEY={fakeKey}\n");

            var defaultSummary = SecurityCheckCommandHandler.ScanPath(tempDir, includeScratch: false);
            Assert.Empty(defaultSummary.Findings);
            Assert.Equal(0, SecurityCheckCommandHandler.Execute([tempDir]));

            var scratchSummary = SecurityCheckCommandHandler.ScanPath(tempDir, includeScratch: true);
            Assert.Single(scratchSummary.Findings);
            Assert.Contains("scratch", scratchSummary.Findings[0].RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, SecurityCheckCommandHandler.Execute([tempDir, "--include-scratch"]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Execute_JsonFlag_OutputsValidStructuredJsonAndCorrectExitCodes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sec_json_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        TextWriter originalOut = Console.Out;
        TextWriter originalErr = Console.Error;

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "clean.cs"), "// clean\n");

            using var cleanOut = new StringWriter();
            using var cleanErr = new StringWriter();
            Console.SetOut(cleanOut);
            Console.SetError(cleanErr);
            int cleanCode = SecurityCheckCommandHandler.Execute([tempDir, "--json"]);
            Assert.Equal(0, cleanCode);

            using (var doc = JsonDocument.Parse(cleanOut.ToString()))
            {
                Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
                Assert.Equal(0, doc.RootElement.GetProperty("exit_code").GetInt32());
                Assert.Equal(0, doc.RootElement.GetProperty("findings_count").GetInt32());
                Assert.False(doc.RootElement.GetProperty("include_scratch").GetBoolean());
                Assert.Equal(1, doc.RootElement.GetProperty("files_scanned").GetInt32());
            }

            string ghToken = string.Concat("ghp_", new string('z', 38));
            File.WriteAllText(Path.Combine(tempDir, "leak.txt"), $"TOKEN={ghToken}\n");

            using var leakOut = new StringWriter();
            using var leakErr = new StringWriter();
            Console.SetOut(leakOut);
            Console.SetError(leakErr);
            int leakCode = SecurityCheckCommandHandler.Execute(["--json", tempDir]);
            Assert.Equal(1, leakCode);

            using (var doc = JsonDocument.Parse(leakOut.ToString()))
            {
                Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
                Assert.Equal(1, doc.RootElement.GetProperty("exit_code").GetInt32());
                Assert.Equal(1, doc.RootElement.GetProperty("findings_count").GetInt32());
                var finding = doc.RootElement.GetProperty("findings")[0];
                Assert.Equal("leak.txt", finding.GetProperty("relative_path").GetString());
                Assert.Equal("GitHub Personal Access Token", finding.GetProperty("secret_type").GetString());
            }

            using var badFlagOut = new StringWriter();
            using var badFlagErr = new StringWriter();
            Console.SetOut(badFlagOut);
            Console.SetError(badFlagErr);
            int badFlagCode = SecurityCheckCommandHandler.Execute([tempDir, "--bogus-flag"]);
            Assert.Equal(1, badFlagCode);
            Assert.Contains("Unknown option", badFlagErr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
