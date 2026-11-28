namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Glacier.Tune.Config;
using Glacier.Tune.Data;
using Glacier.Tune.Export;
using Glacier.Tune.Model;
using Glacier.Tune.Trainer;

public sealed record TuneCliOptions(
    string? BaseGgufPath = null,
    string? DataJsonlPath = null,
    string OutAdapterPath = "adapters/lora_adapter.bin",
    string? MergeOutGguf = null,
    int Rank = 16,
    float Alpha = 32f,
    float LearningRate = 2e-4f,
    int Epochs = 3,
    int BatchSize = 1,
    int GradAccum = 4,
    int MaxSeqLen = 512,
    int? MaxSteps = null,
    string Device = "auto",
    bool CheckpointActivations = false);

public static class TuneCommandHandler
{
    public static TuneCliOptions ParseOptions(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? baseGgufPath = null;
        string? dataJsonlPath = null;
        string outAdapterPath = "adapters/lora_adapter.bin";
        string? mergeOutGguf = null;
        int rank = 16;
        float alpha = 32f;
        float lr = 2e-4f;
        int epochs = 3;
        int batchSize = 1;
        int gradAccum = 4;
        int maxSeqLen = 512;
        int? maxSteps = null;
        string device = "auto";
        bool checkpointActivations = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model")
            {
                baseGgufPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "-d" or "--data")
            {
                dataJsonlPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "-o" or "--out" or "--output")
            {
                outAdapterPath = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg == "--merge")
            {
                mergeOutGguf = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "-r" or "--rank")
            {
                rank = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg == "--alpha")
            {
                alpha = ParsePositiveFloat(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg == "--lr")
            {
                lr = ParsePositiveFloat(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg is "-e" or "--epochs")
            {
                epochs = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg is "-s" or "--steps" or "--max-steps")
            {
                maxSteps = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg is "--seq-len" or "--max-seq-len")
            {
                maxSeqLen = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg is "-b" or "--batch-size")
            {
                batchSize = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg == "--grad-accum")
            {
                gradAccum = ParsePositiveInt(arg, CliOptionHelpers.RequireValue(args, ref i, arg));
            }
            else if (arg == "--device")
            {
                device = CliOptionHelpers.RequireValue(args, ref i, arg);
            }
            else if (arg is "--checkpoint-activations" or "--recompute")
            {
                checkpointActivations = true;
            }
            else if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
            else if (baseGgufPath == null)
            {
                baseGgufPath = arg;
            }
            else
            {
                throw new ArgumentException($"Unexpected positional argument '{arg}'.");
            }
        }

        return new TuneCliOptions(
            BaseGgufPath: baseGgufPath,
            DataJsonlPath: dataJsonlPath,
            OutAdapterPath: outAdapterPath,
            MergeOutGguf: mergeOutGguf,
            Rank: rank,
            Alpha: alpha,
            LearningRate: lr,
            Epochs: epochs,
            BatchSize: batchSize,
            GradAccum: gradAccum,
            MaxSeqLen: maxSeqLen,
            MaxSteps: maxSteps,
            Device: device,
            CheckpointActivations: checkpointActivations);
    }

    public static Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return Task.FromResult(1);
        }

