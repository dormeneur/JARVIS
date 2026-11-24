<div align="center">

https://github.com/user-attachments/assets/ffe818b3-33d3-46c4-a597-5376f1a2aa3e

# JARVIS

**Your AI second brain. Zero bytes to the cloud.**

A self-hosted, offline-first personal knowledge OS. Your life is a folder of plain markdown;
a local model reads it, searches it, and acts on it from your phone and your desktop.

![license](https://img.shields.io/badge/license-MIT-3380FF)
![python](https://img.shields.io/badge/FastAPI-Python_3.11-26272D)
![flutter](https://img.shields.io/badge/Flutter-Android-26272D)
![dotnet](https://img.shields.io/badge/.NET_9-WinUI_3-26272D)
![ollama](https://img.shields.io/badge/LLM-Ollama_(local)-26272D)

</div>

---

## Why

- **Your files are the database.** One vault folder of plain files is the source of truth. Copy it, `docker compose up`, done.
- **Vault content never touches a cloud model.** Anything answered from your notes runs on Ollama, on your machine.
- **Works offline.** The phone keeps a full local mirror and syncs when it can, without losing an edit.

## What it does

| | |
|---|---|
| 🧠 **Ask your vault** | RAG over your notes, PDFs and docs. Hybrid search (vectors + BM25, fused with RRF), answers cite the file. |
| 🖱️ **Desktop companion** | Lives next to your cursor (Windows; macOS port). Hold a key and talk: it sees your screen, answers out loud, points and draws. |
| 🛠️ **Agent with approvals** | Tool-calling agent that reads, searches and edits the vault. Every write waits for your OK, and old versions go to trash for 30 days. |
| 📱 **Phone app** | Flutter on Android: explorer, editor, chat, voice, and versioned offline sync (conflicts are never silently overwritten). |
| 📥 **Inbox** | Background AI work (memory, digests, imports) files *proposals*. Nothing is written until you approve. |
| 📝 **Form filling** | Fills job and signup forms from `Profile/` through a browser extension. An answer survives only if its evidence is in your files. |
| 🔍 **Hunches** | Learns your patterns, then *asks* before saving anything about you. Sensitive topics are dropped. |
| 🔐 **Secrets** | `/Secrets` is end-to-end encrypted (AES-256-GCM) and hard-excluded from every model, every index and every tool. |

## Architecture

```
 phone (Flutter) ─┐                     ┌─ jv-brain  RAG · agent · scheduler ── Ollama (host)
                  ├── Tailscale ── jv-api ┤
 desktop (.NET) ──┘   JWT, :8000         └─ vault/   plain files, the only source of truth
```

NDJSON streams at every hop. Devices register with a one-time secret, and nothing but jv-api is exposed.

## Quick start

```bash
cp .env.template .env          # set JARVIS_HOST_PATH to your vault folder
ollama pull qwen3:4b && ollama pull nomic-embed-text
docker compose up -d --build   # jv-api on :8000
python scripts/doctor.py       # checks the whole stack
```

Phone: `cd mobile && flutter pub get && dart run build_runner build && flutter run`
Desktop: `cd desktop && dotnet build -p:Platform=x64`

Full walkthrough (Windows + Mac): **[HOW_TO_RUN.md](HOW_TO_RUN.md)** · Architecture and gotchas: **[PROJECT_OVERVIEW.md](PROJECT_OVERVIEW.md)**

## Stack

`FastAPI` · `Ollama` · `SQLite (vectors + FTS5)` · `Flutter` · `Riverpod` · `Drift` · `.NET 9` · `WinUI 3` · `Whisper` · `Kokoro` · `Docker` · `Tailscale`

## License

[MIT](LICENSE)
