# 12 — Job Search Integration (ai-job-search × JARVIS)

## Scope

A research and design document. It explains what the `ai-job-search` engine (private repo at
`B:\DEV\ai-job-search`, upstream `ai-job-search-dev/ai-job-search`) is, how it works, what parts
of it fit JARVIS, and how to build a robust integration. The integration drives the engine on
the user's own **Claude subscription**, through one-tap buttons in the JARVIS app, without
weakening the security model either project has today.

## Implementation status (v1, 2026-10-27)

Built: `jobs/` (jv-jobs runner: queue, lock matrix, snapshots + integrity check + restore,
stream-json parsing, gate/resume, envelope with fenced-JSON fallback, deterministic board,
guarded artifact serving; tests use a fake `claude`), `server/app/routers/jobs.py` (admin-only
proxy), compose service behind the `jobs` profile, the LaTeX shell-escape shim, and the app's
Job Search screen (board, chips, job sheet, run screen with answers, PDF view, copy).

Deferred (each noted where it would go): deadline-first queue ordering (FIFO now), the
"auto-draft above score X" policy, Android share-to-JARVIS and share-PDF (needs
`receive_sharing_intent` / `share_plus`), publishing artifacts into the vault, "Feed profile",
`/gmail-sync`, push notifications, a Docker build of jv-jobs in CI (the TeX image is large).
Part 6's checks still have to be done against a live run (setup steps in `HOW_TO_RUN.md`).

---

## Part 1 — What ai-job-search actually is

### 1.1 One sentence

It is **not an app and has no server.** It is a Claude Code workspace: a folder of Markdown
prompts (slash commands, skills, subagents), a few deterministic Python helpers, and some Bun
CLIs that scrape job boards. Claude Code is the runtime. Every "feature" is a prompt that Claude
follows step by step, using files in the folder as its database.

That one fact drives every integration decision below. JARVIS cannot `import` it. JARVIS can
only **run Claude Code against that folder** and read the files it leaves behind.

### 1.2 Layout (what matters)

| Path | Role | Tracked in git? |
|---|---|---|
| `CLAUDE.md` | Candidate profile summary + workflow rules + verification checklist | yes (private repo) |
| `.claude/commands/*.md` | The 12 slash commands (`/apply`, `/rank`, `/outcome`, ...) | yes |
| `.claude/skills/job-application-assistant/01..09-*.md` | Profile, behaviour, writing style, scoring rubric, CV/cover templates, interview prep, form fields, web research rules | yes |
| `.claude/skills/job-scraper/` | `/scrape` orchestration + `search-queries.md` | yes |
| `.claude/skills/upskill/` | `/upskill` | yes |
| `.claude/agents/*.md` | 3 read-only subagents: `posting-fetcher`, `posting-scorer`, `application-reviewer` | yes |
| `.claude/settings.json` | Permission allow / ask / deny lists (the security boundary) | yes |
| `.agents/skills/*-search/` | Portal CLIs (`linkedin-search`, `freehire-search` enabled; 4 Danish ones disabled) | yes |
| `tools/*.py` | Deterministic helpers (state, keys, PDF checks, posting gate, polite fetch) | yes |
| `cv/main_<company>_<role>.tex/.pdf` | Tailored CVs (LaTeX, moderncv) | **ignored** |
| `cover_letters/cover_<company>_<role>.tex/.pdf` | Tailored cover letters (XeLaTeX, `cover.cls`) | **ignored** |
| `documents/` | Source material: `cv/`, `linkedin/`, `projects/`, `postings/` drop box, `applications/<company>_<role>/` archive | **ignored** |
| `job_scraper/seen_jobs.json` | Every job ever seen, with rank scores | **ignored** |
| `job_search_tracker.csv` | Applications ledger (14 columns) | **ignored** |
| `company_research/<company>.json` | 30-day research cache | **ignored** |

Personal data lives in the ignored folders and in the tracked profile files. The tracked profile
files are why the repo has to stay **private**: a public fork would publish the profile.

### 1.3 The commands (the full menu a JARVIS UI must cover)

