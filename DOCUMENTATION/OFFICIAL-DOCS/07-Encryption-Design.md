# 07 — Encryption Design

## Scope

Defines the at-rest encryption strategy for the `/JARVIS/Secrets` folder. Covers key derivation, encryption/decryption flow, key management, and mobile-side handling.

---

## Design Goals

1. **At-rest protection**: Secrets files are unreadable without the user's passphrase
2. **Zero server-side key storage**: Server never stores the decryption key
3. **Per-file encryption**: Each file encrypted independently
4. **Portable**: Encrypted files can be copied between machines; decryption depends only on the passphrase
5. **No key escrow**: Lost passphrase = lost data (by design)

---

## Cryptographic Primitives

| Primitive | Choice | Rationale |
|---|---|---|
| Symmetric cipher | AES-256-GCM | Authenticated encryption; industry standard |
| Key derivation | PBKDF2-HMAC-SHA256 | Well-understood; built into Python stdlib |
| KDF iterations | 600,000 | OWASP 2024 recommendation |
| Salt | 32 bytes, random per file | Prevents rainbow tables; unique per file |
| IV/Nonce | 12 bytes, random per encryption | Required for GCM mode |
| Authentication tag | 16 bytes (128-bit) | Integrity verification included in GCM |

> [!NOTE]
> Argon2id would be preferable for KDF, but PBKDF2 is chosen for maximum portability (Python stdlib, Flutter/Dart support). Migration to Argon2id can be done in a future version.

---

## Encrypted File Format

Each encrypted file in `/JARVIS/Secrets/` has the following binary structure:

```
┌─────────────────────────────────────────────────────┐
│ Magic bytes  │  4 bytes  │  "JVS\x01"  (version 1) │
│ Salt         │ 32 bytes  │  Random per file         │
│ Nonce/IV     │ 12 bytes  │  Random per encryption   │
│ Auth tag     │ 16 bytes  │  GCM authentication tag  │
│ Ciphertext   │ N bytes   │  Encrypted file content  │
└─────────────────────────────────────────────────────┘
Total overhead: 64 bytes per file
```

### File Extension Convention
- Original file: `passwords.md`
- Encrypted file: `passwords.md.jvs` (JARVIS Secret)
- The `.jvs` extension signals the system to treat the file as encrypted

---

## Encryption Flow

### Encrypt (Server-side or Mobile-side)

```
User provides: passphrase + plaintext file

1. Generate random salt (32 bytes)
2. Derive key: PBKDF2(passphrase, salt, iterations=600000, dklen=32)
3. Generate random nonce (12 bytes)
4. Encrypt: AES-256-GCM(key, nonce, plaintext) → (ciphertext, auth_tag)
5. Write file: magic + salt + nonce + auth_tag + ciphertext
6. Zero out key and plaintext from memory
7. Return encrypted file
```

### Decrypt (Server-side or Mobile-side)

```
User provides: passphrase + encrypted file

1. Read file header: magic bytes, salt, nonce, auth_tag
2. Validate magic bytes (reject if not "JVS\x01")
3. Derive key: PBKDF2(passphrase, salt, iterations=600000, dklen=32)
4. Decrypt: AES-256-GCM(key, nonce, ciphertext, auth_tag) → plaintext
5. If auth_tag validation fails → REJECT (wrong passphrase or tampered)
6. Zero out key from memory
7. Return plaintext content
```

---

## Key Management

### Passphrase Rules
| Rule | Value |
|---|---|
| Minimum length | 12 characters |
| Maximum length | 256 characters |
| Character set | Any UTF-8 |
| Stored | **Never** — entered each session |
| Cached | Only in volatile memory during active session |

### Key Derivation Diagram
```
┌────────────┐     ┌──────────┐
│ Passphrase │────►│ PBKDF2   │────► 256-bit AES key
│ (user)     │     │ + Salt   │      (ephemeral, in-memory only)
└────────────┘     └──────────┘
```

### Memory Safety
- Derived key held in memory only during encrypt/decrypt operation
- Key zeroed after use (best-effort; depends on language runtime GC)
- Python: `ctypes.memset` for explicit zeroing
- Dart: Overwrite `Uint8List` with zeros, then discard

