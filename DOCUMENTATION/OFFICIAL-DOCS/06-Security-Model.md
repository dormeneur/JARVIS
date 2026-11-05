# 06 — Security Model

## Scope

Defines the threat model, authentication, authorization, network security, and access control for the JARVIS system. Covers both server-side and mobile-side security.

---

## Threat Model

### Assets to Protect
| Asset | Sensitivity | Location |
|---|---|---|
| Vault files (Personal, Work, etc.) | High | `/JARVIS` on host, app cache |
| Secrets folder | Critical | `/JARVIS/Secrets` (encrypted at rest) |
| AI chat history | Medium | Mobile device only |
| JWT signing key | Critical | Server `.env` / Docker secret |
| User passphrase | Critical | Never stored; entered per-session |
| Sync metadata | Low | Server + mobile SQLite |

### Threat Actors
| Actor | Capability | Mitigated By |
|---|---|---|
| College WiFi snooper | Network sniffing, port scanning | Tailscale encryption, no open ports |
| Physical device theft (phone) | App data access | Android Keystore, app-level auth |
| Physical device theft (laptop) | Disk access | Secrets encryption, OS disk encryption recommended |
| Malicious app on phone | Inter-app data access | Android sandbox, secure storage |
| Insider with server access | Read raw files | Secrets encryption, audit logging |

### Out of Scope (by design)
- Nation-state level attacks
- Hardware keyloggers
- Supply-chain attacks on dependencies
- Multi-user access control

---

## Network Security: Tailscale

```
┌──────────────────┐          WireGuard tunnel          ┌──────────────────┐
│  Android Device  │◄──────────────────────────────────►│   Laptop/Server  │
│  100.x.y.z       │     (encrypted, authenticated)     │   100.a.b.c      │
└──────────────────┘                                    └──────────────────┘
              │                                                │
              └──── NO public IP exposure ─────────────────────┘
                    NO open firewall ports
                    NO port forwarding
```

### Tailscale Configuration
| Setting | Value | Rationale |
|---|---|---|
| ACLs | Only registered devices | Prevent unauthorized device access |
| MagicDNS | Enabled | Use hostnames instead of IPs |
| Key expiry | 180 days (recommended) | Force periodic re-auth |
| Funnel | **Disabled** | No public exposure |
| Exit node | **Disabled** | Not a VPN use case |

### Server Binding
```
127.0.0.1:8000  ← API binds to localhost only
Tailscale route  ← 100.x.y.z:8000 accessible only to mesh devices
```

> [!WARNING]
> Never bind to `0.0.0.0:8000`. This would expose the API on all network interfaces, bypassing Tailscale protection.

---

## Authentication: JWT Tokens

### Token Architecture
| Property | Value |
|---|---|
| Algorithm | HS256 |
| Secret | 256-bit random (generated on first run, stored in `.env`) |
| Expiry | 30 days (configurable) |
| Payload | `{sub: device_id, iat, exp, permissions: [...]}` |
| Refresh | New token issued before expiry |
| Revocation | Server-side revocation list (in-memory + persisted) |

### Token Lifecycle
```
1. Device Registration (one-time):
   ┌─────────┐    POST /auth/register     ┌──────────┐
   │ jv-app  │ ──────────────────────────► │ jv-api   │
   │         │    {device_name, secret}    │          │
   │         │ ◄────────────────────────── │          │
   │         │    {jwt_token, expires_at}  │          │
   └─────────┘                             └──────────┘

2. Subsequent Requests:
   Authorization: Bearer <jwt_token>

3. Token Refresh (before expiry):
   POST /auth/refresh
   Authorization: Bearer <current_token>
   → {new_token, expires_at}

4. Revocation (if device lost):
   POST /auth/revoke
   {device_id: "stolen_phone"}
   → Token blacklisted immediately
```

### Device Registration Security
- First device registration requires a **setup secret** (generated during server init, displayed once)
- Subsequent device registrations require approval from an already-registered device
- Maximum **5 registered devices** (configurable)

---

## Authorization

### Permission Model
Single-user system with device-level permissions:

