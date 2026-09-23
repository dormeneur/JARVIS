# JARVIS — TODO

> Grounded in `DOCUMENTATION/OFFICIAL-DOCS/` (specs), `AI-AGENT-DOCS/` (decisions), and the
> actual code as of 2026-09-23 (re-verified against git log + live grep, not just the prior
> pass from 2026-07-09 — most of that list is now done, see §0). Three horizons: **Now**
> (real gaps against our own specs), **Next** (decided features not yet built), **Vision**
> (the cognitive-assistant direction — critically assessed), and a **Parking lot**.

---

## 0. Since the last pass (2026-07-09 → 2026-09-23) — verified done ✅

Don't re-do these; confirmed against current code, not just commit messages:

- **jv-api bound to `127.0.0.1:8000`**, not `0.0.0.0` (`docker-compose.yml:10`).
- **Non-root containers**: both Dockerfiles have `USER jarvis`.
- **Brain vault mount is `:ro`** (`docker-compose.yml:34`); jv-api keeps read-write.
- **Dead debug router removed** (`brain/app/routers/debug.py` no longer exists).
- **`extra_hosts` removed** from compose.
- **`langchain` → `langchain-text-splitters`** (minimal dep, confirmed in `brain/requirements.txt`).
- **SQLAlchemy dropped** — `history_db.py` now stdlib `sqlite3`, two tables, no ORM.
- **Token auto-refresh** — `auth_provider.dart` refreshes silently at <24h to expiry
  (`_maybeRefreshToken`), plus silent reconnect on 401 via stored device secret.
- **QR guest/device registration** — shipped (`fbf1da6`): `invite_qr_screen.dart` (admin
  generates a 10-min countdown QR), `qr_scan_screen.dart` (guest scans + names device),
  server-side invite token flow in `server/app/routers/auth.py` / `auth_models.py`. Device
  Management screen now has an "Invite a device" QR action gated on secrets-authorization.
  *(Just fixed today: `device_management_screen.dart` had its entire class + provider
  duplicated — a paste error that broke the Gradle/kernel build. Also cleaned an unnecessary
  cast in `auth_provider.dart` and an unused `theme` var in `invite_qr_screen.dart`.)*
- **CI pipeline** — `.github/workflows/ci.yml` runs server pytest, brain pytest (unit subset),
  `flutter analyze` + `flutter test` (with Drift codegen step), and a Docker build smoke test,
  on push/PR to `main`.
- **Chat auto-archive** — fully committed (`e176dce`), not just staged as the old doc said.

**Not re-verified this pass** (lower confidence, spot-check before relying on): rate limiting,
audit logging, backup/export, sync tombstones — see §1, still believed absent.

---

## 1. NOW — real gaps against our own specs

### Security (still open — highest priority before more personal data lives here)

- [ ] **Rate limiting** (spec: 02 §Security, 06 §Endpoint Matrix). No `slowapi` or counter
      middleware found in `server/app`. Still nothing stopping a compromised/buggy device
      from hammering `/auth/*` or `/files/*`.
- [ ] **Audit logging** to `/JARVIS/.system/audit.log` (spec: 06 §Audit Logging). No `audit`
      references anywhere in `server/app`. Mutations, auth failures, secrets access, device
      identity — none of it is logged today.

### Bugs / debt

- [ ] **Sync tombstones** — still the biggest data-correctness hole. Deletes on the server can
      resurrect from a device that hasn't pulled the deletion. Documented non-goal, but worth
      a fresh look now that sync has been stable for months.
- [ ] **Legacy unrecoverable `.jvs` files** in `Secrets/` (pre per-file-salt fix) — no cleanup
      UI shipped. Either add a "delete unrecoverable" affordance to the restore flow or confirm
      they were removed manually.
- [ ] `index-status` stuck `pending_files: 1` — unclear if this was fixed; re-check against a
      live `docker compose up` before trusting the indexer's status endpoint.
- [ ] `run_tests_for_user.py` at repo root is a scratch script (shells out to pytest, dumps to
      `.txt` files) — looks like a leftover debugging artifact, not part of the test suite or
      CI. Delete it or move it under a `scripts/` dir if it's still useful.
- [ ] **Doc drift** — `02-Backend-Specification.md` still describes a `middleware/` dir and
      `/ask` shapes that may not match current routers; `walkthrough.md` predates Phase 4/5
      entirely. Worth a pass now that QR + refresh + CI have landed, or mark sections
      explicitly superseded.

### Phase 5 (Polish & QA)

- [ ] **Backup/export** (`POST /backup/export` + download, spec 02 §Backup) — nothing exists.
      Even a zip-the-vault endpoint beats nothing; this is the one gap that turns a bug into
      permanent data loss.
- [ ] **Migration test** — copy vault + repo to a clean machine, `docker compose up`, confirm
      every feature works. Write the result down (`HOW_TO_RUN.md` or a dated note) instead of
      re-deriving it from memory each time.
- [ ] **CONTRIBUTING.md + issue templates** — README/HOW_TO_RUN/OVERVIEW exist; contributor
      onboarding docs don't. Only matters if outside contributions are actually wanted.
- [ ] **Performance sanity check** — file ops < 500 ms, AI answer < 15 s on target hardware
      (spec 05). No measured numbers on record.

---

## 2. NEXT — decided in the docs, not yet started

