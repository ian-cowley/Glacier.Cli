# Changelog

All notable changes to `Glacier.Cli` are documented here.

## v1.6.3

- Added MSBuild indirect guard property support, optional `USE_LOCAL_*` defines, and `--standalone-only` filtering to `glacier verify-ecosystem`.

## v1.5.0

- Added native AOT `glacier security-check` CLI handler (`SecurityCheckCommandHandler`) scanning `.github` workflows, UTF-16 streams, connection strings, and credential patterns.
- Aligned all Tier 0–3 Glacier dependencies (`Glacier.Serve 1.9.0`, `Glacier.Tune 1.7.0`, `Glacier.Agent 1.5.3`, `Glacier.MemEx 1.2.3`, `Glacier.Inference 1.10.1`, `Glacier.Rag 1.4.3`, `Glacier.Clavier 1.2.1`).
- Hardened `glacier pull` (`HF_TOKEN` auth, `.part` HTTP `Range` resume, GGUF magic verification), reused `InferenceSession` in `glacier rag` REPL, and added `--host` binding to `glacier serve`.
