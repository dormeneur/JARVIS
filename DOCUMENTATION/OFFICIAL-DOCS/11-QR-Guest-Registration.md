# 11 — QR Code Guest Registration

## Scope
The technical implementation plan for secure, QR code-based guest device registration within the JARVIS environment, minimizing setup friction without compromising the no-public-port security model.

## Problem Statement
The JARVIS mesh relies on Tailscale and JWTs. Handing over sensitive setup secrets or manually coordinating Tailscale auth keys to add new mobile devices or guest devices is cumbersome. Providing a QR-based flow on an already authorized primary device solves this seamlessly.

## Architecture Request Flow
1. **QR Generation:** Primary (admin) device initiates `POST /auth/guest/invite`.
2. `jv-api` responds with a short-lived (TTL: 10 mins) single-use `invite_token`.
3. Admin device displays a QR containing: `{"server_ip": "100.x.y.z", "token": "abc...123", "ts_authkey": "tskey-auth-..."}`. *(Note: Providing Tailscale context is critical since the device needs to be on Tailscale first).*
4. **Guest Scan:** Guest device scans the QR code.
5. If not on Tailscale, it joins using the embedded ephemeral Tailscale AuthKey (optional integration).
6. Guest app hits `POST /auth/guest/register` with the `invite_token` and its local `device_name`.
7. `jv-api` validates the single-use token, generates a JWT, and returns it.

## Device Registry Schema & Storage
**Storage:** SQLite `devices.db` (Server-side, replacing in-memory cache)
- `id`: UUID PRIMARY KEY
- `device_name`: TEXT
- `role`: TEXT ('admin' or 'guest')
- `permissions`: JSON array of scopes
- `status`: TEXT ('active' or 'revoked')
- `created_at` / `last_seen`: TIMESTAMP
- `added_by`: UUID (Refers to the inviter)

## Permission Model
Guest scoped JWTs possess restricted permissions:
- `files:read` — **Granted**
- `files:write` — **Optional/Restricted** (Could be restricted to a specific `/Guest` folder or just block deletes)
- `sync:full` — **Granted** (Only for allowed path configurations)
- `ai:query` — **Granted**
- `secrets:access` — **Denied** (Guests cannot view `/Secrets`)
- `admin:manage` — **Denied** (Cannot generate invites or revoke devices)

## Revocation Flow
1. Admin user opens **Active Devices** in Flutter UI.
2. Taps "Revoke" on a guest device ID.
3. Mobile sends `POST /auth/revoke { "device_id": "..." }`.
4. `jv-api` updates `devices.db` setting `status='revoked'`.
5. Any active JWTs for that device instantly fail validation.

## Flutter UI Changes
Primary Device (Admin):
- **Settings -> Devices:** Displays currently active devices queried via `GET /auth/devices`.
- **"Invite Device":** Initiates QR generation, popping up a fullscreen QR dialog with a 10-minute countdown timer.
- **Revoke Button:** Beside every device (except the current one) to trigger revocation.

Guest Device:
- **Login screen addition:** A "Scan QR Invite" button as an alternative to "Enter Setup Secret".

## Security Constraints
- **Single-use:** The `invite_token` must be invalidated exactly after a successful registration.
- **TTL:** 10 minutes strict expiry to prevent QR theft or leak.
- **Scope limitation:** Guest devices must not have access to encrypted secrets or admin routes.
- **Tailscale AuthKey (Open Question):** Emitting Tailscale auth keys from our API means `jv-api` needs Tailscale API keys. This significantly elevates risk.

## Verification Plan
### Automated Tests
- Test that generating invite tokens requires `admin:manage` permission.
- Assert registration fails if the token is reused or expired.
- Guarantee that a guest-JWT gets `403 Forbidden` when attempting to list or decrypt `/Secrets`.
- Verifying device revocation immediately bounces guest requests with `401 Unauthorized`.

### Manual Adversarial Checks
- Scan a QR code, let it expire, and verify registration failure.
- Try scanning the same QR with two devices concurrently to verify race condition protections.
- Revoke an active guest device and guarantee its real-time SSE or polling sync connection receives an authentication error immediately.

## Open Questions
1. **Tailscale Onboarding:** Should the JARVIS UI automate the guest Tailscale connection using an ephemeral AuthKey, or should we assume the device is already manually authenticated to Tailscale? *(Recommending manual Tailscale setup first to keep `jv-api` devoid of Tailscale admin keys)*.
2. **File Write Boundaries:** Should guest devices be allowed to modify all files, or only isolated public files? For MVP, we will grant vault-wide access minus `Secrets`, but this needs confirmation.
