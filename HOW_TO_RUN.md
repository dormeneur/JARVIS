# How to Run JARVIS

Everything backend (API + AI + vector DB + Ollama) runs in Docker. One command starts it all.
The only things installed on your machine: **Docker Desktop**, **Flutter**, **Git**.

---

## 1. Install (one time)

- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
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

> **Windows with NVIDIA GPU** (faster AI): use this instead:
> `docker compose -f docker-compose.yml -f docker-compose.gpu.yml up -d --build`

Download the two AI models (one time, ~5 GB total):
```bash
docker exec jv-ollama ollama pull llama3
docker exec jv-ollama ollama pull nomic-embed-text
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

## If something breaks

| Problem | Fix |
|---|---|
| `flutter run` stuck at "Resolving dependencies" | Slow internet to pub.dev — just wait or retry |
| `docker pull` fails with "TLS handshake timeout" | Restart Docker Desktop, run the command again |
| AI chat says offline | `docker exec jv-api curl -s http://brain:8001/brain/status` — if ollama unreachable, `docker restart jv-ollama`; if models missing, redo the `ollama pull` step |
| Changed a database table in `mobile/` and build breaks | `dart run build_runner build --delete-conflicting-outputs` |
| Lost the setup secret | It shows only on the server's first boot — check `docker logs jv-api` |

> Before writing code, read `PROJECT_OVERVIEW.md` — especially the gotchas and the list of locked sync files.
