namespace Glacier.Cli.Commands;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Glacier.Tune.Export;

public sealed record MergeCliOptions(string BaseGguf, string AdapterBin, string OutputGguf, int Threads);

public static class MergeCommandHandler
{
    public static MergeCliOptions ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var positionals = new List<string>(3);
        int threads = Environment.ProcessorCount;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-t" or "--threads")
            {
                string raw = CliOptionHelpers.RequireValue(args, ref i, arg);
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out threads) || threads <= 0)
                    throw new ArgumentException($"Invalid {arg} '{raw}'. Expected a positive integer.");
            }
            else if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
            else
            {
                positionals.Add(arg);
            }
        }

        if (positionals.Count != 3)
            throw new ArgumentException("Usage: glacier merge <base.gguf> <adapter.bin> <output.gguf> [options]");

        return new MergeCliOptions(positionals[0], positionals[1], positionals[2], threads);
    }

    public static int Execute(string[] args)
    {
        if (args.Length < 3)
        {
            PrintHelp();
            return 1;
        }

        MergeCliOptions opts;
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

        string baseGguf = opts.BaseGguf;
        string adapterBin = opts.AdapterBin;
        string outGguf = opts.OutputGguf;
        int threads = opts.Threads;

        if (!File.Exists(baseGguf))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Base GGUF model not found: {baseGguf}");
            Console.ResetColor();
            return 1;
        }

        if (!File.Exists(adapterBin))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: LoRA adapter binary not found: {adapterBin}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            GLACIER.TUNE - Zero-Copy GGUF LoRA Adapter Fusion                  ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"  Base Model:    {baseGguf}");
        Console.WriteLine($"  LoRA Adapter:  {adapterBin}");
        Console.WriteLine($"  Target Output: {outGguf}");
        Console.WriteLine($"  Threads:       {threads} (AVX-512 / AVX2 SIMD)\n");

        var sw = Stopwatch.StartNew();
        var options = new GgufMerger.MergeOptions
        {
            MaxDegreeOfParallelism = threads,
            ProgressCallback = msg => Console.WriteLine($"  {msg}")
        };

        GgufMerger.Merge(baseGguf, adapterBin, outGguf, options);
        sw.Stop();

        long outBytes = new FileInfo(outGguf).Length;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ Merging successfully completed in {sw.Elapsed.TotalSeconds:F2} seconds!");
        Console.WriteLine($"✓ Output GGUF Model Size: {outBytes / (1024.0 * 1024.0 * 1024.0):F2} GB");
        Console.ResetColor();

        return 0;
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier merge - Instant zero-copy LoRA adapter fusion into GGUF");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier merge <base.gguf> <adapter.bin> <output.gguf> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -t, --threads <int>                  Number of worker threads (default: ProcessorCount)");
        Console.WriteLine();
        Console.WriteLine("Description:");
        Console.WriteLine("  Fuses low-rank Delta W matrices into target Q4_K/Q8_0 layers with loss-free Q8_0");
        Console.WriteLine("  quantization while keeping all other layers and GGUF metadata bit-for-bit intact.");
    }
}
