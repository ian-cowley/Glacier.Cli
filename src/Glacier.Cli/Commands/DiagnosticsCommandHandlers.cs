namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Inference.Config;
using Glacier.Inference.Engine;
using Glacier.Inference.Gguf;
using Glacier.Inference.Hardware;
using Glacier.Inference.Model;
using Glacier.Inference.Sampling;

public static class DiagnosticsCommandHandlers
{
    public static int RunDevices(string[] args)
    {
        var devices = DeviceManager.GetDevices();
        var (activeDevice, activeEngine) = GlacierSettings.ResolveTarget(null, null);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("============================================================================================================");
        Console.WriteLine("                                  GLACIER DETECTED ACCELERATORS & ENGINES                                   ");
        Console.WriteLine("============================================================================================================");
        Console.ResetColor();
        Console.WriteLine($"{"[ID]",-18} | {"Device Name",-34} | {"Memory",-15} | {"Safe Driver Engines",-25}");
        Console.WriteLine(new string('-', 108));

        foreach (var dev in devices)
        {
            string memStr;
            if (dev.Vendor == GpuVendor.Cpu)
            {
                memStr = $"{dev.SharedVramGb:F1} GB RAM";
            }
            else if (dev.DedicatedVramGb < 1.0 && dev.SharedVramGb > 0)
            {
                memStr = $"{dev.SharedVramGb:F1} GB Unified";
            }
            else
            {
                memStr = $"{dev.DedicatedVramGb:F1} GB VRAM";
            }

            string safeEngines = string.Join(", ", dev.SupportedEngines.Select(FormatEngineName));
            bool isCurrent = dev.Id == activeDevice.Id;

            if (isCurrent) Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{dev.Id,-18} | {dev.Name,-34} | {memStr,-15} | {safeEngines,-25} {(isCurrent ? $"[ACTIVE: {FormatEngineName(activeEngine)}]" : "")}");
            if (isCurrent) Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  └─ Driver Safety: {dev.SafetyNotes}");
            Console.ResetColor();
        }

        Console.WriteLine(new string('-', 108));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("* To switch your active hardware & driver engine:");
        Console.WriteLine("  glacier config --device <id|name> [--engine <baremetal|directml|cpu>]");
        Console.WriteLine("  Example: glacier config --device nvidia-rtx-4060 --engine baremetal");
        Console.WriteLine("  Example: glacier config --device amd-890m --engine directml");
        Console.WriteLine("  Example: glacier config --device cpu");
        Console.ResetColor();

        return 0;
    }

    public static int RunConfig(string[] args)
    {
        var settings = GlacierSettings.Load();

        if (args.Length == 0)
        {
            var (activeDev, activeEng) = GlacierSettings.ResolveTarget(null, null);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=======================================================================");
            Console.WriteLine("                     GLACIER PERSISTENT SETTINGS                      ");
            Console.WriteLine("=======================================================================");
            Console.ResetColor();
            Console.WriteLine($"Config File:       {GlacierSettings.GetSettingsFilePath()}");
            Console.WriteLine($"Default Device:    {settings.DeviceId ?? "auto"} (Resolved: {activeDev.Name})");
            Console.WriteLine($"Default Engine:    {FormatEngineName(settings.Engine)} (Resolved: {FormatEngineName(activeEng)})");
            Console.WriteLine($"CPU Fallback:      {settings.FallbackToCpu}");
            Console.WriteLine($"Max Seq Length:    {settings.MaxSeqLen}");
            Console.WriteLine($"Default Temp:      {settings.DefaultTemperature}");
            Console.WriteLine($"Default Top-K:     {settings.DefaultTopK}");
            Console.WriteLine($"Default Top-P:     {settings.DefaultTopP}");
            Console.WriteLine();
            Console.WriteLine("Commands to configure:");
            Console.WriteLine("  glacier config --device <id|name> [--engine <baremetal|directml|cpu>]");
            Console.WriteLine("  glacier config --reset");
            return 0;
        }

        if (args[0] is "--reset" or "reset")
        {
            settings.DeviceId = "auto";
            settings.Engine = InferenceEngineType.Auto;
            settings.Save();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Settings successfully reset to auto-detect defaults.");
            Console.ResetColor();
            return 0;
        }

        string? targetDev = null;
        string? targetEng = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--device" && i + 1 < args.Length)
                targetDev = args[++i];
            else if (args[i] == "--engine" && i + 1 < args.Length)
                targetEng = args[++i];
        }

        if (targetDev != null || targetEng != null)
        {
            var (resolvedDev, resolvedEng) = GlacierSettings.ResolveTarget(
                targetDev ?? settings.DeviceId,
                targetEng ?? (settings.Engine != InferenceEngineType.Auto ? settings.Engine.ToString() : null));

            if (targetDev != null) settings.DeviceId = resolvedDev.Id;
            if (targetEng != null) settings.Engine = resolvedEng;
            settings.Save();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Successfully updated settings!");
            Console.ResetColor();
            Console.WriteLine($"Active Device: {resolvedDev.Name} ({resolvedDev.Id})");
            Console.WriteLine($"Active Engine: {FormatEngineName(resolvedEng)}");
            Console.WriteLine($"Settings saved to: {GlacierSettings.GetSettingsFilePath()}");
            return 0;
        }

