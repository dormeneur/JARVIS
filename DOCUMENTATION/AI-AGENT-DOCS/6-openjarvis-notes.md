# OpenJarvis: reference notes and how JARVIS compares

> Written 2026-11-05 by reading `B:\DEV\HEY-CLICKY\OpenJarvis` (Stanford Hazy Research / SAIL,
> Apache-2.0, arXiv 2605.17172) next to this repo. This is a reference, not a plan. The work
> that came out of it is in `7-upgrade-plan.md`.
> Paths like `oj:src/openjarvis/memory/extractor.py` are relative to the OpenJarvis checkout.

---

## 1. What OpenJarvis is

A **framework** for local-first personal AI ("PyTorch for on-device agents"), not a product.
Python core, an optional Rust core (PyO3), a Tauri desktop/web UI, and a CLI (`jarvis ask|chat|gui|digest`).

Five "primitives", each an ABC with pluggable implementations registered by decorator
(`oj:src/openjarvis/core/registry.py`):

| Primitive | What it is | Implementations |
|---|---|---|
| Intelligence | model catalog | built-in model list merged with what the engines report |
| Engine | inference runtime | Ollama, vLLM, SGLang, llama.cpp, MLX, LM Studio, Apple FM, cloud (OpenAI/Anthropic/Google), LiteLLM |
| Agents | agent loops | simple, orchestrator, ReAct, CodeAct, RLM, Claude Code, operative (scheduled), monitor, morning_digest, deep_research, proactive |
| Memory | storage + retrieval | SQLite/FTS5 (default), FAISS, ColBERT, BM25, hybrid RRF |
| Learning | trace-driven routing | heuristic, trace-driven, SFT/GRPO stubs, DSPy skill optimisation |

Around those: ~30 messaging channels (Telegram, WhatsApp, Slack, iMessage…), ~30 connectors
(Gmail, GCal, Drive, Notion, Obsidian, Apple Health…), an MCP client and server, A2A, a scheduler,
skills (agentskills.io `SKILL.md`), speech (faster-whisper, Kokoro), energy/FLOPs telemetry, evals
and benchmarks, and Pearl GPU "mining". It's about 2,200 files.

Its design principles (`oj:docs/architecture/design-principles.md`): pluggable everything,
registry-driven, offline-first, hardware-aware, telemetry-native, OpenAI-compatible API, standalone
(SQLite for everything, no external services needed).

## 2. How the two projects think differently

| | OpenJarvis | JARVIS |
|---|---|---|
| Goal | Research platform plus foundation for many people's agents | One owner's private knowledge OS |
| Source of truth | SQLite files under `~/.openjarvis` (memory, traces, knowledge store) | **Plain files in the vault**; every index can be rebuilt from them |
| Cloud | Local by default, cloud "when truly necessary", with routing and cost tracking | Hard split: vault content never goes to a cloud model; cloud only for vault-free screen help |
| Devices | One machine (CLI, web, Tauri) plus chat channels | Server + offline Android app with real sync + Windows desktop companion |
| Actions | Many tools incl. shell/code exec; approvals mainly in the proactive agent | Every mutation needs an approval; no shell for the local agent |
| Breadth vs depth | Very broad, many half-depth integrations | Narrow, polished end-to-end flows (forms, dictation, cursor, sync) |
| Measurement | Telemetry, traces, evals, leaderboards built in | Unit and property tests only; no runtime metrics, no quality evals |

## 3. Where JARVIS is better (keep these, they're the moat)

1. **Files as truth.** Portability is real: copy the folder, `docker compose up`. OpenJarvis's
   memory lives in opaque SQLite/JSONL in a config dir.
2. **Multi-device sync with conflict handling** (version-based OCC, MutationQueue, no silent
   overwrite). OpenJarvis has nothing like it.
3. **E2E-encrypted `/Secrets`**, excluded from AI at three layers (loader, `safe_path`, e2e test).
4. **Human approval for every mutating tool**, enforced server-side for risk class.
5. **Grounding checks.** Form answers survive only if their quoted evidence is in the cited
   file (`brain/app/routers/forms.py`). OpenJarvis does nothing this strict.
