# JARVIS — TODO

> Grounded in `DOCUMENTATION/OFFICIAL-DOCS/` (specs), `AI-AGENT-DOCS/` (decisions), and the
> actual code as of 2026-07-09. Three horizons: **Now** (gaps against our own specs),
> **Next** (already-decided features not yet built), **Vision** (the cognitive-assistant
> direction — critically assessed), and a **Parking lot** for out-of-scope ideas.

---

## 1. NOW — finish what the specs already promise

### Security gaps (our own docs mandate these — highest priority before real personal data lives here)

- [ ] **Bind jv-api to Tailscale, not `0.0.0.0`.** `06-Security-Model.md` explicitly warns
      "Never bind to 0.0.0.0:8000" — `docker-compose.yml` does exactly that today. Options:
      bind `127.0.0.1` + `tailscale serve`, or bind the Tailscale IP. Decide and document.
- [ ] **Rate limiting** (spec: 02 §Security, 06 §Endpoint Matrix — e.g. 60/min files, 5/min auth).
      Not implemented at all. `slowapi` or a simple middleware counter.
- [ ] **Audit logging** to `/JARVIS/.system/audit.log` (spec: 06 §Audit Logging). All mutations,
      auth failures, secrets access — with device identity. Not implemented.
- [ ] **Containers run as root** (06 checklist says non-root). Add `USER` to Dockerfiles.
- [ ] **Mount vault read-only into jv-brain** (`:ro`, per 08's compose spec) — brain only reads;
      today it has write access it doesn't need. *(Check: generate-files writes via jv-api or
      directly? If brain writes, route through api first.)*
- [ ] **Token auto-refresh** before 30-day expiry (spec: 03 §Security). Endpoint exists
      (`POST /auth/refresh`); the app never calls it — sessions just die.

### Bugs / debt (found in code, this month)

- [ ] **Sync tombstones** — deletes can resurrect files (documented non-goal in
      `5-version-control.md`, but it's the biggest data-correctness hole left).
- [ ] Legacy `.jvs` cleanup: the two unrecoverable old-format files in `B:\JARVIS\Secrets`
      (pre per-file-salt fix). Add a "delete unrecoverable" button to the restore flow, or
      just delete them manually.
- [ ] `index-status` shows a stuck `pending_files: 1` — investigate the indexer's pending count.
- [ ] **Ponytail-audit cleanups** (report 2026-07-09): replace full `langchain` dep with
      `langchain-text-splitters`; declare or drop SQLAlchemy (stdlib `sqlite3` for the 2-table
      history DB); delete `brain/app/routers/debug.py`; remove dead `extra_hosts` from compose;
      delete `run_tests_for_user.py`.
- [ ] **Doc drift**: 02-Backend-Spec describes `middleware/`, `secrets.py` router and
      `/ask` shapes that don't match the code; walkthrough.md predates everything. Mark
      superseded sections or update. `PROJECT_OVERVIEW.md` is current — keep it that way.

### Phase 5 (Polish & QA — the roadmap's next phase, `10-Implementation-Phases.md`)

- [ ] **CI pipeline** (GitHub Actions): pytest (server, brain), `flutter analyze` + `flutter test`,
      docker build. The repo is public open source now — CI is table stakes for contributors.
- [ ] **Backup/export**: `POST /backup/export` + download (spec: 02 §Backup). Nothing exists.
      Even a zip-the-vault endpoint beats nothing.
- [ ] **Migration test**: copy vault + repo to a clean machine → `docker compose up` → all
      features work. The friend's Mac onboarding *is* this test — write down the results.
- [ ] **Contributor docs**: CONTRIBUTING.md, issue templates. (README/HOW_TO_RUN/OVERVIEW done.)
- [ ] Performance sanity: file ops < 500 ms, AI answer < 15 s on target hardware (spec: 05).

---

## 2. NEXT — decided in the docs, not yet started

- [ ] **QR guest/device registration** (`11-QR-Guest-Registration.md` — fully designed, zero code).
      Single-use invite tokens, guest-scoped JWTs (`secrets:access` denied), revocation UI,
      "Scan QR" on the login screen. Also fixes the "connecting is manual" complaint properly.
- [ ] **Background sync** on app resume + connectivity change (03 §Sync Manager; today sync is
      manual). Needs the concurrency guard noted in `5-version-control.md` future list.
- [ ] **Biometric app lock** (fingerprint before app opens) — Phase 6 item, small and high value.
- [ ] **Per-file version history + diff preview** in conflict screen (5-version-control future list).
- [ ] **Multi-model config** — model names are already env vars; surface them in Settings and
      test with a second model (llama3.1/qwen — see Vision below, this matters for tool use).
- [ ] **Chat "resumed" session status** (placeholder already in `chat_sessions_table.dart`).
- [ ] iOS build of the Flutter app (03 future list — mostly storage adapters + testing).

---

## 3. VISION — "JARVIS as my cognitive center" (critical assessment first)

**The honest constraints, before the task list:**

1. **A local 8B model cannot "replace your thinking."** llama3:8b on an RTX 3050 hallucinates,
   has weak tool-calling, and is slow for multi-step agent loops. It CAN: summarize, draft,
   retrieve, classify, and ask you questions. It CANNOT be trusted to act unsupervised on
   your behalf (send an email, submit a job application). Every outbound action needs a
   **human approval step**. This isn't a temporary limitation to code around — it's the
   design principle. JARVIS proposes; Aditya disposes.
2. **Email/Forms integration breaks a core constraint deliberately.** The docs say "never call
   external APIs." Reading Gmail means talking to Google. Decision to make (and write into
   06-Security-Model): *external inference stays forbidden; external **actions/data** via
   user-authorized OAuth are allowed.* Your email content would then flow through the vault —
   treat it like vault data (never leaves the machine except back to Google).
