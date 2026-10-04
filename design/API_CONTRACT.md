# OpenAstro Ara — API contract design log

Append-only design log for the server↔client REST + WebSocket API. One entry per endpoint or wire-shape decision.

**Still live (kept when the other design-status docs were retired on 2026-09-28).** Since #1131 `OpenAstroAra.Server/openapi.yaml` is a generated snapshot of the daemon's own OpenAPI document (every mapped REST route; `OpenApiContractSnapshotTest` fails CI when it drifts), so the REST contract is that file plus the endpoint sources under `OpenAstroAra.Server/Endpoints/`. What the generator cannot express — the WebSocket wire protocol — lives in the section at the end of this file. Append an entry here in the PR that adds or changes a wire shape; breaking changes inside `/api/v1/` (permitted within v0.x) are recorded here too.

This file captures the *reasoning* behind each contract decision — DTO shapes, idempotency choices, WebSocket event taxonomy, error-shape conventions — for future contributors who need to understand "why does endpoint X look like this."

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

**Related:** `RUN_REDESIGN`, the retired Run redesign doc (two moods — live mood gains scoped editing; citation key, see `design/README.md`), PORT_PLAYBOOK.md §38

### 2026-07-27 — §38.10 resume refinement (re-center + optional refocus)

**Endpoint(s) or area:** `POST /api/v1/sequences/{id}/resume` (optional body added); WS `sequence.resume_recentering`

