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

---

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
