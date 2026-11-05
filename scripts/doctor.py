#!/usr/bin/env python3
"""JARVIS doctor: checks the whole stack and says what to fix. Standard library only.

    python scripts/doctor.py

Run it on the host (not inside Docker). Exits 1 if anything FAILs; WARNs are worth a look.
Output is plain ASCII so it prints on any Windows console code page.
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
results: list[tuple[str, str, str]] = []


def report(level: str, name: str, fix: str = "") -> None:
    results.append((level, name, fix))
    print(f"[{level:<4}] {name}" + (f"\n         -> {fix}" if fix and level != "OK" else ""))


def run(cmd: list[str], timeout: int = 10) -> tuple[int, str]:
    """Run a command; (returncode, combined output). A missing executable is returncode 127."""
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, errors="replace")
        return p.returncode, (p.stdout + p.stderr).strip()
    except FileNotFoundError:
        return 127, f"{cmd[0]} not found"
    except subprocess.TimeoutExpired:
        return 124, f"{cmd[0]} timed out after {timeout}s"


def http_json(url: str, timeout: int = 5):
    with urllib.request.urlopen(url, timeout=timeout) as r:  # noqa: S310 - fixed local URLs
        return r.status, json.loads(r.read().decode("utf-8", "replace") or "null")


def read_env() -> dict[str, str]:
    env: dict[str, str] = {}
    path = ROOT / ".env"
    if path.exists():
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, _, value = line.partition("=")
                env[key.strip()] = value.strip().strip('"').strip("'")
    return env


def check_env(env: dict[str, str]) -> None:
    if not (ROOT / ".env").exists():
        report("FAIL", ".env missing", "copy .env.template to .env and fill it in")
        return
    host = env.get("JARVIS_HOST_PATH", "")
    if not host or "CHANGE" in host:
        report("FAIL", "JARVIS_HOST_PATH not set", "set it in .env to your vault folder")
    elif not Path(host).is_dir():
        report("FAIL", f"vault folder does not exist: {host}", "fix JARVIS_HOST_PATH in .env")
    else:
        report("OK", f"vault folder exists ({host})")
    secret = env.get("JARVIS_JWT_SECRET", "")
    if len(secret) < 32 or "CHANGE" in secret:
        report("FAIL", "JARVIS_JWT_SECRET is missing or shorter than 32 characters", "put a long random string in .env")
    else:
        report("OK", "JWT secret is set")


def check_docker(env: dict[str, str]) -> list[str]:
    code, out = run(["docker", "ps", "--format", "{{.Names}}"])
    if code != 0:
        report("FAIL", "Docker is not reachable", "start Docker Desktop, then `docker compose up -d`")
        return []
    names = out.split()
    needed = ["jv-api", "jv-brain"] + ([] if env.get("JARVIS_VECTOR_BACKEND") == "sqlite" else ["jv-chromadb"])
    for name in needed:
        if name in names:
            report("OK", f"{name} is running")
        else:
            report("FAIL", f"{name} is not running", "docker compose up -d --build")
    return names


def check_api(names: list[str]) -> None:
    if "jv-api" not in names:
        return
    try:
        status, _ = http_json("http://127.0.0.1:8000/health")
        report("OK" if status == 200 else "FAIL", f"jv-api /health -> {status}", "docker logs jv-api")
    except (urllib.error.URLError, OSError, ValueError) as exc:
        report("FAIL", f"jv-api /health unreachable ({exc})", "docker logs jv-api")


def check_ollama(env: dict[str, str], names: list[str]) -> None:
    try:
        _, tags = http_json("http://127.0.0.1:11434/api/tags")
        have = {m.get("name", "") for m in (tags or {}).get("models", [])}
    except (urllib.error.URLError, OSError, ValueError):
        report("FAIL", "Ollama is not reachable on 127.0.0.1:11434", "start Ollama (ollama.com)")
        return
    wanted = [
        env.get("JARVIS_LLM_MODEL", "qwen3:4b"),
        env.get("JARVIS_EMBEDDING_MODEL", "nomic-embed-text"),
    ]
    if env.get("JARVIS_FORM_MODEL"):
        wanted.append(env["JARVIS_FORM_MODEL"])
    for model in wanted:
        present = model in have or f"{model}:latest" in have
        report("OK" if present else "FAIL", f"model {model} " + ("is pulled" if present else "is missing"), f"ollama pull {model}")

    if "jv-brain" in names:
        code, out = run(["docker", "exec", "jv-brain", "curl", "-s", "-m", "5", "http://host.docker.internal:11434/api/version"])
        if code == 0 and "version" in out:
            report("OK", "Ollama is reachable from Docker")
        else:
            report("FAIL", "Docker cannot reach Ollama", "set the user environment variable OLLAMA_HOST=0.0.0.0 and restart Ollama")
        code, out = run(["docker", "exec", "jv-brain", "curl", "-s", "-m", "5", "http://localhost:8001/brain/status"])
        try:
            status = json.loads(out)
            ok = status.get("ollama") == "reachable" and status.get("chromadb") in ("reachable", "not used")
            note = " (indexing in progress)" if status.get("indexing") else ""
            report("OK" if ok else "FAIL", f"brain status: ollama={status.get('ollama')} chromadb={status.get('chromadb')}{note}", "docker logs jv-brain")
        except ValueError:
            report("FAIL", "brain /brain/status gave no answer", "docker logs jv-brain")

    code, out = run(["ollama", "ps"])
    if code == 0:
        cpu = [line for line in out.splitlines()[1:] if "CPU" in line]
        if cpu:
            report("WARN", "a loaded model is partly on the CPU (slow answers)",
                   "lower JARVIS_NUM_CTX to 6144, or set OLLAMA_FLASH_ATTENTION=1 and OLLAMA_KV_CACHE_TYPE=q8_0 on the host")
        else:
            report("OK", "loaded models fit on the GPU (or none loaded)")


def check_backup(env: dict[str, str]) -> None:
    folder = Path(env.get("JARVIS_BACKUP_HOST_PATH") or ROOT / "backups")
    zips = sorted(folder.glob("jarvis-vault-*.zip"), key=lambda p: p.stat().st_mtime) if folder.is_dir() else []
    if not zips:
        report("WARN", f"no vault backup found in {folder}", "start the stack and wait an hour, or POST /backup/export as admin")
        return
    age_h = (time.time() - zips[-1].stat().st_mtime) / 3600
    report("OK" if age_h < 26 else "WARN", f"newest backup is {age_h:.0f} h old ({zips[-1].name})", "check `docker logs jv-api` for backup errors")


def check_optional() -> None:
    code, _ = run(["tailscale", "status"])
    report("OK" if code == 0 else "WARN", "Tailscale is up" if code == 0 else "Tailscale is not running or logged out",
           "run `tailscale up` (the phone reaches the PC through it)")
    code, out = run(["claude", "--version"])
    report("OK" if code == 0 else "WARN", f"Claude CLI: {out.splitlines()[0]}" if code == 0 else "Claude CLI not found",
           "install Claude Code (only the desktop app and the jobs runner need it)")


def main() -> int:
    env = read_env()
    check_env(env)
    names = check_docker(env)
    check_api(names)
    check_ollama(env, names)
    check_backup(env)
    check_optional()
    fails = sum(1 for level, _, _ in results if level == "FAIL")
    warns = sum(1 for level, _, _ in results if level == "WARN")
    print(f"\n{len(results) - fails - warns} ok, {warns} warning(s), {fails} failure(s)")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
