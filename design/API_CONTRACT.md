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

### 2026-08-06 — §29 exFAT store + user-triggered disk check

**Endpoint(s) or area:** `POST /api/v1/storage/configure` (`filesystem` field: `exfat` default | `ext4`); `POST /api/v1/storage/check` (new)

**Decision:** the store drive formats as exFAT by default — the remote-imaging workflow is "pack up, pull the drive, read it on any PC at home", and exFAT is the only filesystem Windows and macOS both read/write natively with no drivers. ext4 remains the rig-resident option. exFAT has no journal, so recovery after an unclean power cut is the new `/storage/check`: unmount → `fsck.exfat -y` (or `e2fsck -f -y` for ext4) → remount, result code `clean` or `repaired`. Deliberately user-triggered (a Storage-panel button), never automatic on mount — Joey's explicit call. Same 409 exclusions as configure (active run, in-flight exposure, capture scan) and the same scan-lock exclusivity. Helper mounts exFAT with `uid/gid` options (exFAT carries no Unix ownership; chown is skipped), and fstab still pins the filesystem UUID.

**Reasoning:** journaling's real benefit is bounded blast radius + automatic repair; with temp+rename frame writes, an on-rig fsck one tap away, the §28.8 rescan, and the mirror as second copy, that benefit no longer outweighed native take-home readability. NTFS (journaled + Windows-native) lost on macOS being read-only and the younger ntfs3 driver; FAT32 is disqualified by the 4 GB file cap (§77 SER); LKL/desktop ext4 drivers rejected (kernel-fork dependency, GPL, privileged raw-device access, corruption risk in the very scenario ext4 was chosen against).

**Spec ref:** `packaging/debian/opt/openastroara/scripts/configure-storage.sh` (`--fs`, `--check`), `Services/StorageDeviceService.cs`, `Endpoints/SystemEndpoints.cs`. openapi.yaml still pending its refresh (PORT_TODO).

**Related:** PR #923 (§29 arc), CHANGELOG [Unreleased]

### 2026-08-07 — §65 stretch echo + §65.4 cache maintenance + §36 add-on catalogs & seeds

**Endpoint(s) or area:** `POST /api/v1/frames/{id}/preview` (response headers `X-Ara-Stretch-Black/Midtone/White`; knobless manual auto-seeds); `GET/DELETE /api/v1/storage/cache` (new); `GET /api/v1/data-manager/packages` (six new catalog ids); `GET /api/v1/data-manager/dso-catalog` (magnitude-less nebulae pass the cull); `GET /api/v1/catalogs` (six new toggleable sets)

**Decision:**
- A manual-palette preview request with all three knobs null no longer applies the profile's static seeds (absolute-range values that render linear astro data black — signal lives below 2% of full scale). The server derives bp/mp/wp from the image's own STF statistics and echoes whatever manual values it ACTUALLY rendered with via `X-Ara-Stretch-*` response headers, so client sliders can always match the pixels. Headers only on manual renders; calibration frames still force linear and carry none.
- `GET /storage/cache` measures and `DELETE /storage/cache` sweeps the §65.4 sidecars (`*.thumb.jpg`, `*.preview.*.jpg`) under the save directory — best-effort, inaccessible-dir-safe, never touches FITS. Deletion is always recoverable: sidecars re-render on demand and via the boot warmer, hence a 200 with `{files, bytes}` rather than any confirmation ceremony server-side (the client owns the confirm dialog).
- Six add-on catalog packages (`sharpless-hii`, `ldn-dark`, `barnard-dark`, `vdb-reflection`, `abell-pn`, `arp-peculiar`) join the curated set — commit-pinned in `open-astro/sky-data` @ 9ce09f7, SHA-256-verified, normalized to the exact OpenNGC column layout so `SkyCatalogReader` needs no new parser. `SkyCatalogService` merges every installed DSO source (cache invalidates on install — no restart) and `/catalogs` grows six sets.
- The `.deb` bundles every curated package as a seed under `/opt/openastroara/seed-data/{id}/` (`packaging/seed-manifest.tsv` drives `build-deb.sh`; `DataManagerSeedManifestTest` locks it to the curated list). Boot installs missing packages from seeds; a Download request prefers a verifying seed over the network — offline-first for remote sites.
- `/dso-catalog`'s mag ≤ 12 cull no longer drops magnitude-less rows of nebula types (HII/EmN/RfN/DrkN/Neb/Cl+N/SNR/PN) — an integrated magnitude is a number those objects don't have, and requiring it made every Sh2/LDN/Barnard row unreachable by planning. Magnitude-less rows of other types (stars, dup stubs) stay dropped.

