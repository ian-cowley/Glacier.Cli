namespace Glacier.Cli.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Sandbox;
using Glacier.Cli;
using Glacier.Cli.Commands;
using Glacier.Inference.Engine;
using Glacier.Inference.Gguf;
using Glacier.Inference.Hardware;
using Glacier.Inference.Memory;
using Glacier.Inference.Sampling;
using Glacier.MemEx;
using Glacier.Serve.Daemons;
using Glacier.Serve.Inference.Batching;
using Glacier.Serve.Inference.Bounce;
using Glacier.Serve.Serialization;
using Glacier.Serve.Server;
using Xunit;

[Collection("ConsoleTests")]
public sealed class M5bCliWiringTests : IDisposable
{
    private readonly string _tempDir;

    public M5bCliWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GlacierCliM5b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void PackageAlignment_ResolvesExpectedVersions()
    {
        Assert.Equal(new Version(1, 8, 4), typeof(SleepMemoryService).Assembly.GetName().Version?.ToString(3) is string sv ? Version.Parse(sv) : null);
        Assert.Equal(new Version(1, 5, 2), typeof(OsProcessSandbox).Assembly.GetName().Version?.ToString(3) is string av ? Version.Parse(av) : null);
        Assert.Equal(new Version(1, 2, 2), typeof(MemExEngine).Assembly.GetName().Version?.ToString(3) is string mv ? Version.Parse(mv) : null);
        Assert.Equal(new Version(1, 8, 2), typeof(InferenceSession).Assembly.GetName().Version?.ToString(3) is string iv ? Version.Parse(iv) : null);
    }

    [Fact]
    public void CliOptionHelpers_ParseLoraSpec_HandlesNamedUnnamedDirectoryAndRejectsInvalid()
    {
        var named = CliOptionHelpers.ParseLoraSpec("roslyn=C:/adapters/roslyn.bin");
        Assert.Equal("roslyn", named.Name);
        Assert.Equal("C:/adapters/roslyn.bin", named.Path);

        var unnamed = CliOptionHelpers.ParseLoraSpec("C:/adapters/my_adapter.bin");
        Assert.Equal("my_adapter", unnamed.Name);
        Assert.Equal("C:/adapters/my_adapter.bin", unnamed.Path);

        string dirPath = Path.Combine(_tempDir, "dir_adapter");
        Directory.CreateDirectory(dirPath);
        var dirOpt = CliOptionHelpers.ParseLoraSpec(dirPath);
        Assert.Equal("dir_adapter", dirOpt.Name);
        Assert.Equal(Path.Combine(dirPath, "adapter_model.bin"), CliOptionHelpers.ResolveLoraFilePath(dirOpt.Path));

        Assert.Throws<ArgumentException>(() => CliOptionHelpers.ParseLoraSpec("=missing_name.bin"));
        Assert.Throws<ArgumentException>(() => CliOptionHelpers.ParseLoraSpec("bad:name=file.bin"));
        Assert.Throws<ArgumentException>(() => CliOptionHelpers.ParseLoraSpec("  "));
    }

    [Theory]
    [InlineData("auto", KvCachePrecision.Auto)]
    [InlineData("fp32", KvCachePrecision.Fp32)]
    [InlineData("fp16", KvCachePrecision.Fp16)]
    [InlineData("fp8", KvCachePrecision.Fp8)]
    [InlineData("q8_0", KvCachePrecision.Q8_0)]
    [InlineData("q4_0", KvCachePrecision.Q4_0)]
    public void ServeAndRun_ParseOptions_AcceptAllKvPrecisionModes(string rawPrecision, KvCachePrecision expected)
    {
        var serveOpts = ServeCommandHandler.ParseOptions(["model.gguf", "--kv-precision", rawPrecision]);
        Assert.Equal(expected, serveOpts.KvPrecision);

        var runOpts = RunCommandHandler.ParseOptions(["model.gguf", "--kv-precision=" + rawPrecision]);
        Assert.Equal(expected, runOpts.KvPrecision);
    }

