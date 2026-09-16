namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static class PullCommandHandler
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromHours(4) };

    public static async Task<int> ExecuteAsync(string[] args)
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

        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        string downloadUrl;
        string destinationFileName;

        if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            downloadUrl = spec;
            destinationFileName = targetFile ?? Path.GetFileName(new Uri(spec).LocalPath);
        }
        else
        {
            // HuggingFace repo spec e.g. "Qwen/Qwen2.5-7B-Instruct-GGUF" or "user/repo:filename"
            string repo = spec;
            if (repo.Contains(':'))
            {
                var parts = repo.Split(':');
                repo = parts[0];
                targetFile ??= parts[1];
            }

            if (string.IsNullOrEmpty(targetFile))
            {
                Console.WriteLine($"[Glacier.Pull] Resolving GGUF files in repository: {repo}...");
                targetFile = await ResolveDefaultGgufFileAsync(repo);
                if (string.IsNullOrEmpty(targetFile))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error: No GGUF file found in repository '{repo}'. Specify with --file <filename>.");
                    Console.ResetColor();
                    return 1;
                }
            }

            downloadUrl = $"https://huggingface.co/{repo}/resolve/main/{targetFile}";
            destinationFileName = targetFile;
        }

        string destPath = Path.Combine(outDir, destinationFileName);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║             GLACIER.PULL - High-Speed Chunked Model Downloader                ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"  Source URL:  {downloadUrl}");
        Console.WriteLine($"  Destination: {destPath}\n");

        var sw = Stopwatch.StartNew();
        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);

        byte[] buffer = new byte[256 * 1024];
        long downloaded = 0;
        int bytesRead;
        var lastReport = Stopwatch.StartNew();
        long lastDownloaded = 0;

        Console.CursorVisible = false;
        try
        {
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                downloaded += bytesRead;

                if (lastReport.ElapsedMilliseconds > 200)
                {
                    double speedBytesPerSec = (downloaded - lastDownloaded) / (lastReport.Elapsed.TotalSeconds);
                    double speedMbPerSec = speedBytesPerSec / (1024 * 1024);
                    lastDownloaded = downloaded;
                    lastReport.Restart();

                    if (totalBytes > 0)
                    {
                        double pct = (double)downloaded / totalBytes * 100.0;
                        double remainingSec = speedBytesPerSec > 0 ? (totalBytes - downloaded) / speedBytesPerSec : 0;
                        int filled = (int)(pct / 4); // 25 chars width
                        string bar = new string('=', Math.Max(0, filled)) + (filled < 25 ? ">" : "") + new string(' ', Math.Max(0, 24 - filled));

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
            Console.CursorVisible = true;
        }

        sw.Stop();
        Console.WriteLine();

        // Validate GGUF Magic Header
        fileStream.Flush();
        fileStream.Seek(0, SeekOrigin.Begin);
        byte[] magic = new byte[4];
        fileStream.ReadExactly(magic, 0, 4);
        string magicStr = System.Text.Encoding.ASCII.GetString(magic);

        if (magicStr != "GGUF")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[Warning] File downloaded but magic header is '{magicStr}' instead of 'GGUF'. Check source link.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n✓ Download complete & GGUF header verified in {sw.Elapsed.TotalSeconds:F1}s ({downloaded / 1e9:F2} GB)!");
            Console.WriteLine($"✓ Model ready for instant execution: glacier run \"{destPath}\"");
            Console.ResetColor();
        }

        return 0;
    }

    private static async Task<string?> ResolveDefaultGgufFileAsync(string repo)
    {
        try
        {
            string apiUrl = $"https://huggingface.co/api/models/{repo}";
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "Glacier-Cli/1.0");

            var res = await _http.SendAsync(request);
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
