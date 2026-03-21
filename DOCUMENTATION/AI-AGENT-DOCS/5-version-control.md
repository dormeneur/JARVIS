# JARVIS Version Control & Sync Architecture

## Vision

JARVIS should behave like a **Git-style, deterministic, per-file versioned sync system** — without full Git history complexity.

The system must be:

* Predictable
* Robust
* Deterministic
* Explicit about conflicts
* Free of silent overwrites
* Stable across devices
* Per-file isolated

This is not full Git with branches and commit graphs.
It is a **single-branch distributed file system with optimistic concurrency and manual conflict resolution**.

---

# Core Principles

## 1. Deterministic Outcomes

For every sync cycle:

| Scenario           | Expected Behavior          |
| ------------------ | -------------------------- |
| No changes         | No push, no pull           |
| Remote-only change | Pull from remote           |
| Local-only change  | Push to remote             |
| Both changed       | Conflict triggered         |
| Conflict resolved  | Clean state, no re-trigger |

No silent overwrites.
No hidden state transitions.
No ambiguous outcomes.

---

## 2. Per-File Isolation

Each file is versioned and processed independently.

* Version tracking is per file
* Conflicts are per file
* Resolution is per file
* Multiple files may conflict simultaneously
* Resolution of one file does not affect others

---

## 3. Version-Based Optimistic Concurrency

Each file maintains:

* `serverVersion`
* `baseVersion`
* Local content hash
* Local mirror path

Conflict rule:

```
If baseVersion != current serverVersion
→ Conflict
```

This guarantees no silent overwrites.

---

# Sync Engine Architecture

Sync operates in 4 deterministic phases:

## Phase 1 — Process Pending Mutations (Push)

* All `status == pending` mutations are pushed
* On success:

  * serverVersion updated
  * mutation removed
* On conflict:

  * mutation marked failed
  * conflict persisted

---

## Phase 2 — Manifest Diff

Server returns:

* to_push
* to_pull
* conflicts (paths only)

For manifest conflicts:

* Synthetic mutation row is created (status = failed)
* No conflict snapshot file is created
* conflictFilePath = null

Invariant:

> Every conflict must correspond to a MutationQueue row.

---

## Phase 3 — Push Missing Files

Files listed in `to_push` are pushed
(excluding those already handled in Phase 1 via processedPaths).

---

## Phase 4 — Pull Missing Files

Files listed in `to_pull` are downloaded.

On pull:

* Local mirror updated
* serverVersion updated
* No conflict state

---

# Conflict Semantics

There are two conflict types:

## Type A — Push Conflict (Phase 1)

Occurs when:

```
Client pushes with stale baseVersion
```

Server response includes:

* Conflict file path
* Remote snapshot

UI shows:

* Local version
* Remote snapshot
* Option to merge

---

## Type B — Manifest Conflict (Phase 2)

Occurs when:

```
Server manifest detects newer version before push
```

No conflict snapshot file exists.

UI shows:

* Local version
* Message: "Server version is newer."
* Accept Remote downloads latest server file
* Keep Local overwrites server on next sync

---

# Conflict Resolution State Machine

## Keep Local

Transition:

```
failed → pending
update baseVersion → latest serverVersion
next sync → Phase 1 push
server overwritten
conflict cleared
```

Must guarantee:

* Conflict does NOT re-trigger
* Server version increments
* Mutation removed after successful push

---

## Accept Remote

Behavior:

* Download server version
* Overwrite local file
* Remove mutation
* Update serverVersion
* No push required

---

## Manual Merge

User edits content manually:

```
Edit merged content
→ treat as Keep Local
→ pending mutation
→ push on next sync
```

---

# Multi-File Conflict Handling

If multiple files are modified concurrently:

* Each file creates its own mutation row
* Each file appears independently in conflict list
* Resolution per file
* Sync resolves independently per file

No global lock.

---

# Required Guarantees

The system must guarantee:

1. No silent overwrites
2. No infinite conflict loops
3. No duplicate synthetic mutations
4. No re-push of already processed files
5. No crash due to null JSON parsing
6. No cross-file interference
7. Clear user-visible state transitions

---

# Explicit Non-Goals (For Now)

We are NOT implementing:

* CRDT
* Branching
* Commit graph
* Full Git history
* Automatic 3-way merge engine
* Tombstones (delete propagation) — future stage

Deletes from server may currently resurrect due to lack of tombstones.
This will be addressed in a future stage.

---

# Future Enhancements (Optional Roadmap)

To evolve toward stronger Git-like semantics:

1. Store base snapshot for true 3-way merge
2. Introduce tombstone tracking for delete propagation
3. Add background sync with concurrency guard
4. Add version history per file
5. Add diff preview in conflict screen

---

# Final Definition of Success

The system behaves like this:

### Remote Only Change

Laptop changes → Sync → Pulled 1 file

### Local Only Change

Mobile changes → Sync → Pushed 1 file

### Concurrent Change

Laptop changes
Mobile changes
Sync → Conflict shown

User chooses:

* Keep Local → Server overwritten → Stable
* Accept Remote → Local overwritten → Stable
* Manual Merge → User-edited version pushed → Stable

No ambiguity.
No repeated conflicts after resolution.
Per-file isolation guaranteed.

---

This is the architecture JARVIS must follow.