**Reasoning:** header echo (not a JSON envelope) keeps the preview response a plain image body — existing consumers unaffected, and the knobs are metadata about the render, which is what headers are for. Seeds reuse the exact pinned artifacts + SHA path rather than a parallel format so one verification chain covers network and bundle installs.

**Spec ref:** `Endpoints/ImageEndpoints.cs`, `Endpoints/SystemEndpoints.cs`, `Services/{DataManagerService,SkyCatalogService,SkyCatalogReader,PreviewCacheMaintenance,ThumbnailWarmerService}.cs`, `packaging/{build-deb.sh,seed-manifest.tsv}`. openapi.yaml still pending its refresh (PORT_TODO).

**Related:** branch library-photos-redesign, CHANGELOG [Unreleased]

### 2026-09-20 — #1068 run ETA published by the sequencer

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` (`SequenceRunStateDto`), WS `sequence.progress` (and the other run-lifecycle frames `EmitAsync` publishes), `sequence.instruction_failed` and `sequence.run_items_changed` payloads.

**Decision:** *(Superseded in part by the #1080 entry below: the client shows the daemon figure first and the observed rate only as a fallback; a `TakeExposure`/`WaitForTime`/`WaitForTimeSpan` that estimates zero costs zero rather than the 15 s nominal; a DISABLED subtree is out of the total; a `ParallelContainer` pass costs its longest child.)* two optional fields, `estimated_total_seconds` and `estimated_remaining_seconds` (double, null until the run tree has loaded), computed by `RunEtaEstimator` from the LIVE tree: a leaf costs its own `GetEstimatedDuration()` (TakeExposure = exposure time, WaitForTime = the wait, …) or a flat 15 s when it reports none; a container multiplies its children by its `LoopCondition.Iterations`; remaining credits terminal leaves (finished/failed/skipped/disabled), completed loop passes, and counts only the unfinished children of the pass in progress. The client's `estimateRunEta` body walk is deleted; its header keeps only the display blend (observed elapsed rate once ≥10 % and ≥2 leaves are done, else the daemon's remaining figure, else nothing is shown).

**Reasoning:** the client re-implemented a cruder copy of the sequencer's duration model (exposure × iterations + 15 s/instruction) because run state never exposed it — a duplicate of execution-side logic that drifted as instructions gained real estimates. The daemon owns the tree and its statuses, so it is the only place a remaining figure that credits completed passes can be computed.

**Spec ref:** `Services/RunEtaEstimator.cs`, `Services/SequencerService.cs` (`RunState.EstimatedSeconds`, `EmitAsync`), `Services/SequencerService.LiveEdit.cs`, `Contracts/SequenceDtos.cs`. openapi.yaml still pending its refresh (PORT_TODO).

**Related:** #1068 (from the 2026-09-20 client/server separation audit), CHANGELOG [Unreleased]

### 2026-09-21 — #1080 run ETA follow-ups: blend order, item sets, side effects, per-tick cost

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` (`estimated_total_seconds` / `estimated_remaining_seconds`, no wire change); the client's run-header blend.

**Decision:** (1) the client shows the daemon's remaining figure whenever it is present (a non-negative number); the observed elapsed rate is only the fallback for a daemon that sent none. This reverses the #1068 blend order. (2) `estimated_total_seconds` and `estimated_remaining_seconds` count the same items: a DISABLED subtree is out of both (a SKIPPED one is credited for the pass in progress only, since `ResetProgress()` re-runs it on the next loop pass); a `ParallelContainer` pass costs its longest child, not the sum. (3) An instruction with a duration model of its own (`TakeExposure`, `WaitForTime`, `WaitForTimeSpan`) that estimates zero costs zero (an unset or invalid exposure of 0, an already-passed wait); only an instruction whose base estimate is the `Zero` placeholder gets the 15 s nominal. (4) `WaitForTime.GetEstimatedDuration()` no longer assigns `RolloverTime`: an estimate never writes the live tree. (5) The two tree walks are cached against a run tree version (bumped when a leaf changes status or the run reaches a lifecycle state through the `State` setter; pause/resume go through `TryTransition` and rely on the TTL; never bumped on a same-status progress tick) plus a 250 ms TTL on a monotonic clock for the time-based estimates, so the per-report checkpoint, every WS publish and every `GET …/state` share one walk per tick and a lifecycle frame is never stale. Known limits, unchanged: a loop gated only by a `TimeCondition`/`TimeSpanCondition`/horizon (no `LoopCondition`) counts as one pass; `estimated_total_seconds` is not constant across a run when the body holds a `WaitForTime` (it estimates time-until-target), so a consumer must not treat the total as fixed.

