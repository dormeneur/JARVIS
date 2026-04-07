@echo off
REM JARVIS Auto-Start Script
REM Place a shortcut to this file in:
REM   %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
REM
REM This will automatically start the JARVIS Docker containers
REM when you log in to Windows.

echo [JARVIS] Starting Ollama AI server...
start "Ollama Server" /min cmd /c "ollama serve"

echo [JARVIS] Starting Docker containers...
cd /d "B:\DEV\JARVIS"
docker compose up -d

echo [JARVIS] Containers started.
