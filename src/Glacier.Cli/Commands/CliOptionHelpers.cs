namespace Glacier.Cli.Commands;

using System;
using System.IO;
using Glacier.Inference.Memory;

public sealed record LoraAdapterOption(string Name, string Path);

public static class CliOptionHelpers
{
    public static LoraAdapterOption ParseLoraSpec(string? rawSpec)
    {
        if (string.IsNullOrWhiteSpace(rawSpec))
            throw new ArgumentException("Error: --lora requires a <[name=]path> value.");

        string trimmed = rawSpec.Trim();
        string name;
        string rawPath;

        if (!File.Exists(trimmed) && !Directory.Exists(trimmed))
        {
            int eqIdx = trimmed.IndexOf('=');
            if (eqIdx == 0)
                throw new ArgumentException("LoRA adapter name before '=' cannot be empty.");

            if (eqIdx > 0)
            {
                name = trimmed[..eqIdx].Trim();
                rawPath = trimmed[(eqIdx + 1)..].Trim();
            }
            else
            {
                rawPath = trimmed;
                name = DeriveLoraName(rawPath);
            }
        }
        else
        {
            rawPath = trimmed;
            name = DeriveLoraName(rawPath);
        }

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"Could not derive a valid LoRA adapter name from '{rawSpec}'.");

        if (name.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException($"LoRA adapter name '{name}' cannot contain ':'.");

        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("LoRA adapter file path cannot be empty.");

        return new LoraAdapterOption(name, rawPath);
    }

    public static string ResolveLoraFilePath(string path)
    {
        return Directory.Exists(path)
            ? Path.Combine(path, "adapter_model.bin")
            : path;
    }

    public static KvCachePrecision ParseKvPrecision(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return KvCachePrecision.Auto;

        if (!KvCachePrecisionExtensions.TryParse(rawValue, out var precision))
        {
            throw new ArgumentException(
                $"Unsupported --kv-precision '{rawValue}'. Valid values: auto, fp32, fp16, fp8, q8_0, q4_0.");
        }

        return precision;
    }

    public static string RequireValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Option '{flag}' requires a value.");

        return args[++index];
    }

    private static string DeriveLoraName(string rawPath)
    {
        string trimmedPath = rawPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(rawPath))
            return Path.GetFileName(trimmedPath);

        string fileName = Path.GetFileNameWithoutExtension(trimmedPath);
        return string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(trimmedPath) : fileName;
    }
}