| Command | What it does | Writes | Interactive points | Cost |
|---|---|---|---|---|
| `/setup` | Builds the profile. Path A reads `documents/`, B imports one CV, C interviews. `--section search` redoes only the search queries | profile skill files, `CLAUDE.md` | many (path choice, cross-reference conflicts, approve change sets) | high, rare |
| `/expand` | Mines GitHub, portfolio, course syllabi for skills; additive only | `01`, `02` profile files | approve additions | medium, rare |
| `/scrape` | Runs every enabled portal CLI in parallel, dedupes against history and tracker, quick fit tag, LinkedIn recruiter/peer search links, portal health check | `seen_jobs.json` (through `tools/scrape_state.py`) | "evaluate any?" at the end | low to medium |
| `/rank` | Parallel `posting-scorer` agents fetch and score up to 10 new jobs on 4 weighted dimensions + location/language vetoes; expiry sweep | `seen_jobs.json` (through `tools/rank_state.py`) | "apply to any?" at the end | medium |
| `/apply <url\|text>` | The core pipeline (see 1.4) | CV + cover `.tex/.pdf`, tracker row `drafted`, `documents/applications/<x>/job_posting.md`, research cache, profile facts | guard warning, "proceed with drafting?", stretch bullets, optional form fields | **highest** |
| `/outcome [company \| followup [N] \| stale [N]]` | Records progress/result, archives submitted docs, drafts follow-ups and thank-you notes (never sends), batch-closes stale ones | tracker, `outcome.md`, `followup_*.md` | what happened, confirm sends, confirm sweep | low |
| `/interview [company]` | Stage-specific prep pack from the archive + verified company research; optional mock interview | `interview_prep_<stage>.md`, research cache | stage/date/interviewers, mock interview turns | medium to high |
| `/upskill [url]` | Gap heatmap across tracked and ranked jobs + learning plan | `upskill/report-*.md` | none | medium |
| `/html-report` | Static HTML dashboard from the tracker | `reports/*.html` | none | low |
| `/gmail-sync` | Reads Gmail via the claude.ai Gmail connector, proposes status changes, writes after approval | tracker, `outcome.md` | approve batch | low to medium |
| `/notion-sync` | One-way push to Notion via MCP | Notion only | none | low |
| `/add-portal`, `/add-template`, `/reset` | Maintenance | skills, templates, deletes | heavy | rare |

### 1.4 How `/apply` works (the part worth protecting)

1. **Step 0, fetch.** The `posting-fetcher` agent (no Edit or Write) gets the URL. Order: portal
   CLI `detail` → WebFetch → `tools/fetch_page.py` (browser headers, `robots.txt` enforced, GET
   only) → search for the employer's own posting. The result goes through
   `tools/posting_guard.py`, which strips hidden HTML and invisible characters, quotes lines
   that look like instructions to an AI, and prints a one-line verdict.
2. **Step 1, evaluate.** An eligibility gate, a language gate, and a host check (installed
   portal, known ATS apex, or ⚠ unverified host). Then a score out of 100 (Technical 30,
   Experience 25, Behavioral 15, Career 30), an optional salary benchmark, and a verdict band.
   **It stops and asks whether to draft.**
3. **Step 2, draft.** A tailored CV (2 pages) and cover letter (1 page). Every claim is audited
   against the profile, the master CV and `CLAUDE.md`.
4. **Step 3, review.** The `application-reviewer` agent (no Edit or Write) does company research
   (from the research cache when it is fresh) and returns exact edits as JSON plus prose notes.
5. **Step 4, revise.** The drafter applies the edits.
6. **Step 5, compile and check.** Compile with lualatex and xelatex, then
   `verify_pdf.py --pages`, `verify_layout.py` (holes, orphaned headings, footer collisions),
   a visual read, and an ATS text-layer check with a keyword coverage table. Repeat until clean.
7. **Step 6, report.** Verification checklist, tracker row `drafted`, posting archived. It then
   offers optional application-form answers (`08-application-forms.md`, with measured character
   counts).

Design properties to keep:

- **Postings are untrusted data.** The gate, agents that hold the posting and cannot write, no
  following links found inside a posting.
- **No made-up claims.** Three grounding sources and a strict audit. Any fact the user confirms
  is written back to `01-candidate-profile.md` in the same turn.
- **State moves through tools, not through the model's context.** `rank_state.py` and
  `scrape_state.py` read and write the growing JSON atomically, so the model never loads the
  backlog. This is already a big token saving.
- **Nothing is ever sent.** Drafts only. The user submits.

### 1.5 Its own security model (`SECURITY.md`, `.claude/settings.json`)

- An exact allowlist of pre-approved commands (portal CLIs, `tools/*.py`, `pdftotext`). CI fails
  on any widening.
- `ask` rules on edits to permission and command files, and on `git push`.
- `deny` rules: reading `.env` / `~/.ssh` / `~/.aws`, curl/wget uploads, `ssh` / `scp` / `nc`.
- No hooks at all, on purpose.
- LaTeX is **deliberately not pre-approved**: a `lualatex:*` grant would also approve
  `-shell-escape`, which runs arbitrary commands.
- Admitted gaps: it is not a sandbox (the Bash sandbox does not run on native Windows), WebFetch
  can reach any domain, and prefix rules can be evaded.

### 1.6 State of this workspace today (observed, not judged)

- Profile is personalised (`CLAUDE.md`, `01`, `03`, `05`, `search-queries.md`).
  `01-candidate-profile.md` has an uncommitted edit.