---

## Server-Side API

### Encrypt a File
```
POST /secrets/encrypt
Content-Type: multipart/form-data

Fields:
  - passphrase: "user_secret_passphrase"
  - file: (binary upload)
  - filename: "passwords.md"

Response:
  201 Created
  {
    "path": "Secrets/passwords.md.jvs",
    "size_bytes": 1088,
    "encrypted_at": "2026-02-19T10:00:00Z"
  }
```

### Decrypt a File
```
POST /secrets/decrypt
Content-Type: application/json

Body:
  {
    "path": "Secrets/passwords.md.jvs",
    "passphrase": "user_secret_passphrase"
  }

Response:
  200 OK
  Content-Type: application/octet-stream
  Body: <decrypted file content>

Error (wrong passphrase):
  403 Forbidden
  {"error": {"code": "DECRYPTION_FAILED", "message": "Invalid passphrase or corrupted file"}}
```

### List Secrets (Metadata Only)
```
GET /secrets

Response:
  200 OK
  {
    "files": [
      {
        "path": "Secrets/passwords.md.jvs",
        "size_bytes": 1088,
        "last_modified": "2026-02-19T10:00:00Z"
      }
    ]
  }
```

> [!CAUTION]
> The passphrase is transmitted over the network — this is acceptable **only because** traffic is encrypted by Tailscale (WireGuard). On an untrusted network, this would be a critical vulnerability.

---

## Mobile-Side Handling

### Decrypt Flow on Mobile
1. User navigates to Secrets folder in jv-app
2. Sees list of `.jvs` files (metadata only — encrypted)
3. Taps a file → prompted for passphrase
4. Passphrase sent to server `POST /secrets/decrypt` (over Tailscale)
5. Decrypted content displayed in-app
6. On navigate away or app background → clear decrypted content from memory
7. Never write decrypted content to local disk or SQLite

### Alternative: Client-Side Decryption (Future)
- Download `.jvs` file to mobile
- Decrypt locally using Dart `pointycastle` (AES-256-GCM + PBKDF2)
- Advantage: Passphrase never leaves the device
- Disadvantage: More complex implementation; need to ship crypto library

> MVP uses server-side decryption. Client-side decryption is a Phase 2+ enhancement.

---

## Sync Handling for Secrets

| Behavior | Rule |
|---|---|
| Sync direction | `pull_only` by default (server is authoritative) |
| Content transfered | **Encrypted** `.jvs` files only |
| Decrypted content synced | **Never** |
| Mobile local copy | Stores `.jvs` files (still encrypted at rest) |
| Offline decrypt | Only if client-side decryption is implemented |

---

## Failure Handling

| Failure | Handling |
|---|---|
| Wrong passphrase | GCM auth tag fails; return `403 DECRYPTION_FAILED` |
| Corrupted file | GCM auth tag fails; same error returned |
| Partial write during encrypt | Write to temp file first, then atomic rename |
| Memory not zeroed (GC) | Best-effort; documented limitation |
| Passphrase forgotten | Data is irrecoverable (by design); warn user during setup |

---

## Security Considerations

1. **Passphrase strength**: Enforce minimum 12 characters; show strength meter in UI
2. **No passphrase storage**: Not in `.env`, not in Keystore, not in SQLite
3. **No key caching across sessions**: User must re-enter passphrase each app session
4. **Brute-force protection**: 600,000 PBKDF2 iterations makes brute-force expensive
5. **Side-channel attacks**: Out of scope (local, single-user system)
6. **Backup safety**: Exported backups include encrypted `.jvs` files (still protected)
7. **Secrets excluded from AI**: Vector indexing pipeline skips `/Secrets` folder entirely

---

## Future Extensibility

- **Argon2id migration**: Stronger KDF; version `JVS\x02` format
- **Client-side decryption**: Decrypt on mobile without sending passphrase over network
- **Per-file passphrases**: Different secrets protected by different passphrases
- **Hardware key support**: YubiKey HMAC challenge-response for key derivation
- **Encrypted backup archives**: Full vault export with encryption