**Reasoning:** the observed rate counts leaves, not loop passes (a SmartExposure of 60×120 s is two leaves) and its elapsed time includes pauses, so it was worst on exactly the loop-heavy sequences the daemon figure was built for; a total that counted disabled blocks the remaining figure credited read as progress that never happened; the estimator ran per capture tick on ARM64.

**Spec ref:** `Services/RunEtaEstimator.cs`, `Services/SequencerService.cs` (`RunState.EstimatedSecondsLocked`), `OpenAstroAra.Sequencer/SequenceItem/Utility/WaitForTime.cs`, `client/…/lib/models/sequence/run_eta.dart`; tests in `RunEtaEstimatorTest` and `run_eta_test.dart`.

**Related:** #1080 (from the #1077 reviews), CHANGELOG [Unreleased]

### 2026-09-20 — #1067 Alpaca device-name lookup proxied through the daemon

**Endpoint(s) or area:** `GET /api/v1/equipment/guider/alpacadevicenames?host=<host>&port=<1..65535>` (new).

**Decision:** the daemon performs `GET http://host:port/management/v1/configureddevices` (3 s cap, plain http) and answers `200 {"names": {"<devicetype>/<devicenumber>": "<DeviceName>"}}` with the type lowercased (`"camera/1"`). Entries missing a type, number or name (or with an empty name) are skipped. An unreachable host, non-2xx answer or malformed body yields `200 {"names": {}}` (best-effort labelling, never an error status). Empty host or an out-of-range port is a 400. The client's direct call to the Alpaca host is deleted.

**Reasoning:** this was the one place the Flutter client reached equipment without the daemon, and it failed whenever the client machine could not route to the rig's Alpaca LAN. Only the JSON body is parsed and only names come out, on a trusted-LAN surface (§52/§67).

**Spec ref:** `Services/AlpacaManagementClient.cs`, `Endpoints/EquipmentEndpoints.cs` (`GetAlpacaDeviceNamesAsync`), `Contracts/EquipmentDtos.cs` (`AlpacaDeviceNamesResponseDto`). openapi.yaml still pending its refresh (PORT_TODO).

**Related:** #1067 (from the 2026-09-20 client/server separation audit), CHANGELOG [Unreleased]
### 2026-09-20 — #1066 filter-wheel first-connect home moves daemon-side

**Endpoint(s) or area:** `POST /api/v1/equipment/filterwheel/connect` (side effect); no wire change.

**Decision:** the FIRST successful connect of a given wheel (by Alpaca UniqueId) per daemon session claims the home at connect time; the decision is taken on the first refresh tick (the seed read, or a later 2 s tick of the same connection) that reads a KNOWN position — if it is not slot 0 the daemon commands `Position = 0` in the background (the same path as `POST /filterwheel/change`). Already-at-0 counts as homed. A later (re)connect of the same wheel — including the §42.3 auto-reconnect — never re-homes, whether or not the first connection ever reported a position. An explicit filter change accepted while the decision is pending (`POST /filterwheel/change` or a sequence `SwitchFilter`) retires it: a requested slot is a deliberate position and is never overridden by the home. The pending window is bounded to the seed read plus four ticks (~8 s); a wheel still reporting an unknown position after that is left alone (logged). The client's own first-launch home (`ExposureController._homeToSlot0`) and its `homing` UI flag are deleted; the picker follows the wheel's observed `current_slot` like any other move.

**Reasoning:** unrequested hardware motion was client policy, so it only happened when a client was attached and once per app session — the daemon is the hardware orchestrator (PORT_DECISIONS 2026-07-15) and the once-per-session guard there also protects a running sequence from a reconnect-triggered re-home.

**Spec ref:** `Services/FilterWheelService.cs` (`ConnectInBackground`, `ClaimFirstConnectHome`, `NeedsHomeToDefaultSlot`).

