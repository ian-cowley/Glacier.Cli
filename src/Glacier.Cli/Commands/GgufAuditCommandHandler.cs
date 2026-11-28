namespace Glacier.Cli.Commands;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Glacier.Inference.Gguf;

public sealed record GgufAuditOptions(
    IReadOnlyList<string> ForbiddenTypes,
    long? MaxBytes = null,
    IReadOnlyList<string>? ProbeTensors = null,
    bool JsonOutput = false);

public sealed record GgufTensorAuditEntry(
    string Name,
    GgufType Type,
    ulong[] Dimensions,
    ulong Offset,
    ulong ByteSize);

public sealed record GgufProbeResult(
    string Name,
    GgufType Type,
    int ElementsSampled,
    int NanCount,
    int InfCount,
    double Mean,
    double StdDev,
    double Min,
    double Max,
    bool Supported);

public sealed record GgufAuditResult(
    string FilePath,
    long FileSizeBytes,
    uint Version,
    ulong TensorCount,
    ulong MetadataKvCount,
    string Architecture,
    string ModelName,
    uint Alignment,
    ulong HeaderEndOffset,
    ulong TensorDataOffset,
    ulong MaxPayloadEndOffset,
    IReadOnlyDictionary<string, int> TypeCounts,
    IReadOnlyList<GgufProbeResult> Probes,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

public static class GgufAuditCommandHandler
{
    private const uint GgufMagic = 0x46554747; // "GGUF" LE
    private const int DefaultMaxProbes = 8;

    public static GgufAuditResult AuditFile(string filePath, GgufAuditOptions? options = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"GGUF model file not found: {filePath}", filePath);

        options ??= new GgufAuditOptions([]);
        string fullPath = Path.GetFullPath(filePath);
        long fileSize = new FileInfo(fullPath).Length;

        var failures = new List<string>();
        var typeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var probes = new List<GgufProbeResult>();

        uint version = 0;
        ulong tensorCount = 0;
        ulong metadataKvCount = 0;
        string arch = "unknown";
        string modelName = "";
        uint alignment = 32;
        ulong headerEndOffset = 0;
        ulong tensorDataOffset = 0;
        ulong maxPayloadEnd = 0;

        if (options.MaxBytes.HasValue && fileSize > options.MaxBytes.Value)
        {
            failures.Add($"File size {fileSize:N0} bytes exceeds configured ceiling {options.MaxBytes.Value:N0} bytes.");
        }

        if (fileSize < 24)
        {
            failures.Add($"File is smaller than minimum 24-byte GGUF header ({fileSize} bytes).");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (!TryReadUInt32(fs, out uint magic) || magic != GgufMagic)
        {
            failures.Add($"Invalid GGUF magic header: 0x{magic:X8} (expected 0x{GgufMagic:X8}).");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        if (!TryReadUInt32(fs, out version) || (version != 2 && version != 3))
        {
            failures.Add($"Unsupported GGUF version {version} (expected 2 or 3).");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        if (!TryReadUInt64(fs, out tensorCount) || !TryReadUInt64(fs, out metadataKvCount))
        {
            failures.Add("Truncated GGUF header while reading tensor/KV counts.");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        ulong remainingHeaderBytes = (ulong)Math.Max(0L, fileSize - fs.Position);
        if (metadataKvCount > remainingHeaderBytes / 8 || tensorCount > remainingHeaderBytes / 16)
        {
            failures.Add($"Corrupt GGUF header counts exceed file length (TensorCount={tensorCount}, MetadataKVCount={metadataKvCount}).");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        try
        {
            for (ulong i = 0; i < metadataKvCount; i++)
            {
                string key = ReadGgufString(fs, fileSize);
                if (!TryReadUInt32(fs, out uint rawValType))
                    throw new EndOfStreamException($"Unexpected EOF reading value type for metadata key '{key}'.");

                var valType = (GgufValueType)rawValType;
                bool keepScalar = key is "general.architecture" or "general.name" or "general.alignment";
                object? val = ReadOrSkipGgufValue(fs, valType, fileSize, keepScalar, depth: 0);

                if (key == "general.architecture" && val is string sArch && !string.IsNullOrEmpty(sArch))
                {
                    arch = sArch;
                }
                else if (key == "general.name" && val is string sName)
                {
                    modelName = sName;
                }
                else if (key == "general.alignment" && TryExtractUInt32(val, out uint parsedAlign))
                {
                    alignment = parsedAlign;
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            failures.Add($"Malformed metadata section: {ex.Message}");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            failures.Add($"Invalid general.alignment={alignment}: must be a positive power of 2.");
            alignment = 32;
        }

        var tensors = new List<GgufTensorAuditEntry>((int)Math.Min(tensorCount, 65536));
        try
        {
            for (ulong i = 0; i < tensorCount; i++)
            {
                string tName = ReadGgufString(fs, fileSize);
                if (!TryReadUInt32(fs, out uint nDims))
                    throw new EndOfStreamException($"Unexpected EOF reading dimensions for tensor '{tName}'.");
                if (nDims > 8)
                    throw new InvalidDataException($"Tensor '{tName}' has implausible dimension count ({nDims}).");

                var dims = new ulong[nDims];
                for (uint d = 0; d < nDims; d++)
                {
                    if (!TryReadUInt64(fs, out dims[d]))
                        throw new EndOfStreamException($"Unexpected EOF reading dimension {d} of tensor '{tName}'.");
                }

                if (!TryReadUInt32(fs, out uint rawType) || !TryReadUInt64(fs, out ulong offset))
                    throw new EndOfStreamException($"Unexpected EOF reading type/offset for tensor '{tName}'.");

                var ggufType = (GgufType)rawType;
                string typeKey = Enum.IsDefined(ggufType)
                    ? ggufType.ToString()
                    : $"UNKNOWN({rawType})";
                typeCounts[typeKey] = typeCounts.TryGetValue(typeKey, out int prevCnt) ? prevCnt + 1 : 1;

                var info = new GgufTensorInfo
                {
                    Name = tName,
                    DimensionsCount = nDims,
                    Dimensions = dims,
                    Type = ggufType,
                    Offset = offset
                };
                tensors.Add(new GgufTensorAuditEntry(tName, ggufType, dims, offset, info.GetByteSize()));
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            failures.Add($"Malformed tensor directory: {ex.Message}");
            return new GgufAuditResult(fullPath, fileSize, version, tensorCount, metadataKvCount, arch, modelName, alignment, headerEndOffset, tensorDataOffset, maxPayloadEnd, typeCounts, probes, failures);
        }

        foreach (string forbidden in options.ForbiddenTypes)
        {
            if (string.IsNullOrWhiteSpace(forbidden))
                continue;

            foreach (var kv in typeCounts)
            {
                if (kv.Value > 0 && kv.Key.Equals(forbidden.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Forbidden tensor type '{kv.Key}' present ({kv.Value} tensors).");
                }
            }
        }

        headerEndOffset = (ulong)fs.Position;
        ulong rem = headerEndOffset % alignment;
        tensorDataOffset = rem == 0 ? headerEndOffset : headerEndOffset + (alignment - rem);

        if (tensors.Count > 0 && tensorDataOffset > (ulong)fileSize)
        {
            failures.Add($"Aligned tensor data offset ({tensorDataOffset:N0}) exceeds file size ({fileSize:N0}).");
        }

        foreach (var t in tensors)
        {
            if (t.Offset % alignment != 0)
            {
                failures.Add($"Tensor '{t.Name}' offset {t.Offset} is not {alignment}-byte aligned.");
            }
        }

        var sorted = tensors.OrderBy(t => t.Offset).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        var validBoundsTensors = new HashSet<string>(StringComparer.Ordinal);
        ulong prevEnd = 0;
        string? prevName = null;

        for (int i = 0; i < sorted.Count; i++)
        {
            var t = sorted[i];
            ulong start;
            ulong end;
            try
            {
                start = checked(tensorDataOffset + t.Offset);
                end = checked(start + t.ByteSize);
            }
            catch (OverflowException)
            {
                failures.Add($"Tensor '{t.Name}' offset/size overflows 64-bit bounds.");
                continue;
            }

            if (end > (ulong)fileSize)
            {
                failures.Add($"Tensor '{t.Name}' exceeds file boundary ({end:N0} > {fileSize:N0}).");
            }
            else
            {
                validBoundsTensors.Add(t.Name);
            }

            if (i > 0 && start < prevEnd)
            {
                failures.Add($"Payload overlap between '{prevName}' (end {prevEnd:N0}) and '{t.Name}' (start {start:N0}).");
            }

            if (end > maxPayloadEnd)
                maxPayloadEnd = end;

            prevEnd = Math.Max(prevEnd, end);
            prevName = t.Name;
        }

        var tensorLookup = tensors.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var probeList = SelectProbeTensors(tensors, tensorLookup, options.ProbeTensors, failures);

        foreach (var probeTensor in probeList)
        {
            if (!validBoundsTensors.Contains(probeTensor.Name))
                continue;

            var probe = ProbeTensorValues(fs, tensorDataOffset, probeTensor);
            probes.Add(probe);

            if (probe.Supported)
            {
                if (probe.NanCount > 0)
                    failures.Add($"Probe tensor '{probe.Name}' contains {probe.NanCount} NaN value(s) in {probe.ElementsSampled} sampled elements.");
                if (probe.InfCount > 0)
                    failures.Add($"Probe tensor '{probe.Name}' contains {probe.InfCount} Inf value(s) in {probe.ElementsSampled} sampled elements.");
            }
        }

        return new GgufAuditResult(
            fullPath,
            fileSize,
            version,
            tensorCount,
            metadataKvCount,
            arch,
            modelName,
            alignment,
            headerEndOffset,
            tensorDataOffset,
            maxPayloadEnd,
            typeCounts,
            probes,
            failures);
    }

    public static int ExecuteAudit(string modelPath, GgufAuditOptions options, TextWriter? output = null, TextWriter? error = null)
    {
        TextWriter outWriter = output ?? Console.Out;
        TextWriter errWriter = error ?? Console.Error;

        if (!File.Exists(modelPath))
        {
            errWriter.WriteLine($"[ERROR] GGUF file not found: {modelPath}");
            return 2;
        }

        var result = AuditFile(modelPath, options);
        if (options.JsonOutput)
        {
            outWriter.WriteLine(FormatJson(result));
            return result.Passed ? 0 : 1;
        }

        outWriter.WriteLine("===========================================================================");
        outWriter.WriteLine("GLACIER GGUF DEEP AUDIT REPORT");
        outWriter.WriteLine($"Target File: {result.FilePath}");
        outWriter.WriteLine("===========================================================================");
        outWriter.WriteLine($"  File Size:         {result.FileSizeBytes:N0} bytes ({result.FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F4} GiB)");
        outWriter.WriteLine($"  GGUF Version:      v{result.Version}");
        outWriter.WriteLine($"  Architecture:      {result.Architecture}");
        if (!string.IsNullOrEmpty(result.ModelName))
            outWriter.WriteLine($"  Model Name:        {result.ModelName}");
        outWriter.WriteLine($"  Alignment:         {result.Alignment} bytes");
        outWriter.WriteLine($"  Header / Data Off: {result.HeaderEndOffset:N0} / {result.TensorDataOffset:N0} bytes");
        outWriter.WriteLine($"  Tensors / KVs:     {result.TensorCount:N0} / {result.MetadataKvCount:N0}");
        outWriter.WriteLine($"  Max Payload End:   {result.MaxPayloadEndOffset:N0} bytes");

        outWriter.WriteLine();
        outWriter.WriteLine("--- Tensor Type Distribution ---");
        foreach (var kv in result.TypeCounts)
        {
            outWriter.WriteLine($"  {kv.Key,-14}: {kv.Value,5} tensors");
        }

        if (result.Probes.Count > 0)
        {
            outWriter.WriteLine();
            outWriter.WriteLine("--- Physical Dequantization Spot-Check ---");
            foreach (var p in result.Probes)
            {
                if (!p.Supported)
                {
                    outWriter.WriteLine($"  {p.Name,-34} [{p.Type,-6}] (dequant spot-check skipped)");
                }
                else
                {
                    string status = (p.NanCount == 0 && p.InfCount == 0) ? "PASS" : "FAIL";
                    outWriter.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {p.Name,-34} [{p.Type,-6}] | N={p.ElementsSampled,3} | Mean: {p.Mean:+0.00000;-0.00000;+0.00000} | Std: {p.StdDev:0.00000} | Min: {p.Min:+0.00000;-0.00000;+0.00000} | Max: {p.Max:+0.00000;-0.00000;+0.00000} [{status}]"));
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

    public static string FormatJson(GgufAuditResult result)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("passed", result.Passed);
            writer.WriteString("filePath", result.FilePath);
            writer.WriteNumber("fileSizeBytes", result.FileSizeBytes);
            writer.WriteNumber("version", result.Version);
            writer.WriteNumber("tensorCount", result.TensorCount);
            writer.WriteNumber("metadataKvCount", result.MetadataKvCount);
            writer.WriteString("architecture", result.Architecture);
            writer.WriteString("modelName", result.ModelName);
            writer.WriteNumber("alignment", result.Alignment);
            writer.WriteNumber("headerEndOffset", result.HeaderEndOffset);
            writer.WriteNumber("tensorDataOffset", result.TensorDataOffset);
            writer.WriteNumber("maxPayloadEndOffset", result.MaxPayloadEndOffset);

            writer.WriteStartObject("typeCounts");
            foreach (var kv in result.TypeCounts)
                writer.WriteNumber(kv.Key, kv.Value);
            writer.WriteEndObject();

            writer.WriteStartArray("probes");
            foreach (var p in result.Probes)
            {
                writer.WriteStartObject();
                writer.WriteString("name", p.Name);
                writer.WriteString("type", p.Type.ToString());
                writer.WriteBoolean("supported", p.Supported);
                writer.WriteNumber("elementsSampled", p.ElementsSampled);
                writer.WriteNumber("nanCount", p.NanCount);
                writer.WriteNumber("infCount", p.InfCount);
                writer.WriteNumber("mean", p.Mean);
                writer.WriteNumber("stdDev", p.StdDev);
                writer.WriteNumber("min", p.Min);
                writer.WriteNumber("max", p.Max);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("failures");
            foreach (string f in result.Failures)
                writer.WriteStringValue(f);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<GgufTensorAuditEntry> SelectProbeTensors(
        List<GgufTensorAuditEntry> tensors,
        Dictionary<string, GgufTensorAuditEntry> lookup,
        IReadOnlyList<string>? requestedProbes,
        List<string> failures)
    {
        var selected = new List<GgufTensorAuditEntry>();
        if (requestedProbes != null && requestedProbes.Count > 0)
        {
            foreach (string name in requestedProbes)
            {
                if (lookup.TryGetValue(name, out var t))
                    selected.Add(t);
                else
                    failures.Add($"Requested probe tensor '{name}' was not found in GGUF.");
            }
            return selected;
        }

        var blkTensors = tensors.Where(t => t.Name.StartsWith("blk.", StringComparison.Ordinal)).ToList();
        var pool = blkTensors.Count > 0 ? blkTensors : tensors;
        if (pool.Count <= DefaultMaxProbes)
        {
            selected.AddRange(pool);
            return selected;
        }

        for (int i = 0; i < DefaultMaxProbes; i++)
        {
            int idx = (int)((long)i * (pool.Count - 1) / (DefaultMaxProbes - 1));
            selected.Add(pool[idx]);
        }
        return selected;
    }

    private static GgufProbeResult ProbeTensorValues(FileStream fs, ulong tensorDataOffset, GgufTensorAuditEntry tensor)
    {
        ulong elementCount = 1;
        for (int i = 0; i < tensor.Dimensions.Length; i++)
            elementCount *= tensor.Dimensions[i];

        long absOffset = checked((long)(tensorDataOffset + tensor.Offset));
        fs.Position = absOffset;

        Span<float> vals = stackalloc float[256];
        int count = 0;

        switch (tensor.Type)
        {
            case GgufType.F32:
            {
                int n = (int)Math.Min(elementCount, 256);
                Span<byte> buf = stackalloc byte[256 * 4];
                int needed = n * 4;
                if (ReadExact(fs, buf[..needed]))
                {
                    for (int i = 0; i < n; i++)
                        vals[i] = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(i * 4, 4));
                    count = n;
                }
                break;
            }
            case GgufType.F16:
            {
                int n = (int)Math.Min(elementCount, 256);
                Span<byte> buf = stackalloc byte[256 * 2];
                int needed = n * 2;
                if (ReadExact(fs, buf[..needed]))
                {
                    for (int i = 0; i < n; i++)
                        vals[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(buf.Slice(i * 2, 2));
                    count = n;
                }
                break;
            }
            case GgufType.BF16:
            {
                int n = (int)Math.Min(elementCount, 256);
                Span<byte> buf = stackalloc byte[256 * 2];
                int needed = n * 2;
                if (ReadExact(fs, buf[..needed]))
                {
                    for (int i = 0; i < n; i++)
                    {
                        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(i * 2, 2));
                        vals[i] = BitConverter.Int32BitsToSingle(bits << 16);
                    }
                    count = n;
                }
                break;
            }
            case GgufType.Q4_0 when tensor.ByteSize >= 18:
            {
                Span<byte> block = stackalloc byte[18];
                if (ReadExact(fs, block))
                {
                    float d = (float)BinaryPrimitives.ReadHalfLittleEndian(block[..2]);
                    for (int i = 0; i < 16; i++)
                    {
                        byte b = block[2 + i];
                        vals[i] = ((b & 0x0F) - 8) * d;
                        vals[i + 16] = ((b >> 4) - 8) * d;
                    }
                    count = 32;
                }
                break;
            }
            case GgufType.Q8_0 when tensor.ByteSize >= 34:
            {
                Span<byte> block = stackalloc byte[34];
                if (ReadExact(fs, block))
                {
                    float d = (float)BinaryPrimitives.ReadHalfLittleEndian(block[..2]);
                    for (int i = 0; i < 32; i++)
                        vals[i] = unchecked((sbyte)block[2 + i]) * d;
                    count = 32;
                }
                break;
            }
            case GgufType.Q4_K when tensor.ByteSize >= 144:
            {
                Span<byte> block = stackalloc byte[144];
                if (ReadExact(fs, block))
                {
                    DequantizeQ4KBlock(block, vals);
                    count = 256;
                }
                break;
            }
            default:
                return new GgufProbeResult(tensor.Name, tensor.Type, 0, 0, 0, 0, 0, 0, 0, Supported: false);
        }

        if (count == 0)
            return new GgufProbeResult(tensor.Name, tensor.Type, 0, 0, 0, 0, 0, 0, 0, Supported: false);

        int nans = 0;
        int infs = 0;
        double sum = 0.0;
        double sumSq = 0.0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        int finiteCount = 0;

        for (int i = 0; i < count; i++)
        {
            float v = vals[i];
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
            finiteCount++;
            sum += v;
            sumSq += (double)v * v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double mean = finiteCount > 0 ? sum / finiteCount : double.NaN;
        double var = finiteCount > 0 ? Math.Max(0.0, (sumSq / finiteCount) - mean * mean) : double.NaN;
        double std = finiteCount > 0 ? Math.Sqrt(var) : double.NaN;
        if (finiteCount == 0)
        {
            min = double.NaN;
            max = double.NaN;
        }

        return new GgufProbeResult(tensor.Name, tensor.Type, count, nans, infs, mean, std, min, max, Supported: true);
    }

    private static void DequantizeQ4KBlock(ReadOnlySpan<byte> block, Span<float> dst)
    {
        float d = (float)BinaryPrimitives.ReadHalfLittleEndian(block[..2]);
        float dmin = (float)BinaryPrimitives.ReadHalfLittleEndian(block.Slice(2, 2));
        ReadOnlySpan<byte> scales = block.Slice(4, 12);
        ReadOnlySpan<byte> qs = block.Slice(16, 128);

        int isIdx = 0;
        int qOffset = 0;
        int outIdx = 0;

        for (int j = 0; j < 256; j += 64)
        {
            GetScaleMinK4(isIdx + 0, scales, out int sc0, out int m0);
            float d1 = d * sc0;
            float min1 = dmin * m0;

            GetScaleMinK4(isIdx + 1, scales, out int sc1, out int m1);
            float d2 = d * sc1;
            float min2 = dmin * m1;

            for (int l = 0; l < 32; l++)
                dst[outIdx++] = d1 * (qs[qOffset + l] & 0x0F) - min1;
            for (int l = 0; l < 32; l++)
                dst[outIdx++] = d2 * (qs[qOffset + l] >> 4) - min2;

            qOffset += 32;
            isIdx += 2;
        }
    }

    private static void GetScaleMinK4(int j, ReadOnlySpan<byte> q, out int d, out int m)
    {
        if (j < 4)
        {
            d = q[j] & 63;
            m = q[j + 4] & 63;
        }
        else
        {
            d = (q[j + 4] & 0x0F) | ((q[j - 4] >> 6) << 4);
            m = (q[j + 4] >> 4) | ((q[j] >> 6) << 4);
        }
    }

    private static bool TryExtractUInt32(object? val, out uint parsed)
    {
        parsed = 0;
        switch (val)
        {
            case uint u32: parsed = u32; return true;
            case int i32 when i32 >= 0: parsed = (uint)i32; return true;
            case ulong u64 when u64 <= uint.MaxValue: parsed = (uint)u64; return true;
            case long i64 when i64 >= 0 && i64 <= uint.MaxValue: parsed = (uint)i64; return true;
            default: return false;
        }
    }

    private static object? ReadOrSkipGgufValue(FileStream fs, GgufValueType vType, long fileSize, bool retainScalar, int depth)
    {
        if (depth > 8)
            throw new InvalidDataException("GGUF metadata array nesting depth exceeds limit.");

        switch (vType)
        {
            case GgufValueType.Uint8:
            {
                int b = fs.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                return retainScalar ? (byte)b : null;
            }
            case GgufValueType.Int8:
            {
                int b = fs.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                return retainScalar ? unchecked((sbyte)(byte)b) : null;
            }
            case GgufValueType.Uint16:
            {
                Span<byte> b = stackalloc byte[2];
                if (!ReadExact(fs, b)) throw new EndOfStreamException();
                return retainScalar ? BinaryPrimitives.ReadUInt16LittleEndian(b) : null;
            }
            case GgufValueType.Int16:
            {
                Span<byte> b = stackalloc byte[2];
                if (!ReadExact(fs, b)) throw new EndOfStreamException();
                return retainScalar ? BinaryPrimitives.ReadInt16LittleEndian(b) : null;
            }
            case GgufValueType.Uint32:
            {
                if (!TryReadUInt32(fs, out uint v)) throw new EndOfStreamException();
                return retainScalar ? v : null;
            }
            case GgufValueType.Int32:
            {
                Span<byte> b = stackalloc byte[4];
                if (!ReadExact(fs, b)) throw new EndOfStreamException();
                return retainScalar ? BinaryPrimitives.ReadInt32LittleEndian(b) : null;
            }
            case GgufValueType.Float32:
            {
                Span<byte> b = stackalloc byte[4];
                if (!ReadExact(fs, b)) throw new EndOfStreamException();
                return retainScalar ? BinaryPrimitives.ReadSingleLittleEndian(b) : null;
            }
            case GgufValueType.Bool:
            {
                int b = fs.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                return retainScalar ? b != 0 : null;
            }
            case GgufValueType.String:
            {
                string s = ReadGgufString(fs, fileSize);
                return retainScalar ? s : null;
            }
            case GgufValueType.Uint64:
            {
                if (!TryReadUInt64(fs, out ulong v)) throw new EndOfStreamException();
                return retainScalar ? v : null;
            }
            case GgufValueType.Int64:
            {
                Span<byte> b = stackalloc byte[8];
                if (!ReadExact(fs, b)) throw new EndOfStreamException();
                return retainScalar ? BinaryPrimitives.ReadInt64LittleEndian(b) : null;
            }
            case GgufValueType.Float64:
            {
                Span<byte> b = stackalloc byte[8];
                if (!ReadExact(fs, b)) throw new EndOfStreamException();
                return retainScalar ? BinaryPrimitives.ReadDoubleLittleEndian(b) : null;
            }
            case GgufValueType.Array:
            {
                if (!TryReadUInt32(fs, out uint elemTypeRaw) || !TryReadUInt64(fs, out ulong count))
                    throw new EndOfStreamException("Unexpected EOF reading GGUF array header.");

                var elemType = (GgufValueType)elemTypeRaw;
                if (TryGetFixedElementWidth(elemType, out int width))
                {
                    ulong remaining = (ulong)Math.Max(0L, fileSize - fs.Position);
                    if (count > remaining / (ulong)width)
                        throw new InvalidDataException($"GGUF metadata array length {count} exceeds file bounds.");
                    fs.Seek((long)(count * (ulong)width), SeekOrigin.Current);
                }
                else
                {
                    ulong remaining = (ulong)Math.Max(0L, fileSize - fs.Position);
                    if (count > remaining)
                        throw new InvalidDataException($"GGUF metadata array length {count} exceeds remaining file bytes.");
                    for (ulong i = 0; i < count; i++)
                        ReadOrSkipGgufValue(fs, elemType, fileSize, retainScalar: false, depth + 1);
                }
                return null;
            }
            default:
                throw new InvalidDataException($"Unknown GGUF metadata value type: {(uint)vType}");
        }
    }

    private static bool TryGetFixedElementWidth(GgufValueType vType, out int bytes)
    {
        switch (vType)
        {
            case GgufValueType.Uint8:
            case GgufValueType.Int8:
            case GgufValueType.Bool:
                bytes = 1;
                return true;
            case GgufValueType.Uint16:
            case GgufValueType.Int16:
                bytes = 2;
                return true;
            case GgufValueType.Uint32:
            case GgufValueType.Int32:
            case GgufValueType.Float32:
                bytes = 4;
                return true;
            case GgufValueType.Uint64:
            case GgufValueType.Int64:
            case GgufValueType.Float64:
                bytes = 8;
                return true;
            default:
                bytes = 0;
                return false;
        }
    }

    private static string ReadGgufString(FileStream fs, long fileSize)
    {
        if (!TryReadUInt64(fs, out ulong len))
            throw new EndOfStreamException("Unexpected EOF reading GGUF string length.");

        ulong remaining = (ulong)Math.Max(0L, fileSize - fs.Position);
        if (len > remaining || len > 16 * 1024 * 1024)
            throw new InvalidDataException($"Implausible GGUF string length {len}.");

        int ilen = (int)len;
        if (ilen == 0)
            return string.Empty;

        byte[] buf = new byte[ilen];
        if (!ReadExact(fs, buf))
            throw new EndOfStreamException("Unexpected EOF reading GGUF string payload.");

        return Encoding.UTF8.GetString(buf);
    }

    private static bool TryReadUInt32(FileStream fs, out uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        if (!ReadExact(fs, buf))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        return true;
    }

    private static bool TryReadUInt64(FileStream fs, out ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        if (!ReadExact(fs, buf))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt64LittleEndian(buf);
        return true;
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
