"""Prints a one-time invite code for registering another device, from the host itself.

    docker exec jv-api python -m app.invite

The QR invite needs an admin device's screen; this is the same single-use, 10-minute token for
a device on this PC (the desktop app). Whoever can run it already has the vault folder on disk,
so it grants nothing new. Devices registered with it never get Secrets access.
"""

from app.services import auth

if __name__ == "__main__":
    token, expires_at = auth.create_invite_token()
    print(token)
    print(f"(single use, expires {expires_at:%H:%M} UTC)")
