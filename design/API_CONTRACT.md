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

### 2026-07-31 — guiding auto-tune proposal and transaction endpoints

**Endpoint(s) or area:** `/api/v1/guiding/autotune/*`; WS `guiding.autotune.*`

**Decision:** expose capabilities, latest in-memory session, start-analysis, cancel, explicit apply, and
rollback. Start analyzes accepted live `GuideStep` telemetry already held by ARA; it does not silently begin
a long hardware experiment. Apply is explicit and uses PHD2's existing exposure, algorithm, DEC-mode, and
guide-output RPCs inside a snapshot/restore boundary. Mount guide-rate writes remain locked until a safe
Alpaca adapter exists.

**Reasoning:** the current server has no persisted auto-tune store, interleaved experiment worker, or telescope
guide-rate transaction seam. Exposing those as supported would create unsafe false capability. The first wire
slice therefore makes measured planning inspectable and keeps unsupported operations visible in the capability
payload. The next slice can add persistent experiments without breaking this route shape.

**Spec ref:** `design/GUIDER_AUTO_TUNING.md`, `OpenAstroAra.Server/Endpoints/GuidingAutoTuneEndpoints.cs`

**Related:** `OpenAstroAra.Core/Guiding`, `GuidingTelemetryCollector`, `PHD2Guider.AutoTune.cs`

### 2026-07-31 — guiding auto-tune bounded experiments and persisted report

**Endpoint(s) or area:** `/api/v1/guiding/autotune/*`; WS `guiding.autotune.*`; SQLite auto-tune tables

**Decision:** `POST /guiding/autotune/sessions` accepts `dry_run`, evaluation, stabilization, and
characterization limits. A live request runs in a server-owned background task. It captures native mount motion
with guide output disabled, restores output in `finally`, evaluates bounded coordinate candidates with interleaved
baseline windows, persists session/telemetry windows, calculates deterministic moving-block bootstrap confidence,
and leaves a tested winner in `Proposed` until explicit apply. Guide-rate candidates use Alpaca readback and force
calibration invalidation. `GET /sessions/latest/report` returns a Markdown report. Cancel, failure, device loss, and
apply errors enter rollback and verify the restored values through existing readback methods.

**Reasoning:** long hardware work cannot run in the request thread or client process. Persisted windows make failure
analysis and replay possible. A candidate cannot be applied from a planner-only result; it must have live metrics,
an improvement threshold, no critical regression, and at least 80% bootstrap confidence.

**Spec ref:** `OpenAstroAra.Server/Endpoints/GuidingAutoTuneEndpoints.cs`, `design/GUIDER_AUTO_TUNING.md`

**Related:** `OpenAstroAra.Core/Guiding/GuidingResponseAnalyzer.cs`, `GuidingAutoTuneRepository`,
`TelescopeService.SetGuideRatesAsync`

### 2026-07-31 — optional main-camera validation

**Endpoint:** `POST /api/v1/guiding/autotune/sessions`

**Decision:** add `use_main_camera_validation` plus bounded exposure, binning, and frame-count fields. The
server captures analysis frames through the existing camera gate, measures median star eccentricity, includes
it in candidate scoring, and rejects a live request when the camera, stars, or main image scale are unavailable.
Dry-run requests never capture or mutate the camera.

**Reasoning:** guide RMS alone can select settings that worsen science-camera star shape. Reusing the existing
analysis capture path keeps camera ownership and cancellation rules centralized.

**Related:** `MainCameraGuidingValidator`, `IAnalysisFrameSource`, `GuidingScorer.CalculateScore`