**Related:** #1066 (from the 2026-09-20 client/server separation audit), CHANGELOG [Unreleased]
### 2026-09-20 — #1065 cooling-fan interlock moves daemon-side

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/cooler` (now also syncs the fan; a failed sync is an `equipment.fault`, the call's own status is unchanged); `POST /api/v1/equipment/switch/{id}/value` (new 409 refusal).

**Decision:**
- *(Superseded in part by the #1076 entry below: ordering on cooler-on, the cached-value skip, the never-fails rule and the fault sentences.)* After a committed cooler write the daemon writes the first connected switch whose name contains "Thermal Switch" and which exposes a writable port named "Fan" to that port's own `max` (cooler on) or `min` (cooler off). No such switch = no-op; a port whose CACHED value already holds the target is not re-written (the §58 warm ramp calls the cooler once a minute), so a stale cache under a failing port read can skip a write (#1076). A failed fan write never fails the cooler call (the cooler change has landed, and the §58 warm ramp must reach its final cooler-off): it is published as an `equipment.fault` of kind `op_error` for the switch ("the cooler is on|off, but the cooling fan could not be synced (…) — check the fan") and logged.
- A switch-value write that takes that same Fan port to `value <= min` is refused with 409 unless the camera resolved with `runtime.cooler_on == false` (a not-connected camera also reads as off). While a connected Thermal Switch's port snapshot has not been read yet (up to one refresh interval after connect) the port cannot be identified, so a write of `value <= 0` to ANY of its ports is held to the same rule — it asks the camera exactly like an identified fan-off, so it goes through when the cooler resolves off (a fan-on or a mid-range value is never held). A switch that is not Connected (Error / Disconnected / unknown id) gets the write path's own "not connected" refusal, never the fan one. Cooler on, or no camera service / a throwing status read, refuses. ~~Known gap (#1076): a connected camera whose `CoolerOn` property read fails is reported as `cooler_on: false` by `CameraStateDto`, so that case is allowed.~~ Closed by the #1076 entry below (`cooler_state_known`). Superseded there as well: the cooler-on fan write now happens BEFORE the cooler write (not after), a cooler-on always writes the fan (no cached-value skip), a failed fan write refuses a cooler that is currently off, and the fault sentences are now "the cooling fan could not be started (…) — check the fan" / "the cooler is off, but the cooling fan could not be stopped (…) — check the fan". The refusal `detail` is the user-facing sentence the client used to generate locally.
- The client's own fan sync and fan-off pre-check are deleted; it renders the server's `detail` verbatim.

**Reasoning:** the client-side interlock only covered the client's own buttons — the §58 unattended shutdown's warm ramp, a second client or a direct API call all bypassed it. The daemon services are where those paths meet. ~~Not yet covered (#1076): the sequencer's `SetSwitchValue` instruction writes the switch raw~~ (covered by the #1076 entry below) and — still true — its `CoolCamera`/`WarmCamera` mediator stubs never reach `SetCoolerAsync`. The camera's `GetAsync` is the probe (cached runtime state), resolved lazily (`Func<>`) to break the CameraService ↔ SwitchService construction cycle; the camera's own post-cooler fan write bypasses the interlock (`ICoolingFanActuator`) because the cached cooler state may still read on for one tick.

**Spec ref:** `Services/CoolingFanInterlock.cs`, `Services/CameraService.cs` (`SyncCoolingFanAsync`), `Services/SwitchService.cs` (`FanOffRefusalForAsync`), `Endpoints/EquipmentEndpoints.cs`.

**Related:** #1065 (from the 2026-09-20 client/server separation audit), CHANGELOG [Unreleased]

### 2026-09-21 — #1072/#1078 MoveAxis band snapping, secondary fallback, time-based settle

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/moveaxis` (behaviour of the rate guard; no wire change).