- `07-interview-prep.md` still has the template STAR placeholders, so `/interview` will draft
  STAR answers from scratch until it is filled in.
- `seen_jobs.json`: 30 jobs (20 `new`, 10 `ranked`), all from linkedin-search and
  freehire-search.
- **No `job_search_tracker.csv` yet.** No `/apply` has finished Step 6b in this workspace.
- One hand-made application folder (`documents/applications/sertis_solutions_architect_intern/`)
  does not follow the archive schema (`coverletter.txt` + PDF instead of `cover_letter.tex` +
  `outcome.md`). `/setup` Path A and `/interview` will only partly read it.
- One tailored CV exists in `cv/` with its `.aux/.log/.out` leftovers, so Step 5e's cleanup was
  skipped or interrupted.
- Search queries name Wellfound, Internshala, Naukri, JobsDB and JobThai, but no CLI exists for
  them. They go through the slower WebSearch fallback. `/add-portal` can build some of them.
- Tooling on the host: Claude Code 2.1.310 is installed and logged in on Windows. The engine
  expects Bun, Python, and a TeX distribution with lualatex/xelatex, moderncv and fontawesome5.
  For `verify_layout.py` it also needs Poppler `pdftotext -bbox`, which Git-for-Windows' build
  lacks, so on Windows that check runs degraded.

---

## Part 2 — Compatibility with JARVIS

### 2.1 Where they agree

| Principle | JARVIS | ai-job-search | Fit |
|---|---|---|---|
| Plain files are the source of truth | vault folder | repo folder + ignored data folders | ✅ same philosophy |
| Local-first, personal | Docker on the laptop, Tailscale | runs on the user's machine | ✅ |
| AI never acts unsupervised outward | agent approvals (`approval_required`) | "drafts only, never sends" | ✅ same rule |
| Untrusted input handling | `safe_path()`, Secrets exclusion | `posting_guard`, read-only agents | ✅ complementary |
| Deterministic helpers keep state out of the model | NDJSON proxies, tool layer | `rank_state.py`, `scrape_state.py` | ✅ |

### 2.2 Where they clash (and the decision for each)

| Clash | Why it matters | Decision |
|---|---|---|
| **External inference.** JARVIS's vision says "external inference stays forbidden" (TODO §3). This feature is *defined* by sending profile + postings to Claude. | Biggest policy question. | Make it an **explicit, opt-in, admin-only exception** with a hard boundary: Claude only sees the job workspace, never the vault. The vault, RAG and `/Secrets` stay local-only. Record this as a decision in `AI-AGENT-DOCS`. |
| **Runtime.** JARVIS backend is Linux containers. The engine assumes a Claude Code CLI with LaTeX, Bun and Python on PATH. | Must decide where Claude runs. | A new container, `jv-jobs` (§3.2). |
| **Interactivity.** The commands stop and ask questions mid-workflow. JARVIS buttons are one-shot. | Headless runs cannot answer prompts. | Run each stage as a headless turn, persist the Claude session, and resume it when the user taps an answer (§3.4). |
| **Profile duplication.** JARVIS interview mode writes `Profile/*.md` in the vault. The engine's canonical profile is its skill files. | Two editable profiles drift. | One direction only. The engine's skill files stay canonical. `Profile/` feeds `documents/` as source material for `/setup` Path A (§3.8). |
| **Concurrency.** The engine assumes one human in one session. JARVIS wants parallel applications. | Shared files (`seen_jobs.json`, tracker CSV) could lose writes. | A lock matrix plus snapshots (§3.6). |
| **Model.** JARVIS agent mode needs a tool-calling Ollama model; this needs Claude. | Different engines. | Keep them separate. The local model does free jobs (profile interviews, RAG over archived applications). Claude does drafting. |

**Verdict: compatible, as long as JARVIS stays the controller and the engine stays untouched.**
JARVIS should never re-implement the prompts, because that would fork and rot. It should run
them, read their outputs, and give them a phone UI.

---

## Part 3 — The integration design

### 3.1 Principles (non-negotiable)

1. **Zero changes to the engine repo.** Every adaptation comes from the JARVIS side: CLI flags,
   an appended system prompt, environment variables, container tooling. The private repo keeps
   pulling upstream releases cleanly (SETUP.md §8). If something truly needs an engine change,
   it goes upstream as a PR, not into a local patch.
2. **Claude never sees the vault.** The job container mounts the job workspace only. JARVIS
   moves selected artifacts *out* to the vault. It never moves vault data *in*, except the
   explicit "feed profile" action, which the user sees and approves.
3. **Nothing is submitted automatically.** "Apply" in JARVIS means *prepare everything and make
   submitting take two taps*. The human clicks submit on the employer's site. This matches both
   projects' rules, keeps portal terms of service intact, and keeps the user accountable for
   what is sent.
