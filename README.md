# JARVIS

**Neural Knowledge Vault. Self-Hosted Intelligence.**

JARVIS is not just a file browser. It is a secure, offline-first personal cloud designed as the foundational memory layer for future AI integration. It treats your data as a synaptic network—accessible anywhere, synchronized instantly, and ready for inference.

---

## Core Systems

### 🧠 Server (Cortex)
The central nervous system. Built for speed and extensibility.
- **Engine**: Python 3.11 + FastAPI (Async/Await architecture)
- **Database**: Zero-dependency filesystem. Your data *is* the database.
- **Security**: JWT-based authentication. Stateless and scalable.
- **Deployment**: Dockerized for instant spin-up.

### 📱 Mobile Link (Synapse)
The peripheral interface. Fast, reactive, and always available.
- **Framework**: Flutter (Dart). Native performance on Android.
- **State**: Riverpod. Reactive and predictable.
- **Memory**: Drift (SQLite). Millisecond-latency local queries.
- **Protocol**: Custom V1 Sync Engine. SHA-256 integrity checks.

---

## Intelligence & Capabilities

- **Offline-First Architecture**: operate without the cloud. Your data belongs to you.
- **Conflict-Resilient Sync**: Bidirectional differential synchronization. Handles network fragmentation gracefully.
- **Markdown-Native**: Optimized for LLM consumption and generation.
- **Tailscale Ready**: Designed for secure, zero-trust peer-to-peer mesh networking.

---

## Deployment

### Server Node
```bash
cp .env.template .env       # Configure environment
docker-compose up -d --build # Ignite the core
```
*Port 8000 is now active.*

### Mobile Node
```bash
cd mobile
flutter pub get             # Install dependencies
dart run build_runner build # Generate data layer
flutter run                 # Launch interface
```

---

*System status: Online.*
