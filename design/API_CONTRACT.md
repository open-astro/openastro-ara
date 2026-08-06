# OpenAstro Ara — API contract design log

Append-only design log for the server↔client REST + WebSocket API. One entry per endpoint or wire-shape decision.

Per PORT_PLAYBOOK.md §1: created Phase 0.5 (this file), populated starting Phase 5 (API contract definition) per §9.

The source-of-truth contract itself lives in `OpenAstroAra.Server/openapi.yaml` (Phase 5+). This file captures the *reasoning* behind each contract decision — DTO shapes, idempotency choices, WebSocket event taxonomy, error-shape conventions — for future contributors who need to understand "why does endpoint X look like this."

---

## 2026-05-26 — Phase 5: initial OpenAPI 3.1 contract

**Endpoint(s) or area:** entire `/api/v1/*` surface

**Decision:** hand-written `OpenAstroAra.Server/openapi.yaml` covering 6 endpoint groups (Server, Equipment, Sequence, Image, Log, Stream). Equipment + Sequence + Image + Log return JSON; Image preview returns JPEG bytes; Image FITS returns FITS bytes; Stream documented in description-form (OpenAPI 3.1 paths can't express WebSocket — see §60.9 for the live taxonomy).

**Reasoning:**
- Each endpoint group corresponds to a phase (Equipment=6, Sequence=7, Image=8, Log+Stream=9). Defining the full contract upfront lets each subsequent phase implement against a stable target.
- Operation endpoints (connect, sequence start/pause/abort, etc.) return 202 with an `OperationAccepted` body containing `operation_id`. Live progress comes via WebSocket `operation.*` events. Avoids blocking long-running operations on the HTTP response.
- `Problem` shape follows RFC 7807. Validation errors include a `field`/`code`/`message` triplet per error per §73.
- `Frame` schema includes `quality_score` per §50.10 (composite scoring) — present even though the implementation lands in Phase 8.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml`

**Related:** PORT_PLAYBOOK.md §9, §60.9 (WS taxonomy), §73 (error shape), §50.10 (quality score)

---

### Template for future entries

```
### YYYY-MM-DD — <short title>

**Endpoint(s) or area:** `POST /api/v1/...`

**Decision:** <what was decided>

**Reasoning:** <why; alternatives considered>

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/...`

**Related:** §X.Y of PORT_PLAYBOOK.md, PR #N
```

### 2026-07-27 — §38.9 live mid-run sequence editing

**Endpoint(s) or area:** `POST /api/v1/sequences/{id}/run/items`, `DELETE /api/v1/sequences/{id}/run/items?path=i,j`, `POST /api/v1/sequences/{id}/run/items/move`; WS `sequence.run_items_changed`

**Decision:** three targeted live-edit operations on an ACTIVELY RUNNING sequence — insert a container node (a target block), remove a not-yet-started node, reorder a not-yet-started node within its parent. Paths are child-index paths over the body's `Items` arrays, rooted at the body's top container. Positions at/above the parent's last started item are locked (422); removing/moving a started item is refused (409 `sequence-item-already-started` — use skip-current for the running target); edits during teardown or wind-down are refused (409 `sequence-run-not-mutable`). On success the server mutates the executor's in-memory tree, re-bases the run's progress denominator, persists the RE-SERIALIZED live tree as the stored body (file == executing plan by construction, including Newtonsoft `$id`/`$ref` numbering), and emits `sequence.run_items_changed { sequence_id, run_id, op, instructions_completed, instructions_total }`. Returns the updated `Sequence` dto. All three ops take an optional `Idempotency-Key` header with a per-run replay cache (a retry after a lost response returns the original Applied outcome instead of double-applying). The DELETE addresses its item via the query string (`?path=1,2`) — DELETE bodies are dropped by some intermediaries. The plain `PATCH /sequences/{id}` keeps its 409-while-running refusal.

**Reasoning:** the engine's `SequentialStrategy` re-snapshots container children at every instruction boundary and executes the first CREATED item, so lock-guarded pending-item mutation is picked up naturally — targeted ops ride that; a generalized PATCH-while-running was rejected because the body has no stable node ids, making "started items untouched" undiffable. Whole-body re-serialization from the live tree was chosen over JSON splicing to avoid `$id` collisions. A persist failure after tree mutation is surfaced as 500 (`sequence-live-edit-persist-failed`) rather than rolled back — the plan kept executing, so an inverse tree op is fragile; the client re-fetches and retries.

**Spec ref:** `OpenAstroAra.Server/Endpoints/SequenceEndpoints.cs` (§38.9 group), `OpenAstroAra.Server/Services/SequencerService.LiveEdit.cs`

**Related:** design/RUN_REDESIGN.md (two moods — live mood gains scoped editing), PORT_PLAYBOOK.md §38

### 2026-07-27 — §38.10 resume refinement (re-center + optional refocus)

**Endpoint(s) or area:** `POST /api/v1/sequences/{id}/resume` (optional body added); WS `sequence.resume_recentering`

**Decision:** the resume route accepts an optional `{ "recenter": bool, "refocus": bool }` body. When the run is still paused on the SAME target it paused on (reference identity of the RUNNING DSO container, snapshotted in OnPauseEntered), the daemon plate-solves + re-centers — and on request runs an autofocus sweep — BEFORE releasing the pause gate, while the engine is suspended and the rig idle. Absent body = `recenter=true, refocus=false`, so pre-§38.10 clients gain the pointing refinement transparently. The choice/prompt lives CLIENT-side pre-resume (dialog on the Resume tap): a daemon-side prompt would hold the gate hostage to a client that may be gone. Refinement is bounded (5-min re-center cap), best-effort (no solver/equipment → skip + honest notification, mirroring §35's verify-pointing messages), single-flight per run (a double-tapped Resume neither re-runs it nor yanks the gate open mid-solve), cancelled by Abort/Stop, and ALWAYS ends in a gate release. The §35 safety auto-resume path is untouched.

**Reasoning:** reuses the §35 `TryRecenterQuietlyAsync` machinery (ICenteringService.CenterOnTarget + bounded token) and the sequencer's own `IAutofocusExecutor` (same sweep the RunAutofocus instruction uses) rather than injecting instructions into the plan — no live-edit locking concerns and works regardless of the paused position. Rejected: the §48 WS-prompt pattern (fire-and-forget fits auto-flats; a blocking resume prompt does not).

**Spec ref:** `OpenAstroAra.Server/Services/SequencerService.ResumeRefinement.cs`, `SequenceEndpoints.cs` resume route

**Related:** §35 (SafetyReactionService recenter), §59 (autofocus executor), design/RUN_REDESIGN.md

### 2026-08-05 — §12c.2 frame statistics + §44 mirror naming + §29 storage identifiers

**Endpoint(s) or area:** `GET /api/v1/frames/{id}/histogram` (new); `GET /api/v1/server/backup-stream/queue` entry shape (`relative_path` added); `POST /api/v1/storage/configure` (`uuid` field accepts a `/dev/` node path; empty `confirm_label` legal only for truly label-less drives)

**Decision:**
- `frames/{id}/histogram` returns the frame's RAW 16-bit statistics: 128 bins (ADU >> 9) for plotting, exact mean/SD/median/MAD from a full-resolution count pass, min/max with their pixel counts, true-rail clip fractions (exactly 0 / 65535 — the 512-ADU-wide bottom bin would flag every bias-level dark as clipped), and the catalog's width/height/bit-depth/stars/hfr/gain/offset merged fresh at serve time (analysis lands asynchronously). Pixel stats cache as `<stem>.hist.v2.json` beside the §65.4 preview variants, warmed for free during the capture-time preview pre-warm.
- Backup-stream queue entries carry `relative_path`: the frame's §29-templated path relative to the store root, forward-slashed; null for frames outside the current store (drive swapped) or from older servers. The desktop mirror reproduces the layout under `Backups/<host>/`, sanitizing every segment independently — a compromised server cannot escape the mirror root; absolute rig paths never cross the wire.
- Storage configure accepts a `/dev/[A-Za-z0-9]{1,32}` node path as the identifier for the blank-disk case (no filesystem → no UUID); fstab always pins the post-mkfs filesystem UUID, never a device path. Empty confirm-label passes the server only when the drive's ACTUAL label is equally empty (helper re-checks); the client adds a type-ERASE bar for that case, deliberately client-side-only.

**Reasoning:** statistics computed rig-side because the client only ever holds the stretched JPEG — the numbers must come from the raw pixels, and the Pi already has them in memory at preview time. `relative_path` rather than client-side re-derivation because only the server knows which template expanded and against which store root.

**Spec ref:** `OpenAstroAra.Server/Endpoints/ImageEndpoints.cs` (histogram), `Services/BackupStreamService.cs`, `Services/StorageDeviceService.cs`. NOTE: `openapi.yaml` is broadly stale (frozen pre-§29/§44/§45/§63/§64 — see PORT_TODO "openapi.yaml refresh") and does not yet describe these.

**Related:** PR #923 (§29 arc), branch backup-mirror-names (§44 naming, §12c.2 statistics), CHANGELOG [Unreleased]
