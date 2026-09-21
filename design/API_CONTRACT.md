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
- After a committed cooler write the daemon writes the first connected switch whose name contains "Thermal Switch" and which exposes a writable port named "Fan" to that port's own `max` (cooler on) or `min` (cooler off). No such switch = no-op; a port whose CACHED value already holds the target is not re-written (the §58 warm ramp calls the cooler once a minute), so a stale cache under a failing port read can skip a write (#1076). A failed fan write never fails the cooler call (the cooler change has landed, and the §58 warm ramp must reach its final cooler-off): it is published as an `equipment.fault` of kind `op_error` for the switch ("the cooler is on|off, but the cooling fan could not be synced (…) — check the fan") and logged.
- A switch-value write that takes that same Fan port to `value <= min` is refused with 409 unless the camera resolved with `runtime.cooler_on == false` (a not-connected camera also reads as off). While a connected Thermal Switch's port snapshot has not been read yet (up to one refresh interval after connect) the port cannot be identified, so a write of `value <= 0` to ANY of its ports is held to the same rule — it asks the camera exactly like an identified fan-off, so it goes through when the cooler resolves off (a fan-on or a mid-range value is never held). A switch that is not Connected (Error / Disconnected / unknown id) gets the write path's own "not connected" refusal, never the fan one. Cooler on, or no camera service / a throwing status read, refuses. Known gap (#1076): a connected camera whose `CoolerOn` property read fails is reported as `cooler_on: false` by `CameraStateDto`, so that case is allowed. The refusal `detail` is the user-facing sentence the client used to generate locally.
- The client's own fan sync and fan-off pre-check are deleted; it renders the server's `detail` verbatim.

**Reasoning:** the client-side interlock only covered the client's own buttons — the §58 unattended shutdown's warm ramp, a second client or a direct API call all bypassed it. The daemon services are where those paths meet. Not yet covered (#1076): the sequencer's `SetSwitchValue` instruction writes the switch raw and its `CoolCamera`/`WarmCamera` mediator stubs never reach `SetCoolerAsync`. The camera's `GetAsync` is the probe (cached runtime state), resolved lazily (`Func<>`) to break the CameraService ↔ SwitchService construction cycle; the camera's own post-cooler fan write bypasses the interlock (`ICoolingFanActuator`) because the cached cooler state may still read on for one tick.

**Spec ref:** `Services/CoolingFanInterlock.cs`, `Services/CameraService.cs` (`SyncCoolingFanAsync`), `Services/SwitchService.cs` (`FanOffRefusalForAsync`), `Endpoints/EquipmentEndpoints.cs`.

**Related:** #1065 (from the 2026-09-20 client/server separation audit), CHANGELOG [Unreleased]

### 2026-09-21 — #1076 cooling-fan interlock: sequencer path, fail-closed states, fan-first, late connect

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/cooler`, `POST /api/v1/equipment/switch/{id}/value`, the sequencer's `SetSwitchValue`, `CameraStateDto` (new optional `cooler_state_known`, default true).

**Decision:**
- The sequencer's `SetSwitchValue` goes through the same fan-off interlock as the REST write; a refusal fails the instruction (`SequenceEntityFailedException` with the refusal sentence) so Attempts / `instruction_failed` engage.
- `CameraStateDto.cooler_state_known` is false when the `CoolerOn` property read threw this pass (`cooler_on` then reads false by fallback). The interlock treats that, a camera in `Error`, or no camera DTO at all as UNKNOWN (fan-off refused); a camera that is not connected (`Disconnected`, none) reads as cooler off.
- Cooler ON: the fan is written FIRST (always — a stale cached port value never skips it); if the rig has a fan port and that write fails, the cooler call is refused with 409 ("the cooling fan could not be started … the cooler was left off") and an `op_error` fault is published; nothing was committed. Cooler OFF: cooler write first, then the fan (skipped when the cached port already holds the floor); a fan-off failure never fails the call (fault + log, as before).
- A Thermal Switch that connects while the camera is (known to be) cooling has its fan started as part of the connect (best-effort, fault on failure).

**Reasoning:** the #1070 reviews listed these as the interlock's remaining holes: the raw sequencer write, the fail-open `CoolerOn`-read-fails / camera-dropped states, the "TEC on, fan off" window on cooler-on, the cache short-circuit skipping a needed fan-on, and a switch connecting after the cooler.

**Spec ref:** `Services/CoolingFanInterlock.cs` (`CoolerStateFor`, `FanSyncRequest`), `Services/SwitchService.cs` (`ProbeCoolerStateAsync`, `SyncFanToCoolingCameraAsync`), `Services/SwitchService.Mediator.cs`, `Services/CameraService.cs` (`SetCoolerAsync`, `SyncCoolingFanAsync`), `OpenAstroAra.Test/CoolingFanInterlockBenchTest.cs`.

**Related:** #1076 (from the #1070 reviews), CHANGELOG [Unreleased]