    [Fact]
    public void ServeAndRun_ParseOptions_RejectInvalidOrMissingKvPrecisionAndLora()
    {
        var exServe = Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--kv-precision", "bad"]));
        Assert.Contains("q8_0", exServe.Message, StringComparison.OrdinalIgnoreCase);

        var exRun = Assert.Throws<ArgumentException>(() => RunCommandHandler.ParseOptions(["model.gguf", "--kv-precision", "bad"]));
        Assert.Contains("q8_0", exRun.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fp8", exRun.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--kv-precision"]));
        Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--lora"]));
        Assert.Throws<ArgumentException>(() => RunCommandHandler.ParseOptions(["model.gguf", "--kv-precision"]));
        Assert.Throws<ArgumentException>(() => RunCommandHandler.ParseOptions(["model.gguf", "--lora"]));
    }

    [Fact]
    public void ServeCommandHandler_ParseOptions_SupportsMultiLoraSleepAndPeerBounceFlags()
    {
        var opts = ServeCommandHandler.ParseOptions([
            "model.gguf",
            "--kv-precision", "q8_0",
            "--lora", "coder=adapters/coder.bin",
            "--lora=adapters/math.bin",
            "--sleep-budget", "512",
            "--sleep-checkpoint", "ckpt.gmoe",
            "--no-bounce",
            "--bounce-primary", "http://127.0.0.1:18080",
            "--critic", "http://127.0.0.1:18081",
            "--bounce-harness", "harness.exe",
            "--bounce-max-repairs", "3",
            "--no-compiler-bounce",
            "--max-think-tokens", "256"
        ]);

        Assert.Equal(KvCachePrecision.Q8_0, opts.KvPrecision);
        Assert.NotNull(opts.LoraAdapters);
        Assert.Equal(2, opts.LoraAdapters!.Count);
        Assert.Equal("coder", opts.LoraAdapters[0].Name);
        Assert.Equal("math", opts.LoraAdapters[1].Name);
        Assert.Equal(512, opts.SleepBudget);
        Assert.Equal("ckpt.gmoe", opts.SleepCheckpointPath);
        Assert.False(opts.EnablePeerBounce);
        Assert.Equal("http://127.0.0.1:18080", opts.BouncePrimaryEndpoint);
        Assert.Equal("http://127.0.0.1:18081", opts.BounceCriticEndpoint);
        Assert.Equal("harness.exe", opts.BounceHarnessPath);
        Assert.Equal(3, opts.BounceMaxRepairs);
        Assert.False(opts.CompilerBounceEnabled);
        Assert.Equal(256, opts.MaxThinkTokens);
    }

    [Fact]
    public async Task RunCommandHandler_RejectsMultipleLoraAndMissingLoraFile()
    {
        string loraA = Path.Combine(_tempDir, "a.bin");
        string loraB = Path.Combine(_tempDir, "b.bin");
        WriteMinimalLoraBinary(loraA);
        WriteMinimalLoraBinary(loraB);

        var err = new StringWriter();
        var origErr = Console.Error;
        Console.SetError(err);
        try
        {
            int multiCode = await RunCommandHandler.ExecuteAsync(["model.gguf", "--lora", loraA, "--lora", loraB]);
            Assert.Equal(1, multiCode);

            int missingCode = await RunCommandHandler.ExecuteAsync(["model.gguf", "--lora", Path.Combine(_tempDir, "nope.bin")]);
            Assert.Equal(1, missingCode);
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task BounceCommand_HelpAndOptions_WorkCleanly()
    {
        var bounceOpts = BounceCommandHandler.ParseOptions([
            "--primary", "http://127.0.0.1:11434",
            "--critic", "http://127.0.0.1:11435",
            "--port", "18099",
            "--max-repairs", "1",
            "--no-compiler-bounce",
            "--model-alias", "CustomBounce"
        ]);
        Assert.Equal(18099, bounceOpts.Port);
        Assert.Equal(1, bounceOpts.MaxRepairs);
        Assert.False(bounceOpts.CompilerBounceEnabled);
        Assert.Equal("CustomBounce", bounceOpts.ModelAlias);

        var outWriter = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(outWriter);
        try
        {
            int code = await Program.Main(["bounce", "--help"]);
            Assert.Equal(0, code);
            Assert.Contains("glacier bounce", outWriter.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task ServeCommandHandler_RegisterEndpoints_ExposesSleepMemoryPeerBounceMultiLoraAndKvQ8_0()
    {
        string ggufPath = Path.Combine(_tempDir, "tiny_qwen2.gguf");
        WriteMinimalQwen2Gguf(ggufPath);

        string loraCoder = Path.Combine(_tempDir, "coder.bin");
        string loraReviewer = Path.Combine(_tempDir, "reviewer.bin");
        WriteMinimalLoraBinary(loraCoder);
        WriteMinimalLoraBinary(loraReviewer);

        int port = GetAvailablePort();
        using var sleepMemory = new SleepMemoryService(tokenBudget: 400);
        using var engine = new ContinuousBatchEngine(
            ggufPath,
            totalKvBlocks: 32,
            maxBatchSize: 4,
            maxSeqLen: 64,
            useGpu: false,
            kvCachePrecision: KvCachePrecision.Q8_0);
        engine.LoadLoraAdapter("coder", loraCoder);
        engine.LoadLoraAdapter("reviewer", loraReviewer);
        engine.AttachSleepMemory(sleepMemory);

        using var bounceHttp = new HttpClient();
        var verifier = new SandboxedProcessBounceVerifier(harnessExePath: null);
        var bounceService = new PeerBounceService(bounceHttp, verifier, $"http://127.0.0.1:{port}");

        using var app = GlacierServeApp.CreateBuilder()
            .UseHost("127.0.0.1")
            .UsePort(port)
            .Build();
        ServeCommandHandler.RegisterEndpoints(app, engine, "tiny-qwen2", sleepMemory, bounceService);

        using var cts = new CancellationTokenSource();
        await app.StartAsync(cts.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            Assert.Equal(KvCachePrecision.Q8_0, engine.KvPrecision);

            // Base model first + LoRA adapters on /v1/models
            var models = await client.GetFromJsonAsync<JsonElement>("/v1/models");
            var data = models.GetProperty("data");
            Assert.Equal(3, data.GetArrayLength());
            Assert.Equal("tiny-qwen2", data[0].GetProperty("id").GetString());
            Assert.Equal("tiny-qwen2:coder", data[1].GetProperty("id").GetString());
            Assert.Equal("tiny-qwen2:reviewer", data[2].GetProperty("id").GetString());

            // /health still owned by MapInference (ContinuousBatchEngine)
            var health = await client.GetFromJsonAsync<JsonElement>("/health");
            Assert.Equal("ContinuousBatchEngine", health.GetProperty("engine").GetString());

            // /api/sleep/* (SleepMemoryService) and /v1/sleep/* (Glacier.Studio)
            var apiSleep = await client.GetFromJsonAsync<SleepMemoryStatusResponse>("/api/sleep/status");
            Assert.NotNull(apiSleep);
            Assert.True(apiSleep!.Enabled);

            var v1Sleep = await client.GetFromJsonAsync<SleepStatusResponse>("/v1/sleep/status");
            Assert.NotNull(v1Sleep);
            Assert.True(v1Sleep!.Enabled);

            sleepMemory.RecordTurnCompletion("remember token ALPHA_91", "stored ALPHA_91");
            var trigResp = await client.PostAsync("/api/sleep/trigger", content: null);
            Assert.Equal(HttpStatusCode.OK, trigResp.StatusCode);
            var trigStatus = await trigResp.Content.ReadFromJsonAsync<SleepMemoryStatusResponse>();
            Assert.True(trigStatus!.SleepCycles >= 1);

            // /api/bounce/status co-hosted cleanly
            var bounceStatus = await client.GetFromJsonAsync<BounceStatusResponse>("/api/bounce/status");
            Assert.NotNull(bounceStatus);
            Assert.Equal($"http://127.0.0.1:{port}", bounceStatus!.PrimaryEndpoint);
            Assert.True(bounceStatus.CompilerBounceEnabled);
        }
        finally
        {
            cts.Cancel();
        }
    }

    [Fact]
    public async Task Bench_ParseOptionsAndRunBenchAsync_SupportAndValidateKvPrecision()
    {
        var optsSpace = DiagnosticsCommandHandlers.ParseBenchOptions(["model.gguf", "--kv-precision", "q8_0", "-n", "4"]);
        Assert.Equal(KvCachePrecision.Q8_0, optsSpace.KvPrecision);
        Assert.Equal(4, optsSpace.Tokens);

        var optsEq = DiagnosticsCommandHandlers.ParseBenchOptions(["model.gguf", "--kv-precision=q4_0"]);
        Assert.Equal(KvCachePrecision.Q4_0, optsEq.KvPrecision);

        var invalidEx = Assert.Throws<ArgumentException>(() =>
            DiagnosticsCommandHandlers.ParseBenchOptions(["model.gguf", "--kv-precision", "bogus"]));
        Assert.Contains("q8_0", invalidEx.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentException>(() =>
            DiagnosticsCommandHandlers.ParseBenchOptions(["model.gguf", "--kv-precision"]));
        Assert.Throws<ArgumentException>(() =>
            DiagnosticsCommandHandlers.ParseBenchOptions(["model.gguf", "-n"]));

        string ggufPath = Path.Combine(_tempDir, "bench_kv.gguf");
        WriteMinimalQwen2Gguf(ggufPath);

        var errWriter = new StringWriter();
        var outWriter = new StringWriter();
        var origErr = Console.Error;
        var origOut = Console.Out;
        Console.SetError(errWriter);
        Console.SetOut(outWriter);
        try
        {
            int invalidCode = await DiagnosticsCommandHandlers.RunBenchAsync([ggufPath, "--kv-precision", "bogus"]);
            Assert.Equal(1, invalidCode);

            int missingCode = await DiagnosticsCommandHandlers.RunBenchAsync([ggufPath, "--kv-precision"]);
            Assert.Equal(1, missingCode);

            int okCode = await DiagnosticsCommandHandlers.RunBenchAsync([ggufPath, "-n", "1", "--kv-precision", "q8_0"]);
            Assert.Equal(0, okCode);

            outWriter.GetStringBuilder().Clear();
            DiagnosticsCommandHandlers.PrintBenchHelp();
            string help = outWriter.ToString();
            Assert.Contains("--kv-precision", help);
            Assert.Contains("q8_0", help);
        }
        finally
        {
            Console.SetError(origErr);
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task RunCommandHandler_DeviceAutoWithLora_DefaultsToCpu()
    {
        var (autoDev, autoEng) = RunCommandHandler.ResolveLoraTarget(hasLora: true, device: "auto", engine: InferenceEngineType.Auto);
        Assert.Equal("cpu", autoDev);
        Assert.Equal(InferenceEngineType.Cpu, autoEng);

        var (upperDev, upperEng) = RunCommandHandler.ResolveLoraTarget(hasLora: true, device: "AUTO", engine: InferenceEngineType.Auto);
        Assert.Equal("cpu", upperDev);
        Assert.Equal(InferenceEngineType.Cpu, upperEng);

        var (gpuDev, gpuEng) = RunCommandHandler.ResolveLoraTarget(hasLora: true, device: "nvidia-rtx-4060", engine: InferenceEngineType.Auto);
        Assert.Equal("nvidia-rtx-4060", gpuDev);
        Assert.Equal(InferenceEngineType.Auto, gpuEng);

        string ggufPath = Path.Combine(_tempDir, "run_lora_auto.gguf");
        WriteMinimalQwen2Gguf(ggufPath);
        string loraPath = Path.Combine(_tempDir, "roslyn.bin");
        WriteMinimalLoraBinary(loraPath);

        var outWriter = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(outWriter);
        try
        {
            int code = await RunCommandHandler.ExecuteAsync([ggufPath, "hi", "-n", "1", "--device", "auto", "--lora", $"roslyn={loraPath}"]);
            Assert.Equal(0, code);
            string output = outWriter.ToString();
            Assert.Contains("LoRA Adapter: roslyn", output);
            Assert.Contains("Rank=4", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void ServeCommandHandler_DefaultMaxThinkTokens_IsAutoBudgetSentinel_AndSupportsEqualsSyntax()
    {
        var defaultOpts = ServeCommandHandler.ParseOptions(["model.gguf"]);
        Assert.Equal(ReasoningBudgetPolicy.AutoBudgetSentinel, defaultOpts.MaxThinkTokens);
        Assert.Equal(-1, defaultOpts.MaxThinkTokens);

        var equalsOpts = ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens=12"]);
        Assert.Equal(12, equalsOpts.MaxThinkTokens);

        var offOpts = ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens=off"]);
        Assert.Equal(0, offOpts.MaxThinkTokens);

        Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens", "-2"]));
        Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens=-2"]));
    }

    [Fact]
    public void ServeCommandHandler_MaxThinkTokens_AcceptsMinusOneAsAuto_AndRejectsEmptyOrBelowMinusOne()
    {
        var spaceMinusOne = ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens", "-1"]);
        Assert.Equal(ReasoningBudgetPolicy.AutoBudgetSentinel, spaceMinusOne.MaxThinkTokens);

        var eqMinusOne = ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens=-1"]);
        Assert.Equal(ReasoningBudgetPolicy.AutoBudgetSentinel, eqMinusOne.MaxThinkTokens);

        var exEmpty = Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens="]));
        Assert.Contains("-1", exEmpty.Message, StringComparison.Ordinal);

        var exInvalid = Assert.Throws<ArgumentException>(() => ServeCommandHandler.ParseOptions(["model.gguf", "--max-think-tokens", "-2"]));
        Assert.Contains("-1", exInvalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServeCommandHandler_SleepMinDreams_WiresIntoSleepMemoryService()
    {
        var opts = ServeCommandHandler.ParseOptions([
            "model.gguf",
            "--sleep-budget", "256",
            "--sleep-idle-seconds", "90",
            "--sleep-min-dreams=4"
        ]);
        Assert.Equal(4, opts.SleepMinDreams);

        using var sleep = ServeCommandHandler.CreateSleepMemoryService(opts);
        Assert.Equal(256, sleep.TokenBudget);
        Assert.Equal(4, sleep.MinDreamsToTriggerSleep);
    }

    [Fact]
    public void BounceCommandHandler_ResolveDefaultHarnessPath_FindsCrossPlatformBinary()
    {
        string exeName = OperatingSystem.IsWindows() ? "Glacier.EvaluationHarness.exe" : "Glacier.EvaluationHarness";

        // 1. Direct sibling in baseDirectory
        string fakeBaseDir = Path.Combine(_tempDir, "base_dir");
        Directory.CreateDirectory(fakeBaseDir);
        string baseExe = Path.Combine(fakeBaseDir, exeName);
        File.WriteAllText(baseExe, string.Empty);

        string? resolvedFromBase = BounceCommandHandler.ResolveDefaultHarnessPath(_tempDir, fakeBaseDir);
        Assert.Equal(Path.GetFullPath(baseExe), resolvedFromBase);

        // 2. Direct sibling in currentDirectory when absent from baseDirectory
        string emptyBaseDir = Path.Combine(_tempDir, "empty_base");
        string fakeCwd = Path.Combine(_tempDir, "cwd_dir");
        Directory.CreateDirectory(emptyBaseDir);
        Directory.CreateDirectory(fakeCwd);
        string cwdExe = Path.Combine(fakeCwd, exeName);
        File.WriteAllText(cwdExe, string.Empty);

        string? resolvedFromCwd = BounceCommandHandler.ResolveDefaultHarnessPath(fakeCwd, emptyBaseDir);
        Assert.Equal(Path.GetFullPath(cwdExe), resolvedFromCwd);

        // 3. Walking up parent tree to tests/Glacier.EvaluationHarness/bin/Release/net10.0/<exeName>
        string fakeRepoRoot = Path.Combine(_tempDir, "repo_root");
        string nestedCwd = Path.Combine(fakeRepoRoot, "sub", "deep");
        string binRelease = Path.Combine(fakeRepoRoot, "tests", "Glacier.EvaluationHarness", "bin", "Release", "net10.0");
        Directory.CreateDirectory(nestedCwd);
        Directory.CreateDirectory(binRelease);
        string nestedExe = Path.Combine(binRelease, exeName);
        File.WriteAllText(nestedExe, string.Empty);

        string? resolvedFromTree = BounceCommandHandler.ResolveDefaultHarnessPath(nestedCwd, emptyBaseDir);
        Assert.Equal(Path.GetFullPath(nestedExe), resolvedFromTree);

        // 4. Returns null when absent everywhere
        string isolatedDir = Path.Combine(_tempDir, "isolated");
        Directory.CreateDirectory(isolatedDir);
        Assert.Null(BounceCommandHandler.ResolveDefaultHarnessPath(isolatedDir, isolatedDir));
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void WriteMinimalLoraBinary(string path)
    {
        const int dim = 64;
        const int rank = 4;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);
        bw.Write(0x41524F4CU);
        bw.Write(1U);
        bw.Write(1);
        bw.Write(rank);
        bw.Write(8.0f);
        bw.Write(2);

        bw.Write("blk.0.attn_q.lora_a.weight");
        bw.Write(dim);
        bw.Write(rank);
        for (int i = 0; i < dim * rank; i++) bw.Write(0.01f);

        bw.Write("blk.0.attn_q.lora_b.weight");
        bw.Write(rank);
        bw.Write(dim);
        for (int i = 0; i < rank * dim; i++) bw.Write(0.02f);
    }

    private static void WriteMinimalQwen2Gguf(string path)
    {
        const int dim = 64;
        const int ffnDim = 128;
        const int headCount = 2;
        const int headCountKv = 1;
        const int headDim = dim / headCount;
        const int vocabSize = 288;

        string[] vocab = new string[vocabSize];
        for (int i = 0; i < vocabSize; i++)
        {
            vocab[i] = i == vocabSize - 1 ? "<|endoftext|>" : i == vocabSize - 2 ? "<|im_start|>" : $"tok_{i}";
        }

        var metadata = new List<(string Key, GgufValueType Type, object Value)>
        {
            ("general.architecture", GgufValueType.String, "qwen2"),
            ("qwen2.block_count", GgufValueType.Uint32, 1u),
            ("qwen2.context_length", GgufValueType.Uint32, 64u),
            ("qwen2.embedding_length", GgufValueType.Uint32, (uint)dim),
            ("qwen2.feed_forward_length", GgufValueType.Uint32, (uint)ffnDim),
            ("qwen2.attention.head_count", GgufValueType.Uint32, (uint)headCount),
            ("qwen2.attention.head_count_kv", GgufValueType.Uint32, (uint)headCountKv),
            ("tokenizer.ggml.eos_token_id", GgufValueType.Uint32, (uint)(vocabSize - 1)),
            ("tokenizer.ggml.bos_token_id", GgufValueType.Uint32, (uint)(vocabSize - 2)),
            ("tokenizer.ggml.tokens", GgufValueType.Array, vocab)
        };

        float[] ones = new float[dim];
        Array.Fill(ones, 1.0f);
        var tensors = new List<(string Name, ulong[] Dims, float[] Data)>
        {
            ("token_embd.weight", new[] { (ulong)dim, (ulong)vocabSize }, new float[dim * vocabSize]),
            ("output_norm.weight", new[] { (ulong)dim }, ones),
            ("output.weight", new[] { (ulong)dim, (ulong)vocabSize }, new float[dim * vocabSize]),
            ("blk.0.attn_norm.weight", new[] { (ulong)dim }, ones),
            ("blk.0.attn_q.weight", new[] { (ulong)dim, (ulong)(headCount * headDim) }, new float[dim * headCount * headDim]),
            ("blk.0.attn_k.weight", new[] { (ulong)dim, (ulong)(headCountKv * headDim) }, new float[dim * headCountKv * headDim]),
            ("blk.0.attn_v.weight", new[] { (ulong)dim, (ulong)(headCountKv * headDim) }, new float[dim * headCountKv * headDim]),
            ("blk.0.attn_output.weight", new[] { (ulong)(headCount * headDim), (ulong)dim }, new float[headCount * headDim * dim]),
            ("blk.0.ffn_norm.weight", new[] { (ulong)dim }, ones),
            ("blk.0.ffn_gate.weight", new[] { (ulong)dim, (ulong)ffnDim }, new float[dim * ffnDim]),
            ("blk.0.ffn_up.weight", new[] { (ulong)dim, (ulong)ffnDim }, new float[dim * ffnDim]),
            ("blk.0.ffn_down.weight", new[] { (ulong)ffnDim, (ulong)dim }, new float[ffnDim * dim])
        };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);
        bw.Write(0x46554747U);
        bw.Write(3U);
        bw.Write((ulong)tensors.Count);
        bw.Write((ulong)metadata.Count);

        foreach (var (key, type, val) in metadata)
        {
            WriteStr(bw, key);
            bw.Write((uint)type);
            if (type == GgufValueType.String) WriteStr(bw, (string)val);
            else if (type == GgufValueType.Uint32) bw.Write((uint)val);
            else if (type == GgufValueType.Array)
            {
                var arr = (string[])val;
                bw.Write((uint)GgufValueType.String);
                bw.Write((ulong)arr.Length);
                foreach (var s in arr) WriteStr(bw, s);
            }
        }

        ulong offset = 0;
        foreach (var (name, dims, data) in tensors)
        {
            WriteStr(bw, name);
            bw.Write((uint)dims.Length);
            foreach (var d in dims) bw.Write(d);
            bw.Write((uint)GgufType.F32);
            bw.Write(offset);
            ulong len = (ulong)data.Length * sizeof(float);
            offset = (offset + len + 31UL) & ~31UL;
        }

        long alignedStart = ((fs.Position + 31L) & ~31L);
        while (fs.Position < alignedStart) bw.Write((byte)0);
        foreach (var (_, _, data) in tensors)
        {
            bw.Write(MemoryMarshal.AsBytes(data.AsSpan()));
            long nextAligned = ((fs.Position - alignedStart + 31L) & ~31L) + alignedStart;
            while (fs.Position < nextAligned) bw.Write((byte)0);
        }
    }

    private static void WriteStr(BinaryWriter bw, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        bw.Write((ulong)b.Length);
        bw.Write(b);
    }
}