**Decision:** the resume route accepts an optional `{ "recenter": bool, "refocus": bool }` body. When the run is still paused on the SAME target it paused on (reference identity of the RUNNING DSO container, snapshotted in OnPauseEntered), the daemon plate-solves + re-centers — and on request runs an autofocus sweep — BEFORE releasing the pause gate, while the engine is suspended and the rig idle. Absent body = `recenter=true, refocus=false`, so pre-§38.10 clients gain the pointing refinement transparently. The choice/prompt lives CLIENT-side pre-resume (dialog on the Resume tap): a daemon-side prompt would hold the gate hostage to a client that may be gone. Refinement is bounded (5-min re-center cap), best-effort (no solver/equipment → skip + honest notification, mirroring §35's verify-pointing messages), single-flight per run (a double-tapped Resume neither re-runs it nor yanks the gate open mid-solve), cancelled by Abort/Stop, and ALWAYS ends in a gate release. The §35 safety auto-resume path is untouched.

**Reasoning:** reuses the §35 `TryRecenterQuietlyAsync` machinery (ICenteringService.CenterOnTarget + bounded token) and the sequencer's own `IAutofocusExecutor` (same sweep the RunAutofocus instruction uses) rather than injecting instructions into the plan — no live-edit locking concerns and works regardless of the paused position. Rejected: the §48 WS-prompt pattern (fire-and-forget fits auto-flats; a blocking resume prompt does not).

**Spec ref:** `OpenAstroAra.Server/Services/SequencerService.ResumeRefinement.cs`, `SequenceEndpoints.cs` resume route

**Related:** §35 (SafetyReactionService recenter), §59 (autofocus executor), `RUN_REDESIGN` (retired; citation key, see `design/README.md`)

### 2026-08-05 — §12c.2 frame statistics + §44 mirror naming + §29 storage identifiers

**Endpoint(s) or area:** `GET /api/v1/frames/{id}/histogram` (new); `GET /api/v1/server/backup-stream/queue` entry shape (`relative_path` added); `POST /api/v1/storage/configure` (`uuid` field accepts a `/dev/` node path; empty `confirm_label` legal only for truly label-less drives)

**Decision:**
- `frames/{id}/histogram` returns the frame's RAW 16-bit statistics: 128 bins (ADU >> 9) for plotting, exact mean/SD/median/MAD from a full-resolution count pass, min/max with their pixel counts, true-rail clip fractions (exactly 0 / 65535 — the 512-ADU-wide bottom bin would flag every bias-level dark as clipped), and the catalog's width/height/bit-depth/stars/hfr/gain/offset merged fresh at serve time (analysis lands asynchronously). Pixel stats cache as `<stem>.hist.v2.json` beside the §65.4 preview variants, warmed for free during the capture-time preview pre-warm.
- Backup-stream queue entries carry `relative_path`: the frame's §29-templated path relative to the store root, forward-slashed; null for frames outside the current store (drive swapped) or from older servers. The desktop mirror reproduces the layout under `Backups/<host>/`, sanitizing every segment independently — a compromised server cannot escape the mirror root; absolute rig paths never cross the wire.
- Storage configure accepts a `/dev/[A-Za-z0-9]{1,32}` node path as the identifier for the blank-disk case (no filesystem → no UUID); fstab always pins the post-mkfs filesystem UUID, never a device path. Empty confirm-label passes the server only when the drive's ACTUAL label is equally empty (helper re-checks); the client adds a type-ERASE bar for that case, deliberately client-side-only.

**Reasoning:** statistics computed rig-side because the client only ever holds the stretched JPEG — the numbers must come from the raw pixels, and the Pi already has them in memory at preview time. `relative_path` rather than client-side re-derivation because only the server knows which template expanded and against which store root.

**Spec ref:** `OpenAstroAra.Server/Endpoints/ImageEndpoints.cs` (histogram), `Services/BackupStreamService.cs`, `Services/StorageDeviceService.cs`. NOTE: `openapi.yaml` was broadly stale when this was written (frozen pre-§29/§44/§45/§63/§64); superseded by the #1131 regenerated snapshot, which describes these.

**Related:** PR #923 (§29 arc), branch backup-mirror-names (§44 naming, §12c.2 statistics), CHANGELOG [Unreleased]

### 2026-08-06 — §29 exFAT store + user-triggered disk check

**Endpoint(s) or area:** `POST /api/v1/storage/configure` (`filesystem` field: `exfat` default | `ext4`); `POST /api/v1/storage/check` (new)

**Decision:** the store drive formats as exFAT by default — the remote-imaging workflow is "pack up, pull the drive, read it on any PC at home", and exFAT is the only filesystem Windows and macOS both read/write natively with no drivers. ext4 remains the rig-resident option. exFAT has no journal, so recovery after an unclean power cut is the new `/storage/check`: unmount → `fsck.exfat -y` (or `e2fsck -f -y` for ext4) → remount, result code `clean` or `repaired`. Deliberately user-triggered (a Storage-panel button), never automatic on mount — Joey's explicit call. Same 409 exclusions as configure (active run, in-flight exposure, capture scan) and the same scan-lock exclusivity. Helper mounts exFAT with `uid/gid` options (exFAT carries no Unix ownership; chown is skipped), and fstab still pins the filesystem UUID.

**Reasoning:** journaling's real benefit is bounded blast radius + automatic repair; with temp+rename frame writes, an on-rig fsck one tap away, the §28.8 rescan, and the mirror as second copy, that benefit no longer outweighed native take-home readability. NTFS (journaled + Windows-native) lost on macOS being read-only and the younger ntfs3 driver; FAT32 is disqualified by the 4 GB file cap (§77 SER); LKL/desktop ext4 drivers rejected (kernel-fork dependency, GPL, privileged raw-device access, corruption risk in the very scenario ext4 was chosen against).

**Spec ref:** `packaging/debian/opt/openastroara/scripts/configure-storage.sh` (`--fs`, `--check`), `Services/StorageDeviceService.cs`, `Endpoints/SystemEndpoints.cs`. openapi.yaml was still pending its refresh when this was written; superseded by the #1131 regenerated snapshot.

**Related:** PR #923 (§29 arc), CHANGELOG [Unreleased]

### 2026-08-07 — §65 stretch echo + §65.4 cache maintenance + §36 add-on catalogs & seeds

**Endpoint(s) or area:** `POST /api/v1/frames/{id}/preview` (response headers `X-Ara-Stretch-Black/Midtone/White`; knobless manual auto-seeds); `GET/DELETE /api/v1/storage/cache` (new); `GET /api/v1/data-manager/packages` (six new catalog ids; seven with `wr-stars`, below); `GET /api/v1/data-manager/dso-catalog` (magnitude-less nebulae pass the cull); `GET /api/v1/catalogs` (six new toggleable sets; seven with `wolf-rayet`)

**Decision:**
- A manual-palette preview request with all three knobs null no longer applies the profile's static seeds (absolute-range values that render linear astro data black — signal lives below 2% of full scale). The server derives bp/mp/wp from the image's own STF statistics and echoes whatever manual values it ACTUALLY rendered with via `X-Ara-Stretch-*` response headers, so client sliders can always match the pixels. Headers only on manual renders; calibration frames still force linear and carry none.
- `GET /storage/cache` measures and `DELETE /storage/cache` sweeps the §65.4 sidecars (`*.thumb.jpg`, `*.preview.*.jpg`) under the save directory — best-effort, inaccessible-dir-safe, never touches FITS. Deletion is always recoverable: sidecars re-render on demand and via the boot warmer, hence a 200 with `{files, bytes}` rather than any confirmation ceremony server-side (the client owns the confirm dialog).
- Six add-on catalog packages (`sharpless-hii`, `ldn-dark`, `barnard-dark`, `vdb-reflection`, `abell-pn`, `arp-peculiar`) join the curated set — commit-pinned in `open-astro/sky-data` @ 9ce09f7, SHA-256-verified, normalized to the exact OpenNGC column layout so `SkyCatalogReader` needs no new parser. `SkyCatalogService` merges every installed DSO source (cache invalidates on install — no restart) and `/catalogs` grows six sets.
- A seventh, `wr-stars` (717 Galactic Wolf-Rayet stars, Sheffield catalogue / Rosslowe & Crowther 2015, sky-data @ 05404eb), uses type `WR*`: `/dso-catalog` passes those rows regardless of magnitude so the client's offline search resolves any WR number, and the client ranker skips star types. `/catalogs/wolf-rayet` overlays them.
- The `.deb` bundles every curated package as a seed under `/opt/openastroara/seed-data/{id}/` (`packaging/seed-manifest.tsv` drives `build-deb.sh`; `DataManagerSeedManifestTest` locks it to the curated list). Boot installs missing packages from seeds; a Download request prefers a verifying seed over the network — offline-first for remote sites.
- `/dso-catalog`'s mag ≤ 12 cull no longer drops magnitude-less rows of nebula types (HII/EmN/RfN/DrkN/Neb/Cl+N/SNR/PN) — an integrated magnitude is a number those objects don't have, and requiring it made every Sh2/LDN/Barnard row unreachable by planning. Magnitude-less rows of other types (stars except `WR*`, which pass regardless of magnitude — see the `wr-stars` bullet; dup stubs) stay dropped.

**Reasoning:** header echo (not a JSON envelope) keeps the preview response a plain image body — existing consumers unaffected, and the knobs are metadata about the render, which is what headers are for. Seeds reuse the exact pinned artifacts + SHA path rather than a parallel format so one verification chain covers network and bundle installs.

**Spec ref:** `Endpoints/ImageEndpoints.cs`, `Endpoints/SystemEndpoints.cs`, `Services/{DataManagerService,SkyCatalogService,SkyCatalogReader,PreviewCacheMaintenance,ThumbnailWarmerService}.cs`, `packaging/{build-deb.sh,seed-manifest.tsv}`. openapi.yaml was still pending its refresh when this was written; superseded by the #1131 regenerated snapshot.

**Related:** branch library-photos-redesign, CHANGELOG [Unreleased]

### 2026-09-20 — #1068 run ETA published by the sequencer

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` (`SequenceRunStateDto`), WS `sequence.progress` (and the other run-lifecycle frames `EmitAsync` publishes), `sequence.instruction_failed` and `sequence.run_items_changed` payloads.

**Decision:** *(Superseded in part by the #1080 entry below: the client shows the daemon figure first and the observed rate only as a fallback; a `TakeExposure`/`WaitForTime`/`WaitForTimeSpan` that estimates zero costs zero rather than the 15 s nominal; a DISABLED subtree is out of the total; a `ParallelContainer` pass costs its longest child.)* two optional fields, `estimated_total_seconds` and `estimated_remaining_seconds` (double, null until the run tree has loaded), computed by `RunEtaEstimator` from the LIVE tree: a leaf costs its own `GetEstimatedDuration()` (TakeExposure = exposure time, WaitForTime = the wait, …) or a flat 15 s when it reports none; a container multiplies its children by its `LoopCondition.Iterations`; remaining credits terminal leaves (finished/failed/skipped/disabled), completed loop passes, and counts only the unfinished children of the pass in progress. The client's `estimateRunEta` body walk is deleted; its header keeps only the display blend (observed elapsed rate once ≥10 % and ≥2 leaves are done, else the daemon's remaining figure, else nothing is shown).

**Reasoning:** the client re-implemented a cruder copy of the sequencer's duration model (exposure × iterations + 15 s/instruction) because run state never exposed it — a duplicate of execution-side logic that drifted as instructions gained real estimates. The daemon owns the tree and its statuses, so it is the only place a remaining figure that credits completed passes can be computed.

**Spec ref:** `Services/RunEtaEstimator.cs`, `Services/SequencerService.cs` (`RunState.EstimatedSeconds`, `EmitAsync`), `Services/SequencerService.LiveEdit.cs`, `Contracts/SequenceDtos.cs`. openapi.yaml was still pending its refresh when this was written; superseded by the #1131 regenerated snapshot.

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

**Spec ref:** `Services/AlpacaManagementClient.cs`, `Endpoints/EquipmentEndpoints.cs` (`GetAlpacaDeviceNamesAsync`), `Contracts/EquipmentDtos.cs` (`AlpacaDeviceNamesResponseDto`). openapi.yaml was still pending its refresh when this was written; superseded by the #1131 regenerated snapshot.

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

**Reasoning:** the client-side interlock only covered the client's own buttons — the §58 unattended shutdown's warm ramp, a second client or a direct API call all bypassed it. The daemon services are where those paths meet. ~~Not yet covered (#1076): the sequencer's `SetSwitchValue` instruction writes the switch raw~~ (covered by the #1076 entry below) and ~~its `CoolCamera`/`WarmCamera` mediator stubs never reach `SetCoolerAsync`~~ (closed by #1187: every set-point and cooler write in `CoolCamera`/`WarmCamera` goes through `SetCoolerAsync`). The camera's `GetAsync` is the probe (cached runtime state), resolved lazily (`Func<>`) to break the CameraService ↔ SwitchService construction cycle; the camera's own post-cooler fan write bypasses the interlock (`ICoolingFanActuator`) because the cached cooler state may still read on for one tick.

**Spec ref:** `Services/CoolingFanInterlock.cs`, `Services/CameraService.cs` (`SyncCoolingFanAsync`), `Services/SwitchService.cs` (`FanOffRefusalForAsync`), `Endpoints/EquipmentEndpoints.cs`.

**Related:** #1065 (from the 2026-09-20 client/server separation audit), CHANGELOG [Unreleased]

### 2026-09-21 — #1072/#1078 MoveAxis band snapping, secondary fallback, time-based settle

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/moveaxis` (behaviour of the rate guard; no wire change).

**Decision:** the daemon caches each pad axis's AxisRates as `[Min, Max]` bands (read with the capabilities, never on the nudge path). A nonzero rate is SNAPPED (sign preserved): inside a band → unchanged; above the top band → that max; ~~below the lowest band → that min~~ (since #1085, raised only while within 4× of it, refused with 409 when slower — see the #1085 entry below); in a gap between bands (a discrete-rate mount has `Min == Max` steps) → the nearest band edge. An axis with no known bands still refuses (409). A secondary axis whose AxisRates never answered (unknown) while the primary's are known borrows the primary's bands (logged once per session) instead of losing N/S for the session; an honestly empty secondary (the mount offers no rates) stays refused, since MoveAxis on it throws by spec. The cache settles when both reads completed, when `CanMoveAxis` is false, or 30 s of wall clock after connect with a read still throwing — a burst of command-triggered refreshes can no longer exhaust the retries in a second. The capabilities' `move_axis_rates_deg_per_sec` list is ~~unchanged (both endpoints of every band, capped at the secondary's max)~~ (since #1126, the endpoints of the bands clipped to the secondary's floor AND ceiling — see the #1126 entry below).

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

---

### 2026-09-26 — per-switch reconnect: `POST /api/v1/equipment/switch/{id}/connect`

**Endpoint(s) or area:** `POST /api/v1/equipment/switch/{id}/connect` (new); the Switches panel card.

**Decision:** reconnect a KNOWN switch by its id from the discovery record the daemon already holds (`DisconnectAsync` keeps the entry as Disconnected). 202 + `OperationAccepted` when dispatched (idempotent when the switch is already Connecting/Connected, exactly like `/connect`); 404 when the id is unknown — removed via `DELETE /switch/{id}`, or never connected this daemon session — so the client falls back to Add switch. The card now shows Disconnect while the switch is connected or connecting and Connect otherwise (Disconnected / Error / unknown).

**Reasoning:** a card offered only Disconnect whatever its state, so a switch the user disconnected could not be reconnected from its card: the header Reconnect (`/switch/reconnect`) is deliberately offered only when EVERY switch is down (re-dispatching a live switch under a changed remembered endpoint can tear it down), and the only other path was re-picking the device through the discovery chooser. `SwitchDto` carries no host/port/https, so the client cannot rebuild a `/connect` body itself; adding those fields to the DTO was the alternative, but the daemon is the owner of the discovery record and a by-id route keeps the wire shape unchanged for older clients. No re-remember on this route: the entry was remembered by its first `/connect` and only `DELETE` forgets it.

**Spec ref:** `OpenAstroAra.Server/Endpoints/EquipmentEndpoints.cs` (switch group); `ISwitchService.ReconnectAsync`.

**Related:** `/switch/reconnect` (§52.1 manual reconnect, all remembered switches); `DELETE /switch/{id}` (§45 stuck-device removal).

---

### 2026-09-26 — remove a single-instance device: `DELETE /api/v1/equipment/{type}`

**Endpoint(s) or area:** `DELETE /api/v1/equipment/{camera|telescope|focuser|filterwheel|rotator|dome|observingconditions|safetymonitor|flatdevice}` (new); the shared `EquipmentConnectionCard` (every single-instance panel).

**Decision:** drop the service's retained device (the one it keeps after a disconnect so the status GET reports `state: disconnected` instead of 404) AND forget its remembered auto-connect entry. 204 (idempotent — nothing retained is still a 204); 409 while the device is Connecting/Connected, so a removal on live hardware is an explicit disconnect-then-remove, exactly like `DELETE /switch/{id}`. An Error device is removed directly (its state is published as Disconnected first, then dropped). The existing `DELETE …/remembered` keeps its store-only, never-409 semantics — the wizard's "None" slot relies on it while a device may still be live.

The card now keeps a known device's card while it is not live: name, state chip, a **Connect** icon (`POST …/reconnect`, no chooser) and a **Remove** icon (this route, confirmed first), with the chooser ("Connect…") still available to pick a different device. Disconnect (or Cancel while connecting) stays the only header action while live. The bare "nothing known" row (Reconnect + Connect…) is unchanged.

**Reasoning:** the Switch card gained Connect/Remove on its card (entry above); the maintainer asked for the same on every other device. Before this a disconnected camera/mount/etc. showed the anonymous "No X connected." row with a Reconnect button — the device's name vanished and there was no way to drop dead or replaced hardware short of a daemon restart. Clearing the retained record needs the service (only it holds the record), hence a per-service `ForgetAsync` behind one route rather than widening `/remembered`.

**Spec ref:** `OpenAstroAra.Server/Endpoints/EquipmentEndpoints.cs` (`RemoveDeviceAsync`); `I{Type}Service.ForgetAsync`.

**Related:** `DELETE /switch/{id}` (§45); `DELETE …/remembered` (§52.1); the per-switch `POST /switch/{id}/connect` entry above.

---

### 2026-09-29 — #1121 star-database status: `GET /api/v1/platesolve/database`

**Endpoint(s) or area:** `GET /api/v1/platesolve/database` (new, read-only); Settings → Plate solving.

**Decision:** report `{ configured_path, effective_path, file_count, databases, solver_path, solver_found }` for the active profile. `effective_path` is the index path when it holds files, else null: the same rule `ASTAPSolver` applies before passing `-d` (`AstapStarDatabase.EffectiveLocation`), so the panel says what a solve will actually use. `databases` lists the abbreviations read from ASTAP's file names (`d80_0101.1476` → `d80`). The client treats a 404 (a daemon older than this route), no connection or any other failure as "unknown", never as an error.

**Reasoning:** a fresh install that skipped the DEPLOY.md star-database step fails every solve with ASTAP exit 32, and only the daemon log said why. Folding this into `GET /profile/plate-solve` was the alternative, but that route round-trips a settings DTO the client PUTs back; a computed, file-system-backed status does not belong in it.

**Spec ref:** `OpenAstroAra.Server/Endpoints/PlateSolveEndpoints.cs` (`GetDatabaseStatus`); `PlateSolveDatabaseStatusDto`.

**Related:** §18.I of PORT_PLAYBOOK.md; #1094 (`-d` wiring); the `-D` selection in `AstapStarDatabase.Select`.

---

### 2026-09-29 — #1126 MoveAxis rate bands on the wire; pad bands clipped to the secondary's floor

**Endpoint(s) or area:** `GET /api/v1/equipment/telescope` (`capabilities` gains `move_axis_rate_bands_deg_per_sec`; `move_axis_rates_deg_per_sec` is now derived from the clipped bands); the client's speed-preset ladder.

**Decision:** `move_axis_rate_bands_deg_per_sec: [{ "min": number, "max": number }]` — the direction pad's rate bands (deg/s), ascending by `min`. Each is one of the primary axis's AxisRates `[Min, Max]` bands clipped to the secondary axis's floor (its lowest `Min`) AND ceiling (its highest `Max`); a discrete rate is a band with `min == max`, "any speed up to max" is a band with `min` 0. A thrown or honestly-empty secondary applies no clip; when nothing survives the clip the primary bands are published as-is (better a rate the secondary may snap than none). Empty when the mount reports no rates. `move_axis_rates_deg_per_sec` is kept as the legacy flattening of the same clipped bands to their positive endpoints (ascending, deduped) for clients that predate the bands. Client: the speed chips are built from the bands — every band discrete → the driver's steps verbatim (a single-rate mount gets one chip, which is the default); exactly one continuous band → the #1085 percentage ladder from its minimum up; several bands with a continuous one → the endpoints verbatim. The default chip is the middle option, which by construction lies inside a band. A daemon that sends only the endpoint list is read the old way (one rate = up to max, two = one band, three or more = steps).

**Reasoning:** the endpoint list cannot tell `(0, 6)` from `(6, 6)` (both flatten to `[6]`), so the client built percentage presets for a fixed-rate mount and defaulted to 10 % of max — more than `SnapUpBoundFactor` (4×) below the only rate, refused with 409: a dead pad on a fresh connect. Separately, #1064 capped the offered rates at the secondary's maximum but not its minimum, so a diagonal press at a rate the primary honours could be refused on the secondary; inside `[floor, ceiling]` the snap never throws (a gap between discrete steps snaps to the nearest edge), so clipping to both bounds is exactly the guarantee the pad needs. A nested `{min, max}` object rather than a `[min, max]` pair keeps the two ends self-describing on the wire.

**Spec ref:** `Contracts/EquipmentDtos.cs` (`TelescopeCapabilitiesDto.MoveAxisRateBandsDegPerSec`, `MoveAxisRateBandDto`), `Services/TelescopeService.cs` (`PadBandsFrom`, `BandDtosOf`, `EndpointsOf`), `client/…/lib/util/slew_rates.dart` (`SlewRateBand`, `buildSlewRateOptionsFromBands`, `defaultSlewRate`), `client/…/lib/models/mount_status.dart` (`MountCapabilities.axisRateBands` / `padRateBands`); tests in `TelescopeMoveAxisClampTest`, `slew_rates_test.dart`, `mount_status_test.dart`, `equipment_mount_panel_test.dart`.

**Related:** #1126 (follow-ups of #1087), #1064, #1085, CHANGELOG [Unreleased]


---

### 2026-10-01 — §33 client-pushed update (#1122)

**Decision:** three routes under `/api/v1/server/update`. `POST ""` takes the `.deb` as a raw body (not multipart: one file, no fields, streamable from the client) with an optional `X-Update-Sha256`, returns 202 `ServerUpdateStagedDto { id, package, version, installed_version, size_bytes }`. `POST /{id}/apply` returns 202 `ServerUpdateStatusDto` with `status: "pending"`, emitting `server.restart_imminent { reason: "update", update_id, version, in_seconds }` only after systemd accepted the helper job; one apply at a time. `GET /{id}` returns the same DTO with `pending | applied | rolled_back | failed`, readable from the restarted daemon. Refusals are Problem responses whose `title` is a stable token: `not_packaged`/`helper_unavailable`/`update_in_progress` (409), `too_large` (413), `unknown_id` (404), `not_a_package`/`wrong_package`/`wrong_architecture`/`not_newer`/`checksum_mismatch`/`empty` (422).

The result lives on tmpfs, so after a **reboot** `GET /{id}` is 404: clients treat a 404 after an apply as "outcome unknown" and compare `/api/v1/server/info`'s version with the one they pushed.

**Why:** the restart happens between apply and the outcome, so the outcome has to outlive the process — it lives in the root helper's result file on tmpfs, keyed by the id the client already holds. A `.deb` rather than the §33.3 sketch's tarball keeps dpkg the owner of `/opt/openastroara`.

**Spec ref:** `Endpoints/ServerUpdateEndpoints.cs`, `Services/ServerUpdateService.cs`, `packaging/debian/opt/openastroara/scripts/{update-request,apply-update}.sh`, playbook §33.3.

---

### 2026-10-03 — Rotate camera by hand: the rotation readout, the run's target name and frame counter

**Endpoint(s) or area:** `POST /api/v1/rotation-assist/start`, `POST …/stop`, `GET …/state`, `GET …/frame`; the sequencer instruction `OpenAstroAra.Sequencer.SequenceItem.Rotator.RotateCameraByHand` (`PositionAngle`); `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` gain `frames_captured`, and `current_target_name` is now populated; frames' `target_name` for client-built runs.

**Decision:** a rig without a rotator cannot act on a framing angle, so the run builder emits `Rotate camera by hand` (after the blind slew, before `Center and Rotate`) when a position angle is set and the rig has no rotator. The instruction starts the daemon's rotation READOUT and parks the run in `paused_awaiting_user` INSIDE the step (it awaits the pause gate itself), so Resume — or an abort — stops the readout before the next instruction takes the camera; with a rotator connected it is a no-op, and on a rig that cannot solve (no solver binary, no optics in the profile) it logs a warning and skips rather than parking the run on a readout that can only fail. The readout: `POST /rotation-assist/start {position_angle_deg}` (202; 400 non-finite; 409 already running / rig cannot solve) loops capture-through-the-analysis-seam → plate solve with the main optics → `RotationAssistStatusDto { active, state: idle|running|stopped|error, target_position_angle_deg, tolerance_deg (the profile's rotation tolerance), seq, started_utc, latest, recent[≤120], within_tolerance, error, consecutive_failures, has_frame, frame_seq }` where a sample is `{seq, solved_utc, solved_position_angle_deg, delta_deg, ra_deg, dec_deg, pixel_scale_arcsec, flipped, frame_width, frame_height}` — `delta_deg` is the signed shortest turn to the target folded into (−90°, +90°] because a frame rotated by 180° is the same framing. `GET …/frame` is the latest solved frame rendered (JPEG, auto-stretched with star rings, `X-Frame-Seq`; 204 before one exists). The loop ends in `error` after 5 consecutive failed solves; `POST …/stop` is 204 once the in-flight solve has drained. No WebSocket events: the client polls like the guide-focus loop. The client turns the delta history into advice relative to the user's last turn (the daemon cannot know which way "clockwise" turns the sky on a given optical train), draws north and the planned framing over the frame (`flipped` reverses the on-screen sense), and pushes each solve to the planetarium as a second "scope" box beside the planned framing.

The run state carries `frames_captured` (int, default 0): the count of frames filed under the run's §40 session, polled with the live status, so the band can show frames landing while a loop's single `TakeExposure` stays one instruction. `current_target_name` is derived from the live tree (nearest DSO target, else the client-built target block — the container carrying an altitude/horizon condition), and `TakeExposure` files its frames under that same name instead of the imaging loop's container name. `sessions.frame_count` increments per insert.

**Reasoning:** the by-hand turn needs a protractor, not a motor: the plate solver measures, the user turns, and the readout is a loop so the picture follows the hand. Sitting inside the pause is what guarantees the camera is free when `Center and Rotate` runs. Frames under the target and a live frame count are the two facts whose absence made a working run look dead on 2026-10-02.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1rotation-assist~1start` (…`~1stop`, `~1state`, `~1frame`); `Contracts/RotationAssistDtos.cs`, `Services/RotationAssistService.cs`, `Services/RotationFrameSolver.cs`, `OpenAstroAra.Sequencer/SequenceItem/Rotator/RotateCameraByHand.cs`, `OpenAstroAra.Sequencer/Utility/ItemUtility.ResolveTargetName`, `Services/SequencerService.LiveStatus.cs`; client `rotation_overlay.dart`, planetarium `scopeBox` command.

