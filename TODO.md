# JARVIS — TODO

> Grounded in `DOCUMENTATION/OFFICIAL-DOCS/` (specs), `AI-AGENT-DOCS/` (decisions), and the
> actual code as of 2026-09-24. Horizons: **Now** (gaps against our own specs), **Next**
> (decided features not yet built), **Vision** (cognitive-assistant direction), **Parking lot**.
> Items marked 🙋 need something from Aditya (hardware, an account, or a decision).
>
> **2026-11-05:** the active work list is `DOCUMENTATION/AI-AGENT-DOCS/7-upgrade-plan.md`
> (audit and OpenJarvis comparison in `6-openjarvis-notes.md`). It supersedes §3A's proposal
> queue, scheduler, digest and model spike items below.

---

## 0. Done — verified in code (don't redo)

**Before 2026-09-23:** jv-api bound to `127.0.0.1`; non-root containers; debug router and
`extra_hosts` removed; `langchain-text-splitters` + stdlib `sqlite3` (no SQLAlchemy); token
auto-refresh + silent reconnect; QR guest registration; CI (server + brain pytest, flutter
analyze/test, docker build); chat auto-archive.

**2026-09-24 pass:**
- **Security holes closed** (found while auditing; `server/tests/test_guest_privilege_guards.py`):
  - `system/devices.json` (every device's reconnect secret) was readable/writable by **any**
    device via `/files`, `/download`, `/sync` → a QR guest could reconnect as the admin. `system/`
    is now unreachable through every endpoint and hidden from listings.
  - Guest Secrets checks were bypassable with `secrets/…`, `./Secrets/…`, or bare `Secrets`
    (e.g. `DELETE /files/Secrets`). One case-insensitive `is_secrets_path()` now guards all routes.
  - Any device could `/files/reset` (wipe the vault), `/auth/revoke` the admin, or mint new
    devices. Now admin-only (`require_admin`); a device can still revoke itself.
  - Brain: `extract-pdf` path traversal, and RAG **attachments could pull `Secrets/` into the LLM
    prompt**. Both now go through `tools.safe_path()`, which is also case-insensitive now.
  - Reconnect secret compared in constant time.
- **Regression fixed:** jv-brain's vault mount was `:ro`, but agent create/edit/move/delete and the
  `Memory/global_index.md` writer write directly, so all of them failed. Mount is read-write again.
- **Rate limiting** (`services/rate_limit.py`) — 429 + `Retry-After`; limits, and the deliberate
  deviations from the spec table, are documented in 06-Security-Model.
- **Audit log** → `<vault>/.system/audit.log`, JSON lines, rotated 5×10 MB, survives `/files/reset`.
- **Backups** — daily automatic zip + `POST /backup/export` / list / download (admin), SQLite-safe,
  keeps newest 10, into `JARVIS_BACKUP_HOST_PATH`. Settings → **Vault Backups** on admin devices.
  Restore steps in `HOW_TO_RUN.md`.
- **Device revoke UI** — long-press a device in Device Management (admin) to revoke a lost phone.
- **`index-status` stuck `pending_files: 1`** — root cause: cumulative counters vs a per-run
  total, and zero-chunk files (empty notes) counted nowhere. Also stopped a wasted LLM summary
  call per empty file on every reindex.
- **Tombstones — resolved as not needed:** delete propagation already works both ways; the only
  "resurrection" is edit-vs-delete, where keeping the edit is the safe choice. Written up in
  `5-version-control.md`.
- **Legacy `.jvs`** — the one remaining file in `Secrets/` is current `JVSP` format (recoverable).
- **Stale tests fixed** — CI was red: 6 server sync tests (inputs from before `has_local_changes`,
  plus the trio backend) and the Flutter chat test (still mocked the pre-agent API). All suites
  green: server 251, brain, Flutter 250.
- **Docs de-drifted** — 02 (real layout + endpoints, no phantom `/secrets` router), 06 (as-built
  auth/limits/audit), walkthrough.md marked historical. `run_tests_for_user.py` deleted.
- **Background sync** — on app resume, on server reconnect / cold start, and every 10 min while
  open; throttled, quiet on failure, and guarded so it never overlaps a manual sync.
- **Biometric App Lock** — Settings → App Lock (fingerprint or device PIN, `local_auth`). It
  covers the app instead of replacing it, so unsaved editor text survives; re-locks after 60 s away.
- **Interview mode + `Profile/` schema** — chat ⋮ → "Interview me": JARVIS asks one question at a
  time and records answers in `Profile/{about,work,education,skills,goals,preferences,people,
  standard-answers}.md`. The schema lives in `INTERVIEW_PROMPT` (`brain/app/routers/agent.py`).
  Every write still goes through the normal approval prompt.

---

## 1. NOW — remaining

- [ ] 🙋 **Live smoke test of this pass** — Docker was off, so everything above is verified by
      tests, not a running stack. `docker compose up -d --build`, then check: agent create_file
      works (rw mount), `/ask/index-status` shows `pending_files: 0`, a backup lands in `./backups`.
- [ ] 🙋 **On-device check** of App Lock, background sync, and revoke on the Motorola.
- [ ] 🙋 **Migration test** — clean machine (the friend's Mac?) → restore a backup zip → all works.
      Write the result into `HOW_TO_RUN.md`.
- [ ] 🙋 **Performance sanity** — file ops < 500 ms, AI answer < 15 s (spec 05). Needs the stack up.
- [ ] **CONTRIBUTING.md + issue templates** — only if outside contributions are wanted.

---

- [ ] 🙋 **Job search first live run** — v1 is built (spec 12). Needs `claude setup-token` →
      `jobs.env`, `JARVIS_JOBS_WORKSPACE`, `docker compose --profile jobs up -d --build`, then one
      `/rank` and one `/apply` from the phone. Tune the allowlist from the run's "Blocked" lines,
      and work through spec 12 Part 6.

## 2. NEXT — decided in the docs, not yet started

- [ ] **Per-file version history + diff preview** in the conflict screen. Needs the server to
      keep prior versions (today it keeps only `prev_hash`) and touches the LOCKED conflict screens.
- [ ] **Multi-model config in Settings** — brain endpoint to list Ollama models + switch at
      runtime. Do it together with the §3A model spike.
- [ ] **Chat "resumed" session status** — placeholder column in `chat_sessions_table.dart` with no
      concrete use yet. Drop the column or define the behavior.
- [ ] 🙋 **iOS + macOS build of the Flutter app** — `mobile/` currently only has an `android/`
      platform folder; `ios/` and `macos/` need to be generated (`flutter create .`) and wired
      up (signing, storage-adapter work, platform-specific auth/QR flows) + testing. Needs a Mac.

---

## 3. VISION — "JARVIS as my cognitive center" (critical assessment first)

**The honest constraints — still true:**

1. **A local 8B–ish model cannot "replace your thinking."** It can summarize, draft, retrieve,
   classify, and ask questions. It cannot act unsupervised on your behalf. Every outbound
   action needs a **human approval step** — this is the design principle, not a stopgap.
2. **Email/Forms breaks the "never call external APIs" rule deliberately.** Decision already
   written into the plan: *external inference stays forbidden; external actions/data via
   user-authorized OAuth are allowed.* Treat fetched email like vault data.
3. **Sequencing matters** — "ping me," "learn about me," "approve actions" are the foundation.
   "Learn about me" (interview mode) now exists; "ping me" and "approve actions" are next.
4. **Job auto-apply is the least realistic piece.** The vault already has a `JOBS/` folder in
   use manually — the realistic target is "prepare the application pack, you paste/click,"
   not portal automation.

### 3A. Foundations (in order)

- [x] **Interview mode** — see §0.
- [x] **Structured `Profile/` schema** — see §0.
- [ ] 🙋 **Push notifications via self-hosted ntfy** — needs the ntfy app on the phone and a
      decision on how the phone reaches it (a second `tailscale serve` port). Then wire to: sync
      conflicts, proposal queue items, digest ready.
- [x] **Proposal queue + approval inbox** (built 2026-11-05: `brain/app/services/proposals.py`, phone Inbox tile; needs an on-device check) — server-side table of AI-proposed actions
      (`draft_email`, `fill_form`, `write_file`) with `proposed/approved/rejected/done` status
      + a mobile inbox. The agent's `approval_required` flow is the in-chat version of this.
- [x] **Scheduler in jv-brain** (built 2026-11-05: `brain/app/services/scheduler.py`) — asyncio job loop for digest / mail-poll / reindex. jv-api's
      hourly backup loop (`server/app/main.py`) is the pattern to copy.
- [x] **Daily digest** (built 2026-11-05: writes `Digest/YYYY-MM-DD.md`; set `JARVIS_TIMEZONE`; push via ntfy still open) — morning summary of vault changes, pending conflicts, pending
      proposals → push. First real "proactive JARVIS" milestone. Needs push first.
- [ ] 🙋 **Model upgrade spike** — compare `qwen3:4b` (the `.env.template` default) against
      `qwen2.5:7b` / `llama3.1:8b` on the agent loop, especially interview mode (it needs one-
      question discipline and reliable edit_file calls). Needs the stack running on the RTX 3050.

### 3B. Email (read → digest → drafts, strictly in that order — spec written: `OFFICIAL-DOCS/13-Mail-Ingest.md`; needs 🙋 Gmail API + consent screen set to "In production")

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

### 3D. Career copilot — a personal "Tsenta" inside JARVIS (requested 2026-11-05, not started)

> Aditya's brief: a built-in "apply for jobs" flow like Tsenta (paid), but personal, and as the
> first of many **life-stage copilots** (career now; later e.g. housing, visas, taxes, health
> admin): JARVIS as a second brain that does the repetitive work (applying to many jobs) while
> Aditya decides. **Don't clone Tsenta**; design so the same pieces (profile, matching, swipe
> queue, tracker, email triage, approval) serve other life stages too. Pick up after the current
> upgrade-plan run; design discussion first, then a spec in `OFFICIAL-DOCS/`.

**One-time setup (onboarding wizard on the phone):**
1. **Upload resume** → "found X roles, Y schools, Z skills" (extraction must be reliable: local
   model + validation, like `profile_import` / form-answer evidence checks).
2. **Review details** (all editable):
   - A. Contact: name, phone, email.
   - B. Where you live: street, city, ZIP/postal, country (dropdown), state/province (dropdown
     filtered by country).
   - C. Resume:
     - Work experience: list of roles, most recent first; tap to open, edit, save.
     - Education: degree, major, university, GPA, honors, start, end, currently enrolled?
       (+ Add education).
     - Skills by category, as removable chips + a text box to add: Languages, Frontend & Mobile,
       Backend & Data, AI / ML, Infrastructure & Tooling (+ Add category).
3. **Work authorization** (yes/no): authorized to work in <the job's country>? need visa
   sponsorship now or later? security clearance? ties to government/public office? worked for
   this company before? (the last two are per-company; store a default, ask per application).
4. **Work preferences** (checkboxes): open to in-person/onsite; willing to relocate; can start
   right away; reliable transportation; need workplace accommodations.
5. **Workday password**: one saved password reused to create Workday accounts. **Goes in
   `Secrets/` (encrypted `.jvs`), never into any AI prompt**; only the autofill step reads it.
6. "Finding your matches… you're all set" → start swiping.

**Profile dashboard** ("91% complete, fill in the rest"): personal info (summary, basics filled
from resume, links: GitHub/LinkedIn/website), work preferences (expected salary), work auth
(visa type), background / DEI / additional info.

**Home tabs:**
- **Jobs**: swipe right/left on job cards.
- **Applications**: needs you · in progress · submitted · failed · skipped.
- **Auto**: applies automatically (rules-based; see open questions).
- **Inbox**: all · verification · rejection · interview · recruiter · assessment · reminder ·
  offer · applied (job-related email, classified).
- **Profile**: resume (experience, education, skills, projects, certificates) · personal ·
  files · answers.

**What already exists to build on:** `Profile/*.md` + `profile_import` (resume facts → Inbox
proposals); desktop form filling (`/ask/ai/form-answers`, `Profile/form-memory.json`,
per-form `answers.md`); jv-jobs job search (`jobs/`, desktop job carousel); proposal Inbox +
approvals; scheduler + digest; Gmail ingest spec (`OFFICIAL-DOCS/13-Mail-Ingest.md`) for the
job-email Inbox; `Secrets/` crypto for the portal password.

**Open questions to settle in the design pass:**
- Structured profile store: keep `Profile/*.md` as the source of truth and add a typed
  `Profile/profile.json` (resume sections, auth answers, preferences) the wizard edits? Plain
  files stay the truth either way.
- Who submits: the desktop companion drives the browser with the user watching (fits the
  "assist, not autopilot" rule and handles CAPTCHAs/Workday accounts), vs headless. "Auto" tab =
  pre-approved rules (role, location, salary floor, company blocklist) with a per-day cap and a
  review log, never silent.
- Privacy: matching and answering stay local (vault data). Job search runs through Claude today
  (jv-jobs, no vault access); decide whether the resume itself may go to Claude for matching,
  or matching runs locally on job descriptions pulled by jv-jobs.
- Honesty guard: answers only from approved Profile facts; anything unknown goes to "needs you",
  never invented (the evidence rule from form-answers).
- Generalize: "life-stage copilot" = profile section + intake wizard + item queue + tracker +
  mail categories. Career is the first instance; don't hard-code it.

---

## 4. PARKING LOT — later / probably never

- **Fully autonomous job applications** (Playwright driving portals) — brittle, ToS risk,
  CAPTCHA arms race, model misrepresentation risk. Revisit only after 3A–3C are solid, as a
  supervised extension, never a bot. (§3D revisits this as a supervised career copilot.)
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
list — it drifts fast on an actively-developed repo. Last verified 2026-09-24.*
