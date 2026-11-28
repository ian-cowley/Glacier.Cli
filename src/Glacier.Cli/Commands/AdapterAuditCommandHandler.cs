namespace Glacier.Cli.Commands;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

public sealed record AdapterAuditOptions(
    int? ExpectedLayers = null,
    int? ExpectedRank = null,
    float? ExpectedAlpha = null,
    int? ExpectedTensorCount = null,
    double MinLoraBNonZeroRatio = 0.90,
    bool JsonOutput = false);

public sealed record AdapterTensorAuditStat(
    string Name,
    int LayerIndex,
    string Projection,
    string MatrixKind,
    int Dim0,
    int Dim1,
    int NanCount,
    int InfCount,
    int SubnormalCount,
    int ZeroCount,
    double L1Norm,
    double L2Norm,
    double Mean,
    double StdDev,
    float Min,
    float Max);

public sealed record AdapterAuditResult(
    string FilePath,
    long FileSizeBytes,
    uint Magic,
    uint Version,
    int Layers,
    int Rank,
    float Alpha,
    int DeclaredTensorCount,
    int InspectedTensorCount,
    long TotalFloatElements,
    long TotalNans,
    long TotalInfs,
    long TotalSubnormals,
    long TotalZeros,
    double MinNormA,
    double MaxNormA,
    double MinNormB,
    double MaxNormB,
    bool ExactEof,
    IReadOnlyList<AdapterTensorAuditStat> Tensors,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

public static class AdapterAuditCommandHandler
{
    private const uint LoraMagic = 0x41524F4Cu; // "LORA" LE
    private const int ChunkBytes = 65536; // multiple of 4

    public static int Execute(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        TextWriter outWriter = output ?? Console.Out;
        TextWriter errWriter = error ?? Console.Error;

        if (args.Length == 0)
        {
            PrintHelp(outWriter);
            return 1;
        }

        string? adapterArg = null;
        int? expectLayers = null;
        int? expectRank = null;
        float? expectAlpha = null;
        int? expectTensors = null;
        double minLoraBNonZero = 0.90;
        bool jsonOutput = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--expect-layers")
                {
                    string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!int.TryParse(val, out int parsed) || parsed <= 0)
                        throw new ArgumentException($"Invalid {arg} '{val}'. Expected positive integer.");
                    expectLayers = parsed;
                }
                else if (arg == "--expect-rank")
                {
                    string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!int.TryParse(val, out int parsed) || parsed <= 0)
                        throw new ArgumentException($"Invalid {arg} '{val}'. Expected positive integer.");
                    expectRank = parsed;
                }
                else if (arg == "--expect-alpha")
                {
                    string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) || !float.IsFinite(parsed) || parsed <= 0)
                        throw new ArgumentException($"Invalid {arg} '{val}'. Expected positive number.");
                    expectAlpha = parsed;
                }
                else if (arg == "--expect-tensors")
                {
                    string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!int.TryParse(val, out int parsed) || parsed <= 0)
                        throw new ArgumentException($"Invalid {arg} '{val}'. Expected positive integer.");
                    expectTensors = parsed;
                }
                else if (arg == "--min-lora-b-nonzero")
                {
                    string val = CliOptionHelpers.RequireValue(args, ref i, arg);
                    if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || parsed < 0.0 || parsed > 1.0)
                        throw new ArgumentException($"Invalid {arg} '{val}'. Expected ratio in [0.0, 1.0].");
                    minLoraBNonZero = parsed;
                }
                else if (arg == "--json")
                {
                    jsonOutput = true;
                }
                else if (adapterArg == null && !arg.StartsWith('-'))
                {
                    adapterArg = arg;
                }
                else
                {
                    throw new ArgumentException($"Unknown option or unexpected argument '{arg}'.");
                }
            }
        }
        catch (ArgumentException ex)
        {
            errWriter.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(adapterArg))
        {
            errWriter.WriteLine("[ERROR] Missing adapter binary path. Usage: glacier audit-adapter <adapter.bin> [options]");
            return 1;
        }

        string resolvedPath = CliOptionHelpers.ResolveLoraFilePath(adapterArg);
        if (!File.Exists(resolvedPath))
        {
            errWriter.WriteLine($"[ERROR] LoRA adapter binary not found: {resolvedPath}");
            return 2;
        }

        var options = new AdapterAuditOptions(expectLayers, expectRank, expectAlpha, expectTensors, minLoraBNonZero, jsonOutput);
        var result = AuditFile(resolvedPath, options);

        if (jsonOutput)
        {
            outWriter.WriteLine(FormatJson(result));
            return result.Passed ? 0 : 1;
        }

        outWriter.WriteLine("===========================================================================");
        outWriter.WriteLine("GLACIER LORA ADAPTER WEIGHT AUDIT");
        outWriter.WriteLine($"Target File: {result.FilePath}");
        outWriter.WriteLine("===========================================================================");
        outWriter.WriteLine($"  File Size:      {result.FileSizeBytes:N0} bytes ({result.FileSizeBytes / (1024.0 * 1024.0):F2} MB)");
        outWriter.WriteLine($"  Magic / Ver:    0x{result.Magic:X8} / v{result.Version}");
        outWriter.WriteLine($"  Layers / Rank:  {result.Layers} / r={result.Rank} (alpha={result.Alpha:F1})");
        outWriter.WriteLine($"  Tensors:        {result.InspectedTensorCount} / {result.DeclaredTensorCount}");
        outWriter.WriteLine($"  Float Params:   {result.TotalFloatElements:N0}");
        outWriter.WriteLine($"  NaN / Inf / Sub:{result.TotalNans} / {result.TotalInfs} / {result.TotalSubnormals}");
        if (!double.IsPositiveInfinity(result.MinNormA))
            outWriter.WriteLine($"  LoRA A Norms:   min={result.MinNormA:F4}, max={result.MaxNormA:F4}");
        if (!double.IsPositiveInfinity(result.MinNormB))
            outWriter.WriteLine($"  LoRA B Norms:   min={result.MinNormB:F4}, max={result.MaxNormB:F4}");
        outWriter.WriteLine($"  Exact EOF:      {(result.ExactEof ? "Yes" : "No")}");

        var distinctLayers = result.Tensors.Where(t => t.LayerIndex >= 0).Select(t => t.LayerIndex).Distinct().OrderBy(l => l).ToList();
        if (distinctLayers.Count > 0)
        {
            int mid = distinctLayers[distinctLayers.Count / 2];
            int[] sampleLayers = new[] { distinctLayers[0], mid, distinctLayers[^1] }.Distinct().ToArray();
            outWriter.WriteLine();
            outWriter.WriteLine("--- Boundary Layer Spot-Check ---");
            foreach (int layer in sampleLayers)
            {
                outWriter.WriteLine($"[Layer {layer}]");
                foreach (var t in result.Tensors.Where(x => x.LayerIndex == layer))
                {
                    outWriter.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {t.Projection,-12} {t.MatrixKind,-6} [{t.Dim0,5}x{t.Dim1,5}] | Norm: {t.L2Norm,8:F4} | Std: {t.StdDev,8:F6} | Min: {t.Min,+8:F4} | Max: {t.Max,+8:F4}"));
                }
            }
        }

        outWriter.WriteLine();
        if (result.Passed)
        {
            outWriter.WriteLine("VERDICT: APPROVE");
        }
        else
        {
            outWriter.WriteLine("Failures:");
            foreach (string f in result.Failures)
                outWriter.WriteLine($"  [FAIL] {f}");
            outWriter.WriteLine("VERDICT: REJECT");
        }

        return result.Passed ? 0 : 1;
    }

    public static AdapterAuditResult AuditFile(string path, AdapterAuditOptions? options = null)
    {
        options ??= new AdapterAuditOptions();
        string resolvedPath = CliOptionHelpers.ResolveLoraFilePath(path);
        if (!File.Exists(resolvedPath))
            throw new FileNotFoundException($"LoRA adapter binary not found: {resolvedPath}", resolvedPath);

        string fullPath = Path.GetFullPath(resolvedPath);
        long fileSize = new FileInfo(fullPath).Length;
        var failures = new List<string>();
        var tensorStats = new List<AdapterTensorAuditStat>();

        uint magic = 0;
        uint version = 0;
        int layers = 0;
        int rank = 0;
        float alpha = 0;
        int tensorCount = 0;
        long totalElems = 0;
        long totalNans = 0;
        long totalInfs = 0;
        long totalSubnormals = 0;
        long totalZeros = 0;
        double minNormA = double.PositiveInfinity;
        double maxNormA = 0.0;
        double minNormB = double.PositiveInfinity;
        double maxNormB = 0.0;

        if (fileSize < 24)
        {
            failures.Add($"Adapter file shorter than 24-byte header ({fileSize} bytes).");
            return new AdapterAuditResult(fullPath, fileSize, magic, version, layers, rank, alpha, tensorCount, 0, totalElems, totalNans, totalInfs, totalSubnormals, totalZeros, minNormA, maxNormA, minNormB, maxNormB, ExactEof: false, tensorStats, failures);
        }

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        magic = reader.ReadUInt32();
        version = reader.ReadUInt32();
        layers = reader.ReadInt32();
        rank = reader.ReadInt32();
        alpha = reader.ReadSingle();
        tensorCount = reader.ReadInt32();

        if (magic != LoraMagic)
        {
            failures.Add($"Invalid LoRA magic: 0x{magic:X8} (expected 0x{LoraMagic:X8}).");
            return new AdapterAuditResult(fullPath, fileSize, magic, version, layers, rank, alpha, tensorCount, 0, totalElems, totalNans, totalInfs, totalSubnormals, totalZeros, minNormA, maxNormA, minNormB, maxNormB, ExactEof: false, tensorStats, failures);
        }

        if (version != 1)
            failures.Add($"Unsupported adapter version {version} (expected 1).");
        if (layers <= 0)
            failures.Add($"Invalid layer count {layers}.");
        if (rank <= 0)
            failures.Add($"Invalid rank {rank}.");
        if (!float.IsFinite(alpha) || alpha <= 0)
            failures.Add($"Invalid alpha {alpha}.");
        if (tensorCount <= 0)
        {
            failures.Add($"Invalid tensor count {tensorCount}.");
            return new AdapterAuditResult(fullPath, fileSize, magic, version, layers, rank, alpha, tensorCount, 0, totalElems, totalNans, totalInfs, totalSubnormals, totalZeros, minNormA, maxNormA, minNormB, maxNormB, ExactEof: false, tensorStats, failures);
        }

        if (options.ExpectedLayers.HasValue && layers != options.ExpectedLayers.Value)
            failures.Add($"Layer count mismatch: got {layers}, expected {options.ExpectedLayers.Value}.");
        if (options.ExpectedRank.HasValue && rank != options.ExpectedRank.Value)
            failures.Add($"Rank mismatch: got {rank}, expected {options.ExpectedRank.Value}.");
        if (options.ExpectedAlpha.HasValue && MathF.Abs(alpha - options.ExpectedAlpha.Value) > 1e-4f)
            failures.Add($"Alpha mismatch: got {alpha}, expected {options.ExpectedAlpha.Value}.");
        if (options.ExpectedTensorCount.HasValue && tensorCount != options.ExpectedTensorCount.Value)
            failures.Add($"Tensor count mismatch: got {tensorCount}, expected {options.ExpectedTensorCount.Value}.");

        byte[] poolBuf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            for (int tIdx = 0; tIdx < tensorCount; tIdx++)
            {
                string name;
                int dim0;
                int dim1;
                try
                {
                    name = reader.ReadString();
                    dim0 = reader.ReadInt32();
                    dim1 = reader.ReadInt32();
                }
                catch (EndOfStreamException)
                {
                    failures.Add($"Premature EOF reading tensor header at index {tIdx}.");
                    break;
                }

                if (dim0 <= 0 || dim1 <= 0)
                {
                    failures.Add($"Tensor '{name}' has non-positive dimensions [{dim0}x{dim1}].");
                    break;
                }

                long elemCount = (long)dim0 * dim1;
                long payloadBytes = elemCount * 4L;
                if (fs.Position + payloadBytes > fileSize)
                {
                    failures.Add($"Tensor '{name}' payload ({payloadBytes:N0} bytes) exceeds EOF at index {tIdx}.");
                    break;
                }

                double sumVal = 0.0;
                double sumAbs = 0.0;
                double sumSq = 0.0;
                int zeros = 0;
                int nans = 0;
                int infs = 0;
                int subnormals = 0;
                float minVal = float.PositiveInfinity;
                float maxVal = float.NegativeInfinity;

                long remainingBytes = payloadBytes;
                while (remainingBytes > 0)
                {
                    int toRead = (int)Math.Min(remainingBytes, ChunkBytes);
                    if (!ReadExact(fs, poolBuf.AsSpan(0, toRead)))
                    {
                        failures.Add($"Premature EOF reading payload of tensor '{name}'.");
                        break;
                    }

                    var floats = MemoryMarshal.Cast<byte, float>(poolBuf.AsSpan(0, toRead));
                    for (int i = 0; i < floats.Length; i++)
                    {
                        float v = floats[i];
                        if (float.IsNaN(v))
                        {
                            nans++;
                            continue;
                        }
                        if (float.IsInfinity(v))
                        {
                            infs++;
                            continue;
                        }
                        if (v == 0.0f)
                            zeros++;
                        else if (float.IsSubnormal(v))
                            subnormals++;

                        if (v < minVal) minVal = v;
                        if (v > maxVal) maxVal = v;
                        sumVal += v;
                        sumAbs += Math.Abs((double)v);
                        sumSq += (double)v * v;
                    }

                    remainingBytes -= toRead;
                }

                if (remainingBytes > 0)
                    break;

                double l2Norm = Math.Sqrt(sumSq);
                double mean = sumVal / elemCount;
                double variance = Math.Max(0.0, (sumSq / elemCount) - (mean * mean));
                double stdDev = Math.Sqrt(variance);
                if (float.IsPositiveInfinity(minVal)) minVal = float.NaN;
                if (float.IsNegativeInfinity(maxVal)) maxVal = float.NaN;

                ParseTensorMetadata(name, out int layerIdx, out string proj, out string matrixKind);

                if (nans > 0)
                    failures.Add($"Tensor '{name}' contains {nans} NaN value(s).");
                if (infs > 0)
                    failures.Add($"Tensor '{name}' contains {infs} Inf value(s).");
                if (l2Norm <= 1e-7)
                    failures.Add($"Tensor '{name}' has zero or near-zero L2 norm ({l2Norm:E3}).");
                if (stdDev <= 1e-7)
                    failures.Add($"Tensor '{name}' has zero or near-zero std dev ({stdDev:E3}).");

                bool isLoraB = matrixKind.Equals("lora_b", StringComparison.OrdinalIgnoreCase);
                if (isLoraB)
                {
                    double nonZeroRatio = (double)(elemCount - zeros) / elemCount;
                    if (options.MinLoraBNonZeroRatio > 0.0 && nonZeroRatio < options.MinLoraBNonZeroRatio)
                    {
                        failures.Add($"Tensor '{name}' (LoRA B) non-zero ratio {nonZeroRatio:P2} is below required {options.MinLoraBNonZeroRatio:P2} ({zeros}/{elemCount} zeros).");
                    }
                    if (l2Norm < minNormB) minNormB = l2Norm;
                    if (l2Norm > maxNormB) maxNormB = l2Norm;
                }
                else
                {
                    if (l2Norm < minNormA) minNormA = l2Norm;
                    if (l2Norm > maxNormA) maxNormA = l2Norm;
                }

                totalElems += elemCount;
                totalNans += nans;
                totalInfs += infs;
                totalSubnormals += subnormals;
                totalZeros += zeros;

                tensorStats.Add(new AdapterTensorAuditStat(
                    name, layerIdx, proj, matrixKind, dim0, dim1,
                    nans, infs, subnormals, zeros,
                    sumAbs, l2Norm, mean, stdDev, minVal, maxVal));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(poolBuf);
        }

        bool exactEof = fs.Position == fileSize && tensorStats.Count == tensorCount;
        if (fs.Position != fileSize)
        {
            failures.Add($"File cursor {fs.Position:N0} != file size {fileSize:N0} (trailing or unconsumed bytes detected).");
        }

        return new AdapterAuditResult(
            fullPath,
            fileSize,
            magic,
            version,
            layers,
            rank,
            alpha,
            tensorCount,
            tensorStats.Count,
            totalElems,
            totalNans,
            totalInfs,
            totalSubnormals,
            totalZeros,
            minNormA,
            maxNormA,
            minNormB,
            maxNormB,
            exactEof,
            tensorStats,
            failures);
    }

    public static string FormatJson(AdapterAuditResult result)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("passed", result.Passed);
            writer.WriteString("filePath", result.FilePath);
            writer.WriteNumber("fileSizeBytes", result.FileSizeBytes);
            writer.WriteNumber("magic", result.Magic);
            writer.WriteNumber("version", result.Version);
            writer.WriteNumber("layers", result.Layers);
            writer.WriteNumber("rank", result.Rank);
            writer.WriteNumber("alpha", result.Alpha);
            writer.WriteNumber("declaredTensorCount", result.DeclaredTensorCount);
            writer.WriteNumber("inspectedTensorCount", result.InspectedTensorCount);
            writer.WriteNumber("totalFloatElements", result.TotalFloatElements);
            writer.WriteNumber("totalNans", result.TotalNans);
            writer.WriteNumber("totalInfs", result.TotalInfs);
            writer.WriteNumber("totalSubnormals", result.TotalSubnormals);
            writer.WriteNumber("totalZeros", result.TotalZeros);
            writer.WriteBoolean("exactEof", result.ExactEof);

            if (!double.IsPositiveInfinity(result.MinNormA))
            {
                writer.WriteNumber("minNormA", result.MinNormA);
                writer.WriteNumber("maxNormA", result.MaxNormA);
            }
            if (!double.IsPositiveInfinity(result.MinNormB))
            {
                writer.WriteNumber("minNormB", result.MinNormB);
                writer.WriteNumber("maxNormB", result.MaxNormB);
            }

            writer.WriteStartArray("failures");
            foreach (string f in result.Failures)
                writer.WriteStringValue(f);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void PrintHelp() => PrintHelp(Console.Out);

    public static void PrintHelp(TextWriter output)
    {
        output.WriteLine("Usage: glacier audit-adapter <adapter.bin|dir> [options]");
        output.WriteLine();
        output.WriteLine("Audits binary LoRA adapter headers, EOF alignment, NaN/Inf/subnormal health,");
        output.WriteLine("L1/L2 norms, and non-zero LoRA-B backpropagation coverage.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --expect-layers <int>         Assert expected transformer layer count");
        output.WriteLine("  --expect-rank <int>           Assert expected LoRA rank");
        output.WriteLine("  --expect-alpha <float>        Assert expected LoRA alpha scaling factor");
        output.WriteLine("  --expect-tensors <int>        Assert expected total matrix count");
        output.WriteLine("  --min-lora-b-nonzero <ratio>  Minimum non-zero ratio for LoRA B matrices (default: 0.90)");
        output.WriteLine("  --json                        Emit structured JSON report on stdout");
    }

    private static void ParseTensorMetadata(string name, out int layerIndex, out string projection, out string matrixKind)
    {
        layerIndex = -1;
        projection = name;
        matrixKind = name.Contains("lora_b", StringComparison.OrdinalIgnoreCase)
            ? "lora_b"
            : name.Contains("lora_a", StringComparison.OrdinalIgnoreCase) ? "lora_a" : "unknown";

        string[] parts = name.Split('.');
        if (parts.Length >= 4 && parts[0] == "blk" && int.TryParse(parts[1], out int parsedLayer))
        {
            layerIndex = parsedLayer;
            projection = parts[2];
            matrixKind = parts[3];
        }
    }

    private static bool ReadExact(Stream s, Span<byte> buffer)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int n = s.Read(buffer[readTotal..]);
            if (n == 0) return false;
            readTotal += n;
        }
        return true;
    }
}