        TuneCliOptions opts;
        try
        {
            opts = ParseOptions(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return Task.FromResult(1);
        }

        string? baseGgufPath = opts.BaseGgufPath;
        string? dataJsonlPath = opts.DataJsonlPath;
        string outAdapterPath = opts.OutAdapterPath;
        string? mergeOutGguf = opts.MergeOutGguf;
        int rank = opts.Rank;
        float alpha = opts.Alpha;
        float lr = opts.LearningRate;
        int epochs = opts.Epochs;
        int batchSize = opts.BatchSize;
        int gradAccum = opts.GradAccum;
        int maxSeqLen = opts.MaxSeqLen;
        int? maxSteps = opts.MaxSteps;
        string device = opts.Device;
        bool checkpointActivations = opts.CheckpointActivations;

        if (string.IsNullOrEmpty(baseGgufPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Missing base GGUF model path. Usage: glacier tune <base.gguf> --data <train.jsonl>");
            Console.ResetColor();
            return Task.FromResult(1);
        }

        if (!File.Exists(baseGgufPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Base model file not found: {baseGgufPath}");
            Console.ResetColor();
            return Task.FromResult(1);
        }

        if (string.IsNullOrEmpty(dataJsonlPath) || !File.Exists(dataJsonlPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Training dataset file not found: {dataJsonlPath}");
            Console.ResetColor();
            return Task.FromResult(1);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("               GLACIER.TUNE - Pure C# .NET 10 LLM Fine-Tuning                   ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        var loraConfig = new LoraConfig
        {
            Rank = rank,
            Alpha = alpha,
            Dropout = 0.05f
        };

        var trainArgs = new TrainingArguments
        {
            LearningRate = lr,
            Epochs = epochs,
            BatchSize = batchSize,
            GradientAccumulationSteps = gradAccum,
            MaxSteps = maxSteps,
            CheckpointActivations = checkpointActivations,
            OutputDir = Path.GetDirectoryName(outAdapterPath) ?? "."
        };

        Console.WriteLine($"[1/4] Loading base model into frozen unmanaged memory...");
        var swTotal = Stopwatch.StartNew();
        using var model = GgufLoraModel.Load(baseGgufPath, loraConfig, device);

        Console.WriteLine($"[2/4] Parsing ChatML training dataset (Max Length: {maxSeqLen}): {dataJsonlPath}...");
        var dataset = ChatMlDataset.FromFile(dataJsonlPath, model.Tokenizer, maxSeqLength: maxSeqLen);

        Console.WriteLine($"[3/4] Initializing LoRA Trainer (Rank: {rank}, Alpha: {alpha}, LR: {lr:E2})...");
        using var trainer = new LoraTrainer(model, trainArgs);

        trainer.Train(dataset);

        Console.WriteLine($"[4/4] Saving LoRA delta adapter to: {outAdapterPath}...");
        string? outDir = Path.GetDirectoryName(outAdapterPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        model.SaveAdapter(outAdapterPath);

        swTotal.Stop();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ Fine-Tuning Complete in {swTotal.Elapsed.TotalMinutes:F2} minutes!");
        Console.WriteLine($"✓ LoRA Adapter saved: {outAdapterPath}");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(mergeOutGguf))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[Post-Training] Fusing LoRA adapter directly into {mergeOutGguf}...");
            Console.ResetColor();
            GgufMerger.Merge(baseGgufPath, outAdapterPath, mergeOutGguf);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Merged standalone model generated: {mergeOutGguf}");
            Console.ResetColor();
        }

        return Task.FromResult(0);
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier tune - In-process pure C# .NET 10 LoRA fine-tuning");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier tune <base.gguf> --data <train.jsonl> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -d, --data <path>                    Training JSONL file (ChatML formatted) (required)");
        Console.WriteLine("  -o, --out <path>                     Output adapter binary path (default: adapters/lora_adapter.bin)");
        Console.WriteLine("  -r, --rank <int>                     LoRA rank dimension (default: 16)");
        Console.WriteLine("  --alpha <float>                      LoRA scaling coefficient alpha (default: 32)");
        Console.WriteLine("  --lr <float>                         Learning rate (default: 0.0002)");
        Console.WriteLine("  -e, --epochs <int>                   Number of training epochs (default: 3)");
        Console.WriteLine("  -s, --steps <int>                    Maximum training steps (overrides epochs if set)");
        Console.WriteLine("  --seq-len <int>                      Maximum sequence length in tokens (default: 512)");
        Console.WriteLine("  -b, --batch-size <int>               Batch size (default: 1)");
        Console.WriteLine("  --grad-accum <int>                   Gradient accumulation steps (default: 4)");
        Console.WriteLine("  --device <auto|cuda|cpu>             Compute target accelerator (default: auto)");
        Console.WriteLine("  --merge <output.gguf>                Automatically fuse adapter into standalone GGUF on completion");
    }

    private static int ParsePositiveInt(string flag, string rawValue)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"Invalid {flag} '{rawValue}'. Expected a positive integer.");
        return parsed;
    }

    private static float ParsePositiveFloat(string flag, string rawValue)
    {
        if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            !float.IsFinite(parsed) || parsed <= 0f)
        {
            throw new ArgumentException($"Invalid {flag} '{rawValue}'. Expected a positive finite number.");
        }
        return parsed;
    }
}
