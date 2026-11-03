"""Prints a one-time invite code for registering another device, from the host itself.

    docker exec jv-api python -m app.invite            # a normal device (no Secrets access)
    docker exec jv-api python -m app.invite --admin    # an admin device: a setup secret for
                                                       # "First device" in the app's Register screen

The QR invite needs an admin device's screen; this is the same single-use, 10-minute token for
a device on this PC (the desktop app). Whoever can run it already has the vault folder on disk,
so it grants nothing new. A device from a plain invite never gets Secrets access; --admin is for
re-registering your own admin device (say, after reinstalling the app).
"""

import sys

from app.services import auth

if __name__ == "__main__":
    if "--admin" in sys.argv:
        print(auth.issue_admin_setup_secret())
        print("(one-time setup secret for an admin device: use it once in the app's Register screen, First device)")
    else:
        token, expires_at = auth.create_invite_token()
        print(token)
        print(f"(single use, expires {expires_at:%H:%M} UTC)")
