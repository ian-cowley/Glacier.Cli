namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>Represents a single ecosystem integrity or dependency-boundary violation.</summary>
public sealed record EcosystemViolation(string Rule, string RepoName, string FilePath, string Message);

/// <summary>Options controlling cross-repo ecosystem verification.</summary>
public sealed record VerifyEcosystemOptions(
    bool CheckSecrets = false,
    bool IncludePredict = false);

/// <summary>Summary of an ecosystem verification pass across one or more repositories.</summary>
public sealed record VerifyEcosystemResult(
    string RootDirectory,
    int ReposScanned,
    int ProjectFilesScanned,
    int SecretFilesScanned,
    IReadOnlyList<EcosystemViolation> Violations)
{
    /// <summary>Gets whether zero ecosystem violations were found.</summary>
    public bool Passed => Violations.Count == 0;
}

/// <summary>
/// Pure C# NativeAOT auditor that validates cross-repo MSBuild &lt;ProjectReference&gt; gating,
/// NuGet fallback parity, retired package removal, and optional secret hygiene.
/// </summary>
public static class VerifyEcosystemCommandHandler
{
    private static readonly HashSet<string> RetiredPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Glacier.StatsViz",
        "AgentDevKit.Adk",
        "Glacier.AgentDevKit.Adk",
        "TinyPdf",
        "tinypdf-csharp",
        "Glacier.Windowing",
        "Glacier.Inference.Distributed"
    };

    private static readonly HashSet<string> ExcludedTreeDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        ".vscode",
        ".gemini",
        "bin",
        "obj",
        "artifacts",
        "TestResults",
        "node_modules",
        "packages",
        "nuget-local",
        "dist",
        "benchmarkdotnet.artifacts"
    };

    private static readonly HashSet<string> ExcludedPredictRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        "Glacier.Predict",
        "Glacier.Predict.Equity"
    };

    /// <summary>Prints CLI help for <c>glacier verify-ecosystem</c>.</summary>
    public static void PrintHelp(TextWriter? output = null)
    {
        TextWriter w = output ?? Console.Out;
        w.WriteLine("Usage: glacier verify-ecosystem [root-path] [options]");
        w.WriteLine();
        w.WriteLine("Audits MSBuild project and props files across repositories for:");
        w.WriteLine("  - Unconditioned cross-repo <ProjectReference> paths escaping the owning repo");
        w.WriteLine("  - Missing matching <PackageReference> NuGet fallback for cross-repo refs");
        w.WriteLine("  - Retired or removed package identifiers (e.g. Glacier.StatsViz, TinyPdf)");
        w.WriteLine("  - Optional credential hygiene via SecurityCheckCommandHandler");
        w.WriteLine();
        w.WriteLine("Options:");
        w.WriteLine("  --root <dir>        Root workspace or repository directory (default: '.')");
        w.WriteLine("  --check-secrets     Also run secret hygiene audit across scanned repos");
        w.WriteLine("  --include-predict   Include Glacier.Predict and Glacier.Predict.Equity");
        w.WriteLine("  --json              Emit machine-readable JSON output");
        w.WriteLine("  -h, --help          Show this help message");
    }

    /// <summary>Executes <c>glacier verify-ecosystem</c> and returns a process exit code (0 = clean, 1 = violations, 2 = invalid args).</summary>
    public static int Execute(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        TextWriter stdout = output ?? Console.Out;
        TextWriter stderr = error ?? Console.Error;

        string rootPath = ".";
        bool rootSpecified = false;
        bool checkSecrets = false;
        bool includePredict = false;
        bool jsonOutput = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        PrintHelp(stdout);
                        return 0;
                    case "--root":
                        rootPath = CliOptionHelpers.RequireValue(args, ref i, "--root");
                        rootSpecified = true;
                        break;
                    case "--check-secrets":
                        checkSecrets = true;
                        break;
                    case "--include-predict":
                        includePredict = true;
                        break;
                    case "--json":
                        jsonOutput = true;
                        break;
                    default:
                        if (arg.StartsWith('-'))
                            throw new ArgumentException($"Unknown option '{arg}'.");
                        if (rootSpecified)
                            throw new ArgumentException($"Unexpected positional argument '{arg}'.");
                        rootPath = arg;
                        rootSpecified = true;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            stderr.WriteLine($"[glacier verify-ecosystem] Error: {ex.Message}");
            return 2;
        }

        string fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            stderr.WriteLine($"[glacier verify-ecosystem] Error: Directory not found: {fullRoot}");
            return 2;
        }

        VerifyEcosystemResult result = VerifyWorkspace(
            fullRoot,
            new VerifyEcosystemOptions(CheckSecrets: checkSecrets, IncludePredict: includePredict));

        if (jsonOutput)
        {
            stdout.WriteLine(FormatJson(result));
        }
        else
        {
            stdout.WriteLine($"[glacier verify-ecosystem] Root: {result.RootDirectory}");
            stdout.WriteLine($"[glacier verify-ecosystem] Scanned {result.ReposScanned} repo(s), {result.ProjectFilesScanned} MSBuild file(s)" +
                (checkSecrets ? $", {result.SecretFilesScanned} file(s) for secrets" : string.Empty) + ".");

            if (result.Passed)
            {
                stdout.WriteLine("[glacier verify-ecosystem] PASS: 0 violations found.");
            }
            else
            {
                stdout.WriteLine($"[glacier verify-ecosystem] FAIL: {result.Violations.Count} violation(s) found:");
                foreach (EcosystemViolation v in result.Violations)
                {
                    stdout.WriteLine($"  [{v.Rule}] ({v.RepoName}) {v.FilePath}: {v.Message}");
                }
            }
        }

        return result.Passed ? 0 : 1;
    }

    /// <summary>Audits a workspace root (or single repository directory) and returns structured findings.</summary>
    public static VerifyEcosystemResult VerifyWorkspace(string rootDirectory, VerifyEcosystemOptions? options = null)
    {
        options ??= new VerifyEcosystemOptions();
        string fullRoot = EnsureTrimmedFullPath(rootDirectory);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"Directory not found: {fullRoot}");

        List<(string Name, string Dir)> repos = DiscoverRepositories(fullRoot, options.IncludePredict);
        var violations = new List<EcosystemViolation>();
        int projectFilesScanned = 0;
        int secretFilesScanned = 0;

        foreach ((string repoName, string repoDir) in repos)
        {
            List<string> msbuildFiles = EnumerateMsBuildFiles(repoDir);
            foreach (string msbuildFile in msbuildFiles)
            {
                projectFilesScanned++;
                AuditMsBuildFile(repoName, repoDir, msbuildFile, violations);
            }

            if (options.CheckSecrets)
            {
                SecurityScanSummary scan = SecurityCheckCommandHandler.ScanPath(repoDir, includeScratch: false);
                secretFilesScanned += scan.FilesScanned;
                foreach (SecurityFinding finding in scan.Findings)
                {
                    violations.Add(new EcosystemViolation(
                        Rule: "EXPOSED_SECRET",
                        RepoName: repoName,
                        FilePath: Path.Combine(repoDir, finding.RelativePath),
                        Message: $"{finding.SecretType} detected at line {finding.LineNumber} ({finding.RedactedPreview})."));
                }
            }
        }

        return new VerifyEcosystemResult(
            RootDirectory: fullRoot,
            ReposScanned: repos.Count,
            ProjectFilesScanned: projectFilesScanned,
            SecretFilesScanned: secretFilesScanned,
            Violations: violations);
    }

    /// <summary>Formats <paramref name="result"/> as deterministic AOT-safe JSON.</summary>
    public static string FormatJson(VerifyEcosystemResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("passed", result.Passed);
            writer.WriteString("rootDirectory", result.RootDirectory);
            writer.WriteNumber("reposScanned", result.ReposScanned);
            writer.WriteNumber("projectFilesScanned", result.ProjectFilesScanned);
            writer.WriteNumber("secretFilesScanned", result.SecretFilesScanned);
            writer.WriteNumber("violationCount", result.Violations.Count);
            writer.WriteStartArray("violations");
            foreach (EcosystemViolation v in result.Violations)
            {
                writer.WriteStartObject();
                writer.WriteString("rule", v.Rule);
                writer.WriteString("repoName", v.RepoName);
                writer.WriteString("filePath", v.FilePath);
                writer.WriteString("message", v.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<(string Name, string Dir)> DiscoverRepositories(string fullRoot, bool includePredict)
    {
        var repos = new List<(string Name, string Dir)>();
        var childDirs = Directory.GetDirectories(fullRoot);
        Array.Sort(childDirs, StringComparer.OrdinalIgnoreCase);

        foreach (string childDir in childDirs)
        {
            string dirName = Path.GetFileName(childDir);
            if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.') || ExcludedTreeDirs.Contains(dirName))
                continue;

            if (!includePredict && ExcludedPredictRepos.Contains(dirName))
                continue;

            if (LooksLikeRepository(childDir, dirName))
                repos.Add((dirName, EnsureTrimmedFullPath(childDir)));
        }

        if (repos.Count == 0 && EnumerateMsBuildFiles(fullRoot).Count > 0)
        {
            string repoName = Path.GetFileName(fullRoot);
            if (includePredict || !ExcludedPredictRepos.Contains(repoName))
                repos.Add((repoName, fullRoot));
        }

        return repos;
    }

    private static bool LooksLikeRepository(string dirPath, string dirName)
    {
        if (dirName.StartsWith("Glacier.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Directory.Exists(Path.Combine(dirPath, ".git")) ||
            File.Exists(Path.Combine(dirPath, "Directory.Packages.props")) ||
            File.Exists(Path.Combine(dirPath, "Directory.Build.props")))
        {
            return true;
        }

        foreach (string ext in new[] { "*.sln", "*.slnx", "*.csproj", "*.fsproj" })
        {
            if (Directory.EnumerateFiles(dirPath, ext, SearchOption.TopDirectoryOnly).GetEnumerator().MoveNext())
                return true;
        }

        return EnumerateMsBuildFiles(dirPath).Count > 0;
    }

    private static List<string> EnumerateMsBuildFiles(string rootDir)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (ExcludedTreeDirs.Contains(name))
                        continue;
                    if (name.StartsWith('.') && !string.Equals(name, ".github", StringComparison.OrdinalIgnoreCase))
                        continue;
                    stack.Push(entry);
                }
                else if (IsAuditableMsBuildFile(name))
                {
                    files.Add(entry);
                }
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool IsAuditableMsBuildFile(string fileName) =>
        fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase);

    private static void AuditMsBuildFile(
        string repoName,
        string repoDir,
        string msbuildFile,
        List<EcosystemViolation> violations)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(msbuildFile, LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            violations.Add(new EcosystemViolation(
                Rule: "XML_LOAD_ERROR",
                RepoName: repoName,
                FilePath: msbuildFile,
                Message: $"Failed to parse MSBuild XML: {ex.Message}"));
            return;
        }

        bool isProjectFile =
            msbuildFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            msbuildFile.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

        var packageRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (XElement element in doc.Descendants())
        {
            string localName = element.Name.LocalName;
            if (string.Equals(localName, "PackageReference", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localName, "PackageVersion", StringComparison.OrdinalIgnoreCase))
            {
                string? pkgId = (element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)?.Trim();
                if (string.IsNullOrEmpty(pkgId))
                    continue;

                if (string.Equals(localName, "PackageReference", StringComparison.OrdinalIgnoreCase))
                    packageRefs.Add(pkgId);

                if (RetiredPackages.Contains(pkgId))
                {
                    violations.Add(new EcosystemViolation(
                        Rule: "RETIRED_PACKAGE",
                        RepoName: repoName,
                        FilePath: msbuildFile,
                        Message: $"Retired package '{pkgId}' referenced via <{localName}>."));
                }
            }
        }

        if (!isProjectFile)
            return;

        string projectDir = Path.GetDirectoryName(msbuildFile) ?? repoDir;
        foreach (XElement projRef in doc.Descendants())
        {
            if (!string.Equals(projRef.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                continue;

            string? rawInclude = projRef.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrEmpty(rawInclude))
                continue;

            // Unresolved MSBuild variables like $(SharedRoot)\... cannot be checked statically by path alone.
            if (rawInclude.Contains("$(", StringComparison.Ordinal))
                continue;

            string normalized = rawInclude
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            string targetFullPath;
            try
            {
                targetFullPath = Path.GetFullPath(Path.Combine(projectDir, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                violations.Add(new EcosystemViolation(
                    Rule: "INVALID_PROJECT_REFERENCE",
                    RepoName: repoName,
                    FilePath: msbuildFile,
                    Message: $"Malformed ProjectReference Include='{rawInclude}': {ex.Message}"));
                continue;
            }

            if (IsPathInside(repoDir, targetFullPath))
                continue;

            string effectiveCondition = CollectEffectiveCondition(projRef);
            bool hasValidGate =
                effectiveCondition.Contains("UseLocalProjectReferences", StringComparison.OrdinalIgnoreCase) ||
                effectiveCondition.Contains("Exists(", StringComparison.OrdinalIgnoreCase) ||
                effectiveCondition.Contains("Release", StringComparison.OrdinalIgnoreCase);

            if (!hasValidGate)
            {
                violations.Add(new EcosystemViolation(
                    Rule: "UNCONDITIONED_CROSS_REPO_REF",
                    RepoName: repoName,
                    FilePath: msbuildFile,
                    Message: $"Cross-repo ProjectReference '{rawInclude}' lacks an Exists(...) or UseLocalProjectReferences Condition."));
            }

            string targetPackageId = Path.GetFileNameWithoutExtension(targetFullPath);
            if (!string.IsNullOrEmpty(targetPackageId) && !packageRefs.Contains(targetPackageId))
            {
                violations.Add(new EcosystemViolation(
                    Rule: "MISSING_NUGET_FALLBACK",
                    RepoName: repoName,
                    FilePath: msbuildFile,
                    Message: $"Cross-repo ProjectReference '{rawInclude}' has no matching <PackageReference Include=\"{targetPackageId}\" /> NuGet fallback."));
            }
        }
    }

    private static string CollectEffectiveCondition(XElement element)
    {
        var sb = new StringBuilder();
        for (XElement? cur = element; cur is not null; cur = cur.Parent)
        {
            string? cond = cur.Attribute("Condition")?.Value;
            if (!string.IsNullOrWhiteSpace(cond))
            {
                if (sb.Length > 0)
                    sb.Append(" && ");
                sb.Append(cond);
            }
        }
        return sb.ToString();
    }

    private static bool IsPathInside(string parentDir, string candidatePath)
    {
        string normalizedParent = EnsureTrimmedFullPath(parentDir) + Path.DirectorySeparatorChar;
        string normalizedCandidate = EnsureTrimmedFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedCandidate, EnsureTrimmedFullPath(parentDir), StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrimmedFullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