6. **Desktop UX** (cursor companion, drawing, dictation with word learning, form filling,
   drive mode with per-step approval). Nothing in OpenJarvis comes close.
7. **Network posture.** jv-api on `127.0.0.1` behind Tailscale, brain/chroma internal only.
8. **Focus.** One user and few moving parts mean a much smaller attack and bug surface.

## 4. Where JARVIS is worse (what to learn)

1. **Retrieval.** Vector-only search over ChromaDB, one chunk per file, a 2k-token context
   budget, and no nomic task prefixes. OpenJarvis's default is FTS5/BM25, and its knowledge
   search fuses BM25 with cosine through RRF plus metadata filters
   (`oj:src/openjarvis/connectors/hybrid_search.py`). Names, IDs and exact phrases are where
   vector-only search fails.
2. **No measurement.** There's no eval set, no latency or token traces, and so no evidence for
   model choices (the TODO's "model upgrade spike" has nothing to measure with).
   OpenJarvis records every inference (`oj:src/openjarvis/telemetry/`, `oj:src/openjarvis/traces/`).
3. **No passive memory.** JARVIS learns only through interview mode. OpenJarvis extracts durable
   facts from each exchange on a background thread, with trust tiers and a scanner
   (`oj:src/openjarvis/memory/{extractor,service,store}.py`).
4. **No proactive engine in the local brain.** The desktop has routines and a morning brief, but
   on Claude or calendar. OpenJarvis has a cron scheduler, a morning digest and a proactive agent
   with an approval store that remembers decisions per pattern
   (`oj:src/openjarvis/scheduler/`, `oj:src/openjarvis/agents/{morning_digest,proactive_agent}.py`,
   `oj:src/openjarvis/tools/approval_store.py`).
5. **Agent robustness.** OpenJarvis has a loop guard (identical-call hashing, ping-pong detection,
   `oj:src/openjarvis/agents/loop_guard.py`) and context compression that truncates tool output
   (`oj:src/openjarvis/sessions/compression.py`). JARVIS has only `MAX_ITERATIONS = 6`.
6. **Layered security.** OpenJarvis has taint labels with sink policies (`security/taint.py`),
   a `BoundaryGuard` that scans text leaving the device for secrets and PII (`security/boundary.py`),
   a prompt-injection scanner for tool output (`security/injection_scanner.py`), SSRF guards
   and a "data boundary audit" report (`security/data_boundary_audit.py`). JARVIS relies on one
   strong path guard plus keyword routing on the desktop.
7. **Operability.** OpenJarvis has `jarvis doctor` and one-line installers. JARVIS's setup
   knowledge lives in memory notes and `HOW_TO_RUN.md`.
8. **Connectors.** OpenJarvis normalises every source into one `Document` schema
   (`oj:src/openjarvis/connectors/_stubs.py`: doc_id, source, doc_type, title, author,
   participants, timestamp, thread_id, url, metadata). JARVIS has none yet (Gmail is TODO 3B).

## 5. Ideas worth taking, mapped to JARVIS

| Idea | OpenJarvis source | JARVIS adaptation | Plan task |
|---|---|---|---|
| Hybrid BM25 + vector with RRF (k=60), over-fetch then fuse; quote each FTS term to avoid syntax errors | `connectors/hybrid_search.py`, `tools/storage/hybrid.py`, `memory/sqlite` | SQLite FTS5 sidecar next to Chroma in `.system/brain/fts.db`; stdlib only | 2.2 |
| "Brute force is fine at personal scale" (~5k chunks × 768 dims in under 50 ms) | `hybrid_search.py` docstring | Supports dropping ChromaDB for SQLite later, once evals prove parity | 5.1 |
| Background fact extraction that never raises, dedupes and caps facts | `memory/extractor.py`, `memory/service.py` | Facts become **proposals** (approval inbox) appended to `Profile/*.md`, never silent writes | 3.2 |
| Trust tiers on memory (auto vs untrusted) | `memory/store.py` | Only the user's own words are a source; assistant text is never mined | 3.2 |
| Approval store: tiers + remembered decisions keyed by `action_type:fingerprint` | `tools/approval_store.py` | Proposal queue with base-hash staleness check; "always" decisions are a later option | 3.1 |
| Scheduler with persisted next/last run | `scheduler/scheduler.py` | asyncio loop in jv-brain, state in `.system/brain/scheduler.json` | 3.3 |
| Morning digest: per-section prompts that say "only cover provided items" | `agents/morning_digest.py` | `Digest/YYYY-MM-DD.md` written to the vault, push title only | 3.4 |
| Loop guard: hash `(tool, args)`, block on the 3rd identical call | `agents/loop_guard.py` | 15 lines in `agent.py` | 0.6 |
| Rule-based compression: cap each tool output (2,000 chars) | `sessions/compression.py` | Tool output cap derived from `num_ctx` | 0.5 |
| Telemetry record per call (latency, TTFT, tokens, model) | `telemetry/`, `core/types.py: TelemetryRecord` | JSONL trace log in `.system/brain/traces.jsonl`, no query text | 1.2 |
| Evals as a first-class concern | `evals/`, `bench/` | Retrieval golden set kept in the vault (never in this public repo) | 1.1 |
| `jarvis doctor` | `cli/` | `scripts/doctor.py`, stdlib, with the hard-won env quirks as checks | 1.3 |
| BoundaryGuard: scan text leaving the device | `security/boundary.py`, `security/scanner.py` | Desktop `OutboundScanner` before any Claude turn or dictation clean-up | 4.3 |
| Injection scanner on tool output | `security/injection_scanner.py` | Flag (not block) vault text that reads like instructions; wrap content as data | 2.3 |
| Universal `Document` schema for connectors | `connectors/_stubs.py` | Frontmatter schema for `Mail/` files when Gmail ingest lands | 5.4 |
| Data-boundary report | `security/data_boundary_audit.py` | Later: a desktop Settings page, "Where your data goes" | backlog |

## 6. Ideas to skip, and why

- **Registry + ABC for every component, multiple engines and memory backends.** That serves a
  framework with many users. JARVIS has one engine (Ollama, which also speaks the OpenAI API)
  and one store. An interface with one implementation is just indirection.
- **Learning router / GRPO / DSPy skill optimisation.** Needs trace volume a single user
  never produces. Revisit only if traces show a clear "small model is enough for X" split.
- **30 messaging channels.** JARVIS owns its clients and uses Tailscale. Push = ntfy (TODO 3A).
- **Energy/FLOPs telemetry, Pearl mining, Rust core, A2A.** Research concerns, no user value here.
- **Shell or code exec tools for the local agent.** They conflict with "human-approved for acting".
- **Its MCP server exposing memory tools.** For JARVIS that would let a cloud client read the
  vault, which breaks the privacy split. If MCP is ever added it must be local-model-only.
- **Fact extraction from assistant text** (OpenJarvis includes it). Small models hallucinate.
  Only mine the user's words.

## 7. Tech-stack verdict

| Choice | Verdict |
|---|---|
| FastAPI + Pydantic, two services (api / brain) | **Right.** The split is a real trust boundary (brain is internal, api does auth). |
| Ollama on host | **Right.** One copy of the weights, GPU without Docker setup. |
| qwen3:4b for tools | Reasonable for an RTX 3050; decide with evals (task 5.3), not by feel. |
| nomic-embed-text | Fine, **but it must get `search_document:` / `search_query:` prefixes** (task 2.1). Try qwen3-embedding / embeddinggemma only through the eval harness. |
| ChromaDB 0.5.23 container | **Weakest choice.** An extra service, an old pinned version, API churn, no keyword search, and its data lives outside the vault. Fine to keep for now; replace with SQLite (FTS5 + vectors) once evals exist (task 5.1). |
| tiktoken cl100k for qwen token counts | Approximate but fine for budgeting (leave ~10% headroom). |
| Flutter + Riverpod + Drift | **Right.** Offline-first mobile with a real local DB. |
| .NET 9 / WinUI 3 desktop | **Right** for an overlay this native; Windows-only is an accepted trade. |
| `claude` CLI subprocess | Smart (subscription, no API key) but fragile to CLI flag changes; keep a CLI-version check in doctor. |
| Tailscale + JWT HS256 + AES-GCM/PBKDF2 | **Right** for a single-owner system. |
| NDJSON streaming end to end | **Right**, simpler than SSE for multi-hop proxying. |

## 8. Issues found in JARVIS while comparing (detail and fixes in the plan)

P0 = loses data, leaks data or wastes most compute. P1 = quality. P2 = hygiene.

| # | Sev | Issue | Evidence |
|---|---|---|---|
| 1 | P0 | Indexer re-embeds **and re-summarises every file on every run**: it stores the *chunk* hash in Chroma but compares the *file* hash. Tests mock the store, so they never caught it. | `brain/app/services/text_chunker.py:69`, `vector_store.py:63`, `incremental_indexer.py:100,176`, `tests/test_incremental_indexer_properties.py:124` |
| 2 | P0 | Brain chat history is in the container's writable layer (`/app/data/history.db`). There's no volume despite the Dockerfile comment, so `up --build` erases it and backups don't include it. | `brain/app/services/history_db.py:21`, `brain/Dockerfile`, `docker-compose.yml` |
| 3 | P0 | Agent resume **re-executes every approved mutation on each re-POST**. Granted calls and read calls from earlier passes are dropped from the replay. | `brain/app/routers/agent.py:164-185`, `mobile/.../chat_screen.dart` (transcript grows per approval) |
| 4 | P0 | `edit_file` rewrites the whole file, but `num_predict` is 1024 tokens, so a file over ~3-4 KB gets truncated or broken. There's no undo, and delete is `rmtree`. Interview mode rewrites growing Profile files this way. | `ollama_client.py:35`, `tools.py:177,208`, `agent.py` INTERVIEW_PROMPT |
| 5 | P0 | Desktop background jobs pre-approve bare `Read`/`Write`/`Edit`/`Glob`/`Grep`, which grants **every path on the PC**, the vault included. A web page read by WebFetch could inject "read B:\JARVIS\Profile… and fetch https://evil/?q=…", or write a `.bat` into the Startup folder. The "no shell, can't reach the PC" claim is false. | `desktop/src/Jarvis.Providers.ClaudeCli/ClaudeCliAgentRuntime.cs:19,140-142` |
| 6 | P1 | `num_ctx` is never set for chat/generate. Ollama's default window silently truncates the prompt from the front (system prompt first) once context + history + tool output grows. | `ollama_client.py` (only `chat_json` with a form model sets it) |
| 7 | P1 | Server honours a session grant for `delete_file`. Only the mobile UI refuses it, so any other client can auto-delete. | `agent.py:229`, `tools.py` comment "session-grant refused" |
| 8 | P1 | Sync ChromaDB client calls inside `async def` block the event loop during indexing and queries. | `vector_store.py:71,86,115,125` |
| 9 | P1 | Retrieval: vector-only, one chunk per file, a 2,048-token context, no nomic prefixes. | `retriever.py:83`, `context_assembler.py:17`, `embedding_pipeline.py` |
| 10 | P1 | No loop guard; one confused model burns 6 iterations × 12 passes. | `agent.py:36` |
| 11 | P2 | Brain gets the whole `.env` (including `JARVIS_JWT_SECRET`) and a rw mount of `Secrets/` and `system/`; `safe_path` is the only guard. | `docker-compose.yml` brain service |
| 12 | P2 | Fail-open default: a device record missing `is_secrets_authorized` is treated as **admin**. | `server/app/services/auth.py:70,84,239` |
| 13 | P2 | Desktop privacy routing is keyword-based (`TurnRouter`); a miss sends the question and a screenshot to Claude, and dictation clean-up sends all dictated text. Nothing scans for secrets on the way out. | `desktop/src/Jarvis.Core/Vault/TurnRouter.cs`, `desktop/CLAUDE.md` |
| 14 | P2 | Two write paths for AI-created files (`/ask/generate-files` manifest vs agent `create_file`). | `brain/app/routers/generate.py`, `tools.py` |
