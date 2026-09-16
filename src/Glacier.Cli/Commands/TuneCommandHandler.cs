namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Glacier.Tune.Config;
using Glacier.Tune.Data;
using Glacier.Tune.Export;
using Glacier.Tune.Model;
using Glacier.Tune.Trainer;

public static class TuneCommandHandler
{
    public static Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return Task.FromResult(1);
        }

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
        string device = "auto";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model" && i + 1 < args.Length)
            {
                baseGgufPath = args[++i];
            }
            else if (arg is "-d" or "--data" && i + 1 < args.Length)
            {
                dataJsonlPath = args[++i];
            }
            else if (arg is "-o" or "--out" or "--output" && i + 1 < args.Length)
            {
                outAdapterPath = args[++i];
            }
            else if (arg is "--merge" && i + 1 < args.Length)
            {
                mergeOutGguf = args[++i];
            }
            else if (arg is "-r" or "--rank" && i + 1 < args.Length)
            {
                rank = int.Parse(args[++i]);
            }
            else if (arg is "--alpha" && i + 1 < args.Length)
            {
                alpha = float.Parse(args[++i]);
            }
            else if (arg is "--lr" && i + 1 < args.Length)
            {
                lr = float.Parse(args[++i]);
            }
            else if (arg is "-e" or "--epochs" && i + 1 < args.Length)
            {
                epochs = int.Parse(args[++i]);
            }
            else if (arg is "-b" or "--batch-size" && i + 1 < args.Length)
            {
                batchSize = int.Parse(args[++i]);
            }
            else if (arg is "--grad-accum" && i + 1 < args.Length)
            {
                gradAccum = int.Parse(args[++i]);
            }
            else if (arg is "--device" && i + 1 < args.Length)
            {
                device = args[++i];
            }
            else if (baseGgufPath == null && !arg.StartsWith("-"))
            {
                baseGgufPath = arg;
            }
        }

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
            OutputDir = Path.GetDirectoryName(outAdapterPath) ?? "."
        };

        Console.WriteLine($"[1/4] Loading base model into frozen unmanaged memory...");
        var swTotal = Stopwatch.StartNew();
        using var model = GgufLoraModel.Load(baseGgufPath, loraConfig, device);

        Console.WriteLine($"[2/4] Parsing ChatML training dataset: {dataJsonlPath}...");
        var dataset = ChatMlDataset.FromFile(dataJsonlPath, model.Tokenizer);

        Console.WriteLine($"[3/4] Initializing LoRA Trainer (Rank: {rank}, Alpha: {alpha}, LR: {lr:E2})...");
        using var trainer = new LoraTrainer(model, trainArgs);

        int totalTokens = 0;
        trainer.Train(dataset, onStep: step =>
        {
            totalTokens += (int)(step.TokensPerSec * (step.StepDurationMs / 1000.0));
            Console.WriteLine($"  Step {step.Step,4} | Loss: {step.Loss:F4} | {step.TokensPerSec:F1} tok/s | Step Time: {step.StepDurationMs:F0} ms");
        });

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
        Console.WriteLine("  -b, --batch-size <int>               Batch size (default: 1)");
        Console.WriteLine("  --grad-accum <int>                   Gradient accumulation steps (default: 4)");
        Console.WriteLine("  --merge <output.gguf>                Automatically fuse adapter into standalone GGUF on completion");
    }
}
