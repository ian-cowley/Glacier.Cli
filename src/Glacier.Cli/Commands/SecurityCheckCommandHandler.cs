namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record SecurityFinding(string RelativePath, int LineNumber, string SecretType, string RedactedPreview);

public sealed record SecurityScanSummary(int FilesScanned, IReadOnlyList<SecurityFinding> Findings);

public static partial class SecurityCheckCommandHandler
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode",
        "packages", "benchmarkdotnet.artifacts", ".gemini", "tmp",
        "nuget-local", "dist", "scratch", "scratch_challenger_1",
        "scratch_challenger_2", "scratch_challenger_3", "models",
        "publish_win", "publish_linux", "cache", "__pycache__"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".png", ".jpg", ".jpeg",
        ".gif", ".ico", ".pdf", ".zip", ".tar", ".gz", ".nupkg", ".gguf",
        ".bin", ".parquet", ".feather", ".mp4", ".mp3", ".wav", ".mkv",
        ".avi", ".flac", ".pt", ".pth", ".safetensors", ".onnx", ".csr",
        ".part", ".tmp", ".pyc"
    };

    private static readonly HashSet<string> EnvYamlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".yaml", ".yml", ".ini", ".conf", ".cfg", ".properties"
    };

    [GeneratedRegex(@"\bAIza[0-9A-Za-z\-_]{35}\b")]
    private static partial Regex GoogleKeyRegex();

    [GeneratedRegex(@"\bsk-[a-zA-Z0-9]{20,}\b")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\bsk-ant-[a-zA-Z0-9\-_]{20,}\b")]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9_]{36,255}\b")]
    private static partial Regex GitHubPatRegex();

    [GeneratedRegex(@"\b(?:AKIA|ABIA|ACCA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"aws_secret_access_key\s*[:=]\s*[""']?([A-Za-z0-9/+=]{40})[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex AwsSecretKeyRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex(@"\bhf_[a-zA-Z0-9]{34,}\b")]
    private static partial Regex HuggingFaceTokenRegex();

    [GeneratedRegex(@"\bxox[baprs]-[0-9a-zA-Z]{10,48}\b")]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\boy2[a-z0-9]{43}\b")]
    private static partial Regex NuGetApiKeyRegex();

    [GeneratedRegex(@"(?:api[_-]?key|secret[_-]?key|access[_-]?token|auth[_-]?token|private[_-]?key|password|(?<![a-z0-9])(?:db|pg|postgres|postgresql|sql|mysql|mariadb|mssql|oracle|sqlite|redis|mongo|mongodb|amqp|rabbitmq|kafka|smtp|imap|pop3|ftp|sftp|ssh|ldap|admin|root|auth|user|proxy|vault|sudo|master|keystore|truststore|tls|ssl|cert|api|client|service|app|prod|staging|dev|backup|encrypt|decrypt|session|token|secret)_pass|client[_-]?secret)\s*[:=]\s*[""']([^""'\s]{12,})[""']", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSecretRegex();

    [GeneratedRegex(@"(?:^|\s)(?:export\s+)?(?:[A-Z0-9_]*?(?:api[_-]?key|secret[_-]?key|access[_-]?token|auth[_-]?token|private[_-]?key|password|(?<![a-z0-9])(?:db|pg|postgres|postgresql|sql|mysql|mariadb|mssql|oracle|sqlite|redis|mongo|mongodb|amqp|rabbitmq|kafka|smtp|imap|pop3|ftp|sftp|ssh|ldap|admin|root|auth|user|proxy|vault|sudo|master|keystore|truststore|tls|ssl|cert|api|client|service|app|prod|staging|dev|backup|encrypt|decrypt|session|token|secret)_pass|client[_-]?secret))\s*[:=]\s*(?![""'])(\$\{\{[^}\r\n]+\}\}[^\s#]*|\$\{(?:[^{}\r\n]|\$\{[^}\r\n]*\})+\}[^\s#]*|[^""'\s#]{12,})", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedConfigSecretRegex();

    [GeneratedRegex(@"(?:\b(?:Password|Pwd)=(?!\{[A-Za-z_][A-Za-z0-9_]*\}(?:[;""'\s@]|$))([^;\s""'<>]{8,})|\b(?:Password|Pwd)\s*=\s*(?!\{[A-Za-z_][A-Za-z0-9_]*\}(?:[;""'\s@]|$))([^;\s""'<>]{8,})(?=\s*;[^""'\r\n]*\b(?:Host|Server|Database|Data\s+Source|Port|Uid|User\s+Id|Username|Pwd|Password|SslMode|Trusted_Connection|Integrated\s+Security)\s*=)|;\s*(?:Password|Pwd)\s*=\s*(?!\{[A-Za-z_][A-Za-z0-9_]*\}(?:[;""'\s@]|$))([^;\s""'<>]{8,}))|(?:postgres(?:ql)?|mongodb(?:\+srv)?|mysql|redis|amqp):\/\/[^:\s/@]+:(?!\{[A-Za-z_][A-Za-z0-9_]*\}(?:[;""'\s@]|$))([^@\s""'<>]{6,})@", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecretRegex();

    [GeneratedRegex(@"(?:private[_-]?key|priv[_-]?key|wallet[_-]?key|signer[_-]?key|secret)\s*[:=]\s*[""']?(0x[0-9a-f]{64})[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex RawHexPrivateKeyRegex();

    [GeneratedRegex(@"(?:[:=]\s*[""']<[^""'<>]+>[""']$|^<[^""'<>]+>$)")]
    private static partial Regex PlaceholderTagRegex();

    [GeneratedRegex(@"\bAIzaSyDummy\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitProviderAllowlistRegex();

    [GeneratedRegex(@"(?:your[-_]?api[-_]?key|^YOUR_[A-Z0-9_]+$|placeholder|dummy|fake|sample|example|process\.env|environment\.getenvironmentvariable|config\[""|AIzaSyDummy|Bearer\s+\$TOKEN)", RegexOptions.IgnoreCase)]
    private static partial Regex GenericAllowlistRegex();

    [GeneratedRegex(@"^(?:\$\{\{\s*(?:secrets|steps|github|env|vars|inputs)\.[A-Za-z0-9_.\-]+\s*\}\}|\$\{[A-Za-z_][A-Za-z0-9_]*(?::[-=?+](?:\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|""""|''|))?\}|\$(?:env:)?[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShellOrCiExpressionAllowlistRegex();

    [GeneratedRegex(@"ReadOnlySpan<byte>\s+password\s*=\s*""UserMasterPassword""|Host\s*=\s*(?:localhost|127\.0\.0\.1)\b[^""'\r\n;]*(?:;(?!\s*(?:Password|Pwd)\s*=)[^""'\r\n;]*)*(;\s*(?:Password|Pwd)\s*=\s*(?:postgres|benchpass|testpass|changeme)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ContextualAllowlistRegex();

    public static int Execute(string[] args)
    {
        string? targetArg = null;
        bool includeScratch = false;
        bool jsonOutput = false;

        foreach (string arg in args)
        {
            if (arg == "--include-scratch")
            {
                includeScratch = true;
            }
            else if (arg == "--json")
            {
                jsonOutput = true;
            }
            else if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"[ERROR] Unknown option: {arg}");
                return 1;
            }
            else if (targetArg is null)
            {
                targetArg = arg;
            }
            else
            {
                Console.Error.WriteLine($"[ERROR] Unexpected positional argument: {arg}");
                return 1;
            }
        }

        string target = targetArg ?? ".";
        if (!Directory.Exists(target) && !File.Exists(target))
        {
            Console.Error.WriteLine($"[ERROR] Target path does not exist: {target}");
            if (jsonOutput)
            {
                WriteJsonSummary(target, includeScratch, exitCode: 2, new SecurityScanSummary(0, []));
            }
            return 2;
        }

        if (!jsonOutput)
        {
            Console.WriteLine($"[SECURITY] Running Glacier C# Secrets Scanner on: {Path.GetFullPath(target)}");
        }

        var summary = ScanPath(target, includeScratch);
        int exitCode = summary.Findings.Count > 0 ? 1 : 0;

        if (jsonOutput)
        {
            WriteJsonSummary(target, includeScratch, exitCode, summary);
            if (exitCode != 0)
            {
                Console.Error.WriteLine("Commit/Publish BLOCKED. Please remove or sanitize secrets before proceeding.");
            }
            return exitCode;
        }

        Console.WriteLine($"  Scanned {summary.FilesScanned} files.");

        if (summary.Findings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("[CRITICAL ALERT] POTENTIAL SECRETS DETECTED!");
            Console.WriteLine(new string('=', 70));
            foreach (var f in summary.Findings)
            {
                Console.WriteLine($"  FAILED: {f.RelativePath}:{f.LineNumber} -> {f.SecretType} ({f.RedactedPreview})");
            }
            Console.WriteLine(new string('=', 70));
            Console.Error.WriteLine("Commit/Publish BLOCKED. Please remove or sanitize secrets before proceeding.");
            return 1;
        }

        Console.WriteLine("  [PASSED] Zero secrets or credentials detected. Safe to commit/publish.");
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: glacier security-check [path] [--include-scratch] [--json]");
        Console.WriteLine();
        Console.WriteLine("Scans files recursively for exposed API keys, private keys, connection strings, and credentials.");
        Console.WriteLine("Scans .github workflows and streams large text files line-by-line.");
        Console.WriteLine("Options:");
        Console.WriteLine("  --include-scratch    Include scratch* directories in recursive scan");
        Console.WriteLine("  --json               Emit structured JSON summary on stdout");
        Console.WriteLine("Exit codes: 0 = clean, 1 = secrets detected or invalid flags, 2 = invalid target path.");
    }

    public static SecurityScanSummary ScanPath(string targetPath) => ScanPath(targetPath, includeScratch: false);

    public static SecurityScanSummary ScanPath(string targetPath, bool includeScratch)
    {
        var findings = new List<SecurityFinding>();
        int scanned = 0;

        if (File.Exists(targetPath))
        {
            var fi = new FileInfo(targetPath);
            if (!ShouldSkipFile(fi.Name, fi.Extension))
            {
                ScanFile(fi.FullName, fi.Name, findings);
                scanned = 1;
            }
            return new SecurityScanSummary(scanned, findings);
        }

        string rootFull = Path.GetFullPath(targetPath);
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (string sub in subDirs)
            {
                string name = Path.GetFileName(sub);
                if (ShouldSkipDirectory(name, includeScratch))
                    continue;
                stack.Push(sub);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string ext = Path.GetExtension(file);
                if (ShouldSkipFile(name, ext))
                    continue;

                string rel = Path.GetRelativePath(rootFull, file);
                ScanFile(file, rel, findings);
                scanned++;
            }
        }

        return new SecurityScanSummary(scanned, findings);
    }

    private static bool ShouldSkipDirectory(string dirName, bool includeScratch)
    {
        if (string.IsNullOrEmpty(dirName)) return true;
        if (includeScratch && dirName.StartsWith("scratch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (IgnoredDirs.Contains(dirName)) return true;
        if (dirName.StartsWith("scratch", StringComparison.OrdinalIgnoreCase) ||
            dirName.StartsWith("publish_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Scan .github workflows; skip other hidden dot-directories (.git, .vs, etc.)
        if (dirName.StartsWith('.') && !dirName.Equals(".github", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static void WriteJsonSummary(string target, bool includeScratch, int exitCode, SecurityScanSummary summary)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("target", Path.GetFullPath(target));
            writer.WriteBoolean("passed", exitCode == 0 && summary.Findings.Count == 0);
            writer.WriteNumber("exit_code", exitCode);
            writer.WriteNumber("files_scanned", summary.FilesScanned);
            writer.WriteBoolean("include_scratch", includeScratch);
            writer.WriteNumber("findings_count", summary.Findings.Count);
            writer.WriteStartArray("findings");
            foreach (var f in summary.Findings)
            {
                writer.WriteStartObject();
                writer.WriteString("relative_path", f.RelativePath);
                writer.WriteNumber("line_number", f.LineNumber);
                writer.WriteString("secret_type", f.SecretType);
                writer.WriteString("redacted_preview", f.RedactedPreview);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        Console.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static bool ShouldSkipFile(string fileName, string extension)
    {
        if (IgnoredExtensions.Contains(extension)) return true;
        if (fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static void ScanFile(string fullPath, string relativePath, List<SecurityFinding> findings)
    {
        bool isEnvOrYaml = IsEnvOrYamlFile(fullPath);

        try
        {
            Encoding? encoding = DetectTextEncoding(fullPath, out bool isBinary);
            if (isBinary || encoding == null)
                return;

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
            using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);

            int lineNo = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                ScanLine(line, lineNo, relativePath, isEnvOrYaml, findings);
            }
        }
        catch
        {
            // Skip unreadable locked files cleanly
        }
    }

    private static bool IsEnvOrYamlFile(string fullPath)
    {
        string name = Path.GetFileName(fullPath);
        string ext = Path.GetExtension(fullPath);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || EnvYamlExtensions.Contains(ext);
    }

    private static Encoding? DetectTextEncoding(string fullPath, out bool isBinary)
    {
        isBinary = false;
        Span<byte> sample = stackalloc byte[4096];
        int read;
        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            read = fs.Read(sample);
        }

        if (read == 0) return Encoding.UTF8;
        ReadOnlySpan<byte> head = sample[..read];

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            return Encoding.Unicode;
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            return Encoding.UTF8;

        int zeroIndex = head.IndexOf((byte)0);
        if (zeroIndex >= 0)
        {
            int evenNulls = 0;
            int oddNulls = 0;
            int pairs = Math.Max(1, head.Length / 2);
            for (int i = 0; i < head.Length - 1; i += 2)
            {
                if (head[i] == 0) evenNulls++;
                if (head[i + 1] == 0) oddNulls++;
            }

            if (oddNulls > pairs * 0.3 && evenNulls < oddNulls * 0.2)
                return Encoding.Unicode;
            if (evenNulls > pairs * 0.3 && oddNulls < evenNulls * 0.2)
                return Encoding.BigEndianUnicode;

            isBinary = true;
            return null;
        }

        return Encoding.UTF8;
    }

    private static void ScanLine(string line, int lineNo, string relativePath, bool isEnvOrYaml, List<SecurityFinding> findings)
    {
        CheckPattern(GoogleKeyRegex(), "Google AI / Gemini / Maps API Key", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(OpenAiKeyRegex(), "OpenAI API Key", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(AnthropicKeyRegex(), "Anthropic API Key", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(GitHubPatRegex(), "GitHub Personal Access Token", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(AwsAccessKeyRegex(), "AWS Access Key ID", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(AwsSecretKeyRegex(), "AWS Secret Access Key", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(PrivateKeyBlockRegex(), "Private Key Block", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(HuggingFaceTokenRegex(), "HuggingFace Token", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(SlackTokenRegex(), "Slack Token", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(NuGetApiKeyRegex(), "NuGet API Key", isGeneric: false, line, lineNo, relativePath, findings);
        CheckPattern(GenericSecretRegex(), "Generic Secret Assignment", isGeneric: true, line, lineNo, relativePath, findings);
        CheckPattern(ConnectionStringSecretRegex(), "Connection String Credential", isGeneric: true, line, lineNo, relativePath, findings);
        CheckPattern(RawHexPrivateKeyRegex(), "Raw Hex Private Key", isGeneric: true, line, lineNo, relativePath, findings);

        if (isEnvOrYaml)
        {
            CheckPattern(UnquotedConfigSecretRegex(), "Unquoted Config/Env Secret Assignment", isGeneric: true, line, lineNo, relativePath, findings);
        }
    }

    private static void CheckPattern(
        Regex regex,
        string secretType,
        bool isGeneric,
        string line,
        int lineNo,
        string relativePath,
        List<SecurityFinding> findings)
    {
        foreach (Match m in regex.Matches(line))
        {
            string token = ExtractCapturedToken(m);
            if (IsAllowlisted(token, m.Index, m.Length, isGeneric, line))
                continue;

            string raw = m.Value.Trim();
            string redacted = raw.Length > 12
                ? $"{raw[..8]}...{raw[^4..]}"
                : "[REDACTED]";
            findings.Add(new SecurityFinding(relativePath, lineNo, secretType, redacted));
        }
    }

    private static string ExtractCapturedToken(Match m)
    {
        for (int g = 1; g < m.Groups.Count; g++)
        {
            if (m.Groups[g].Success && !string.IsNullOrEmpty(m.Groups[g].Value))
                return m.Groups[g].Value;
        }
        return m.Value;
    }

    private static bool IsAllowlisted(string token, int start, int length, bool isGeneric, string line)
    {
        if (PlaceholderTagRegex().IsMatch(token))
            return true;

        if (isGeneric)
        {
            if (GenericAllowlistRegex().IsMatch(token) || ShellOrCiExpressionAllowlistRegex().IsMatch(token))
                return true;
        }
        else
        {
            if (ExplicitProviderAllowlistRegex().IsMatch(token))
                return true;
        }

        int end = start + length;
        foreach (Match allowed in ContextualAllowlistRegex().Matches(line))
        {
            int allowedStart = allowed.Groups[1].Success ? allowed.Groups[1].Index : allowed.Index;
            int allowedEnd = allowed.Groups[1].Success ? allowed.Groups[1].Index + allowed.Groups[1].Length : allowed.Index + allowed.Length;
            if (allowedStart <= start && end <= allowedEnd)
                return true;
        }

        return false;
    }
}
