namespace Glacier.Cli.Tests;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Glacier.Cli.Commands;
using Glacier.Inference.Gguf;
using Xunit;

public sealed class ModelAuditCommandTests
{
    [Fact]
    public void GgufAudit_ValidSyntheticGguf_PassesAlignmentOverlapAndQ4KDequant()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"glacier_valid_{Guid.NewGuid():N}.gguf");
        try
        {
            WriteSyntheticGguf(tempPath, alignment: 32, misalignSecondTensor: false, overlapSecondTensor: false, includeQ8Tensor: false, injectNaNInF32: false);

            var options = new GgufAuditOptions(ForbiddenTypes: ["Q8_0"], MaxBytes: 1_000_000, ProbeTensors: ["blk.0.attn_q.weight", "blk.0.attn_norm.weight"], JsonOutput: true);
            var result = GgufAuditCommandHandler.AuditFile(tempPath, options);

            Assert.True(result.Passed, string.Join("; ", result.Failures));
            Assert.Equal("qwen2", result.Architecture);
            Assert.Equal(32u, result.Alignment);
            Assert.Equal(1, result.TypeCounts["Q4_K"]);
            Assert.Equal(1, result.TypeCounts["F32"]);
            Assert.Equal(2, result.Probes.Count);
            Assert.All(result.Probes, p =>
            {
                Assert.True(p.Supported);
                Assert.Equal(0, p.NanCount);
                Assert.Equal(0, p.InfCount);
            });

            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            int exitCode = GgufAuditCommandHandler.ExecuteAudit(tempPath, options, outWriter, errWriter);
            Assert.Equal(0, exitCode);

            using var doc = JsonDocument.Parse(outWriter.ToString());
            Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal("qwen2", doc.RootElement.GetProperty("architecture").GetString());
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void GgufAudit_DetectsMisalignedOverlapForbiddenTypeAndNaN()
    {
        string misalignedPath = Path.Combine(Path.GetTempPath(), $"glacier_misalign_{Guid.NewGuid():N}.gguf");
        string overlapPath = Path.Combine(Path.GetTempPath(), $"glacier_overlap_{Guid.NewGuid():N}.gguf");
        string forbiddenPath = Path.Combine(Path.GetTempPath(), $"glacier_q8_{Guid.NewGuid():N}.gguf");
        string nanPath = Path.Combine(Path.GetTempPath(), $"glacier_nan_{Guid.NewGuid():N}.gguf");
        try
        {
            WriteSyntheticGguf(misalignedPath, alignment: 32, misalignSecondTensor: true, overlapSecondTensor: false, includeQ8Tensor: false, injectNaNInF32: false);
            WriteSyntheticGguf(overlapPath, alignment: 32, misalignSecondTensor: false, overlapSecondTensor: true, includeQ8Tensor: false, injectNaNInF32: false);
            WriteSyntheticGguf(forbiddenPath, alignment: 32, misalignSecondTensor: false, overlapSecondTensor: false, includeQ8Tensor: true, injectNaNInF32: false);
            WriteSyntheticGguf(nanPath, alignment: 32, misalignSecondTensor: false, overlapSecondTensor: false, includeQ8Tensor: false, injectNaNInF32: true);

            var rMisalign = GgufAuditCommandHandler.AuditFile(misalignedPath);
            Assert.False(rMisalign.Passed);
            Assert.Contains(rMisalign.Failures, f => f.Contains("aligned", StringComparison.OrdinalIgnoreCase));

            var rOverlap = GgufAuditCommandHandler.AuditFile(overlapPath);
            Assert.False(rOverlap.Passed);
            Assert.Contains(rOverlap.Failures, f => f.Contains("overlap", StringComparison.OrdinalIgnoreCase));

            var rForbidden = GgufAuditCommandHandler.AuditFile(forbiddenPath, new GgufAuditOptions(["Q8_0"]));
            Assert.False(rForbidden.Passed);
            Assert.Contains(rForbidden.Failures, f => f.Contains("Q8_0", StringComparison.OrdinalIgnoreCase));

            var rNan = GgufAuditCommandHandler.AuditFile(nanPath);
            Assert.False(rNan.Passed);
            Assert.Contains(rNan.Failures, f => f.Contains("NaN", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (string p in new[] { misalignedPath, overlapPath, forbiddenPath, nanPath })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
    }

    [Fact]
    public void AdapterAudit_ValidLoraBinary_PassesAndComputesNorms()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"glacier_adapter_ok_{Guid.NewGuid():N}.bin");
        try
        {
            WriteSyntheticLoraAdapter(tempPath, layers: 2, rank: 4, alpha: 8.0f, zeroLoraB: false, injectNaN: false, appendTrailingBytes: false);

            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            int exitCode = AdapterAuditCommandHandler.Execute(
                [tempPath, "--expect-layers", "2", "--expect-rank", "4", "--expect-alpha", "8.0", "--expect-tensors", "4", "--json"],
                outWriter,
                errWriter);

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(outWriter.ToString());
            Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("exactEof").GetBoolean());
            Assert.Equal(0L, doc.RootElement.GetProperty("totalNans").GetInt64());
            Assert.True(doc.RootElement.GetProperty("minNormA").GetDouble() > 0.0);
            Assert.True(doc.RootElement.GetProperty("minNormB").GetDouble() > 0.0);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void AdapterAudit_DetectsZeroLoraB_NaN_AndTrailingBytes()
    {
        string zeroBPath = Path.Combine(Path.GetTempPath(), $"glacier_adapter_zerob_{Guid.NewGuid():N}.bin");
        string nanPath = Path.Combine(Path.GetTempPath(), $"glacier_adapter_nan_{Guid.NewGuid():N}.bin");
        string trailingPath = Path.Combine(Path.GetTempPath(), $"glacier_adapter_trail_{Guid.NewGuid():N}.bin");
        try
        {
            WriteSyntheticLoraAdapter(zeroBPath, layers: 1, rank: 4, alpha: 8.0f, zeroLoraB: true, injectNaN: false, appendTrailingBytes: false);
            WriteSyntheticLoraAdapter(nanPath, layers: 1, rank: 4, alpha: 8.0f, zeroLoraB: false, injectNaN: true, appendTrailingBytes: false);
            WriteSyntheticLoraAdapter(trailingPath, layers: 1, rank: 4, alpha: 8.0f, zeroLoraB: false, injectNaN: false, appendTrailingBytes: true);

            var rZeroB = AdapterAuditCommandHandler.AuditFile(zeroBPath);
            Assert.False(rZeroB.Passed);
            Assert.Contains(rZeroB.Failures, f => f.Contains("LoRA B", StringComparison.OrdinalIgnoreCase));

            var rNan = AdapterAuditCommandHandler.AuditFile(nanPath);
            Assert.False(rNan.Passed);
            Assert.Contains(rNan.Failures, f => f.Contains("NaN", StringComparison.OrdinalIgnoreCase));

            var rTrailing = AdapterAuditCommandHandler.AuditFile(trailingPath);
            Assert.False(rTrailing.Passed);
            Assert.False(rTrailing.ExactEof);
            Assert.Contains(rTrailing.Failures, f => f.Contains("trailing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (string p in new[] { zeroBPath, nanPath, trailingPath })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
    }

    private static void WriteSyntheticGguf(
        string path,
        uint alignment,
        bool misalignSecondTensor,
        bool overlapSecondTensor,
        bool includeQ8Tensor,
        bool injectNaNInF32)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Header: GGUF v3, 2 tensors, 2 metadata entries
        bw.Write(0x46554747u);
        bw.Write(3u);
        bw.Write(2UL);
        bw.Write(2UL);

        // KV 1: general.architecture = "qwen2"
        WriteGgufString(bw, "general.architecture");
        bw.Write((uint)GgufValueType.String);
        WriteGgufString(bw, "qwen2");

        // KV 2: general.alignment = alignment
        WriteGgufString(bw, "general.alignment");
        bw.Write((uint)GgufValueType.Uint32);
        bw.Write(alignment);

        // Tensor 0: blk.0.attn_q.weight [256] Q4_K (144 bytes) at offset 0
        ulong secondOffset = misalignSecondTensor ? 160UL : (overlapSecondTensor ? 64UL : 160UL + 0UL);
        if (!misalignSecondTensor && !overlapSecondTensor)
        {
            // Align 144 up to alignment (32 -> 160)
            secondOffset = ((144UL + alignment - 1) / alignment) * alignment;
        }
        else if (misalignSecondTensor)
        {
            secondOffset = 148UL; // valid >= 144 but not 32-byte aligned
        }

        WriteTensorDesc(bw, "blk.0.attn_q.weight", [256UL], GgufType.Q4_K, 0UL);
        WriteTensorDesc(bw, "blk.0.attn_norm.weight", [32UL], includeQ8Tensor ? GgufType.Q8_0 : GgufType.F32, secondOffset);

        bw.Flush();
        long headerEnd = ms.Position;
        long pad = (alignment - (headerEnd % alignment)) % alignment;
        for (long i = 0; i < pad; i++)
            bw.Write((byte)0);

        long dataStart = ms.Position;
        // Write Q4_K block (144 bytes): d=0.5 (f16), dmin=0.1 (f16), scales, qs
        byte[] q4kBlock = new byte[144];
        BinaryPrimitives.WriteHalfLittleEndian(q4kBlock.AsSpan(0, 2), (Half)0.5f);
        BinaryPrimitives.WriteHalfLittleEndian(q4kBlock.AsSpan(2, 2), (Half)0.1f);
        for (int i = 4; i < 16; i++) q4kBlock[i] = 1;
        for (int i = 16; i < 144; i++) q4kBlock[i] = (byte)(i & 0xFF);
        bw.Write(q4kBlock);

        long targetSecondAbs = dataStart + (long)secondOffset;
        while (ms.Position < targetSecondAbs)
            bw.Write((byte)0);

        if (ms.Position > targetSecondAbs)
        {
            // Ensure file extends past end of overlapping/misaligned tensor descriptor
            ms.Position = targetSecondAbs;
        }

        if (includeQ8Tensor)
        {
            byte[] q8Block = new byte[34];
            BinaryPrimitives.WriteHalfLittleEndian(q8Block.AsSpan(0, 2), (Half)0.25f);
            for (int i = 2; i < 34; i++) q8Block[i] = (byte)i;
            bw.Write(q8Block);
        }
        else
        {
            for (int i = 0; i < 32; i++)
            {
                float val = (injectNaNInF32 && i == 7) ? float.NaN : (i - 16) * 0.05f;
                bw.Write(val);
            }
        }

        bw.Flush();
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteGgufString(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((ulong)bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteTensorDesc(BinaryWriter bw, string name, ulong[] dims, GgufType type, ulong offset)
    {
        WriteGgufString(bw, name);
        bw.Write((uint)dims.Length);
        foreach (ulong d in dims) bw.Write(d);
        bw.Write((uint)type);
        bw.Write(offset);
    }

    private static void WriteSyntheticLoraAdapter(
        string path,
        int layers,
        int rank,
        float alpha,
        bool zeroLoraB,
        bool injectNaN,
        bool appendTrailingBytes)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);

        int tensorCount = layers * 2;
        bw.Write(0x41524F4Cu); // 'LORA'
        bw.Write(1u);
        bw.Write(layers);
        bw.Write(rank);
        bw.Write(alpha);
        bw.Write(tensorCount);

        const int hidden = 8;
        for (int l = 0; l < layers; l++)
        {
            // lora_a [rank x hidden]
            bw.Write($"blk.{l}.attn_q.lora_a.weight");
            bw.Write(rank);
            bw.Write(hidden);
            for (int i = 0; i < rank * hidden; i++)
                bw.Write(((i + 1) % 7 - 3) * 0.1f + 0.05f);

            // lora_b [hidden x rank]
            bw.Write($"blk.{l}.attn_q.lora_b.weight");
            bw.Write(hidden);
            bw.Write(rank);
            for (int i = 0; i < hidden * rank; i++)
            {
                if (zeroLoraB)
                    bw.Write(0.0f);
                else if (injectNaN && l == 0 && i == 3)
                    bw.Write(float.NaN);
                else
                    bw.Write(((i + 2) % 5 - 2) * 0.12f + 0.03f);
            }
        }

        if (appendTrailingBytes)
        {
            bw.Write(0xDEADBEEFu);
        }
    }
}
