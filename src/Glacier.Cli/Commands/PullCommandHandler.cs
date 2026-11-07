namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public static class PullCommandHandler
{
    private static readonly HttpClient _sharedHttp = new HttpClient { Timeout = TimeSpan.FromHours(4) };

    public static Task<int> ExecuteAsync(string[] args) => ExecuteAsync(args, customHandler: null);

    public static async Task<int> ExecuteAsync(string[] args, HttpMessageHandler? customHandler)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        string spec = args[0];
        string outDir = "./models";
        string? targetFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--out" && i + 1 < args.Length)
            {
                outDir = args[++i];
            }
            else if (args[i] is "-f" or "--file" && i + 1 < args.Length)
            {
                targetFile = args[++i];
            }
        }

        Directory.CreateDirectory(outDir);

        using HttpClient? ownedClient = customHandler != null
            ? new HttpClient(customHandler, disposeHandler: false) { Timeout = TimeSpan.FromHours(4) }
            : null;
        HttpClient http = ownedClient ?? _sharedHttp;

        string? hfToken = ResolveHuggingFaceToken();
        string downloadUrl;
        string rawFileName;

        if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            downloadUrl = spec;
            rawFileName = targetFile ?? Path.GetFileName(new Uri(spec).LocalPath);
        }
        else
        {
            // HuggingFace repo spec e.g. "Qwen/Qwen2.5-7B-Instruct-GGUF" or "user/repo:filename"
            string repo = spec;
            if (repo.Contains(':'))
            {
                var parts = repo.Split(':', 2);
                repo = parts[0];
                targetFile ??= parts[1];
            }

            if (string.IsNullOrEmpty(targetFile))
            {
                Console.WriteLine($"[Glacier.Pull] Resolving GGUF files in repository: {repo}...");
                targetFile = await ResolveDefaultGgufFileAsync(http, repo, hfToken);
                if (string.IsNullOrEmpty(targetFile))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error: No GGUF file found in repository '{repo}'. Specify with --file <filename>.");
                    Console.ResetColor();
                    return 1;
                }
            }

            downloadUrl = $"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(targetFile).Replace("%2F", "/")}";
            rawFileName = targetFile;
        }

        string destPath;
        try
        {
            destPath = ResolveSafeDestinationPath(outDir, rawFileName);
        }
        catch (ArgumentException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        string partPath = destPath + ".part";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║             GLACIER.PULL - High-Speed Chunked Model Downloader                ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"  Source URL:  {downloadUrl}");
        Console.WriteLine($"  Destination: {destPath}\n");

        long existingOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.ParseAdd("Glacier-Cli/1.1");
        if (!string.IsNullOrWhiteSpace(hfToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hfToken);
        }
        if (existingOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingOffset, null);
        }

        var sw = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        bool isPartialResume = existingOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        long initialBytes = isPartialResume ? existingOffset : 0L;
        long contentLength = response.Content.Headers.ContentLength ?? -1L;
        long totalBytes = contentLength >= 0 ? initialBytes + contentLength : -1L;

        using var contentStream = await response.Content.ReadAsStreamAsync();
        FileMode fileMode = isPartialResume ? FileMode.Open : FileMode.Create;

        byte[] magic = new byte[4];
        long finalLength;

        using (var fileStream = new FileStream(partPath, fileMode, FileAccess.ReadWrite, FileShare.None, 1024 * 1024))
        {
            if (isPartialResume)
            {
                fileStream.Seek(0, SeekOrigin.End);
            }

            byte[] buffer = new byte[256 * 1024];
            long downloaded = initialBytes;
            int bytesRead;
            var lastReport = Stopwatch.StartNew();
            long lastDownloaded = initialBytes;

            TrySetCursorVisible(false);
            try
            {
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloaded += bytesRead;

                    if (lastReport.ElapsedMilliseconds > 200)
                    {
                        double speedBytesPerSec = (downloaded - lastDownloaded) / lastReport.Elapsed.TotalSeconds;
                        double speedMbPerSec = speedBytesPerSec / (1024 * 1024);
                        lastDownloaded = downloaded;
                        lastReport.Restart();

                        if (totalBytes > 0)
                        {
                            double pct = (double)downloaded / totalBytes * 100.0;
                            double remainingSec = speedBytesPerSec > 0 ? (totalBytes - downloaded) / speedBytesPerSec : 0;
                            int filled = Math.Clamp((int)(pct / 4), 0, 25);
                            string bar = new string('=', filled) + (filled < 25 ? ">" : "") + new string(' ', Math.Max(0, 24 - filled));

                            Console.Write($"\r  [{bar}] {pct,5:F1}% | {downloaded / 1e9:F2} GB / {totalBytes / 1e9:F2} GB | {speedMbPerSec,5:F1} MB/s | ETA: {remainingSec,3:F0}s ");
                        }
                        else
                        {
                            Console.Write($"\r  Downloaded: {downloaded / 1e9:F2} GB | Speed: {speedMbPerSec:F1} MB/s ");
                        }
                    }
                }
            }
            finally
            {
                TrySetCursorVisible(true);
            }

            await fileStream.FlushAsync();
            finalLength = fileStream.Length;
            if (finalLength >= 4)
            {
                fileStream.Seek(0, SeekOrigin.Begin);
                fileStream.ReadExactly(magic, 0, 4);
            }
        }

        sw.Stop();
        Console.WriteLine();

        string magicStr = finalLength >= 4 ? Encoding.ASCII.GetString(magic) : string.Empty;
        if (finalLength < 4 || magicStr != "GGUF")
        {
            try { File.Delete(partPath); } catch { }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nError: Downloaded payload failed GGUF header validation (length={finalLength}, magic='{magicStr}'). Corrupt file removed.");
            Console.ResetColor();
            return 1;
        }

        File.Move(partPath, destPath, overwrite: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ Download complete & GGUF header verified in {sw.Elapsed.TotalSeconds:F1}s ({finalLength / 1e9:F2} GB)!");
        Console.WriteLine($"✓ Model ready for instant execution: glacier run \"{destPath}\"");
        Console.ResetColor();

        return 0;
    }

    public static string ResolveSafeDestinationPath(string outDir, string rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
            throw new ArgumentException("Target filename cannot be empty.", nameof(rawFileName));

        // Reject explicit traversal segments before normalizing
        string normalized = rawFileName.Replace('\\', '/');
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                throw new ArgumentException($"Unsafe path traversal detected in filename '{rawFileName}'.", nameof(rawFileName));
        }

        string leafName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(leafName) || leafName is "." or "..")
            throw new ArgumentException($"Invalid target filename '{rawFileName}'.", nameof(rawFileName));

        string fullOutDir = Path.GetFullPath(outDir);
        string fullOutDirWithSep = fullOutDir.EndsWith(Path.DirectorySeparatorChar)
            ? fullOutDir
            : fullOutDir + Path.DirectorySeparatorChar;

        string destPath = Path.GetFullPath(Path.Combine(fullOutDir, leafName));
        StringComparison pathComp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destPath.StartsWith(fullOutDirWithSep, pathComp))
            throw new ArgumentException($"Resolved path '{destPath}' escapes destination directory '{fullOutDir}'.", nameof(rawFileName));

        return destPath;
    }

    private static string? ResolveHuggingFaceToken()
    {
        string? token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN");
        }
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private static void TrySetCursorVisible(bool visible)
    {
        if (Console.IsOutputRedirected) return;
        try
        {
            Console.CursorVisible = visible;
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static async Task<string?> ResolveDefaultGgufFileAsync(HttpClient http, string repo, string? hfToken)
    {
        try
        {
            string apiUrl = $"https://huggingface.co/api/models/{repo}";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("Glacier-Cli/1.1");
            if (!string.IsNullOrWhiteSpace(hfToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hfToken);
            }

            using var res = await http.SendAsync(request);
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("siblings", out var siblings))
            {
                string? fallback = null;
                foreach (var item in siblings.EnumerateArray())
                {
                    if (item.TryGetProperty("rfilename", out var rfile))
                    {
                        string fn = rfile.GetString() ?? "";
                        if (fn.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                        {
                            if (fn.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase)) return fn;
                            fallback ??= fn;
                        }
                    }
                }
                return fallback;
            }
        }
        catch
        {
            // Network fallback
        }
        return null;
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier pull - High-speed chunked GGUF downloader");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier pull <repo-or-url> [options]");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  HF_TOKEN / HUGGING_FACE_HUB_TOKEN    Optional Bearer token for gated HuggingFace repositories");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  glacier pull https://huggingface.co/.../model.gguf");
        Console.WriteLine("  glacier pull lmstudio-community/Meta-Llama-3.1-8B-Instruct-GGUF");
        Console.WriteLine("  glacier pull Qwen/Qwen2.5-7B-Instruct-GGUF --file qwen2.5-7b-instruct-q4_k_m.gguf");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --out <dir>                      Destination directory (default: ./models)");
        Console.WriteLine("  -f, --file <name>                    Target filename in HuggingFace repository");
    }
}
