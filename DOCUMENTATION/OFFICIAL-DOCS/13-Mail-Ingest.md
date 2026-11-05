# 13. Mail ingest (read-only Gmail into the vault)

Status: **spec, not built.** Written 2026-11-05 as the gate for TODO §3B. Build it only after the
prerequisites in §6 are done by the owner.

## 1. Goal and non-goals

Goal: the owner's mail becomes plain Markdown files in the vault, so the existing search, chat, digest
and phone sync work on it, and nothing else about JARVIS needs to learn what "mail" is.

Non-goals (all decided in TODO §3B, restated because they are the dangerous parts):
- **Read-only.** Scope is `gmail.readonly`. No send, no delete, no labelling, no drafts in this spec
  (drafts come later through the Inbox, as proposals).
- **Mail never goes to Claude.** It is vault data (root CLAUDE.md privacy split). Only the local brain reads it.
- No attachments (names are listed in the file, content is not fetched).

## 2. Architecture

```
Gmail API  --(desktop, OAuth)-->  Jarvis.Core/Mail  --(JvApiVault.WriteFileAsync)-->  jv-api /files  -->  vault  Mail/YYYY-MM/*.md
                                                                                                           |
                                                          existing indexer + sync + digest <----------------+
```

- Runs in the **desktop app**, which already has Google sign-in (`Jarvis.Core/Google`: PKCE, loopback
  redirect, offline access, tokens in protected storage) and an authenticated vault client
  (`JvApiVault`). The server and brain gain nothing, and no mail credential ever enters Docker.
- A `MailSync` job on the existing 30 s timer pattern (`ProactiveTimer`), but its own interval: every
  15 min while the PC is awake and not paused, plus once at start-up. Nothing while paused or in private mode.
- Scope: add `https://www.googleapis.com/auth/gmail.readonly` to `GoogleAccounts`. The existing calendar
  tokens lack it, so the user must **sign in again once** (the `prompt=consent` already forces the screen).

## 3. What is fetched

- First run: the last **60 days** of mail in `INBOX` plus `SENT` (configurable `Mail:Days`, max 365).
  Skip `SPAM`, `TRASH`, and the `CATEGORY_PROMOTIONS` / `CATEGORY_SOCIAL` tabs by default (`Mail:IncludePromotions`).
- Later runs: Gmail `history.list` from the stored `historyId` (added messages only). If Gmail answers 404
  (history expired, roughly a week of inactivity), do a fresh 60-day listing; dedupe (§4) makes that cheap.
- Limits per run: at most 200 messages and 5 MB of bodies, so a first sync trickles in over several runs
  instead of hammering the API; a progress line goes to the notch "Mail: 140 of 900" only when the user opens it.
- API budget: `messages.list` + `messages.get?format=full`. Back off on 429/403 `rateLimitExceeded` with
  the `Retry-After` / exponential delay; never retry a 401 more than once (refresh the token, then stop and
  show "Sign in to Google again").

## 4. File format and identity

Path: `Mail/YYYY-MM/<YYYY-MM-DD>-<slug>-<msgid8>.md`, where `slug` is the subject lowercased, ASCII,
max 40 chars, and `msgid8` the first 8 chars of the Gmail message id. The Gmail message id (not the path)
is the identity; the id is also in the frontmatter so a renamed file is still recognised.

```markdown
---
source: gmail
message_id: 18c4f...            # Gmail id, the dedupe key
thread_id: 18c4e...
from: "Raj Malhotra <raj@acme.com>"
to: ["aditya@example.com"]
date: 2026-11-04T09:12:00+05:30
subject: "Interview next week"
labels: [INBOX, IMPORTANT]
attachments: ["offer.pdf (212 KB)"]
---
<plain-text body>
```