4. **Admin device only.** The runs spend the user's personal subscription and read personal
   data. Guest devices never see or trigger anything (`require_admin`, same as backups).
   Sharing account use with other people would also break subscription terms.
5. **Deterministic first, Claude second.** Anything that is just reading, counting, linking or
   formatting is done in Python or Dart for zero tokens. Claude is called only for judgement and
   writing.

### 3.2 Topology

```
 Phone (Flutter)                Laptop (Docker, Tailscale-only)
 ┌──────────────┐  JWT/admin   ┌────────────┐  internal   ┌──────────────────────────────┐
 │ Jobs tab     │─────────────▶│  jv-api    │────────────▶│ jv-jobs  (NEW)               │
 │ run sheets   │◀── NDJSON ───│ /jobs/*    │◀── NDJSON ──│  FastAPI runner + scheduler  │
 │ PDF / copy   │              │ proxy +    │             │  claude -p  (Claude Code)    │
 │ share / open │              │ artifact   │             │  bun · python3 · TeX Live    │
 └──────────────┘              │ publisher  │             │  poppler · git · bubblewrap  │
                               └─────┬──────┘             │  /work = ai-job-search (rw)  │
                                     │ writes via vault   │  /home/claude (sessions vol) │
                                     ▼ service            └──────────────┬───────────────┘
                               vault/JOBS/Applications/…                │ HTTPS
                               (synced to phone, RAG-indexed)           ▼
                                                              api.anthropic.com (subscription)
                                                              job boards / employer sites
```

**Why a container and not the Windows host's `claude`:**

- On Linux the Claude Code Bash sandbox (bubblewrap) works. The engine's own SECURITY.md calls
  turning it on "the single strongest addition you can make", and it is impossible on native
  Windows.