| Permission | Description | Default |
|---|---|---|
| `files:read` | Read vault files | All devices |
| `files:write` | Create/update/delete files | All devices |
| `sync:full` | Full bi-directional sync | All devices |
| `ai:query` | Submit AI queries | All devices |
| `secrets:access` | Access encrypted secrets | Per-device opt-in |
| `admin:manage` | Register/revoke devices | Primary device only |

### Endpoint Protection Matrix
| Endpoint Group | Required Permission | Rate Limit |
|---|---|---|
| `GET /health` | None | 120/min |
| `/files/*` | `files:read` or `files:write` | 60/min |
| `/sync/*` | `sync:full` | 10/min |
| `/ask` | `ai:query` | 20/min |
| `/secrets/*` | `secrets:access` | 10/min |
| `/auth/*` | `admin:manage` | 5/min |
| `/backup/*` | `admin:manage` | 2/min |

**As implemented** (`server/app/services/rate_limit.py`, `dependencies.require_admin`), verified 2026-09-24:

- `admin:manage` = any **secrets-authorized** device (the first device, plus any device an admin
  authorizes). Admin-only: `/auth/register/device`, `/auth/invite`, `/auth/authorize_secrets`,
  `/auth/revoke` (a device may always revoke itself), `/files/reset`, `/backup/*`.
- Non-admin devices get a 403 on any `Secrets/` path, whatever the spelling (`secrets/`, `./Secrets/`,
  bare `Secrets`). The check is case-insensitive because the vault is a Windows bind mount.
- `system/` (device registry with reconnect secrets, version DB) and every dot-path (`.system/`
  audit log) cannot be reached through any endpoint, including by admins.
- Rate limits are per device token (per IP for unauthenticated calls), fixed one-minute window:

| Group | Limit | Why it differs from the table above |
|---|---|---|
| `/auth/register`, `/auth/reconnect`, `/auth/invite/register` | 10/min **per IP** | Credential-guessing surface; a fake `Authorization` header doesn't buy a fresh bucket |
| `/ask/*` | 30/min | LLM cost; one request per chat message or approval |
| `/backup/export` | 2/min | List and download aren't expensive, so they aren't limited |
| `/sync/*` | 3000/min | Sync does one push/pull **per file**; 10/min would break any real sync |
| everything else | 600/min | Catches runaway loops; bulk file operations stay usable |
| `GET /health` | 120/min | |

Exceeding a limit returns `429` + `Retry-After`, and is audit-logged.

---

## What jv-brain can see (as built 2026-11-05)

The brain is the one component that talks to the model, so it is kept deliberately blind:

- **Environment:** it gets an explicit list of `JARVIS_*` settings in `docker-compose.yml`, not `.env`. It
  has no `JARVIS_JWT_SECRET` (enough to mint admin tokens) and no jobs secret. A new brain setting must be
  added to that list.