- [ ] **Background sync** on app resume + connectivity change (03 §Sync Manager — today sync
      is manual/pull-to-refresh). Needs the concurrency guard noted as a future item in
      `5-version-control.md`.
- [ ] **Biometric app lock** (fingerprint before app opens) — small, high value, Phase 6 item.
- [ ] **Per-file version history + diff preview** in the conflict screen.
- [ ] **Multi-model config surfaced in Settings** — model names are already env vars
      (`JARVIS_LLM_MODEL`); no UI to switch/test a second model. Matters for tool-calling
      quality (see Vision, model-upgrade spike).
- [ ] **Chat "resumed" session status** — placeholder column already exists in
      `chat_sessions_table.dart`, unused.
- [ ] iOS build of the Flutter app — mostly storage-adapter work + testing, not started.

---

## 3. VISION — "JARVIS as my cognitive center" (critical assessment first)

**The honest constraints, unchanged since last pass — still true:**

1. **A local 8B–ish model cannot "replace your thinking."** It can summarize, draft, retrieve,
   classify, and ask questions. It cannot act unsupervised on your behalf. Every outbound
   action needs a **human approval step** — this is the design principle, not a stopgap.
2. **Email/Forms breaks the "never call external APIs" rule deliberately.** Decision already
   written into the plan: *external inference stays forbidden; external actions/data via
   user-authorized OAuth are allowed.* Treat fetched email like vault data.
3. **Sequencing matters** — "ping me," "learn about me," "approve actions" are the foundation.
   Nothing in §3A has been started yet (no `Profile/` folder in the vault, no proposal queue,
   no push notification path, no interview-mode chat) — this entire section is still greenfield.
4. **Job auto-apply is the least realistic piece.** The vault already has a `JOBS/` folder in
   use manually — the realistic target is "prepare the application pack, you paste/click,"
   not portal automation.

### 3A. Foundations (build in this order — none started)

- [ ] **Interview mode** — chat mode where JARVIS asks structured questions and writes answers
      into the vault via the existing generate-files dry-run pipeline. Highest value-per-effort:
      no new infra, reuses what's already built.
- [ ] **Structured `Profile/` schema** — contact, education, work history, skills, standard
      Q&A. Interview mode fills it; forms/jobs features consume it.
- [ ] **Proposal queue + approval inbox** — server-side table of AI-proposed actions
      (`draft_email`, `fill_form`, `write_file`) with `proposed/approved/rejected/done` status
      + a mobile inbox. The file-creation dry-run modal is the embryo of this pattern.
- [ ] **Push notifications** — recommend self-hosted **ntfy** over Tailscale (fits the
      no-cloud model over FCM). Wire to: sync conflicts, proposal queue items, digest ready.
- [ ] **Scheduler in jv-brain** — asyncio cron-like job table for digest/mail-poll/reindex.
      The chat-archive job (client-side today) shows the pattern; server-side is the right
      home for anything proactive.
- [ ] **Daily digest** — morning summary of vault changes, pending conflicts, pending
      proposals → push. First real "proactive JARVIS" milestone.
- [ ] **Model upgrade spike** — evaluate a tool-calling-capable small model (llama3.1:8b,
      qwen2.5:7b) for the agent loop. `JARVIS_LLM_MODEL=qwen3:4b` is already the documented
      requirement for tool calling (per CLAUDE.md) — confirm it's actually deployed, since the
      compose default may still be llama3.

### 3B. Email (read → digest → drafts, strictly in that order — not started)

- [ ] Read-only Gmail ingest → vault files (`Mail/YYYY-MM/…`), auto-indexed by existing RAG.
- [ ] Email triage digest (urgent / needs-reply / FYI) via scheduler + push.
- [ ] Draft replies via approval queue — save to Gmail Drafts, never auto-send.

### 3C. Forms & job applications (assist, not autopilot — not started)

- [ ] Form-answering service: paste questions/URL → answers from `Profile/` + vault with
      citations, delivered as a review-first proposal.
- [ ] Application pack generator: job description → tailored CV bullets + cover letter draft
      from `Profile/` + `Work/`.
- [ ] (Later) Browser-extension autofill — keeps a human clicking submit, sidesteps CAPTCHAs
      and ToS risk. Needs the web-dashboard CORS/auth work first.

---

## 4. PARKING LOT — later / probably never

- **Fully autonomous job applications** (Playwright driving portals) — brittle, ToS risk,
  CAPTCHA arms race, model misrepresentation risk. Revisit only after 3A–3C are solid, as a
  supervised extension, never a bot.
- **Voice STT/TTS** — after interview mode proves the conversation loop works.
- **Relationship/knowledge graph, smart tagging** — wait until vault size makes retrieval
  quality the actual bottleneck.
- **Web dashboard** — prerequisite for the browser extension; otherwise low priority.
- **Multi-user vaults, pgvector migration, WebSockets/webhooks, GraphQL, mTLS** — no driver.
- **Cloud LLM fallback** — breaks the privacy promise; if ever added, explicit per-request
  opt-in only, never with Secrets/Mail content.

---

*Rule of thumb: local-only for thinking, human-approved for acting, vault as the single
memory. Anything violating one of those three gets parked.*

*Next re-verification pass: re-run the greps in §0/§1 against the code before trusting this
list — it drifts fast on an actively-developed repo. Last verified 2026-09-23.*