(This mirrors OpenJarvis's `Document` schema: doc_id, author, participants, timestamp, thread_id.)

Body rules:
- Prefer the `text/plain` part. If only HTML exists, convert to text (drop `<script>/<style>`, keep
  links as `text (url)`); never write raw HTML into the vault.
- Strip quoted replies (lines starting with `>` and everything after "On ... wrote:" / "From:" blocks) and
  signatures after `-- `. Keep the first 20,000 characters, then `[truncated]`.
- Decode every MIME encoding to UTF-8; a part that fails to decode is skipped, not written as mojibake.
- Total file at most 200 KB (the vault's own upload cap leaves room).

Dedupe and idempotence: before writing, list `Mail/YYYY-MM/` through the files API and skip a message whose
`message_id` already exists (cache the id set in `%LOCALAPPDATA%\Jarvis\mail-state.json` next to the `historyId`).
Never overwrite or delete: if the user edited a mail file, their edit stays.

## 5. Safety and edge cases

- **Prompt injection.** A stranger wrote this text. It is wrapped in `<vault_file>` tags and flagged when it
  reads like instructions (`brain/app/services/vault_text.py`, built 2026-11-05), and every action the
  agent could take on it still needs approval. Do not build mail ingest before that exists (it does).
- **Secrets in mail** (reset links, one-time codes, card numbers): skip a message whose subject or first
  200 characters match `OutboundScanner.Scan` kinds `password`/`api-key`/`private-key`, or an OTP pattern
  (`\b\d{4,8}\b` within 40 chars of "code", "OTP", "verification"); log the Gmail id only.
- **Privacy of the index.** Mail is searchable by the chat and by the form filler. If the owner wants a
  sender excluded, `Mail:BlockSenders` (list of addresses or domains) is checked before anything is written.
- **Daily digest** already skips nothing under `Mail/`, so new mail appears in "Changed yesterday". That is
  intended; a `Mail:InDigest` flag (default true) can turn it off by adding `Mail` to `digest.SKIP_TOP`.
- **Volume.** At about 20 KB average and 100 messages a day the vault grows ~60 MB a month; the indexer
  makes one model summary call per new file, which is the real cost (about 30 s each on the current GPU).
  Hence the 200 messages per run cap and defaulting to Inbox + Sent only. Consider skipping the summary
  for `Mail/` (a short `subject + from` line is enough for the global index) as the first optimisation.
- **Sync conflicts.** Files are only ever created, so the version-based sync never sees a conflict.
- **Multiple Google accounts:** `GoogleAccounts` supports several; each gets its own `Mail/<account>/` root
  only if more than one is connected, otherwise `Mail/`.
- **Revoking:** Settings → Google "Disconnect" deletes the tokens and `mail-state.json`; existing mail files
  stay in the vault (they are the user's data now) and a toggle "also delete synced mail" is out of scope.

## 6. Prerequisites the owner must do (cannot be automated)

1. In the Google Cloud project used for Jarvis's sign-in: **enable the Gmail API** and add the
   `gmail.readonly` scope to the OAuth consent screen.
2. **Publishing status.** In "Testing" status Google expires refresh tokens after **7 days**, which would
   silently stop mail (and calendar) sync weekly. For a single-owner app, set the consent screen to
   **"In production"** (it stays unverified, which only shows a warning at sign-in), or Internal if the
   account is a Workspace one. `gmail.readonly` is a restricted scope, so verification is not realistic;
   unverified apps are limited to 100 users, which is irrelevant here.
3. Sign in to Google again from Jarvis once to grant the new scope.

## 7. Build order (when the prerequisites are done)

1. `GmailClient` (list, history, get; token refresh via `IGoogleAccounts`) with a fake HTTP handler for tests.
2. `MailRenderer`: MIME to the Markdown above. Test with fixtures: plain, HTML-only, multipart/alternative,
   non-UTF-8 charset, huge body, quoted reply chain, signature, attachment-only message.
3. `MailSync`: state file, dedupe, limits, backoff, skip rules. Tests with a fake Gmail and a fake `IVault`.
4. Settings page "Mail" (connect, interval, days, block list, in-digest) and a tray status line.
5. LIVE: sync a real week, check that `Mail/` appears on the phone, that search finds a known sender, that a
   re-run writes nothing, and that the Claude privacy log shows **no** mail text ever sent.