3. **Sequencing matters.** "Ping me," "learn about me," and "approve actions" are the
   foundation everything else stands on. Build those first; email/forms ride on top.
4. **Job auto-apply is the least realistic piece** — portals are hostile to automation
   (CAPTCHAs, ToS bans, ever-changing DOMs). The realistic version is "prepare my application
   pack in seconds, I paste/click." Full autonomy goes to the parking lot.

### 3A. Foundations (build in this order)

- [ ] **Interview mode ("talk to me to learn about me")** — purely local, no new infra, highest
      value-per-effort. A chat mode where JARVIS asks questions (goals, preferences, history,
      people) and writes structured answers into the vault (`People/`, `Personal/`,
      `TechProfile/`, …) via the existing generate-files pipeline (dry-run → confirm already
      exists!). The vault layout in `3-project-summary.md` was designed for exactly this.
- [ ] **Structured profile** — a `Profile/` schema (contact, education, work history, skills,
      links, standard Q&A like "why do you want this job"). Interview mode fills it; the
      forms/jobs features below consume it. Plain markdown/JSON, human-editable.
- [ ] **Proposal queue + approval inbox** — a server-side table of AI-proposed actions
      (`draft_email`, `fill_form`, `write_file`) with status `proposed/approved/rejected/done`,
      and a mobile inbox screen to approve/edit/reject. The existing file-creation dry-run
      modal is the embryo of this. **Nothing external ever executes without an approval row.**
- [ ] **Push notifications ("ping me when it needs my help")** — server → phone. Options:
      (a) polling (works today, battery cost), (b) ntfy self-hosted over Tailscale (fits the
      no-cloud model), (c) FCM (easy, but Google in the loop). Recommend ntfy. Wire to: sync
      conflicts, proposal queue items, daily digest ready.
- [ ] **Scheduler in jv-brain** — cron-like background jobs (asyncio) with a job table:
      powers daily digest, mail polling, re-indexing. The chat-archive job shows the pattern
      (currently client-side; server-side is the right home for proactive work).
- [ ] **Daily digest** (Phase 6 item, now foundational): morning summary of vault changes,
      pending conflicts, pending proposals → push notification. First real "proactive JARVIS."
- [ ] **Model upgrade spike** — evaluate a tool-calling-capable small model (llama3.1:8b,
      qwen2.5:7b) for the agent loop; llama3:8b's function calling is too weak to build on.

### 3B. Email (read → digest → drafts; strictly in that order)

- [ ] **Read-only Gmail ingest** — OAuth on the server, poll inbox on the scheduler, store
      as vault files (`Mail/2026-07/…`), auto-indexed by the existing RAG pipeline. JARVIS
      can then answer "what did X say about Y?" — huge value, zero risk.
- [ ] **Email triage digest** — scheduled job classifies new mail (urgent / needs-reply / FYI)
      → digest + push. Classification is well within an 8B model's ability.
- [ ] **Draft replies via approval queue** — JARVIS drafts using vault context; the draft sits
      in the approval inbox; you edit/approve; only then does the server send via Gmail API.
      Never auto-send. Start with drafts saved to Gmail's Drafts folder (even safer).

### 3C. Forms & job applications (assist, not autopilot)

- [ ] **Form-answering service** — input: form questions (pasted text or a Google Form URL
      fetched server-side); output: answers generated from `Profile/` + vault, with source
      citations, delivered as a proposal to review → copy-paste. No browser automation needed;
      80 % of the value.
- [ ] **Application pack generator** — given a job description: tailored CV bullet points,
      cover letter draft, answers to standard questions — from `Profile/` + `Work/`. A prompt
      + retrieval feature, very achievable locally.
- [ ] **(Later) Browser-extension autofill** — extension reads the open form's fields, asks
      jv-api (over Tailscale) for answers, fills them in-page, **you click submit**. This is
      the sane end-state of "fill forms for me" — it keeps you in the loop and dodges CAPTCHAs
      and ToS problems. Requires the web-dashboard CORS/auth work first.

---

## 4. PARKING LOT — later / probably never (kept honest)

- **Fully autonomous job applications** (Playwright driving LinkedIn/Workday): brittle, ToS
  violations, CAPTCHA arms race, and an 8B model misrepresenting you to employers. Revisit
  only after 3A–3C are solid — as a supervised browser extension, not a bot.
- **"Replace my thinking"** — reframed permanently as "prepare my thinking": briefs, drafts,
  recall, questions. Autonomy scales with model quality and trust earned, feature by feature.
- **Voice STT/TTS conversations** (Phase 6) — nice, big surface area (whisper + TTS + UI).
  After interview mode proves the conversation loop.
- **Relationship/knowledge graph, smart tagging** (Phase 6) — wait until the vault is big
  enough for retrieval quality to be the bottleneck.
- **Web dashboard** (read-only, behind Tailscale) — prerequisite for the browser extension;
  otherwise low priority.
- **Multi-user vaults, pgvector migration, WebSockets/webhooks, GraphQL, mTLS** — spec'd as
  future extensibility; none has a driver today.
- **Cloud LLM fallback** (Claude/GPT for hard tasks): would instantly solve the model-quality
  ceiling, but breaks the project's core privacy promise. If ever added: explicit per-request
  opt-in, never default, never with Secrets/Mail content.

---

*Rule of thumb encoded above: local-only for thinking, human-approved for acting,
vault as the single memory. Anything violating one of those three gets parked.*
