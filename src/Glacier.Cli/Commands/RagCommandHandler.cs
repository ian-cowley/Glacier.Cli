namespace Glacier.Cli.Commands;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Glacier.Inference.Config;
using Glacier.Inference.Engine;
using Glacier.Inference.Sampling;
using Glacier.Rag.Engine;

public static class RagCommandHandler
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string? modelPath = null;
        string? docsPath = null;
        string? query = null;
        int topK = 3;
        int maxHops = 2;
        int maxTokens = 512;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-m" or "--model" && i + 1 < args.Length)
            {
                modelPath = args[++i];
            }
            else if (arg is "-d" or "--docs" or "--data" && i + 1 < args.Length)
            {
                docsPath = args[++i];
            }
            else if (arg is "-q" or "--query" && i + 1 < args.Length)
            {
                query = args[++i];
            }
            else if (arg is "-k" or "--top-k" && i + 1 < args.Length)
            {
                topK = int.Parse(args[++i]);
            }
            else if (arg is "--hops" && i + 1 < args.Length)
            {
                maxHops = int.Parse(args[++i]);
            }
            else if (arg is "-n" or "--tokens" && i + 1 < args.Length)
            {
                maxTokens = int.Parse(args[++i]);
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            GLACIER.RAG - In-Process SIMD Vector + CSR GraphRAG                ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        using var ragEngine = new GraphRagEngine();

        // 1. Ingest Documents
        if (!string.IsNullOrEmpty(docsPath))
        {
            if (File.Exists(docsPath))
            {
                Console.WriteLine($"[1/3] Ingesting document: {Path.GetFileName(docsPath)}...");
                string content = await File.ReadAllTextAsync(docsPath);
                int chunks = ragEngine.IndexDocument(Path.GetFileName(docsPath), content);
                Console.WriteLine($"  ✓ Indexed {chunks} semantic chunks");
            }
            else if (Directory.Exists(docsPath))
            {
                Console.WriteLine($"[1/3] Scanning and ingesting directory: {docsPath}...");
                var files = Directory.GetFiles(docsPath, "*.*", SearchOption.AllDirectories);
                int totalDocs = 0;
                int totalChunks = 0;
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".md" or ".json" or ".cs" or ".csv")
                    {
                        string content = await File.ReadAllTextAsync(file);
                        totalChunks += ragEngine.IndexDocument(Path.GetFileName(file), content);
                        totalDocs++;
                    }
                }
                Console.WriteLine($"  ✓ Ingested {totalDocs} files into {totalChunks} vector & graph chunks");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Document path '{docsPath}' not found.");
                Console.ResetColor();
            }
        }
        else
        {
            Console.WriteLine("[1/3] No documents supplied via --docs; using empty knowledge index.");
        }

        Console.WriteLine($"  ✓ Graph Knowledge Nodes: {ragEngine.GraphNodeCount:N0} nodes | {ragEngine.GraphEdgeCount:N0} edges");
        Console.WriteLine($"  ✓ Vector Index:          {ragEngine.IndexedChunksCount:N0} semantic embeddings\n");

        // 2. Query Loop or Single-Shot
        if (!string.IsNullOrEmpty(query))
        {
            await ProcessQueryAsync(ragEngine, modelPath, query, topK, maxHops, maxTokens);
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("================================================================================");
        Console.WriteLine("   Interactive GraphRAG Console (Type 'exit' to quit)                           ");
        Console.WriteLine("================================================================================\n");
        Console.ResetColor();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("rag> ");
            Console.ResetColor();

            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            await ProcessQueryAsync(ragEngine, modelPath, line, topK, maxHops, maxTokens);
        }

        return 0;
    }

    private static async Task ProcessQueryAsync(
        GraphRagEngine ragEngine,
        string? modelPath,
        string query,
        int topK,
        int maxHops,
        int maxTokens)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[Query] {query}");
        Console.ResetColor();

        var sw = Stopwatch.StartNew();
        var retrieval = ragEngine.Retrieve(query, new RagOptions { TopK = topK, MaxGraphHops = maxHops });
        sw.Stop();

        Console.WriteLine($"  ✓ Hybrid Retrieval: {sw.Elapsed.TotalMilliseconds:F2} ms (Vector: {retrieval.VectorSearchLatencyMs:F2} ms, Graph: {retrieval.GraphTraversalLatencyMs:F2} ms)");
        Console.WriteLine($"  ✓ Discovered Entities: [{string.Join(", ", retrieval.DiscoveredEntities)}]");
        Console.WriteLine($"  ✓ Graph Relations:    {retrieval.GraphRelations.Count} structural hops traversed");
        Console.WriteLine($"  ✓ Top Vector Matches:   {retrieval.VectorMatches.Count} excerpts\n");

        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("=== SYNTHESIZED HYBRID CONTEXT (Pass --model <gguf> to stream LLM answer) ===");
            Console.WriteLine(retrieval.SynthesizedContext);
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=== STREAMING GROUNDED LLM GENERATION ===");
        string prompt = ragEngine.BuildAugmentedPrompt(query, retrieval);

        using var session = new InferenceSession(modelPath, maxSeqLen: 4096);
        var sampling = new SamplingOptions { MaxTokens = maxTokens, Temperature = 0.2f };

        var genResult = await session.GenerateAsync(
            prompt,
            sampling,
            formatChat: false,
            onToken: token => Console.Write(token));

        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n[Metrics] {genResult.Metrics.GenerationTokensPerSecond:F1} tok/s | Total: {genResult.Metrics.TotalDuration.TotalMilliseconds:F0} ms\n");
        Console.ResetColor();
    }

    public static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("glacier rag - In-process SIMD Vector + CSR GraphRAG query engine");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  glacier rag [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -d, --docs <path>                    Document file or folder to ingest");
        Console.WriteLine("  -q, --query <text>                   Single question to answer (launches REPL if omitted)");
        Console.WriteLine("  -m, --model <model.gguf>             GGUF model to generate answers from");
        Console.WriteLine("  -k, --top-k <int>                    Number of vector matches (default: 3)");
        Console.WriteLine("  --hops <int>                         Max graph hops for CSR traversal (default: 2)");
        Console.WriteLine("  -n, --tokens <int>                   Max tokens for LLM generation (default: 512)");
    }
}
