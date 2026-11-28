# Glacier.Cli Command Reference Manual

Invoking the tool with no arguments or `--help` prints the command list (`Usage: glacier <command> [arguments] [options]`). Append `--help` to any command to inspect its local options (`glacier <command> --help`).

---

## 1. Inference, Fine-Tuning & Serving

### `glacier run <model.gguf> [prompt] [options]`

Runs streaming generation or launches an interactive terminal REPL.

- `-c, --ctx <len>` — Context length (default `2048`)
- `-n, --tokens <count>` — Max tokens to generate (default `512`)
- `--temp <float>` — Sampling temperature (default `0.7`)
- `--top-p <float>` — Nucleus top-p (default `0.9`)
- `--device <id|name>` — Target backend (`nvidia-rtx-4060`, `cpu`, etc.)
- `--engine <baremetal|directml|cpu>` — Explicit execution engine
- `--kv-precision <auto|fp16|fp8|q8_0|q4_0|fp32>` — KV-cache precision
- `--lora <[name=]path>` — Attach binary LoRA adapter (`0x41524F4C`)
- `--max-think-tokens <auto|off|0|N>` — Cap `<think>` tokens and auto-close
- `--split <spec>` — Multi-GPU pipeline split specification

### `glacier tune <base.gguf> --data <train.jsonl> [options]`

Fine-tunes a GGUF base model using in-process LoRA over ChatML JSONL data (`--out` defaults to `adapters/lora_adapter.bin`):

- `-d, --data <path>` *(required)*, `-o, --out <path>`, `-r, --rank <int>` (`16`), `--alpha <float>` (`32`), `--lr <float>` (`0.0002`), `-e, --epochs <int>` (`3`), `-s, --steps <int>`, `--seq-len <int>` (`512`), `-b, --batch-size <int>` (`1`), `--grad-accum <int>` (`4`), `--device <auto|cuda|cpu>`, `--merge <output.gguf>`.

### `glacier merge <base.gguf> <adapter.bin> <output.gguf> [options]`

Fuses LoRA delta matrices into base weights (`Q8_0`) while preserving unaffected tensors and GGUF metadata (`-t, --threads <int>`).

### `glacier serve <model.gguf> [options]`

Starts a PagedAttention continuous-batching HTTP server (`POST /v1/chat/completions` and `POST /api/chat`):

- `-p, --port <int>` (`11434`), `--host <string>` (`127.0.0.1`), `--blocks <int>` (`1024`), `-b, --batch-size <int>` (`16`), `--name <string>`, `--kv-precision`, `--lora`, `--max-think-tokens`
- Sleep consolidation: `--sleep-memory` / `--no-sleep-memory`, `--sleep-budget` (`400`), `--sleep-idle-seconds` (`300`), `--sleep-min-dreams` (`1`), `--sleep-checkpoint <path>`
- Co-hosted PeerBounce: `--bounce` / `--no-bounce`, `--bounce-primary <url>`, `--critic <url>`, `--bounce-harness <path>`, `--bounce-max-repairs <0..3>`, `--compiler-bounce` / `--no-compiler-bounce`

---

## 2. RAG, Agents, Downloads & Proxy

### `glacier rag [options]`

- `-d, --docs <path>`, `-q, --query <text>` (REPL if omitted), `-m, --model <model.gguf>`
- `--embed-endpoint <url|auto-npu>`, `--embed-model <id>`, `--embed-dims <int>`, `--api-key`
- `-k, --top-k <int>` (`3`), `--hops <int>` (`2`), `-n, --tokens <int>` (`512`)

### `glacier agent [options] <prompt>`

- `-m, --model <path|id>`, `--endpoint <url|auto-npu>`, `--npu`, `--api-key <key>`, `--clavier-model <path>`, `--max-steps <int>` (`8`), `-n, --tokens, --max-tokens <int>` (`512`).
- Supports direct built-in tool calls (`CALL ping: {}`, `CALL devices: {}`).

### `glacier bounce [options]`

Standalone Peer Critic + OS-sandboxed compiler verifier HTTP proxy: `--primary` (`http://127.0.0.1:11434`), `--critic` / `--peer-critic` (`url|auto-npu`), `--npu-critic`, `--host` (`127.0.0.1`), `-p/--port` (`18090`), `--harness`, `--max-repairs`, `--compiler-bounce`/`--no-compiler-bounce`, `--model-alias`.

### `glacier pull <repo-or-url> [options]`

Uses `HF_TOKEN` / `HUGGING_FACE_HUB_TOKEN` when set: `-o, --out <dir>` (`./models`), `-f, --file <name>`, `--sha256 <hex>`, `--parallel <1..16>` (`4`).

---

## 3. Diagnostics & Ecosystem Governance

- **`glacier devices`:** Lists GPUs, VRAM, and SIMD ISA capabilities.
- **`glacier config [options]`:** Inspects or updates acceleration configuration.
- **`glacier bench <model.gguf> [options]`:** Measures inference throughput (`-n/--tokens` `25`, `-p/--prompt`, `-c/--ctx` `2048`, `--kv-precision`).
- **`glacier inspect <model.gguf> [options]`:** Inspects tensors (`-f/--filter`, `-a/--all`, `--audit`, `--assert-no-type <type>`, `--max-bytes <bytes>`, `--probe <tensor>`, `--json`).
- **`glacier audit-adapter <adapter.bin|dir> [options]`:** Validates LoRA adapters (`--expect-layers`, `--expect-rank`, `--expect-alpha`, `--expect-tensors`, `--min-lora-b-nonzero` default `0.90`, `--json`).
- **`glacier security-check [path] [--include-scratch] [--json]`:** Native AOT credential/secret scanner (`0` = clean, `1` = findings/invalid args, `2` = missing path).
- **`glacier verify-ecosystem [root-path] [options]`:** Audits MSBuild `ProjectReference` isolation, NuGet fallbacks, retired package IDs (`--root`, `--standalone-only`, `--check-secrets`, `--include-predict`, `--json`).