**Decision:** the daemon caches each pad axis's AxisRates as `[Min, Max]` bands (read with the capabilities, never on the nudge path). A nonzero rate is SNAPPED (sign preserved): inside a band → unchanged; above the top band → that max; ~~below the lowest band → that min~~ (since #1085, raised only while within 4× of it, refused with 409 when slower — see the #1085 entry below); in a gap between bands (a discrete-rate mount has `Min == Max` steps) → the nearest band edge. An axis with no known bands still refuses (409). A secondary axis whose AxisRates never answered (unknown) while the primary's are known borrows the primary's bands (logged once per session) instead of losing N/S for the session; an honestly empty secondary (the mount offers no rates) stays refused, since MoveAxis on it throws by spec. The cache settles when both reads completed, when `CanMoveAxis` is false, or 30 s of wall clock after connect with a read still throwing — a burst of command-triggered refreshes can no longer exhaust the retries in a second. The capabilities' `move_axis_rates_deg_per_sec` list is unchanged (both endpoints of every band, capped at the secondary's max).

**Reasoning:** #1064 capped only the maximum, so a picker preset under a band's minimum or in a gap between discrete steps was forwarded verbatim and rejected by the driver as a 500-shaped InvalidValue; #1069's review flagged the pass-counted settle and the secondary-axis regression. Snapping keeps every forwarded rate one the mount advertised.

**Spec ref:** `Services/TelescopeService.cs` (`SnapMoveAxisRate`, `ShouldSettleAxisRates`, `ReadAxisBands`, `EndpointsOf`), `OpenAstroAra.Test/TelescopeMoveAxisClampTest.cs`.

**Related:** #1072, #1078 (from the #1069 reviews), CHANGELOG [Unreleased]

### 2026-09-21 — #1079 in-flight home token; SwitchFilter before the slot list

**Endpoint(s) or area:** `POST /api/v1/equipment/filterwheel/change` and the sequencer's `SwitchFilter` (mediator `ChangeFilter`); no wire change.

**Decision:** the first-connect home carries a generation token when dispatched; every explicit change (REST or `SwitchFilter`), disconnect, connection loss, newer connect and dispose bump it, and the home task re-checks it under the gate right before its `Position = 0` write and steps aside (logged) if it moved — so a change accepted after the decision but before that re-check is never followed by the home (the token check and the device write are not one atomic step — no device I/O runs under the gate — so a change that lands in the microseconds between them is the one residual window). A `SwitchFilter` retires the pending/in-flight home first, before anything else it does and whether or not the change itself goes ahead; one that arrives before the wheel's slot list has been read then waits up to 6 s for it (polling the cache, no device I/O of its own) instead of being skipped. Dispose logs a still-pending home like disconnect does.

**Reasoning:** #1073's review scoped the guarantee to "accepted while the decision is pending"; the sub-millisecond dispatch window and the boot-auto-connect-then-first-SwitchFilter case both let the daemon park on 0 behind a sequence that believed it had switched.

**Spec ref:** `Services/FilterWheelService.cs` (`HomeInBackground`, `HomeStillWanted`, `RetirePendingHome`), `Services/FilterWheelService.Mediator.cs` (`WaitForSlotsAsync`), `OpenAstroAra.Test/FilterWheelFirstConnectHomeTest.cs`.

**Related:** #1079 (from the #1073 reviews), CHANGELOG [Unreleased]

### 2026-09-21 — #1085 bounded snap-up on MoveAxis; presets respect the reported minimum

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/moveaxis` (behaviour of the rate guard; no wire change); the client's speed-preset ladder.

**Decision:** a requested magnitude below the axis's lowest band minimum is raised to that minimum only while it is within `SnapUpBoundFactor` (4×) of it; anything slower is refused with 409 ("… more than 4x slower than the mount's slowest rate … Pick a faster speed") rather than turned into a nudge many times faster than picked. Client: the daemon publishes both ends of every band in `move_axis_rates_deg_per_sec`, so two reported rates are read as one band `[min, max]`: the percentage presets of `max` drop every value under `min`, and when any was dropped the minimum itself is offered as the slowest chip ("min · 2°/s"; a preset landing exactly on `min` keeps its percentage label). A single rate keeps the plain preset ladder; a driver ladder of three or more rates is still shown verbatim (previously two rates were shown verbatim as two chips).

**Reasoning:** #1082's reviews: on a mount whose lowest band starts high, the 1 % preset snapped up to the band minimum moved the mount 17–33× faster than the user picked with only a server-side log.

**Spec ref:** `Services/TelescopeService.cs` (`SnapMoveAxisRate`, `SnapUpBoundFactor`), `client/…/lib/util/slew_rates.dart`, tests in `TelescopeMoveAxisClampTest` and `slew_rates_test.dart`.

**Related:** #1085, CHANGELOG [Unreleased]

### 2026-09-21 — #1075 filter-wheel policy section (home on first connect)

**Endpoint(s) or area:** `GET/PUT /api/v1/profile/filter-wheel/policy` (new); `profile.json` gains `filter_wheel_policy` (optional, back-filled to the default by the normalizer).

**Decision:** `{ "home_on_first_connect": bool }` (default `true`). `FilterWheelService.ConnectInBackground` reads the policy off the gate at connect; when off, the once-per-session claim is still consumed but no home is decided (logged), so turning the policy on later never homes an already-connected or reconnecting wheel mid-session. A failing policy read falls back to the default (home on), logged. Whole-section PUT like every other profile section; no validation beyond the JSON shape.

**Reasoning:** #1073's reviews: the daemon-side home moved hardware on a manual connect too with no user-facing switch; a mono rig that lives on Hα had no way to opt out.

**Spec ref:** `Contracts/ProfileDtos.cs` (`FilterWheelPolicyDto`), `Contracts/ProfileSnapshotDto.cs`, `Services/ProfileSnapshotNormalizer.cs`, `Services/{File,InMemory}ProfileStore.cs`, `Endpoints/ProfileEndpoints.cs`, `Services/FilterWheelService.cs` (`HomeOnFirstConnectEnabled`), client `state/settings/filter_wheel_policy_state.dart`, settings/help registry `eq.filterwheel.home_on_first_connect`.

**Related:** #1075 (from the #1073 reviews), CHANGELOG [Unreleased]

### 2026-09-21 — #1076 cooling-fan interlock: sequencer path, fail-closed states, fan-first, late connect

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/cooler`, `POST /api/v1/equipment/switch/{id}/value`, the sequencer's `SetSwitchValue`, `CameraStateDto` (new optional `cooler_state_known`, default true).

**Decision:**
- The sequencer's `SetSwitchValue` goes through the same fan-off interlock as the REST write; a refusal fails the instruction (`SequenceEntityFailedException` with the refusal sentence) so Attempts / `instruction_failed` engage.
- `CameraStateDto.cooler_state_known` is false when the `CoolerOn` property read threw this pass (`cooler_on` then reads false by fallback). The interlock treats that, or a camera in `Error`, as UNKNOWN (fan-off refused); a camera that is not connected (`Disconnected`, or no camera device configured at all) reads as cooler off — no TEC this daemon started can be running — and so does a camera whose capabilities report `has_cooler: false` (its `CoolerOn` read throws on every pass, which is definitively "no TEC", not unknown).
- Cooler ON: the fan is written FIRST (always — a stale cached port value never skips it). If the rig has a fan port and that write fails while the cooler is not KNOWN to be on (currently off, or its `CoolerOn` read threw this pass — unknown fails closed), the call is refused with 409 ("the cooling fan could not be started … check the fan") and an `op_error` fault is published; nothing was committed. With the cooler KNOWN to be on (a set-point change; the §58 unattended warm ramp's once-a-minute steps) a failed fan write is the same fault + log but never fails the call. The warm ramp is itself robust to a refused or rejected step (`UnattendedShutdownService.WarmCoolerAsync` logs it, stops stepping, and still issues its final cooler-off and the disconnect), so an unreadable cooler state mid-ramp can never leave the TEC on. Cooler OFF: cooler write first, then the fan (skipped when the cached port already holds the floor); a fan-off failure never fails the call (fault + log, as before).
- A Thermal Switch that connects while the camera is (known to be) cooling has its fan started as part of the connect (best-effort, fault on failure).

**Reasoning:** the #1070 reviews listed these as the interlock's remaining holes: the raw sequencer write, the fail-open `CoolerOn`-read-fails / camera-dropped states, the "TEC on, fan off" window on cooler-on, the cache short-circuit skipping a needed fan-on, and a switch connecting after the cooler.

**Spec ref:** `Services/CoolingFanInterlock.cs` (`CoolerStateFor`, `FanSyncRequest`), `Services/SwitchService.cs` (`ProbeCoolerStateAsync`, `SyncFanToCoolingCameraAsync`), `Services/SwitchService.Mediator.cs`, `Services/CameraService.cs` (`SetCoolerAsync`, `SyncCoolingFanAsync`), `OpenAstroAra.Test/CoolingFanInterlockBenchTest.cs`.

**Related:** #1076 (from the #1070 reviews), CHANGELOG [Unreleased]