        Console.WriteLine("Usage: glacier config [--device <id|name>] [--engine <baremetal|directml|cpu>] [--reset]");
        return 1;
    }

    public static int RunInspect(string[] args)
    {
        if (args.Length == 0)
        {
            PrintInspectHelp();
            return 1;
        }

        string modelPath = args[0];
        string? filter = null;
        bool showAll = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "-f" or "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (args[i] is "-a" or "--all") showAll = true;
        }

        if (!File.Exists(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: File not found: {modelPath}");
            Console.ResetColor();
            return 1;
        }

        var sw = Stopwatch.StartNew();
        using var gguf = GgufFile.Open(modelPath);
        var weights = new ModelWeights(gguf);
        sw.Stop();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                 GLACIER - GGUF Zero-Copy Model Inspection                     ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        int gqaRatio = weights.HeadCount / Math.Max(1, weights.HeadCountKv);
        Console.WriteLine($"  File:                 {Path.GetFileName(modelPath)}");
        Console.WriteLine($"  Architecture Family:  {weights.ArchitectureFamily} (Raw: {gguf.Architecture})");
        Console.WriteLine($"  Transformer Layers:   {weights.BlockCount}");
        Console.WriteLine($"  Embedding Dimension:  {weights.EmbeddingLength} (FFN Dim: {weights.FeedForwardLength})");
        Console.WriteLine($"  Attention Heads:      {weights.HeadCount} Q / {weights.HeadCountKv} KV (GQA Ratio: {gqaRatio})");
        Console.WriteLine($"  Context Window:       {weights.ContextLength:N0} tokens");
        Console.WriteLine($"  Vocabulary Size:      {weights.VocabSize:N0} tokens");
        Console.WriteLine($"  RoPE Base Frequency:  {weights.RopeFreqBase:N0} Hz");
        Console.WriteLine($"  Has RoPE Freq Scaling: {weights.HasRopeFreqs}");
        Console.WriteLine($"  Total Tensors:        {gguf.TensorList.Count:N0}");
        Console.WriteLine($"  Header Parse Time:    {sw.Elapsed.TotalMilliseconds:F2} ms\n");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Tensors Sample:");
        Console.ResetColor();

        int printed = 0;
        foreach (var t in gguf.TensorList)
        {
            if (!string.IsNullOrEmpty(filter) && !t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            Console.WriteLine($"  • {t.Name,-45} [{t.Type,-6}] Dims: [{string.Join(", ", t.Dimensions)}]");
            printed++;
            if (!showAll && printed >= 20)
            {
                Console.WriteLine($"  ... and {gguf.TensorList.Count - printed} more tensors (pass --all to show all)");
                break;
            }
        }

        return 0;
    }

    public static async Task<int> RunBenchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintBenchHelp();
            return 1;
        }

        string modelPath = args[0];
        int tokens = 25;
        string prompt = "Explain CPU cache levels L1, L2, and L3 in two sentences.";
        int maxSeqLen = 2048;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "-n" or "--tokens" && i + 1 < args.Length) tokens = int.Parse(args[++i]);
            else if (args[i] is "-p" or "--prompt" && i + 1 < args.Length) prompt = args[++i];
            else if (args[i] is "-c" or "--ctx" && i + 1 < args.Length) maxSeqLen = int.Parse(args[++i]);
        }

        if (!File.Exists(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: File not found: {modelPath}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            GLACIER - Pure C# .NET 10 Inference Speed Benchmark                ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        var swLoad = Stopwatch.StartNew();
        using var session = new InferenceSession(modelPath, maxSeqLen);
        swLoad.Stop();

        Console.WriteLine($"  Model:       {Path.GetFileName(modelPath)}");
        Console.WriteLine($"  Family:      {session.Architecture}");
        Console.WriteLine($"  Engine:      {session.ActiveDevice}");
        Console.WriteLine($"  Cold Start:  {swLoad.Elapsed.TotalMilliseconds:F1} ms\n");

        Console.WriteLine($"[Benchmarking] Generating {tokens} tokens for prompt: \"{prompt}\"...");
        var sampling = new SamplingOptions { MaxTokens = tokens, Temperature = 0.0f };

        var result = await session.GenerateAsync(prompt, sampling, formatChat: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nResults:");
        Console.WriteLine($"  • Prefill Latency:     {result.Metrics.PromptEvalDuration.TotalMilliseconds:F2} ms");
        Console.WriteLine($"  • Autoregressive Speed: {result.Metrics.GenerationTokensPerSecond:F2} tokens/sec");
        Console.WriteLine($"  • Total Generation:    {result.Metrics.GeneratedTokens} tokens in {result.Metrics.TotalDuration.TotalMilliseconds:F1} ms");
        Console.ResetColor();

        return 0;
    }

    private static string FormatEngineName(InferenceEngineType engine) => engine switch
    {
        InferenceEngineType.BareMetal => "Native Driver (SASS/HIP)",
        InferenceEngineType.Vulkan => "Vulkan (CoopMat / WMMA)",
        InferenceEngineType.DirectML => "DirectML",
        InferenceEngineType.Cpu => "Cpu",
        _ => engine.ToString()
    };

    public static void PrintDevicesHelp()
    {
        Console.WriteLine("glacier devices - Enumerate compute hardware, VRAM, and safe driver engines");
    }

    public static void PrintConfigHelp()
    {
        Console.WriteLine("glacier config [options] - View or update acceleration preferences");
    }

    public static void PrintInspectHelp()
    {
        Console.WriteLine("glacier inspect <model.gguf> [options] - Inspect GGUF structure and metadata");
    }

    public static void PrintBenchHelp()
    {
        Console.WriteLine("glacier bench <model.gguf> [options] - Run speed and throughput benchmark");
    }
}
