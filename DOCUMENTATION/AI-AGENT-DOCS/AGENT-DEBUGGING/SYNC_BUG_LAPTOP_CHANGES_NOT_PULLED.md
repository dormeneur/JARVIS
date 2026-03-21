# Bug Report: Laptop Changes Not Pulled — Mobile Treats Them As Conflicts Instead

## Status
Root cause identified. Not yet fixed. For AI coding agent implementation.

## What Is Working (Do NOT Break)
- Conflict detection when BOTH devices edit the same file simultaneously ✅
- Conflict UI showing correct Local and Remote content ✅  
- Mobile-side edits pushing correctly to server ✅
- New files only on server being pulled correctly ✅

## What Is Broken
When only the **laptop/server** edits a file (mobile has not changed it), syncing from
mobile should **automatically pull** the server's newer version. Instead, it creates a
**conflict** and shows the conflict resolution screen — even though there's nothing to
conflict with (mobile content is unchanged).

---

## Root Cause

### The Key Insight
`diff_manifests()` in `sync.py` compares `entry["content_hash"]` (what mobile sends) with
`server_entry["content_hash"]` (server's actual file content). If they differ, it currently
always goes to `conflicts`. But this is **too aggressive** — it doesn't distinguish between:

- **Case A**: Mobile content ≠ Server content because **both sides changed** → real conflict ✅
- **Case B**: Mobile content ≠ Server content because **only the server changed** → should pull, not conflict ❌

### What Mobile Sends in Its Manifest

In `file_entry.dart`, `toManifestEntry()`:
```dart
Map<String, dynamic> toManifestEntry() => {
  'path': path,
  'content_hash': contentHash ?? '',   // hash of mobile's LOCAL file content
  'last_modified': lastModified,
  'version': serverVersion,            // last server version mobile knows about
};
```

The mobile sends its **local file content hash** AND the **server version it last synced at**.

### What the Server Has in Its Manifest

In `sync.py`, `build_server_manifest()` returns for each file:
```python
{
    "path": relative,
    "content_hash": content_hash,   # actual current file content hash on server
    "version": version,             # current server version number
    "tracked_hash": tracked_hash,   # hash recorded when version was last updated
}
```

### The Logic That's Missing in `diff_manifests()`

The version numbers tell us **exactly** what happened:

| `client_version` vs `server_version` | `client_hash` vs `server_hash` | Meaning | Correct Action |
|---|---|---|---|
| equal | equal | Nothing changed | No-op |
| equal | different | **Only mobile changed** (mobile edited but version not yet pushed) | Push |
| client < server | different | **Only server changed** (mobile is behind) | **Pull** ← broken |
| client < server + client_hash == tracked_hash | different | Only server changed, mobile untouched | **Pull** ← broken |
| both differ AND client_hash ≠ tracked_hash | different | **Both sides changed** | Conflict |

**The current `diff_manifests()` skips all this logic** and just does:
```python
# Hashes differ and file exists on both sides -> ALWAYS conflict.
conflicts.append(path)
```

This incorrectly treats Case B (only server changed) as a conflict.

### The Correct Detection Logic

The mobile sends `version` = the server version it last successfully synced at.
The server has `version` = the current server version.

If `client_version < server_version` → the server has been updated since mobile last synced.
Now check: has the mobile also changed?

- Mobile changed = mobile's `content_hash` ≠ `tracked_hash` (what the server recorded when it last wrote)
- Mobile unchanged = mobile's `content_hash` == `tracked_hash`

So:
- `client_version < server_version` AND `client_hash == tracked_hash` → **only server changed → pull**
- `client_version < server_version` AND `client_hash != tracked_hash` → **both changed → conflict**
- `client_version == server_version` AND `client_hash != server_hash` → **only mobile changed → push**

---

## The Fix

### Only `diff_manifests()` in `sync.py` needs to change

Replace the current blunt "always conflict" logic with version-aware routing:

```python
def diff_manifests(
    client_entries: list[dict],
    server_manifest: dict[str, dict],
) -> tuple[list[str], list[str], list[str]]:
    to_push: list[str] = []
    to_pull: list[str] = []
    conflicts: list[str] = []

    client_paths = set()

    for entry in client_entries:
        path = entry["path"]
        client_paths.add(path)

        server_entry = server_manifest.get(path)

        if server_entry is None:
            # File only exists on client -> push
            to_push.append(path)
            continue

        client_hash = entry["content_hash"]
        server_hash = server_entry["content_hash"]

        if client_hash == server_hash:
            # Content identical -> nothing to do
            continue

        # Content differs — use versions to determine who changed
        client_version = entry.get("version")
        server_version = server_entry.get("version")
        tracked_hash = server_entry.get("tracked_hash")  # hash when server last wrote

        if client_version is not None and server_version is not None:
            if client_version == server_version:
                # Versions equal but content differs → mobile has unsaved local edits
                # (mobile edited the file but hasn't pushed yet)
                to_push.append(path)

            elif client_version < server_version:
                # Server is ahead of mobile — server has been updated since last sync.
                # Check if mobile also changed (compare mobile hash to what server last recorded)
                if tracked_hash is not None and client_hash == tracked_hash:
                    # Mobile content matches what server last recorded → mobile untouched
                    # Only server changed → pull
                    to_pull.append(path)
                else:
                    # Mobile content differs from server's last recorded hash
                    # → both sides changed independently → conflict
                    conflicts.append(path)

            else:
                # client_version > server_version: should not happen in normal flow
                # Mobile thinks it's ahead of server — push to be safe
                to_push.append(path)
        else:
            # No version info available — fall back to safe conflict
            conflicts.append(path)

    for path in server_manifest:
        if path not in client_paths:
            # File only exists on server -> pull
            to_pull.append(path)

    return sorted(to_push), sorted(to_pull), sorted(conflicts)
```

---

## Why This Is Safe For Conflict Detection

The conflict case is now **more precise**, not less safe:

- Before: any hash mismatch = conflict (too broad, breaks pull)
- After: conflict only when `client_version < server_version` AND `client_hash != tracked_hash`

The `tracked_hash` is the server's record of what content it last wrote for that file. If the
mobile's content matches `tracked_hash`, the mobile hasn't changed the file since last sync —
so there's nothing to conflict with. If it doesn't match, the mobile has genuinely edited the
file independently — real conflict.

---

## What `tracked_hash` Is and Where It Comes From

`tracked_hash` is already in the server manifest (added in a previous fix). It comes from
`build_server_manifest()` in `sync.py`:

```python
version_info = version_tracker.get_version_and_hash(relative)
# ...
version, tracked_hash = version_info

manifest[relative] = {
    "path": relative,
    "content_hash": content_hash,   # actual current file hash
    "version": version,             # current version number
    "tracked_hash": tracked_hash,   # ← hash when version_tracker last recorded a write
}
```

`tracked_hash` is updated every time a file is written through the proper channels
(`push_file()`, `vault.py` `update_file()`, etc.). It represents "what the server content
was the last time a tracked write happened."

---

## Files to Modify

| File | Change |
|------|--------|
| `server/app/services/sync.py` | Replace `diff_manifests()` body with version-aware routing logic shown above |

## Files NOT to Touch

- `version_tracker.py` — correct as-is
- `vault.py` — correct as-is  
- `push_file()` in `sync.py` — correct as-is, conflict detection there is a separate layer
- `sync_repository.dart` — correct as-is
- All conflict UI files — correct as-is, working perfectly

---

## Verification Scenarios After Fix

| Scenario | Expected Result |
|----------|----------------|
| Only laptop edits file → mobile syncs | **Pull** (no conflict screen) |
| Only mobile edits file → mobile syncs | **Push** (no conflict screen) |
| Both edit same file → mobile syncs | **Conflict screen** with both versions |
| No changes on either side → mobile syncs | **No-op** |
| New file only on laptop → mobile syncs | **Pull** (already works) |
| New file only on mobile → mobile syncs | **Push** (already works) |
