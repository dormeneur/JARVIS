# How to Run JARVIS

The backend (API + AI + vector DB) runs in Docker; one command starts it all. The AI models run in
**Ollama** installed on your machine, which uses your GPU on its own.
Installed on your machine: **Docker Desktop**, **Ollama**, **Flutter**, **Git**.

---

## 1. Install (one time)

- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Ollama](https://ollama.com/download)
- [Flutter SDK](https://docs.flutter.dev/get-started/install) — then run `flutter doctor` and fix anything red
- Android Studio (for an emulator) or just plug in an Android phone

## 2. Set up the project (one time)

**Mac (Terminal):**
```bash
git clone <repo-url> && cd JARVIS
mkdir -p ~/JARVIS-vault
cp .env.template .env
```

**Windows (PowerShell):**
```powershell
git clone <repo-url>; cd JARVIS
mkdir C:\JARVIS-vault
Copy-Item .env.template .env
```

Open `.env` and change **two lines**:
```env
JARVIS_HOST_PATH=/Users/<you>/JARVIS-vault   # Mac   (Windows: C:/JARVIS-vault)
JARVIS_JWT_SECRET=<paste any long random string>
```

## 3. Start the backend

```bash
docker compose up -d --build
```

Download the two AI models into Ollama (one time; `qwen3:8b` instead if your GPU has 6 GB or more,
and set `JARVIS_LLM_MODEL` to match):
```bash
ollama pull qwen3:4b
ollama pull nomic-embed-text
```

Check it works:
```bash
curl http://localhost:8000/health
# → {"status":"ok"}
```

Get the **setup secret** (you need it once, to register your first device in the app):
```bash
docker logs jv-api        # look for the "JARVIS SETUP SECRET" box near the top
```

## 4. Run the mobile app

```bash
cd mobile
flutter pub get
dart run build_runner build --delete-conflicting-outputs
flutter run
```

When the app asks for the server URL:

| You are running the app on | Enter |
|---|---|
| Android emulator (same machine) | `http://10.0.2.2:8000` |
| Real phone, same Wi-Fi | `http://<your-computer-IP>:8000` |
| Real phone, anywhere (Tailscale) | `http://<tailscale-100.x-IP>:8000` |

Then paste the setup secret. Done.

## 5. Daily use

```bash
docker compose up -d      # start backend (it also auto-starts with Docker Desktop)
cd mobile && flutter run  # run the app
```

## Backups & restore

The server zips the whole vault once a day (and on demand from **Settings → Vault Backups** on an
admin device) into `JARVIS_BACKUP_HOST_PATH` (default `./backups` next to `docker-compose.yml`,
keeps the newest 10). Point it at a **different disk** in `.env` so a dead drive doesn't take both:

```bash
JARVIS_BACKUP_HOST_PATH=D:/JARVIS-backups
```

Restore onto a clean machine: stop the stack, unzip the newest `jarvis-vault-*.zip` into an
**empty** folder, set `JARVIS_HOST_PATH` to it, `docker compose up -d`. Devices, versions, and
encrypted Secrets come back as they were, so phones reconnect without re-registering.

## Job search (optional)

Runs your private [ai-job-search](https://github.com/ai-job-search-dev/ai-job-search) clone on your
own Claude subscription, driven from the app (drawer → **Job Search**, admin devices only). Design:
`DOCUMENTATION/OFFICIAL-DOCS/12-Job-Search-Integration.md`.

1. On the host, with your subscription logged in: `claude setup-token`. Put the token in a file
   called `jobs.env` next to `docker-compose.yml` (gitignored, read by no other container):
   ```
   CLAUDE_CODE_OAUTH_TOKEN=<token>
   ```
2. In `.env`: `JARVIS_JOBS_WORKSPACE=B:/DEV/ai-job-search` and a random `JARVIS_JOBS_SECRET`.
3. `docker compose --profile jobs up -d --build` (first build is slow: it installs TeX Live).
4. Check: `docker exec jv-jobs claude auth status`. The Job Search screen shows the same.

Claude only sees the ai-job-search folder, never the vault. Nothing is ever submitted for you:
the app prepares the CV, cover letter and answers; you submit, then tap **I submitted it**.
Every run snapshots the tracker, job list and profile files first, so the run screen can undo it.

## If something breaks

| Problem | Fix |
|---|---|
| `flutter run` stuck at "Resolving dependencies" | Slow internet to pub.dev — just wait or retry |
| `docker pull` fails with "TLS handshake timeout" | Restart Docker Desktop, run the command again |
| AI chat says offline | `docker exec jv-api curl -s http://brain:8001/brain/status`: if Ollama is unreachable, start the Ollama app on this machine; if models are missing, redo the `ollama pull` step |
| Changed a database table in `mobile/` and build breaks | `dart run build_runner build --delete-conflicting-outputs` |
| Lost the setup secret | It shows only on the server's first boot — check `docker logs jv-api` |

> Before writing code, read `PROJECT_OVERVIEW.md` — especially the gotchas and the list of locked sync files.
