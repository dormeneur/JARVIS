# EXACT REGRESSION REPRODUCTION TEST

## CRITICAL: Use these EXACT file contents to ensure hash changes!

### STEP 1: LAPTOP FIRST EDIT & SYNC

**File:** `Personal/file1.txt`
**Content:**

```
LAPTOP_FIRST_EDIT_VERSION_1_CONTENT_HASH_DIFFERENT_A
```

**Action:** Save this file locally on laptop, then Sync in app

**Expected Server State:** Personal/file1.txt version=1

---

### STEP 2: MOBILE PULL

**Action:** In mobile app, manually trigger sync or pull

**Expected Mobile State:**

- file_cache_entries table: Personal/file1.txt with serverVersion=1
- mutation_queue table: EMPTY (no pending mutations yet)

---

### STEP 3: MOBILE LOCAL EDIT

**File:** Personal/file1.txt (in your local editor or app)
**Content:**

```
MOBILE_EDIT_VERSION_1_CONTENT_HASH_DIFFERENT_C_DIFFERENT_C
```

**Action:** Save changes (don't sync yet!)

**Expected Mobile State:**

- file_cache_entries table: Personal/file1.txt serverVersion=1
- mutation_queue table: ONE ROW with:
  - path=Personal/file1.txt
  - baseVersion=1
  - status='pending'

---

### STEP 4: LAPTOP SECOND EDIT & SYNC

**File:** `Personal/file1.txt`
**Content:** (CRITICAL: different from laptop's first edit AND mobile's edit)

```
LAPTOP_SECOND_EDIT_VERSION_2_CONTENT_HASH_DIFFERENT_B_DIFFERENT
```

**Action:** Save this file locally on laptop, then Sync in app

**Expected Server State:**

- Personal/file1.txt version=2 (MUST be different from laptop's first edit)

---

### STEP 5: MOBILE SYNC (CRITICAL TEST)

**Action:** In mobile app, trigger sync

**Expected Behavior:**

- Phase 1: Mobile should push mutation
  - Log should show: baseVersion=1
  - Server should detect: server has version=2
  - Result: CONFLICT should be created!
- UI: ConflictDetailScreen should appear

**Actual Behavior (with regression):**

- Mobile overwrites Server (version 2 → becomes mobile's content)
- No conflict detected
- No UI shown

---

## CRITICAL VERIFICATION STEPS

### Before Step 4 (Laptop's second sync):

**On Laptop:**
Run this command to verify file was actually changed:

```bash
# Windows
certutil -hashfile "Personal/file1.txt" SHA256
```

Note down the hash.

**On Server (Docker):**
Run this to check version tracking:

```bash
docker exec jv-api sqlite3 /vault/system/file_versions.db "SELECT path, version, last_hash FROM file_versions WHERE path='Personal/file1.txt';"
```

Should show: Personal/file1.txt | 1 | <hash from step 1>

---

### After Step 4 (Laptop has synced the second edit):

**On Server:**

```bash
docker exec jv-api sqlite3 /vault/system/file_versions.db "SELECT path, version, last_hash FROM file_versions WHERE path='Personal/file1.txt';"
```

**MUST show version=2 now!** If still shows 1, the push failed.

Also check if file was actually written:

```bash
docker exec jv-api cat /vault/Personal/file1.txt
```

Should show the laptop's second edit content.

---

### Before Step 5 (Mobile sync):

**On Mobile (ADB):**

```bash
adb shell sqlite3 /data/data/com.jarvis.mobile/databases/jarvis.db \
  "SELECT path, server_version FROM file_cache_entries WHERE path LIKE '%file1.txt%';"
```

Should show: Personal/file1.txt | 1

```bash
adb shell sqlite3 /data/data/com.jarvis.mobile/databases/jarvis.db \
  "SELECT path, base_version, status FROM mutation_queue WHERE path LIKE '%file1.txt%';"
```

Should show: Personal/file1.txt | 1 | pending

---

### During Step 5 (Mobile sync):

**Capture Server Logs:**

```bash
docker logs -f jv-api | grep "SYNC:PUSH" &
# ... let it run while mobile syncs ...
```

**Expected output:**

```
[SYNC:PUSH:CONFLICT_CHECK] path=Personal/file1.txt client_base_version=1 server_version=2 server_version_for_check=2
[SYNC:PUSH:CONFLICT_DETECTED] path=Personal/file1.txt base_version=1 != server_version_for_check=2
```

**If you see `server_version=2` and `CONFLICT_DETECTED`, the fix is working!**

---

### After Step 5 (Mobile sync):

**On Server:**

```bash
# Should show file hasn't been overwritten - should still be laptop v2
docker exec jv-api cat /vault/Personal/file1.txt
```

**MUST contain:** `LAPTOP_SECOND_EDIT_...` (NOT mobile's content)

**On Server (version tracking):**

```bash
docker exec jv-api sqlite3 /vault/system/file_versions.db "SELECT path, version, last_hash FROM file_versions WHERE path='Personal/file1.txt';"
```

Should show: version=2 (not incremented further, because file wasn't overwritten) OR version=3 (if conflict file was created)

**On Server (check for conflict file):**

```bash
docker exec jv-api ls -la /vault/Personal/ | grep file1
```

Should show: `file1.txt` AND `file1.txt.conflict` (if fix is working)

---

## WHAT TO PROVIDE BACK

After running the EXACT steps above:

1. **Copy of server logs during STEP 5:**

   ```bash
   docker logs jv-api > server_logs.txt
   ```

   Send the lines containing `[SYNC:PUSH` and `[SYNC:MANIFEST`

2. **Server version tracking state after STEP 5:**

   ```bash
   docker exec jv-api sqlite3 /vault/system/file_versions.db \
     "SELECT * FROM file_versions WHERE path LIKE '%file1.txt%';"
   ```

3. **Server file content after STEP 5:**

   ```bash
   docker exec jv-api cat /vault/Personal/file1.txt | head -20
   ```

4. **Mobile mutation queue state after STEP 5:**

   ```bash
   adb shell sqlite3 /data/data/com.jarvis.mobile/databases/jarvis.db \
     "SELECT * FROM mutation_queue WHERE path LIKE '%file1.txt%';"
   ```

5. **Server filesystem (check for conflict file):**
   ```bash
   docker exec jv-api ls /vault/Personal/
   ```
   Should show `file1.txt.conflict` if fix is working

---

## DEBUGGING DECISION TREE

**If conflict IS detected:**
✅ Backend fix is working! Move to next steps.

**If conflict NOT detected:**

- **Check if server_version=2 in logs?**

  - YES: Backend logic bug (version was 2 but conflict check didn't trigger)
  - NO: Version tracker not updating (need to investigate version_tracker.increment_version)

- **Check server file content:**

  - Is it MOBILE content? → Silent overwrite confirmed
  - Is it LAPTOP v2 content? → Conflict was created but mobile didn't see it

- **Check if conflict file exists:**
  - YES: Server created it, but mobile's manifest diff didn't see it
  - NO: Server didn't detect conflict at all

---

This will help us pinpoint EXACTLY where the regression is occurring.