**Related:** §38 rotation fidelity, §45 capture-fetch, §59 analysis seam, §64 Live View; tests `RotateCameraByHandTest`, `RotationAssistServiceTest`, `SequencerLiveStatusTest`.

---

### 2026-10-03 — Smart Focus judges "in focus" against the HFR the sweep measured

**Where:** `GET /api/v1/autofocus/calibration` (`FocusCalibrationDto`), profile JSON `focus_calibration`.

**Change:** `in_focus_hfr` (nullable) — the HFR the calibrating sweep measured on its confirmation
frame at best focus (the fit's predicted minimum when no confirmation frame was usable). The inverse
map's in-focus HFR and its fold-table anchor use it when present; Smart Focus's "already in focus"
target is that value × 1.10 (was the re-fitted minimum × 1.05). A calibration written before the field
existed carries null and behaves as before. A Smart run whose confirmation frame reads lower than the
stored value lowers it (the sweep's own confirmation can sit a few steps off the true minimum).

**Same day, the drawn V:** a bracket-confirmed Smart run now fits the V through its own three to five
shots with the sweep's fitter (`fit.algorithm` parabolic/hyperbolic, real `r_squared`), so the curve
passes through the dots; the calibration model curve (`algorithm: "calibration"`) only stands in when
those points will not fit. The classic sweep fits its V on the probes that saw at least 10 % of the
best-populated probe's stars (`TrimThinWings`): far-wing probes on doughnut images stop tracking defocus
and dragged the fitted minimum up and sideways. Dropped probes stay in `probes` (they were measured).

**Why:** the map re-fitted its minimum from the stored samples. A lumpy 15-point sweep re-fitted to
1.62 px while the same sweep's confirmation frame measured 0.96, so every Smart shot under 1.70 px
"was in focus" (2026-10-03) and the drawn calibration curve bottomed well above the measured points.

### 2026-10-03 — Smart Focus: a reads-as-focused shot is bracketed before it counts

**Where:** the `autofocus.*` WebSocket events (`fallback_classic` reasons) and the run record's `probes`.

**Change:** when Smart Focus's first shot reads at or under the calibrated in-focus HFR it no longer
completes on that one shot. It takes one shot either side at the calibration's half-width (where the
HFR should have doubled; half the classic span when no half-width is stored), returns to the centre,
and accepts the centre as the minimum only when both sides read ≥ 1.3× the centre (the run then goes
on to the vertex and confirmation shots under "Five shots" below). A side under 0.97× the centre, no
clear rise, or a bracket shot with too few stars falls back to the classic sweep with the new
`fallback_classic` reason `bracket_failed`. The run record carries the bracket's three `smart` probes.

The bracket runs backlash-consistently: the − side first (moving down, the direction every calibration
sample was approached from), then the + side, then the centre re-entered from above through the sweep's
one-step overshoot, so the focuser rests on the same side of its backlash as the calibration's best. A
confirmation frame is then taken at the centre (`final_hfr` / `final_stars` / the picture are measured
there, as for the sweep). The run record's `fit` is the V fitted through the run's own shots (see
"Same day, the drawn V" above). Only when those points will not fit does it carry the calibration's
model curve instead — `algorithm: "calibration"`, `r_squared: 1`, `best_position` = where the run ended,
`curve` = h₀·√(1 + 3(d/w)²) over ±1.3 half-widths. The client shows no R² for that algorithm.

**Five shots (same day):** after the bracket, the parabola through the three shots gives a vertex. When
it sits off the centre (and inside the bracket; outside is "no V" → `bracket_failed`) shot 4 is taken
there, landed from above, and the position is kept only when that frame reads < 0.97× the centre shot;
otherwise the focuser returns to the centre from above. Shot 5 is the confirmation frame at the final
position. `total_steps` for a Smart run is now 5 (`SmartMaxShots`); the predict path still completes
in 2–3. The run record's `smart` probes carry up to four points and `fit.best_position` is the final
position.

**Why:** one shot against a stored number is a claim, not a check — a calibration from a lumpy sweep,
or a lucky frame, says "in focus" just as readily (2026-10-03: four one-shot runs at 0.957–0.985 while
the sweep's own curve was far from a clean V).

### 2026-10-03 — Guide-camera live focus: the expected in-focus HFR

**Where:** `GET /api/v1/equipment/guider/focus` (`GuideFocusStatusDto`).

**Change:** two optional fields, `expected_hfr` and `plate_scale_arcsec` (both nullable, absent-as-null
for an older daemon). `expected_hfr` is the HFR in pixels an in-focus star should read on the guide
camera, from the profile's guide optics: a guide scope uses the §63.19 guide focal length and guide pixel
size; an off-axis guider uses the main telescope's focal length and aperture with the guide pixel size.
Seeing (3" assumed) and the aperture's Airy FWHM add in quadrature, HFR ≈ FWHM/2 at the plate scale,
floored at 0.7 px (what the §59 detector reads for a sub-pixel star). Null when the profile has no guide
focal length or pixel size.

**Why:** the live-focus advice compared one frame with the frame four earlier at 3 %. On a guide scope
at 6.4"/px the HFR sits at the detector floor (0.76 px) and jitters ±0.05 px, so a focuser nobody was
touching was told "Keep going" and "Go back" in turn (2026-10-03). The client now says *In focus* at or
under `expected_hfr` × 1.3 before it reads any trend, and the trend compares 3-frame medians. The sample
HFR itself is now the median over the 12 brightest stars rather than the mean over every blob, so faint
stars flickering across the threshold no longer move the number.

**Self-stop (same day):** `stop_reason` (nullable string) on the same DTO. The daemon ends the loop
itself with `"in_focus"` once the median HFR over the last 10 measurable frames (≥ 2 stars) is at or
under `expected_hfr` × 1.3 — a median rather than a streak, since seeing throws single frames well above
the line (an OAG at 3000 mm would never hold ten clean frames in a row). A user stop leaves it null. A
start now resets the frame counter (`seq`), the trend (`recent`), the picture and the stop reason, so a
refocus run never carries frames from before it; the client clears its chart from the same payload.

### 2026-10-03 — Setup → Smart Focus: the autofocus run record and the guide-camera focus loop

**Endpoint(s) or area:** `GET /api/v1/autofocus/state`, `GET /api/v1/autofocus/frame`, `POST /api/v1/autofocus/cancel`; `POST /api/v1/equipment/guider/focus/start`, `POST …/focus/stop`, `GET …/focus`, `GET …/focus/frame`; `POST /api/v1/equipment/camera/connect` gains a 409; WebSocket `autofocus.step_complete`, `autofocus.curve_fit`, `autofocus.completed`, `autofocus.failed`.

**Decision:** the daemon keeps ONE in-memory record of the current / most recent autofocus run (`AutofocusRunDto`: state `idle|running|complete|failed|cancelled`, mode, phase, trigger `manual|sequence`, every probe `{index, phase: coarse|fine|smart, position, hfr, stars, kept}`, the fit `{algorithm, r_squared, best_position, predicted_hfr, within_sampled_range, curve: [{position, hfr}]}` sampled by the daemon from the model it actually fitted, the final position / measured HFR / star count, `reason` + `restored_position` on failure, and `has_frame`/`frame_seq` for the rendered picture). `GET /autofocus/frame` serves that picture (JPEG, auto-stretched with star rings, ≤1024 px, `X-Frame-Seq`, 204 before one exists): the latest kept probe while the sweep runs, a confirmation frame taken AT best focus once it completes — so `final_hfr` is measured, not predicted (the prediction stands in when the confirmation frame is unmeasurable). `POST /autofocus/cancel` (202 / 409 `not_running`) cancels the run whoever started it; the sweep restores per `restore_position_on_failure` and the record ends `cancelled` (a cancel during the `confirming` phase, whether the user's or a sequence abort, is accepted but ignored: the focuser is already at best focus, so the run ends `complete`); the focuser endpoint's job also lands `cancelled` (it reads the record) rather than failed. The Classic sweep now publishes `autofocus.step_complete` per probe (coarse and fine, `kept` false for a dropped probe), `autofocus.curve_fit` per sweep attempt, and every run closes with `autofocus.completed` or `autofocus.failed` (`reason: "cancelled"` for the user's cancel) — the §59.15 shapes with `phase`/`kept`/`total_steps` added. Clients hydrate from `/autofocus/state` on open and after a WS reconnect and treat the events as change notifications.

The guide camera is focused by a READOUT loop, not an autofocus: `POST /equipment/guider/focus/start {exposure_sec (0.05–30), binning?}` (202; 400 bad params; 409 guider not connected / guiding or calibrating / polar alignment running / already running / the daemon refused its lease) takes the guider daemon's single-client PA-session lease and loops `capture_single_frame` → HTTP capture-fetch → star detection on the daemon, publishing `GuideFocusStatusDto { active, state: idle|running|stopped|error, exposure_sec, seq, latest {seq, captured_utc, hfr, stars, peak_adu, fwhm}, best_hfr, best_seq, recent[≤240], error, consecutive_failures, has_frame }` at `GET …/focus` and the rendered frame at `GET …/focus/frame` (`X-Frame-Seq`). The loop ends in `error` after 5 consecutive failed frames. `POST …/focus/stop` is 204 once the in-flight frame has drained (≤ exposure + 30 s) so the daemon never owes a `SingleFrameComplete` to a listener that is gone. Polar alignment's Start stops a running focus loop first (same lease). No WebSocket events: the client polls status + frame the way §64 Live View does.

`POST /equipment/camera/connect` answers 409 `guide_camera_in_use` when the device is the connected guider's own camera (the profile's `phd2.guider_camera` choice string embeds `[host:port/N]`; matched on host name OR IP, port and device number, loopback spellings equal). `?force=true` overrides.

**Reasoning:** the Smart Focus pane needs the whole V-curve, the fit and a picture, and a client that opens mid-run or reconnects cannot rebuild those from events it missed — one snapshot endpoint plus events-as-triggers is simpler and self-healing. The confirmation frame costs one exposure and turns "predicted HFR 1.42" into a measurement the user can see. For the guide camera, Ara deliberately never opens the device itself: on an OAG or a guide scope it is the guider daemon's camera, and two Alpaca clients on one sensor is exactly how guiding broke when the guide camera was connected as the main camera to focus it — the same reason the connect guard exists. Borrowing frames through the guider (the §45 capture-fetch path) means the daemon stays the only client, the lease keeps the two routines from interleaving captures on one socket, and a guide scope's manual helical focuser gets what it actually needs: a live HFR readout, a trend and a best-so-far, not a sweep. The guard is a 409 with a stable title rather than a silent refusal so the chooser can explain and offer the Smart Focus pane.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1autofocus~1state`, `…~1autofocus~1frame`, `…~1autofocus~1cancel`, `…~1equipment~1guider~1focus~1start`; `Contracts/AutofocusRunDtos.cs`, `Contracts/GuideFocusDtos.cs`, `Services/AutofocusRunTracker.cs`, `Services/GuideFocusService.cs`, `Services/CameraConnectGuard.cs`, `Contracts/WsEvents/WsEventCatalog.cs`.

**Related:** §59.12, §59.15, §45 capture-fetch, §64 Live View, §76.3 Setup checklist; tests `AutofocusRunRecordTest`, `GuideFocusServiceTest`.

---

## WebSocket wire protocol (`/api/v1/ws`)

Written from the code when `openapi.yaml` became a generated snapshot (#1131, 2026-10-01): OpenAPI 3.1 cannot express WebSocket endpoints, so this section is the contract for independent clients. Sources: `Endpoints/WebSocketEndpoints.cs`, `Endpoints/WsClientConnection.cs`, `Services/PlaceholderWsServices.cs`, `Services/ClientSessionService.cs`, and the token catalogue `Contracts/WsEvents/WsEventCatalog.cs` (which `GET /api/v1/ws/catalog` also serves). Where the original §60.9 design went further than the code, that is said explicitly as *design intent*.

```
Endpoint:   ws://{host}:{port}/api/v1/ws        (not /api/v1/stream)
Version:    X-Ara-WS-Version: 1 header, or ?ws_version=1 for browser clients
            (the header wins when both are present). Missing or wrong → the
            upgrade is refused with HTTP 426 + a Problem body; no socket exists,
            so no close code is involved.
Frame size: Kestrel/WebSocket defaults; no Ara-specific cap today
            (design intent: 1 MB).
Compression: none; the server does not opt in to permessage-deflate
            (design intent).
Heartbeat:  server WS ping every 30 s (KeepAliveInterval); the socket is
            closed if nothing arrives within 60 s (KeepAliveTimeout).
            Design intent for clients: close + reconnect (§32 modal) after 90 s
            with no server activity.

Resume protocol — optional FIRST client message after the upgrade:
  { "resume_token": "<last seen seq, base-10>" }
  The token is the last `seq` the client saw (v0.x; an opaque token with a
  time window tied to GET /api/v1/server/state is design intent). Replies:
  { "resumed": true,  "missed_events": n, "last_event_id": "<seq>" }
      → the missed events (≤ 1000, the in-memory replay window) are then
        sent as ordinary events
  { "resumed": false, "code": "resume_token_invalid", "reason": "..." }
      → not a non-negative integer; the connection continues as fresh
  { "resumed": false, "code": "resume_token_expired", "reason": "..." }
      → older than the 1000-event window, or newer than the server's current
        seq (daemon restarted); the connection continues as fresh — the
        socket is NOT closed; rehydrate with GET /api/v1/server/state
  { "resumed": false }  (no code)
      → JSON whose resume_token is absent, empty or whitespace ({} works as an
        explicit "fresh, please"); fresh subscription.
  No reply at all, fresh subscription, when the first message is not a resume
  request: malformed JSON, a binary frame, a first message over 16 KB, or the
  client sending nothing within the 5 s resume window. Clients must not
  wait for a resume reply unless they sent a resume request.

Event envelope (every server-sent message, WsEventEnvelopeDto):
  { "type": "frame.complete", "ts": "2026-05-23T19:14:33.123Z",
    "seq": 1234, "payload": { ... } }
  `seq` is a monotonic int64 per server boot and doubles as the resume token.
  There is no `id` field.

Close codes the server sends:
  1000 — normal closure (shutdown: "server closing")
  4004 — single-client policy: another client took over (§27,
         ClientSessionService.TakeoverCloseCode)
  Everything else the client sees is the framework's (1001 going away,
  1011 internal error, keep-alive expiry). Design intent, not emitted:
  1009 frame too large, 1012 restart imminent (the `server.restart_imminent`
  event exists; the close does not), 4001 auth, 4002 resume expired (it is a
  JSON reply, above), 4003 version mismatch (it is a 426).

Backpressure: a bounded per-subscriber buffer of 1000 events, drop-oldest when
full (PlaceholderWsServices.PerSubscriberCapacity). A slow client loses the
oldest events and catches up via the resume protocol on its next connect;
it is not closed (design intent: close 1011 `client_too_slow`).

Sequence-run event ORDERING contract (§60.9):
  * A run's terminal event (sequence.complete / .stopped / .aborted / .failed)
    MAY arrive without a preceding sequence.started — an abort or stop that
    lands between run acceptance and the worker executing skips the (would-be
    misleading) started event. Do not assume a strict started → terminal pair.
  * sequence.progress never arrives after the same run's terminal event: the
    server seals and drains its progress publisher before every terminal emit.
    Progress events are coalesced under load (at most one publish in flight;
    bursts collapse to a trailing publish with the freshest state), so treat
    each progress payload as a snapshot, not a delta.
  * instructions_completed / instructions_total count SEQUENCE INSTRUCTIONS
    (tree leaves), not camera exposures (renamed from frames_* while the wire
    had no external consumers, §60.9).

Camera exposure lifecycle (CameraService.ExposeAndDownloadAsync — the one
device round-trip every capture takes: Take One, sequence lights/flats/darks,
the §59 autofocus probe, the §28 plate-solve capture; NOT the §64 Live View
loop, whose sub-second frames would flood the stream):
  camera.exposure_started  { frame_id, exposure_sec, started_utc, kind,
                             filter_name? }
      kind = lower-cased image type ("light" | "flat" | "dark" | "bias" |
      "snapshot" | "darkflat") for a frame that will be catalogued, or
      "analysis" (autofocus probe) / "plate-solve" for one that is not.
      Published AFTER the driver accepted StartExposure, so a rejected
      exposure never announces.
  camera.exposure_complete { frame_id, exposure_sec, started_utc, kind,
                             elapsed_ms }
      The pixels are downloaded — the camera's USB/IO is free. elapsed_ms is
      shutter-open → pixels-in-hand on the daemon clock (the download is
      INSIDE it). For a catalogued frame, frame.complete follows once the
      FITS is written and registered.
  camera.exposure_failed   { frame_id, kind, reason }
      Device timeout (no ImageReady within the wait bound), disconnect or
      supersede mid-exposure, caller cancellation ("cancelled"), a REST abort
      (POST /equipment/camera/exposure/abort, "aborted": reported at once, no
      equipment.fault) or a thrown device fault. Every started is followed by exactly one complete OR
      failed for the same frame_id; a client timer should still age out on
      its own (exposure + ~2 min) in case the WS link dropped in between.
      Clients should stamp the exposure start on their OWN clock at receipt:
      started_utc is informational (the two clocks can disagree, §31).

Guider step stream (GuiderService, one event per guide frame while the guider
is connected, straight from its GuideStep events):
  guider.step { frame, time_sec, ra_raw_px, dec_raw_px, ra_arcsec?,
                dec_arcsec?, ra_duration_ms, dec_duration_ms,
                pixel_scale_arcsec?, star_mass?, snr? }
      Distances are the star's offset in guide-camera pixels (NINA's sign
      convention, RA negated from PHD2's raw value); the arcsec pair and
      pixel_scale_arcsec are present once the guider has reported its pixel
      scale. Pulse durations are signed: negative = East (RA) / South (Dec),
      PHD2's own graph convention. A non-finite reading (lost star) is null,
      never NaN. The windowed RMS stays on GET /equipment/guider
      (rms_total/ra/dec in px, rms_*_arcsec when the scale is known).
  guider.event { kind, ... }   one per PHD2 session event other than a step —
      the things PHD2's own graph annotates. kind and its detail fields
      (omitted when PHD2 sent none):
        dithered            { dx_px, dy_px }
        settling            { distance_px, time_sec, settle_time_sec }
        settle_done         { status (0 = ok), error? }
        star_lost           { frame?, star_mass?, snr?, distance_px?, status?, error? }
        calibration_started | calibration_complete | calibration_failed { error? }
        guiding_started | guiding_stopped | paused | resumed | lock_position_lost
      A dither is drawn as dithered → (settling …) → settle_done; the client
      shades the settle window and marks the dither, exactly as PHD2 does.
```