- `python3`, `pdftotext -bbox` and TeX Live work as the engine expects. That removes the Windows
  `python3` Store-alias trap (#669) and the degraded `verify_layout` mode.
- `docker compose up` stays the single way to start JARVIS, and it moves to the friend's Mac
  unchanged.
- The container is the blast radius. If an injected posting ever gets a shell, it sees one
  folder, not the user's home directory, `.ssh`, or the vault.

**Cost of that choice:** the image is large (TeX Live with moderncv, fontawesome5, fontspec,
needspace and xetex/luatex is roughly 1.5 to 3 GB). Build it once and cache it. A full
`texlive-full` is not needed; collect the exact packages from one successful compile.

### 3.3 Using the Claude subscription securely

- **Authentication:** run `claude setup-token` once on the host. It needs a Pro or Max
  subscription and prints a long-lived OAuth token. Store it as `JARVIS_CLAUDE_OAUTH_TOKEN` in
  the root `.env`, which is never committed. Compose passes it **only** to `jv-jobs`, as
  `CLAUDE_CODE_OAUTH_TOKEN`. jv-api, jv-brain and the phone never hold it.
- **Never use `--bare`.** In bare mode Claude Code reads only `ANTHROPIC_API_KEY` and ignores
  OAuth, so subscription auth silently fails.
- **Health check without spending tokens:** `claude auth status` (the `auth` subcommand). Show
  "Claude: connected / expired / not set" in the app.
- **Token exposure:** in `dontAsk` mode with a strict allowlist (§3.5), a hijacked model cannot
  run `env`, `printenv`, `cat /proc/self/environ` or `curl`, because none of them are
  pre-approved and anything else is denied without prompting. WebFetch to an arbitrary URL is
  the remaining leak path. That is the gap the engine's SECURITY.md already admits, and
  instruction-level rules cover it. Rotate the token yearly or after any suspicious run, by
  running `setup-token` again and restarting `jv-jobs`.
- **Usage limits:** subscriptions have rolling usage windows. `jv-jobs` reads `usage` and
  `total_cost_usd` (a notional figure) from each run's final `result` event. It shows per-run and
  weekly totals, detects the "usage limit reached" error, and pauses the queue with the reset
  time instead of failing jobs.

### 3.4 Driving interactive commands headlessly (the core mechanism)

Every button starts or resumes **one headless Claude Code turn**:

```
claude -p "<prompt>"                       # e.g. "/apply https://…"  or the user's answer
  --output-format stream-json --verbose    # NDJSON events → progress UI (matches JARVIS convention)
  --permission-mode dontAsk                # not-allowlisted = denied, never a prompt
  --permission-prompts none                # nobody is there to answer
  --settings /opt/jv/claude-settings.json  # JARVIS layer: allowlist + sandbox (§3.5)
  --append-system-prompt-file /opt/jv/headless-contract.md
  --json-schema "$(cat /opt/jv/envelope.schema.json)"
  --model <per run kind>  --effort <per run kind>
  [--resume <session_id>]                  # continue the same conversation for the next stage
```

Run with `cwd=/work`, `SCRATCHPAD=/tmp/jv-runs/<run_id>` (the commands require
`${SCRATCHPAD:?}`, and `fetch_page.py` refuses output paths inside the repo), and a wall-clock
timeout.

**The headless contract** is a short appended system prompt owned by JARVIS. It changes how
the engine talks, not what it does:

> You are running headless for the JARVIS app. There is no terminal user. Whenever the workflow
> tells you to ask the user something, or to wait for a choice, stop at that point and return
> `status: "awaiting_input"` with the question and its options. Do not guess the answer and do
> not continue past the gate. When a stage completes, return `status: "done"` with the
> human-readable report in `report_markdown` and the files you wrote in `artifacts`. Use
> `SCRATCHPAD` from the environment for temporary files. All other rules in the workspace
> apply unchanged.

**The envelope** (`--json-schema`) gives every run a typed final result that JARVIS can parse
without guessing:

```json
{
  "status": "done | awaiting_input | failed",
  "stage": "fetch | evaluate | draft | review | compile | report | forms | outcome | …",
  "question": "Should I proceed with drafting the CV and cover letter?",
  "options": ["Draft", "Stop"],
  "report_markdown": "…",
  "artifacts": [{"kind": "cv_pdf", "path": "cv/main_acme_ml_engineer.pdf"}],
  "data": { "company": "…", "role": "…", "fit_score": 78, "verdict": "Strong Fit",
            "guard": "clean", "host_class": "ats", "deadline": "2026-11-12",
            "strengths": [], "gaps": [] }
}
```

**Resume = the gate.** When the user taps "Draft", JARVIS runs
`claude -p "Yes, proceed with drafting." --resume <session_id> …`. The conversation, including
the posting, the evaluation and the profile already read, carries over. This is what the commands
expect: `/apply` says "never re-read a file already in context". Resuming promptly also keeps the
prompt cache warm. Session files live in a named volume, so a container restart does not lose
paused runs.

**Gate map for `/apply`:**

| Gate in the command | App UI | Auto-answer policy (optional, per user setting) |
|---|---|---|
| Guard verdict ⚠ "Continue with this posting?" | Red card with the quoted findings, Continue / Drop | **Never automatic.** Always a human tap. |
| ⚠ Unverified source host | Amber banner on the evaluation | none |
| "Should I proceed with drafting?" | Evaluation card: score ring, strengths, gaps, Draft / Skip | "Auto-draft when score ≥ X and guard clean" |
| Stretch bullet "Keep, soften, or drop?" | One chip row per bullet | none (a truthfulness call) |
| Optional form fields | "Portal asks free-text questions, draft them?" | "Always draft form fields" |

### 3.5 Permissions for headless runs

The engine's own `.claude/settings.json` still loads (project settings) and still enforces its
deny list. JARVIS adds one settings file through `--settings`, kept in the JARVIS repo under
`jobs/`, never in the engine repo:

- **Allow**, in addition to the engine's list: `Read`, `Glob`, `Grep`, `Edit`, `Write`,
  `WebFetch`, `WebSearch`, `Agent`, `Skill`, `Bash(lualatex:*)`, `Bash(xelatex:*)`, and the
  exact `rm -f` build-artifact cleanup forms from `/apply` Step 5e.
- **Why lualatex/xelatex become safe to allow here:** the image puts a small shim named
  `lualatex`/`xelatex` first on PATH. It refuses `-shell-escape`, `--shell-escape`,
  `-enable-write18` and `--enable-write18`, adds `-no-shell-escape`, then runs the real binary.
  `texmf.cnf` also sets `shell_escape = f`. The risk the engine's SECURITY.md describes is
  removed at the binary, not hoped away in a prefix rule.
- **Deny**, added: `Bash(git push:*)`, `Bash(git remote:*)`, `Bash(gh:*)`, `Bash(env:*)`,
  `Bash(printenv:*)`, `Read(/home/claude/.claude/**)`. In `dontAsk` mode these are already
  denied. Listing them makes the intent explicit and survives a mode change.
- **Sandbox on:** filesystem writes limited to `/work` and `/tmp`. Network can stay broad,
  because postings live on arbitrary hosts. The value is the filesystem and process boundary.
- **Denial log:** every tool call denied by `dontAsk` appears in the stream. `jv-jobs` records
  denials per run kind, so the allowlist is tuned from evidence after the first real runs, not
  guessed now.

### 3.6 Concurrency: fast but safe

What each command writes decides what may run in parallel:

| Lock | Held by | Reason |
|---|---|---|
| `seen_jobs` (exclusive) | `/scrape`, `/rank` | Both rewrite `seen_jobs.json` atomically, but two writers would still lose one update |
| `profile` (exclusive, blocks everything) | `/setup`, `/expand`, `/reset`, `/add-*` | Every other command reads the profile; it must not change mid-draft |
| `tracker` (shared) | `/apply` (Step 6b), `/outcome`, `/gmail-sync` | Writes are short CSV edits. Claude Code's Edit/Write refuse to overwrite a file that changed since it was read, which acts as optimistic concurrency. JARVIS adds a post-run CSV check |
| none | `/interview`, `/upskill`, evaluations | Write only their own files |

- **Parallel applies:** up to N (default 2, configurable) `/apply` runs at once. Each writes its
  own `cv/main_<x>`, `cover_<x>` and `documents/applications/<x>/`. Fifteen applications become
  "evaluate 15 in parallel batches, then draft the 6 you approve", not 15 serial terminal
  sessions.
- **Queue with priority:** deadline within 7 days first (the engine's own 🔥 rule), then score.
  Runs never fail because of usage limits; they wait (§3.3).
- **Snapshots:** before any run, `jv-jobs` copies `seen_jobs.json`, `job_search_tracker.csv` and
  `git diff` of the tracked profile files into `/work/.jarvis/snapshots/<run_id>/` (a folder
  ignored via `.git/info/exclude`, so no engine file changes). After the run it validates the CSV
  (header, column count, no row lost) and the JSON. On failure it flags the run and offers a
  one-tap restore. These are tiny files, so this is cheap insurance for irreplaceable history.
- **Profile changes are reviewable:** the profile files are tracked in git, so after any run
  JARVIS shows `git diff` of `.claude/skills/**` and `CLAUDE.md` as "Profile updated: +2 facts".
  The user can keep or revert. This makes `/apply`'s standing write-back rule visible instead of
  silent.

### 3.7 Spending fewer tokens

| Technique | Saving |
|---|---|
| **Gate early.** Evaluation is one cheap stage. Drafting, reviewing and the LaTeX loop only run after a "Draft" tap. | The most expensive 80% runs only on jobs the user wants |
| **`/rank` before `/apply`.** Triage scoring already runs in parallel subagents and keeps state out of context. | Tens of postings for the price of a few applies |
| **Model and effort per run kind.** Sonnet for `/scrape`, `/rank`, `/outcome`, follow-ups; Opus for the `/apply` draft and `/interview`. All configurable in Settings. | Large on the frequent, mechanical runs |
| **Resume, don't restart.** Each `/apply` stage resumes the same session. | Profile and posting are not re-read; prompt cache stays warm |
| **Replace view commands with native screens.** The app reads `seen_jobs.json`, the tracker and the archive directly. | `/html-report` and `/notion-sync` become unnecessary (0 tokens) |
| **Deterministic extras.** LinkedIn recruiter/peer search links (a pure function, same as `/scrape` Step 4.5), PDF text extraction for "copy cover letter", character counts, deadline badges, days-quiet, follow-up-due lists — all in Python/Dart. | 0 tokens |
| **Local model for free work.** JARVIS interview mode (qwen3 via Ollama) gathers profile facts; RAG answers "what did I tell Sertis?" from archived applications. | Claude is never used for recall |
| **Never stream transcripts to the phone.** Only summarised progress events plus the final envelope. | Bandwidth, not tokens, but keeps the phone fast |

### 3.8 Data flow between the vault and the engine

```
vault/Profile/*.md ──(user taps "Feed profile")──▶ /work/documents/cv/notes/JARVIS-<topic>.md
     (local LLM interview mode)                         └─▶ /setup Path A (asks before merging)

/work/cv/*.pdf, cover_letters/*.pdf,  ──(after a run, by jv-api)──▶ vault/JOBS/Applications/<company>_<role>/
documents/applications/<x>/*,                                       cv.pdf, cover_letter.pdf, cover_letter.txt,
form-field .txt, interview_prep_*.md                                 form_answers.txt, posting.md, prep_*.md
```

- **Into the engine:** only on demand, only `Profile/` (never `Secrets/`, never `system/`), and
  always through `/setup`'s own read-before-write merge, which asks on every conflict.
- **Out to the vault:** jv-api writes through the vault service, so version tracking and sync
  see the files. The phone gets them offline, and the brain indexes them for RAG.
  `Applications/` holds application material, not secrets.
- **The canonical profile stays in the engine.** JARVIS does not edit the skill files itself.

### 3.9 API surface (all admin-only, proxied by jv-api to jv-jobs)

| Method | Path | Purpose |
|---|---|---|
| GET | `/jobs/status` | Claude auth state, queue, running runs, usage this week |
| GET | `/jobs/board` | Deterministic merge of `seen_jobs.json` + tracker + archive presence + run state. Lists by stage |
| POST | `/jobs/runs` | `{kind, args}` → run id. Kinds: `scrape`, `rank`, `evaluate`, `apply`, `forms`, `outcome`, `followup`, `stale`, `interview`, `upskill`, `expand`, `setup`, `portal_health`, `freeform` |
| GET | `/jobs/runs/{id}/events` | NDJSON progress stream (buffered to newline on Flutter, like chat) |
| POST | `/jobs/runs/{id}/answer` | `{option \| text}` → resume the session |
| POST | `/jobs/runs/{id}/cancel` | Stop the subprocess; the session can still be resumed later |
| POST | `/jobs/postings` | `{url}` or `{company, title, text}`. Text becomes `documents/postings/<Company> - <Title>.txt`, the engine's documented drop box |
| GET | `/jobs/artifacts/{app}/{file}` | Stream a PDF or txt. The path is resolved and checked against a fixed set of folders (`cv/`, `cover_letters/`, `documents/applications/`), the same pattern as `safe_path()` |
| POST | `/jobs/runs/{id}/restore` | Restore that run's snapshot |

jv-api ↔ jv-jobs uses a shared secret header on the internal network. `jv-jobs` publishes no
host port. Rate limits: `/jobs/runs` sits in a strict group, the same treatment as
`/backup/export`.

### 3.10 The app experience

**Jobs tab, with a sticky action row of chips:**
`🔎 Scrape` · `🏆 Rank` · `➕ Add posting` · `📬 Follow-ups (n)` · `🎯 Upskill` · `⋯`

**Board sections** (from `/jobs/board`, no tokens):
`New` → `Shortlist` → `Preparing` → `Ready to submit` → `Applied` → `Interviewing` → `Closed`

**Job card:** title, company, location, score ring and verdict, 🔥 deadline, ⚠ guard / host /
language flags, the first strength and the first gap.
Buttons by state:

| State | Buttons |
|---|---|
| New / Shortlist | **Evaluate** · Open posting ↗ · Recruiters ↗ · Dismiss |
| Evaluated | **Draft application** · Skip · Open posting ↗ |
| Preparing | live progress steps (Fetching → Evaluating → Drafting → Reviewing → Compiling → Checking ATS) · Cancel |
| Ready to submit | **View CV** · **View cover letter** · **Copy cover letter** · **Form answers** · **Open apply page ↗** · **Share PDFs** · **I submitted it** |
| Applied | Follow-up draft · Record outcome · Interview prep · Withdraw |
| Interviewing | **Prep pack** · Mock interview (chat) · Thank-you note · Record stage |

**Application detail screen tabs:** Posting · Evaluation · CV · Cover letter · Form answers ·
Prep · Timeline.

- PDFs use the existing `flutter_pdfview`.
- "Copy" uses the Flutter clipboard. Text comes from the PDF text layer through
  `verify_pdf.py --dump-text`, which is exactly what an ATS sees. Form answers come one field at
  a time, each with its own copy button and measured character count.
- "Open ↗" uses the existing `url_launcher`. **Only two kinds of link are ever opened:** the
  user-supplied posting URL, and the apply URL returned by the portal CLI's structured `detail`
  output. Links found inside posting text are never made clickable. This mirrors the engine's
  rule.
- "Share PDFs" opens the Android share sheet to attach files in a portal or email app. This
  needs the `share_plus` package (one new dependency).
- **Add posting** accepts a URL, pasted text, or the Android **share sheet**: share a LinkedIn
  or careers page from the browser to JARVIS, and it lands as a new posting ready to evaluate.
  This needs an intent filter.
- **Run sheet:** a bottom sheet with live steps and, when `awaiting_input`, the question card
  with option buttons and a text field for a free answer.
- **Ask Claude (workspace):** a free-form box tied to a resumable session, for anything that has
  no button ("tighten paragraph 2", "make a Thailand-address variant").
- **Notifications:** none in v1; the board refreshes on app resume (the existing background-sync
  hooks). When ntfy push lands (TODO §3A), "draft ready" and "needs your answer" become pushes.

**Every command reachable without typing:**

| Command | Where |
|---|---|
| `/scrape` (+ `broad`, focus, `health`) | Scrape chip; long-press for options |
| `/rank` (+ focus, `--all`, `--limit`, `--top`) | Rank chip; long-press for options |
| `/apply` | Evaluate, then Draft on any card or added posting |
| `08` form fields | Form answers tab, "Draft form answers" |
| `/outcome` (record, `followup`, `stale`) | "I submitted it", Record outcome, Follow-ups chip, Closed → "Clean up stale" |
| `/interview` + mock | Interviewing card → Prep pack / Mock interview |
| `/upskill [url]` | Upskill chip; "Gaps for this job" in the card menu |
| `/expand`, `/setup --section search`, `/setup` Path A | Settings → Job profile |
| `/gmail-sync` | Settings → Job profile → "Check Gmail" (phase 4, see §5) |
| `/add-portal` | Settings → Job sources → "Add a job board" (guided chat) |
| `/html-report`, `/notion-sync` | Replaced by the native board; not exposed |
| `/add-template`, `/reset` | Desktop only, on purpose (rare and destructive) |

---

## Part 4 — Threat model of the combined system

| Threat | Control |
|---|---|
| Malicious posting steers the agent (prompt injection) | Engine: `posting_guard`, read-only fetch and review agents, never follow links inside postings. JARVIS: `dontAsk` + allowlist (nothing outside it runs), sandbox, container blast radius, guard ⚠ always needs a human tap. |
| Steered agent reads JARVIS secrets | The vault is not mounted in `jv-jobs`. The OAuth token is only in `jv-jobs`, and env-dumping commands are denied. |
| Steered agent leaks profile data to a URL | Remaining risk (WebFetch to any domain, admitted upstream). Mitigated by the instruction rules, and visible because every run's tool calls are logged and can be read in the run timeline. |
| LaTeX shell escape | Shim plus `texmf.cnf` in the image; the risk is gone, not just discouraged. |
| Guest device triggers runs or reads applications | `require_admin` on every `/jobs/*` route; the board is not exposed to guests. |
| Lost writes or corrupted tracker | Lock matrix, Claude Code read-before-write staleness check, post-run validation, snapshots with one-tap restore. |
| Silent profile drift | Git diff of profile files after every run, keep or revert. |
| Fabricated claims sent to employers | The engine's three-source grounding audit, verification checklist and stretch-bullet gate, plus the human submit step. |
| Supply chain (portal CLIs, `bun install`) | The engine's CI guards stay intact (no engine changes). The image installs with `--ignore-scripts`. New portals are added by hand after reading the code, as the engine README says. |
| Account terms | Personal use of the user's own subscription through Claude Code, one user, admin device only. No automated submission to job portals. LinkedIn volume kept low, as that skill already warns. |

---

## Part 5 — Build plan (incremental, each phase usable on its own)

**Phase 0 — prove the engine headless (1 evening, no JARVIS code)**
Build the `jv-jobs` image (Claude Code, Bun, Python with pypdf, Poppler, TeX packages, shims).
Inside it, by hand: `claude auth status`, then `claude -p "/rank" …` and
`claude -p "/apply <url>" …` with the contract and envelope, then `--resume` through the drafting
gate. *Exit criteria:* a 2-page CV and 1-page cover letter compiled in the container, ATS check
not degraded, the envelope parsed, and a list of denied tool calls to tune.

**Phase 1 — runner service**
`jv-jobs` FastAPI: run queue, locks, snapshots and validation, NDJSON event store, envelope
parsing, artifact streaming, usage tracking. jv-api `/jobs/*` proxy with `require_admin`.
*Tests:* lock matrix, a snapshot round trip, a corrupted-CSV restore, path checks on artifact
streaming, a guest getting 403 — all using a fake `claude` binary that replays recorded NDJSON,
so CI never spends tokens.

**Phase 2 — app: board + apply flow**
Jobs tab, board, job card, Evaluate → gate → Draft, run sheet, PDF view, copy, open and share,
"I submitted it". Add posting by URL, text and the share intent.

**Phase 3 — the rest of the menu**
Rank and scrape options, form answers, follow-ups and stale sweep, interview prep and mock chat,
upskill, Ask Claude, vault publishing of artifacts, "Feed profile" into `/setup` Path A.

**Phase 4 — extras that need checking first**
`/gmail-sync` and claude.ai connectors in a headless container (not verified; they may need an
interactive OAuth that headless cannot complete). ntfy push for "needs your answer". New portal
CLIs for Wellfound, Internshala, Naukri and JobsDB via `/add-portal` (desktop session).

---

## Part 6 — Not verified yet (check in Phase 0, before writing code)

- The exact Bash command strings the engine issues under `dontAsk`. The allowlist above is a
  starting point, to be tuned from the denial log.
- That `--json-schema` and slash-command prompts work well together for long multi-step
  commands. If the model fights the schema, fall back to one fenced JSON envelope block at the
  end of the text output.
- That the Claude Code sandbox runs in this container (it needs bubblewrap and may need extra
  Docker capabilities).
- How a usage-limit error looks in the stream-json output, so the queue can pause on it.
- The minimal TeX package set for both templates.
- Whether claude.ai connectors (Gmail) are reachable from a container session authenticated with
  `setup-token`.

## Part 7 — Housekeeping in the engine workspace (do by hand; not done here)

- Commit or revert the pending edit to `01-candidate-profile.md`.
- Fill in `07-interview-prep.md` STAR examples (`/setup` Path A or JARVIS interview mode, then
  feed the result in).
- Rebuild the Sertis folder to the archive schema with `/outcome sertis` so it counts for
  calibration.
- Delete the leftover `.aux/.log/.out` in `cv/`.
- Keep the `origin` remote private (`/setup` Step 0 already warns about a public one). The
  `public-fork` remote must never receive profile commits.
