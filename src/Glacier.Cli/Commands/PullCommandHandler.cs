namespace Glacier.Cli.Commands;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

public sealed record PullCliOptions(
    string Spec,
    string OutDir = "./models",
    string? TargetFile = null,
    string? ExpectedSha256 = null,
    int ParallelConnections = 4);

public static class PullCommandHandler
{
    public const long DefaultParallelThresholdBytes = 8L * 1024L * 1024L;
    private static readonly HttpClient _sharedHttp = new HttpClient { Timeout = TimeSpan.FromHours(4) };

    public static PullCliOptions ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? spec = null;
        string outDir = "./models";
        string? targetFile = null;
        string? expectedSha256 = null;
        int parallelConnections = 4;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-o" or "--out")
            {
                outDir = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "-f" or "--file")
            {
                targetFile = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg == "--sha256")
            {
                expectedSha256 = NormalizeAndValidateSha256(CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg == "--parallel")
            {
                string raw = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parallelConnections) ||
                    parallelConnections < 1 || parallelConnections > 16)
                {
                    throw new ArgumentException($"Invalid --parallel '{raw}'. Expected integer 1..16.");
                }
            }
            else if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
            else if (spec == null)
            {
                spec = arg;
            }
            else
            {
                throw new ArgumentException($"Unexpected positional argument '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException("Missing repository or URL specification.");

        return new PullCliOptions(spec, outDir, targetFile, expectedSha256, parallelConnections);
    }

    public static Task<int> ExecuteAsync(string[] args)
        => ExecuteAsync(args, customHandler: null, CancellationToken.None, DefaultParallelThresholdBytes);

    public static Task<int> ExecuteAsync(string[] args, HttpMessageHandler? customHandler)
        => ExecuteAsync(args, customHandler, CancellationToken.None, DefaultParallelThresholdBytes);

    public static async Task<int> ExecuteAsync(
        string[] args,
        HttpMessageHandler? customHandler,
        CancellationToken cancellationToken,
        long parallelThresholdBytes = DefaultParallelThresholdBytes)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        PullCliOptions opts;
        try
        {
            opts = ParseOptions(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        Directory.CreateDirectory(opts.OutDir);

        using HttpClient? ownedClient = customHandler != null
            ? new HttpClient(customHandler, disposeHandler: false) { Timeout = TimeSpan.FromHours(4) }
            : null;
        HttpClient http = ownedClient ?? _sharedHttp;

        string? hfToken = ResolveHuggingFaceToken();
        string downloadUrl;
        string rawFileName;

        if (opts.Spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            opts.Spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            downloadUrl = opts.Spec;
            rawFileName = opts.TargetFile ?? Path.GetFileName(new Uri(opts.Spec).LocalPath);
        }
        else
        {
            string repo = opts.Spec;
            string? repoFile = opts.TargetFile;
            if (repo.Contains(':'))
            {
                var parts = repo.Split(':', 2);
                repo = parts[0];
                repoFile ??= parts[1];
            }

            if (string.IsNullOrEmpty(repoFile))
            {
                Console.WriteLine($"[Glacier.Pull] Resolving GGUF files in repository: {repo}...");
                repoFile = await ResolveDefaultGgufFileAsync(http, repo, hfToken, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(repoFile))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error: No GGUF file found in repository '{repo}'. Specify with --file <filename>.");
                    Console.ResetColor();
                    return 1;
                }
            }

            downloadUrl = $"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(repoFile).Replace("%2F", "/")}";
            rawFileName = repoFile;
        }

        string destPath;
        try
        {
            destPath = ResolveSafeDestinationPath(opts.OutDir, rawFileName);
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
        var sw = Stopwatch.StartNew();

        bool downloadedParallel = false;
        if (opts.ParallelConnections > 1 && existingOffset == 0)
        {
            downloadedParallel = await TryParallelDownloadAsync(
                http,
                downloadUrl,
                partPath,
                opts.ParallelConnections,
                parallelThresholdBytes,
                hfToken,
                cancellationToken).ConfigureAwait(false);
        }

        if (!downloadedParallel)
        {
            await DownloadSequentialResumableAsync(
                http,
                downloadUrl,
                partPath,
                existingOffset,
                hfToken,
                cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        Console.WriteLine();

        byte[] magic = new byte[4];
        long finalLength;
        string? actualSha256 = null;

        using (var verifyStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, useAsync: true))
        {
            finalLength = verifyStream.Length;
            if (finalLength >= 4)
            {
                await verifyStream.ReadExactlyAsync(magic.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            }

            if (opts.ExpectedSha256 != null && finalLength >= 4 && Encoding.ASCII.GetString(magic) == "GGUF")
            {
                verifyStream.Position = 0;
                byte[] hash = await SHA256.HashDataAsync(verifyStream, cancellationToken).ConfigureAwait(false);
                actualSha256 = Convert.ToHexString(hash);
            }
        }

        string magicStr = finalLength >= 4 ? Encoding.ASCII.GetString(magic) : string.Empty;
        if (finalLength < 4 || magicStr != "GGUF")
        {
            TryDeleteCorruptPart(partPath);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nError: Downloaded payload failed GGUF header validation (length={finalLength}, magic='{magicStr}'). Corrupt file removed.");
            Console.ResetColor();
            return 1;
        }

        if (opts.ExpectedSha256 != null &&
            !string.Equals(actualSha256, opts.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteCorruptPart(partPath);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(
                $"\nError: SHA-256 verification failed. Expected {opts.ExpectedSha256}, actual {actualSha256}. Corrupt file removed.");
            Console.ResetColor();
            return 1;
        }

        File.Move(partPath, destPath, overwrite: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ Download complete & GGUF header verified in {sw.Elapsed.TotalSeconds:F1}s ({finalLength / 1e9:F2} GB)!");
        if (actualSha256 != null)
            Console.WriteLine($"✓ SHA-256 verified: {actualSha256.ToLowerInvariant()}");
        Console.WriteLine($"✓ Model ready for instant execution: glacier run \"{destPath}\"");
        Console.ResetColor();

        return 0;
    }

    private static async Task<bool> TryParallelDownloadAsync(
        HttpClient http,
        string downloadUrl,
        string partPath,
        int parallelConnections,
        long parallelThresholdBytes,
        string? hfToken,
        CancellationToken cancellationToken)
    {
        long totalLength;
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            headReq.Headers.UserAgent.ParseAdd("Glacier-Cli/1.1");
            if (!string.IsNullOrWhiteSpace(hfToken))
                headReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hfToken);

            using var headRes = await http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!headRes.IsSuccessStatusCode)
                return false;

            if (headRes.Content.Headers.ContentLength is not long len || len < parallelThresholdBytes)
                return false;

            bool acceptsBytes = false;
            foreach (string unit in headRes.Headers.AcceptRanges)
            {
                if (unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
                {
                    acceptsBytes = true;
                    break;
                }
            }
            if (!acceptsBytes)
                return false;

            totalLength = len;
        }
        catch (HttpRequestException ex)
        {
            Trace.TraceWarning($"[Glacier.Pull] Parallel HEAD probe fell back to sequential download: {ex.Message}");
            return false;
        }

        using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, useAsync: true))
        {
            fs.SetLength(totalLength);
        }

        const long maxChunkBytes = 8L * 1024L * 1024L;
        long targetChunk = Math.Max(256L, (totalLength + parallelConnections - 1) / parallelConnections);
        long chunkSize = Math.Min(maxChunkBytes, targetChunk);

        var ranges = new List<(long Start, long End)>();
        for (long offset = 0; offset < totalLength; offset += chunkSize)
        {
            long end = Math.Min(totalLength - 1, offset + chunkSize - 1);
            ranges.Add((offset, end));
        }

        using SafeFileHandle handle = File.OpenHandle(
            partPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        using var throttle = new SemaphoreSlim(parallelConnections);
        var tasks = new List<Task>(ranges.Count);
        foreach ((long start, long end) in ranges)
        {
            tasks.Add(DownloadRangeAsync(http, downloadUrl, handle, start, end, hfToken, throttle, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return true;
    }

    private static async Task DownloadRangeAsync(
        HttpClient http,
        string downloadUrl,
        SafeFileHandle handle,
        long start,
        long end,
        string? hfToken,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.UserAgent.ParseAdd("Glacier-Cli/1.1");
            if (!string.IsNullOrWhiteSpace(hfToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hfToken);
            req.Headers.Range = new RangeHeaderValue(start, end);

            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            using Stream body = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            long writeOffset = start;
            long remaining = end - start + 1;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await body.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), writeOffset, cancellationToken).ConfigureAwait(false);
                writeOffset += read;
                remaining -= read;
            }

            if (remaining != 0)
                throw new EndOfStreamException($"Range [{start}..{end}] ended early ({remaining} byte(s) missing).");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throttle.Release();
        }
    }

    private static async Task DownloadSequentialResumableAsync(
        HttpClient http,
        string downloadUrl,
        string partPath,
        long existingOffset,
        string? hfToken,
        CancellationToken cancellationToken)
    {
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

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool isPartialResume = existingOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        long initialBytes = isPartialResume ? existingOffset : 0L;
        long contentLength = response.Content.Headers.ContentLength ?? -1L;
        long totalBytes = contentLength >= 0 ? initialBytes + contentLength : -1L;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        FileMode fileMode = isPartialResume ? FileMode.Open : FileMode.Create;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using var fileStream = new FileStream(partPath, fileMode, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, useAsync: true);
            if (isPartialResume)
            {
                fileStream.Seek(0, SeekOrigin.End);
            }

            long downloaded = initialBytes;
            int bytesRead;
            var lastReport = Stopwatch.StartNew();
            long lastDownloaded = initialBytes;

            TrySetCursorVisible(false);
            try
            {
                while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
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

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteCorruptPart(string partPath)
    {
        try
        {
            if (File.Exists(partPath))
                File.Delete(partPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[Glacier.Pull] Unable to remove corrupt partial file '{partPath}': {ex.Message}");
        }
    }

    private static string NormalizeAndValidateSha256(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["sha256:".Length..].Trim();

        if (trimmed.Length != 64)
            throw new ArgumentException($"Invalid --sha256 digest '{raw}'. Expected 64 hex characters.");

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
                throw new ArgumentException($"Invalid --sha256 digest '{raw}'. Non-hex character at index {i}.");
        }

        return trimmed.ToLowerInvariant();
    }

    public static string ResolveSafeDestinationPath(string outDir, string rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
            throw new ArgumentException("Target filename cannot be empty.", nameof(rawFileName));

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

    private static async Task<string?> ResolveDefaultGgufFileAsync(
        HttpClient http,
        string repo,
        string? hfToken,
        CancellationToken cancellationToken)
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

            using var res = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            string json = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Trace.TraceWarning($"[Glacier.Pull] Failed resolving HuggingFace repo manifest '{repo}': {ex.Message}");
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
        Console.WriteLine("  glacier pull Qwen/Qwen2.5-7B-Instruct-GGUF --file qwen2.5-7b-instruct-q4_k_m.gguf --sha256 <hex>");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --out <dir>                      Destination directory (default: ./models)");
        Console.WriteLine("  -f, --file <name>                    Target filename in HuggingFace repository");
        Console.WriteLine("  --sha256 <hex>                       Verify SHA-256 digest before promoting downloaded file");
        Console.WriteLine("  --parallel <1..16>                   Concurrent HTTP Range connections (default: 4)");
    }
}