- **Filesystem:** the vault is mounted read-write (the agent's approved tools write there), but an empty
  in-memory folder is laid over `Secrets/` and `system/` (device registry). Defence in depth only: on a
  case-insensitive Windows bind mount a differently-cased spelling (`secrets/`) can still reach the real
  folder (verified), so `tools.safe_path()` (casefold + vault-root containment) and the loader's
  `EXCLUDED_FOLDERS` stay the primary guards, each with tests.
- **Retrieved text is data:** vault text is wrapped in `<vault_file>` tags and flagged if it reads like
  instructions (`services/vault_text.py`), and every mutation still needs an approval. Deletions always
  ask (no session grant) and are moved to `.system/brain/trash/` for 30 days.
- **Desktop:** text bound for Claude is scanned for keys, passwords and card numbers
  (`Jarvis.Core/Assistant/OutboundScanner`): a question asks first, page text and job briefs are redacted,
  dictation with a secret skips the Claude clean-up. Background jobs can only read and write their own
  folder (`Read(./**)`, `Edit(./**)`, `dontAsk`).
- **The one exception to "vault data never reaches Claude" (owner's decision, 2026-11-05):** the resume
  the owner picks in the career setup is sent to Claude through jv-jobs (`POST /extract`: no tools, no
  skills, no MCP, no session) to read it reliably, and later its sections feed the job engine
  (`14-Career-Copilot.md` §1, §3). Everything else in `Profile/profile.json` (address beyond what the
  resume prints, work authorization, salary, background/DEI, accommodations), mail, and the job-portal
  password never do; form answers come from the local brain. The portal password lives only in each
  device's OS store (Android Keystore, Windows Credential Manager), never in the vault or jv-api.

## Input Validation

### File Path Validation
```python
def validate_path(path: str) -> str:
    """Sanitize and validate file paths."""
    # 1. Reject path traversal
    if ".." in path:
        raise PathTraversalError()
    
    # 2. Reject absolute paths
    if path.startswith("/") or path.startswith("\\"):
        raise InvalidPathError()
    
    # 3. Normalize separators
    path = path.replace("\\", "/")
    
    # 4. Reject hidden files/folders (except allowed ones)
    for part in path.split("/"):
        if part.startswith(".") and part not in ALLOWED_DOTFILES:
            raise HiddenFileError()
    
    # 5. Resolve against vault root and verify containment
    resolved = (VAULT_ROOT / path).resolve()
    if not str(resolved).startswith(str(VAULT_ROOT.resolve())):
        raise PathEscapeError()
    
    return path
```

### Request Size Limits
| Limit | Value |
|---|---|
| Max request body | 100 MB (file upload) |
| Max JSON body | 1 MB |
| Max path length | 500 characters |
| Max query length | 10,000 characters |
| Max filename length | 255 characters |

---

## Audit Logging

All security-relevant events logged:

```json
{
  "timestamp": "2026-02-19T10:00:00Z",
  "event": "file.write",
  "device_id": "pixel_7",
  "path": "Personal/journal.md",
  "ip": "100.64.0.2",
  "result": "success"
}
```

| Event | Logged |
|---|---|
| Device registration | ✅ |
| Token refresh | ✅ |
| Token revocation | ✅ |
| File mutation (create/update/delete) | ✅ |
| Sync operation | ✅ |
| Secrets access | ✅ |
| Failed auth attempt | ✅ |
| Rate limit exceeded | ✅ |
| Path traversal attempt | ✅ |

- Logs stored in `/JARVIS/.system/audit.log` (rotated, max 50MB)
- Not synced to mobile (server-only)
- **As implemented** (`server/app/main.py` middleware + `services/audit.py`): one JSON line per
  non-GET request, per 4xx/5xx, per 401/403 (`auth.denied`), per 429 (`rate_limit.exceeded`), and per
  `Secrets/` access through `/files`, `/download`, `/upload` (`secrets.access`), with `device_id`, `ip`,
  `method`, `path`, `status`. `/files/reset` preserves `.system/`. Request bodies are never logged.

---

## CORS Policy

```python
CORS_CONFIG = {
    "allow_origins": [],      # Empty = no CORS (API not browser-accessed)
    "allow_methods": ["*"],
    "allow_headers": ["*"],
    "allow_credentials": False
}
```

- CORS is effectively **disabled** since the API is accessed only by native mobile apps (not browsers)
- If a web dashboard is added later, specific Tailscale origins would be whitelisted

---

## Security Checklist for Development

- [ ] JWT secret generated cryptographically (not hardcoded)
- [ ] `.env` file excluded from version control (`.gitignore`)
- [ ] All endpoints validate JWT before processing
- [ ] Path traversal tests included in test suite
- [ ] Rate limiting enabled on all endpoints
- [ ] Audit logging active for all mutations
- [ ] Docker containers run as non-root user
- [ ] Tailscale Funnel is disabled
- [ ] No `0.0.0.0` bindings in production config
- [ ] `/Secrets` excluded from AI indexing pipeline

---

## Future Extensibility

- **mTLS**: Mutual TLS between mobile and server for certificate-based auth
- **Biometric unlock**: Mobile app requires fingerprint/face to open
- **IP allowlisting**: Additional layer restricting to specific Tailscale IPs
- **Intrusion detection**: Anomaly detection on access patterns
- **Backup encryption**: Encrypt exported zip archives
