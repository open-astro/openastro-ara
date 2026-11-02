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

### 2026-11-01 — The guider profile picker: `runtime.profiles` and `phd2_profile_id`

**Endpoint(s) or area:** `GET /api/v1/equipment/guider` — `runtime.profiles` (`GuiderStateDto.Profiles`); `GET/PUT /api/v1/profile/phd2` — `phd2_profile_id` (`Phd2SettingsDto.Phd2ProfileId`).

**Decision:** `runtime.profiles` lists the guider's profiles as `[{ "id": int, "name": string }]`, from its `get_profiles` (an immutable copy the guider swaps in after each refresh, so a status read never sees a half-rebuilt list). It is empty until the guider has listed its profiles, and absent from older daemons; clients treat both as "no profiles to offer". `phd2_profile_id` is the guider profile to select on connect, by that id: `null` (or `0`) means Automatic, the §63.4 guider profile named after the active Ara profile; a positive id is applied to `GuiderSettings.PHD2ProfileId`, which the connect path already honours as the explicit override (select by id). A pinned id the guider no longer lists (the profile was deleted on its side) falls back to Automatic on connect instead of failing the switch every time. The free-text `phd2_profile` field stays on the wire for older profiles and clients but is still not read by anything.

**Reasoning:** the Settings guider page offered a free-text "Profile" field that the daemon stored and never used, so typing a name did nothing. A picker needs the guider's real profile list, and an override needs an id rather than a name, because names are user-editable on the guider side while the id is what `set_profile` takes.

**Spec ref:** `Contracts/EquipmentDtos.cs` (`GuiderStateDto.Profiles`, `GuiderProfileDto`), `Contracts/ProfileDtos.cs` (`Phd2SettingsDto.Phd2ProfileId`), `Services/GuiderService.cs` (`GetAsync`), `Services/StoreBackedProfileService.cs` (`ApplyPhd2`), `Equipment/MyGuider/PHD2/PHD2Guider.cs` (`ProfilesSnapshot`); client `models/guider_status.dart`, `screens/settings/panels/equipment_guider_panel.dart` (`guiderProfileItems`).

**Related:** §63.4, PR #1474.

---

### 2026-10-27 — Guiding Assistant Apply also writes Ara's profile; the Dec guide mode is always pushed (#1448)

**Endpoint(s) or area:** `POST /api/v1/equipment/guider/live/assistant/apply` (same request and `{ applied[] }` answer); `GET/PUT /api/v1/profile/phd2` and `/profile/guider-advanced` now change after an Apply; every guider push (`POST /equipment/guider/profile/push`, both scopes, and the connect push) sends `set_dec_guide_mode`.

**Decision:** after the guider applies the recommendations, the daemon reads its resulting values back (`get_exposure`, `get_algo_param minMove` per axis, `get_dec_guide_mode`, `get_algo dec`, `get_backlash_comp`) and writes them into the active profile: `guide_exposure_ms`, `minimum_move`, `dec_minimum_move`, `dec_guide_mode` in the PHD2 block; `dec_algorithm` and the backlash group in the guider-advanced block. A value the guider does not report, or one the PUT boundary would refuse, keeps the profile's. The read-back is best-effort: its failure is logged and the Apply still answers 200. Clients re-read `/profile/phd2` after Apply. The Dec guide mode has no "unset" value any more: Auto is pushed like North/South/Off.

**Reasoning:** Ara owns the guider settings (§63.5) and pushes them on every connect, but Apply wrote only the guider's profile and Auto was skipped as "unset". A North the assistant applied stayed in force under Ara's Auto, and South drift went uncorrected for whole nights. Reading back instead of copying the recommendation stores what the guider actually runs (the exposure range is rounded to one of its durations; the backlash pulse is capped).

**Spec ref:** `Services/GuiderLiveService.cs` (`ApplyGuidingAssistantAsync`), `Services/GuiderService.GuidingAssistant.cs`, `Equipment/MyGuider/PHD2/PHD2Guider.GuidingAssistant.cs` (`ReadGuidingAssistantAppliedAsync`), `PHD2Guider.GuiderEngineConfig.cs` (`BuildGuiderEngineConfigMessages`); client `state/guider/guiding_assistant_state.dart`.

**Related:** #1364 item 4 (the assistant endpoints), PR #1452.

---

### 2026-10-27 — Take One refuses without a storage disk: `POST /equipment/camera/exposure` 409 (#1450)

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/exposure` gains a 409; `camera.exposure_failed` can now arrive with no `camera.exposure_started` before it.

**Decision:** the exposure endpoint asks the same `StoreRefusal()` as sequence start and answers 409 Problem `title: store_unavailable` with the user's words in `detail` ("No storage disk: nothing is mounted at …" / "The storage disk is full …"). A store lost between the 202 and the capture is refused by the §29 pre-capture check before the shutter opens; that refusal now publishes `camera.exposure_failed { frame_id, kind, reason }` with the same words, so the frame's failure reaches the client although its exposure never started. Take One ends its frame poll on a failed event for its `frame_id` and shows the reason.

**Reasoning:** found on the CM4 2026-10-27 with no drive mounted: the POST answered 202, the capture was dropped in the background with only a journal line, and the client polled `GET /frames/{id}` until exposure + 20 s and then said it "couldn't confirm the frame arrived".

**Spec ref:** `OpenAstroAra.Server/Endpoints/EquipmentEndpoints.cs` (`StartExposureAsync`), `CameraService.CaptureCoreAsync`, client `imaging_tab.dart` (`_takeOne`), `capture_progress_state.dart`.

**Related:** #1374 (the run-start refusal); tests `NoStorageDiskTest`, `CameraServiceExposureEventsTest`, client `imaging_tab_test`, `capture_progress_state_test`.

---

### 2026-10-22 — A connect announces again once the device's details are read (`details_ready`)

**Endpoint(s) or area:** WS `equipment.state_changed` (every device type).

**Decision:** a connect now emits two `equipment.state_changed` events with `state: "connected"`. The first is the state transition, published before the service has read the device; the second, `{ device_type, device_id, device_name, state: "connected", details_ready: true }`, follows once the seed read (caps, readings, switch ports) has run. The second is not a reconnect: `equipment.connected` still fires once, and the daemon's in-process transition listeners (identity reconcile, fault-log resolve) do not see it. Clients re-read the device on either event.

**Reasoning:** a client re-reading on the first event got a connected device with no details and then waited out its 15 s liveness poll (a camera removed and connected again on the CM4, 2026-10-22). Setting Connected only after the seed read was the alternative, but the seed read itself only runs on a Connected service.

**Spec ref:** `OpenAstroAra.Server/Services/EquipmentEventPublisher.cs` (`DetailsReady`); each service's `ConnectInBackground`.

**Related:** PR #1444

---

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

### 2026-10-20 — §63.4 deleting a profile asks about its guider twin

**Endpoint(s) or area:** `DELETE /api/v1/profiles/{id}` gains `?guider_profile=keep|delete` (default `keep`) and `?dark_library=keep|delete` (default `delete`, meaningful only with `guider_profile=delete`) and answers **200** `ProfileDeleteResultDto { guider_profile: "deleted" | "queued" | "kept" | "not_found" | "failed", reason? }` instead of 204; an unknown value is 400. New `GET /api/v1/profiles/{id}/guider-twin` → `GuiderTwinPreviewDto { twin_names[], exists (bool|null), shared, shared_with?, selected_on_daemon, can_delete_now, reason? }`.

**Decision:** the guider twin is deleted only on an explicit `guider_profile=delete`, so an older client (no parameter) never deletes guider data without the user being asked; the old fire-and-forget delete is gone. The delete is awaited (bounded at 15 s, not by the request's abort) and its outcome returned, so the dialog can say what happened. The daemon refuses `delete_profile` while any of its equipment is connected, so the service releases the equipment, deletes, re-ensures the active profile's twin when the selected one went, and reconnects — only from a Stopped guider; a guiding / calibrating / looping guider, no guider, or a timed-out daemon queues the delete in `{profileDir}/guider-twin-deletes.json`, drained on the next guider connect (after `get_profiles`, before the twin ensure, with no reconnect of its own) and on the first idle ping tick. A queued name a live ARA profile maps to again is dropped, unless the entry was queued as shared (the user ticked the box for a twin another profile shares), which deletes regardless, as the immediate path would. Under an imaging run or a polar alignment a delete never releases the guider's equipment: the preview answers `can_delete_now: false` with the reason and the delete is queued (the connect-path drain is exempt: a connect releases and reconnects the equipment itself, so a queued delete runs inside that window). `exists: null` means no guider is connected; `shared` is computed from the ARA profile list (trimmed, case-insensitive name match, the same rule the rename hook uses).

**Reasoning:** the silent delete failed nearly every time (the guider's equipment is connected whenever the guider is) and told nobody; a user deleting a rig they spent a night calibrating deserves the question, and an answer.

**Spec ref:** `Endpoints/ProfilesEndpoints.cs`, `Contracts/GuiderTwinDtos.cs`, `Services/GuiderService.ProfileLifecycle.cs` (`DescribeAraGuiderTwinAsync`, `DeleteAraGuiderProfileAsync`, `DrainPendingTwinDeletesAsync`), `Services/GuiderTwinDeleteStore.cs`, `PHD2Guider.GuiderProfile.cs` (`DescribeGuiderProfilesAsync`, `DeleteGuiderProfilesAsync`, `BeforeAraProfileEnsureAsync`); client `widgets/profile/delete_profile_dialog.dart`, `models/guider_twin.dart`, `services/profile_api.dart`.

**Related:** PORT_PLAYBOOK §63.4 lifecycle table; tests `ProfilesEndpointsGuiderHooksTest` (§63.4 group), `GuiderFakeIntegrationTest` (§63.4 group), client `delete_profile_dialog_test.dart`, `launch_profile_screen_test.dart`.

---

### 2026-10-13 — #1155 run ETA: the duration-model contract, the running-leaf credit, disabled leaves out of the count

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` (`estimated_total_seconds` / `estimated_remaining_seconds`, `instructions_total` / `instructions_completed`; no wire change); the client's run-header blend.

**Decision:** (1) *Supersedes #1080 item (3).* An instruction declares that its `GetEstimatedDuration()` is a real figure by implementing `IEstimatesOwnDuration` (OpenAstroAra.Sequencer); for such an instruction zero means zero seconds and costs nothing. Every instruction that overrides the estimate carries the contract (`TakeExposure`, `WaitForTime`, `WaitForTimeSpan`, `TrackingProbe`, `SetReadoutMode`, `SetUSBLimit`, `Dither`, `SkyFlats`, `CoolCamera`, `WarmCamera`, `FlatPanelFlats` — a reflection test enforces it), so a readout-mode or USB-limit change now costs 0 rather than the 15 s nominal. An instruction without the contract returns the base `Zero` placeholder, which still means "no estimate" and costs the nominal. (2) A RUNNING leaf is charged only its unspent part in the REMAINING figure: `SequenceItem` stamps `RunningSinceUtc` (`IRunningSince`) on its first entry into RUNNING and the estimator subtracts the elapsed time, never below zero; the TOTAL keeps the full cost (it is the plan, not the clock). An instruction whose estimate is already "time left from now" opts out through `IEstimatesOwnDuration.EstimateIsRemaining` (true on `WaitForTime`), so it is not credited twice. Leaves with no estimate are credited too (a slew reads 0 after its 15 s). (3) `instructions_total` and `instructions_completed` leave DISABLED items and subtrees out, so the counts and the time estimate cover the same items. (4) A live-edit MOVE bumps the run tree version (`RunState.NoteTreeChanged`) even though no count changed, so the cached walk is refreshed. (5) The client consults the daemon's remaining figure before its `completed >= total` guard: at a loop-pass boundary every leaf is done while passes remain.

**Reasoning:** the hand-kept type list silently dropped every later instruction with a duration model; a full-cost charge for a running 300 s exposure overstated the remaining time by up to five minutes; a disabled block counted in the denominator but not in the time; a move never reached the cache; the client blanked the header at each pass boundary.

**Spec ref:** `OpenAstroAra.Sequencer/Interfaces/IDurationModel.cs`, `OpenAstroAra.Sequencer/SequenceItem/SequenceItem.cs`, `Services/RunEtaEstimator.cs`, `Services/SequencerService.cs` (`CollectLeaves`, `RunState.NoteTreeChanged`), `Services/SequencerService.LiveEdit.cs`, `client/…/lib/models/sequence/run_eta.dart`; tests in `RunEtaEstimatorTest`, `SequencerLiveEditTest`, `run_eta_test.dart`.

**Related:** #1155 (from the #1088 review notes), CHANGELOG [Unreleased]

### 2026-09-21 — #1080 run ETA follow-ups: blend order, item sets, side effects, per-tick cost

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` (`estimated_total_seconds` / `estimated_remaining_seconds`, no wire change); the client's run-header blend.

**Decision:** (1) the client shows the daemon's remaining figure whenever it is present (a non-negative number); the observed elapsed rate is only the fallback for a daemon that sent none. This reverses the #1068 blend order. (2) `estimated_total_seconds` and `estimated_remaining_seconds` count the same items: a DISABLED subtree is out of both (a SKIPPED one is credited for the pass in progress only, since `ResetProgress()` re-runs it on the next loop pass); a `ParallelContainer` pass costs its longest child, not the sum. (3) *(Superseded by the #1155 entry above: the set is now whatever implements `IEstimatesOwnDuration`.)* An instruction with a duration model of its own (`TakeExposure`, `WaitForTime`, `WaitForTimeSpan`) that estimates zero costs zero (an unset or invalid exposure of 0, an already-passed wait); only an instruction whose base estimate is the `Zero` placeholder gets the 15 s nominal. (4) `WaitForTime.GetEstimatedDuration()` no longer assigns `RolloverTime`: an estimate never writes the live tree. (5) The two tree walks are cached against a run tree version (bumped when a leaf changes status or the run reaches a lifecycle state through the `State` setter; pause/resume go through `TryTransition` and rely on the TTL; never bumped on a same-status progress tick) plus a 250 ms TTL on a monotonic clock for the time-based estimates, so the per-report checkpoint, every WS publish and every `GET …/state` share one walk per tick and a lifecycle frame is never stale. Known limits, unchanged: a loop gated only by a `TimeCondition`/`TimeSpanCondition`/horizon (no `LoopCondition`) counts as one pass; `estimated_total_seconds` is not constant across a run when the body holds a `WaitForTime` (it estimates time-until-target), so a consumer must not treat the total as fixed.

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

**Decision:** the daemon caches each pad axis's AxisRates as `[Min, Max]` bands (read with the capabilities, never on the nudge path). A nonzero rate is SNAPPED (sign preserved): inside a band → unchanged; above the top band → that max; ~~below the lowest band → that min~~ (since #1085, raised only while within 4× of it, refused with 409 when slower — see the #1085 entry below); in a gap between bands (a discrete-rate mount has `Min == Max` steps) → the nearest band edge. An axis with no known bands still refuses (409). A secondary axis whose AxisRates never answered (unknown) while the primary's are known borrows the primary's bands (logged once per session) instead of losing N/S for the session; an honestly empty secondary (the mount offers no rates) stays refused, since MoveAxis on it throws by spec. The cache settles when both reads completed, when `CanMoveAxis` is false, or 30 s of wall clock after connect with a read still throwing — a burst of command-triggered refreshes can no longer exhaust the retries in a second. The capabilities' `move_axis_rates_deg_per_sec` list is ~~unchanged (both endpoints of every band, capped at the secondary's max)~~ (since #1126, the endpoints of the bands clipped to the secondary's floor AND ceiling — see the #1126 entry below; since #1230, the endpoints of the primary∩secondary band intersections — see the #1230 entry).

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

**Decision:** a requested magnitude below the axis's lowest band minimum is raised to that minimum only while it is within `SnapUpBoundFactor` (4×) of it; anything slower is refused with 409 ("… more than 4x slower than the mount's slowest rate … Pick a faster speed") rather than turned into a nudge many times faster than picked. Client (since #1126, legacy path only — the picker is built from `move_axis_rate_bands_deg_per_sec`, see the #1126 entry; the endpoint-list reading below applies only to a daemon that predates the bands): the daemon publishes both ends of every band in `move_axis_rates_deg_per_sec`, so two reported rates are read as one band `[min, max]`: the percentage presets of `max` drop every value under `min`, and when any was dropped the minimum itself is offered as the slowest chip ("min · 2°/s"; a preset landing exactly on `min` keeps its percentage label). A single rate keeps the plain preset ladder; a driver ladder of three or more rates is still shown verbatim (previously two rates were shown verbatim as two chips).

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

**Decision:** `move_axis_rate_bands_deg_per_sec: [{ "min": number, "max": number }]` — the direction pad's rate bands (deg/s), ascending by `min`. Each is ~~one of the primary axis's AxisRates `[Min, Max]` bands clipped to the secondary axis's floor (its lowest `Min`) AND ceiling (its highest `Max`)~~ (since #1230, a non-empty intersection of a primary band with a secondary band — see the #1230 entry below); a discrete rate is a band with `min == max`, "any speed up to max" is a band with `min` 0. A thrown or honestly-empty secondary applies no clip; when nothing survives the clip the primary bands are published as-is (better a rate the secondary may snap than none). Empty when the mount reports no rates. `move_axis_rates_deg_per_sec` is kept as the legacy flattening of the same clipped bands to their positive endpoints (ascending, deduped) for clients that predate the bands. Client: the speed chips are built from the bands — every band discrete → the driver's steps verbatim (a single-rate mount gets one chip, which is the default); exactly one continuous band → the #1085 percentage ladder from its minimum up; several bands with a continuous one → the endpoints verbatim. The default chip is the middle option, which by construction lies inside a band. A daemon that sends only the endpoint list is read the old way (one rate = up to max, two = one band, three or more = steps).

**Reasoning:** the endpoint list cannot tell `(0, 6)` from `(6, 6)` (both flatten to `[6]`), so the client built percentage presets for a fixed-rate mount and defaulted to 10 % of max — more than `SnapUpBoundFactor` (4×) below the only rate, refused with 409: a dead pad on a fresh connect. Separately, #1064 capped the offered rates at the secondary's maximum but not its minimum, so a diagonal press at a rate the primary honours could be refused on the secondary; inside `[floor, ceiling]` the snap never throws (a gap between discrete steps snaps to the nearest edge), so clipping to both bounds is exactly the guarantee the pad needs (superseded by #1230: it stops the refusal but not the snap, see below). A nested `{min, max}` object rather than a `[min, max]` pair keeps the two ends self-describing on the wire.

**Spec ref:** `Contracts/EquipmentDtos.cs` (`TelescopeCapabilitiesDto.MoveAxisRateBandsDegPerSec`, `MoveAxisRateBandDto`), `Services/TelescopeService.cs` (`PadBandsFrom`, `BandDtosOf`, `EndpointsOf`), `client/…/lib/util/slew_rates.dart` (`SlewRateBand`, `buildSlewRateOptionsFromBands`, `defaultSlewRate`), `client/…/lib/models/mount_status.dart` (`MountCapabilities.axisRateBands` / `padRateBands`); tests in `TelescopeMoveAxisClampTest`, `slew_rates_test.dart`, `mount_status_test.dart`, `equipment_mount_panel_test.dart`.

**Related:** #1126 (follow-ups of #1087), #1064, #1085, CHANGELOG [Unreleased]

### 2026-10-06 — #1149 POST /platesolve/center: another target while one runs is 409

**Endpoint(s) or area:** `POST /api/v1/platesolve/center` (new 409 response); `IBatchJobService.Enqueue` gains an optional job identity.

**Decision:** a batch job may carry an identity (what it is for). The §65.5 one-live-job-per-type rule still joins a repeat enqueue to the running job when the identities match or either is null; a live job of the same type with a *different* identity raises `BatchJobConflictException` and the caller decides. The centering endpoint's identity is its target (`center:{ra:F6}h,{dec:F6}d`), so a second POST for the same coordinates returns the running job as before (202), and one for other coordinates returns `409 Conflict` (`title: center_in_progress`, detail naming the running job id and the cancel route) instead of silently joining and later reporting the wrong target as centred.

**Reasoning:** the #842 review: the single-flight join is right for an identical request and wrong for a different one; a 409 is the honest answer, and cancelling the running job is one call away.

**Spec ref:** `Services/IBatchJobService.cs` (`BatchJobConflictException`), `Services/InMemoryBatchJobService.cs`, `Endpoints/PlateSolveEndpoints.cs` (`CenterIdentity`); tests in `InMemoryBatchJobServiceIdentityTest`, `CenteringEndpointTest`. Also `CameraService.WithCapabilitiesForTest` (a test seam for the solve-path capability guards, `CameraServiceSolveCaptureTest`).

**Related:** #1149, #842, #1090, CHANGELOG [Unreleased]

### 2026-10-06 — #1215 plate-solve database status lists complete databases only; the settings PUT echoes what was stored

**Endpoint(s) or area:** `GET /api/v1/platesolve/database` (`databases` semantics; no shape change); `PUT /api/v1/profile/plate-solve` (response body).

**Decision:** `databases` lists the complete databases only: an abbreviation with fewer than two tile files (an interrupted download's orphan tile) is left out, unless its files carry ASTAP's single-file `.001` extension; `file_count` still counts every file on disk. The solver applies the same rule before naming a database with `-D`, and logs the incomplete ones once. The plate-solve settings PUT returns the settings the store holds after its write-path normalisation (a stock solver path — `/usr/bin/astap`, `/usr/bin/astap_cli` or `/opt/astap/astap_cli` — migrates to the bundled `/opt/openastroara/astap_cli`, #1136), not the request body as sent, so a client that adopts the echo shows the stored value.

**Reasoning:** the #1213 reviews: one orphan H18 tile next to a complete D80 got `-D h18` for narrow fields (ASTAP exit 33), and the Settings field kept showing a typed path the daemon had already rewritten.

**Spec ref:** `OpenAstroAra.PlateSolving/Solvers/AstapStarDatabase.cs` (`Scan`, `MinTileFiles`, `PartialDatabases`), `Endpoints/ProfileEndpoints.cs` (`PutPlateSolveSettings`), `Services/SolverPathMigration.cs` (`BootWarning`); tests in `AstapStarDatabaseTest`, `PlateSolveSettingsWiringTest`, `imaging_plate_solve_panel_test.dart`.

**Related:** #1215 (follow-ups of #1213), #1121, CHANGELOG [Unreleased]

### 2026-10-08 — #1311 polar alignment residual from guiding

**Endpoint(s) or area:** new WS event `guider.pa_residual`; `GET /api/v1/equipment/guider` (`runtime.pa_residual`, nullable); new `GET /api/v1/equipment/guider/pa-residuals?sessionId=&limit=`.

**Decision:** each guided run (guiding started → stopped) is measured once, over its first 300 s of clean guiding (frames inside a dither settle or a guider pause are left out; a dither, settle, pause or lost star closes a segment and each segment gets its own intercept). The daemon rebuilds the uncorrected Dec motion as the Dec offset minus every Dec pulse already applied × the calibration's Dec rate (`get_calibration_data` yRate, read once per run), fits its slope, and reports `pa_error_min_arcmin = |drift ″/min| / (3600 ω)` ≈ 3.809 × drift. Payload (`PaResidualDto`): `{ id, status: measuring|done|unavailable, started_utc, completed_utc?, sample_seconds, target_seconds, frames, drift_arcsec_per_min?, pa_error_min_arcmin?, uncertainty_arcmin?, reliable?, hour_angle_hours?, dec_deg?, align_error_arcmin?, align_ended_utc?, session_id?, reason? }`. `measuring` is published at the start and every 30 s of sample; `done` once. `reason` on `unavailable`: `lock_shift` (comet tracking moves the star in Dec on purpose), `ao` (an adaptive-optics unit is connected, so the Dec corrections are AO steps, not mount pulses; checked with `get_current_equipment` at the run's start), `no_calibration`, `no_pixel_scale`, `no_fit`. `reliable` is false when the 1σ uncertainty (least squares, widened by the residuals' lag-1 autocorrelation) exceeds max(1′, half the value). Guiding that stops after 120 s of sample still reports; earlier, the measurement is dropped and the previous result is re-published (`{ status: "idle" }` when there is none); a run already reported `unavailable` stays so whenever guiding stops. `align_error_arcmin` is the newest Polar Align result with a measured error (complete, or stopped while adjusting) in the 12 h before. Hour angle and Dec are the mount's at the end. A `done` result is logged to the `pa_residuals` table with the running imaging session (`ActiveRunSessionRegistry.Current`); the list route returns rows newest first, `?sessionId=` narrows to one session, `limit` defaults to 20 (1–500).

**Reasoning:** plate-solve alignment gives the geometric error; the drift the guider actually fights also carries refraction, flexure and the mount's own slop, and a drift check costs minutes of setup. The guide stream already has it. The value is a LOWER BOUND: Dec drift at one hour angle sees only the component of the error across the star's hour circle, so the client says "≥". The Dec of the star does not enter: dδ/dt = ω ε sin θ (rotating the sky about a pole ε off the true one), so the 1 / cos δ in Barrett's formula, which PHD2's Guiding Assistant copies, is not applied; at Dec 45° it would read 41 % high.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1guider~1pa-residuals`; `Services/PaResidualEstimator.cs`, `Services/PaResidualTracker.cs`, `Services/GuiderService.PaResidual.cs`, `Services/SqlitePaResidualLog.cs`, `Services/SqlitePolarAlignmentLog.cs` (`GetLatestMeasuredAsync`), `Contracts/EquipmentDtos.cs` (`PaResidualDto`), `PHD2Guider.GetDecGuideRateAsync`; client `state/guider/pa_residual_state.dart`, `widgets/imaging/guiding_strip.dart`.

**Related:** #1311; tests `PaResidualEstimatorTest`, `PaResidualTrackerTest`, `SqlitePaResidualLogTest`, `GuiderFakeIntegrationTest.A_guided_run_reports_the_polar_alignment_residual_from_its_dec_drift`.

---

### 2026-10-13 — #1177 manual capture frame type

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/exposure` (`ExposureRequestDto`): new optional `image_type`.

**Decision:** `image_type` ∈ {LIGHT, DARK, BIAS, FLAT, DARKFLAT}, case-insensitive; null/blank = LIGHT (the historical behaviour, unchanged for existing clients). It is written as the FITS `IMAGETYP`, mapped to the catalog frame type the sequencer path already uses (`DARKFLAT` → Dark), and DARK/BIAS start the Alpaca exposure with `light=false` so a shuttered camera stays dark. Any other value is an argument error → 422 before the connected check. `SNAPSHOT` is not accepted here: it is the Live tab's own kind.

**Reasoning:** manual capture was LIGHT-only by design; a REST calibration affordance was the one thing missing for scripted dark/bias/flat sets without a sequence.

**Spec ref:** `Contracts/EquipmentDtos.cs` (`ExposureRequestDto.ImageType`), `Services/CameraService.cs` (`NormalizeManualImageType`, `IsLightExposure`).

**Related:** #1177; tests `ManualCaptureImageTypeTest`.
### 2026-10-13 — #1175 server internal state (§66.7)

**Endpoint(s) or area:** new `GET /api/v1/server/internal-state` → `ServerInternalStateDto`.

**Decision:** `{ captured_utc, uptime_seconds, process: { working_set_bytes, managed_heap_bytes, heap_committed_bytes, gen0_collections, gen1_collections, gen2_collections, gc_pause_total_ms, cpu_percent?, thread_count, thread_pool_busy_workers, thread_pool_pending_work_items }, web_sockets: { open_connections, max_connections }, host: { processor_count, load_average1_min?, os_description, architecture, runtime_version } }`. Sampled on read from the runtime (no background sampler). `cpu_percent` is the process's share of one core's wall time since the previous read (400 = four cores flat out) and is null on the first read after boot; `load_average1_min` is Linux-only. `Cache-Control: no-store`. The §66.10 latency budget for `/healthz` and this route is a test that records the figures and gates only with `OPENASTROARA_PERF_GATE=1`.

**Reasoning:** the Stats "Pi performance" panel was the one §66.7 item without a wire surface; a read-on-demand sample keeps the cost at zero when nobody looks, which is why the executor's own per-pool figures (still log-only) are not polled here.

**Spec ref:** `Services/ServerInternalStateService.cs`, `Endpoints/ServerStateEndpoints.cs`, `Contracts/ServerStateDtos.cs`; client `widgets/stats/pi_performance_section.dart`, `state/stats/server_internal_state_state.dart`.

**Related:** #1175; tests `ServerInternalStateTest`, `ServerInternalStateRouteTest`.

---

### 2026-10-13 — #1156 per-axis minimum move

**Endpoint(s) or area:** `GET/PUT /api/v1/profile/phd2` (`Phd2SettingsDto`): new optional `dec_minimum_move` (double, px).

**Decision:** `minimum_move` keeps its name and becomes the RA dead band; `dec_minimum_move` is Dec's. The DTO field is nullable with a null default: a profile.json written before the split deserializes to null and hydration uses the RA value for Dec, so an upgrade changes nothing on the wire until the user sets Dec. The client always sends both. The engine push sends `set_algo_param(ra, minMove)` and `set_algo_param(dec, minMove)` independently, each only when > 0.

**Reasoning:** Dec drifts slowly and most mounts have Dec backlash, so Dec wants a wider dead band than RA; one shared value forced a compromise on both axes.

**Spec ref:** `Contracts/ProfileDtos.cs` (`Phd2SettingsDto.DecMinimumMove`), `Services/StoreBackedProfileService.ApplyPhd2`, `PHD2Guider.GuiderEngineConfig.cs`; client `state/settings/phd2_settings_state.dart`, `settings/registry.dart` (`eq.guider.dec_minimum_move`).

**Related:** #1156; tests `PHD2GuiderEngineConfigTest.Build_pushes_each_axis_its_own_minimum_move`, `StoreBackedProfileServiceTest`.
### 2026-10-13 — #1156 guider profile lifecycle hooks + terminal status for guider 202s

**Endpoint(s) or area:** `POST /api/v1/profiles` (duplicate, no settings body) and `PUT /api/v1/profiles/{id}` (rename) gain guider-twin side effects; new WS event `guider.operation_complete`; `guider.dither_complete` (declared, never published) is now published.

**Decision:** a duplicate without a settings body clones the ACTIVE profile's twin under the new profile's name (`create_profile` with `copy_from`, `select=false`); a rename sends `rename_profile` for the twin under the old display name, then the legacy `ara-<slug>-<id8>` name, releasing and reconnecting the daemon's equipment only when that twin is the selected one — and only while the guider is Stopped (releasing it sends `stop_capture`, so a rename during a guided run is skipped and logged instead). Neither runs when another ARA profile still carries the old name (identically-named profiles share a twin) or when the new name maps to the same twin. Both are fire-and-forget and best-effort like the delete hook; the HTTP response never waits on the guider. Start/Stop/Dither over REST publish one `guider.operation_complete { operation_id, operation, ok, error? }` when the op ends; a dither (REST or sequencer) also publishes `guider.dither_complete { ok, operation_id?, error? }` where `ok` is the settle verdict (`PHD2Guider.LastSettleError`: the daemon's SettleDone error, or a timeout sentence when the failsafe gave up).

**Reasoning:** a rejected dither was indistinguishable from a success over a 202 and nothing published the declared dither event; a duplicated or renamed profile lost its calibration to a fresh blank twin while the old one lingered on the daemon.

**Spec ref:** `Endpoints/ProfilesEndpoints.cs`, `Services/GuiderService.ProfileLifecycle.cs` (`TryCloneAraGuiderProfileAsync`, `TryRenameAraGuiderProfileAsync`), `Services/GuiderService.cs` (`RunAndReportAsync`), `PHD2Guider.GuiderProfile.cs` (`CloneGuiderProfileAsync`, `RenameGuiderProfileAsync`), `Contracts/WsEvents/WsEventCatalog.cs`.

**Related:** #1156; tests `ProfilesEndpointsGuiderHooksTest`, `GuiderFakeIntegrationTest` (#1156 group).

---

### 2026-10-07 — #1299 Bahtinov mask focus readout; autofocus and sequence starts refused while it runs

**Endpoint(s) or area:** new `POST /api/v1/bahtinov-focus/start`, `POST …/stop`, `GET …/state`, `GET …/frame`; `POST /api/v1/equipment/focuser/autofocus` and `POST /api/v1/sequences/{id}/start` gain a 409.

**Decision:** `start {exposure_sec 0.05–30 (default 1), binning? 1–4 (default 1)}` → 202; 400 out of range; 409 already running, or the camera is busy (an autofocus run, or any sequence run in flight). The loop captures through the §59 analysis seam and fits the mask pattern on the brightest star (locked to that star after the first fit; after 5 misses the lock falls back to the user's pick, or to the whole frame when there is none — see the 2026-10-16 entry). `state` → `BahtinovFocusStatusDto { active, state: idle|running|stopped|error, exposure_sec, binning, seq, started_utc, latest, recent[≤120], best_offset_px, zone_px, zone_from_optics, zone_um, focal_ratio, within_zone, error, consecutive_failures, has_frame, frame_seq }`; a sample is `{ seq, captured_utc, detected, problem: no_star|near_edge|no_pattern|null, offset_px, defocus_um, within_zone, star_x, star_y, peak_adu, overlay? }`, with `overlay { crop_size, lines[3] { role: outer|central, x1, y1, x2, y2, angle_deg }, intersection_x, intersection_y, spread_deg }` on `latest` only, in crop pixels. `offset_px` is the central spike's signed distance from the outer spikes' crossing; its sign convention is fixed by the session's first fit (which way that is on the focuser depends on the mask and the optical train, so the client gives advice relative to the user's last move). `defocus_um = offset × pixel size × binning × 3πN/4` (N = working focal ratio, reducer applied); `zone_um` is half the CFZ the autofocus step sizing uses (2.2 µm × N² / 2) and `zone_px` its offset equivalent, else a fixed 0.5 px when the profile lacks focal length, aperture or pixel size. A frame without a pattern is a sample, never an error; 3 consecutive capture faults end the readout in `error`. `frame` is the 256-px crop around the locked (or picked) star, with or without a fit, rendered with a sky-relative spike stretch, or the whole frame (stars ringed) when no star is locked — `frame_crop` says which (2026-10-16 entry); JPEG with `X-Frame-Seq`, 204 before one. No WebSocket events: the client polls, as for the guide-camera focus loop. While the readout is active, `POST /equipment/focuser/autofocus` and `POST /sequences/{id}/start` return 409 Problem `title: bahtinov_focus_active`.

**Reasoning:** the mask is on while the readout runs; a sweep through it fits diffraction spikes and a sequence would image through it. Refusing at the daemon makes that impossible from any client. The µm figure ignores the crossing angle's ±tan θ/2 term, whose sign depends on how the mask is cut (about ±15% for a typical mask); it is for "inside the zone or not", not for the micron.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1bahtinov-focus~1start` (…`~1stop`, `~1state`, `~1frame`); `Contracts/BahtinovFocusDtos.cs`, `Services/BahtinovFocusService.cs`, `OpenAstroAra.Image/ImageAnalysis/BahtinovAnalyzer.cs`, `Endpoints/BahtinovFocusEndpoints.cs`, `Program.cs` (`BahtinovOpticsFor`); client `widgets/focus/bahtinov_focus_card.dart`, `state/focus/bahtinov_focus_state.dart`.

**Related:** #1299; tests `BahtinovAnalyzerTest`, `BahtinovFocusServiceTest`, client `bahtinov_focus_card_test`, `bahtinov_focus_state_test`. Amended 2026-10-16 (star pick, cropped no-pattern picture, short-spike detector) — next entry.

---

### 2026-10-19 — Recent and saved targets live in the profile: `GET`/`PUT /api/v1/profile/saved-targets` (#1267 item 5)

**Endpoint(s) or area:** new profile section `saved_targets` → `SavedTargetsDto` `{targets: [{name, aliases[], ra_deg, dec_deg, source, saved_utc, typed_as_j_now}]}`, newest first. `GET` reads it; `PUT` replaces the whole list and echoes the canonical form; 422 for a blank or over-long name, a name with control characters, a non-finite or out-of-range position, an unknown `source` (`typed`, `index`, `cache`, `simbad`) or more than 200 entries. The daemon trims names, keeps one entry per name (case-insensitive, the newest), wraps RA into 0–360, trims and caps aliases at 32. Carried on profile select; kept in a share export (a target list is not rig-specific). A stored file is read leniently (a damaged entry is dropped, not the profile).

**Decision:** the client's recent targets moved from a per-device file to the profile, so a target prepared at home (typed by coordinates, or resolved by name through `/targets/resolve`) is on the list at the dark site on any client. The client keeps a local mirror per (server, profile) with the names the profile held at the last sync: with a server it merges the profile's list three ways against that base (a shared name takes the newer entry, the profile on a tie; an entry new on either side joins; a name forgotten on either side since the sync stays gone), pushes the result when it differs, and writes every change through `PUT`; without a server the last scope's mirror is the list and the next connect syncs. One rig's list never lands in another's profile.

**Reasoning:** issue #1267 part 5; the dark-site rule (offline-first) means the list must be usable with no server, and the profile is where per-rig preparation already lives (custom horizon, filters).

**Spec ref:** `OpenAstroAra.Server/Services/SavedTargetsValidator.cs`, `Endpoints/ProfileEndpoints.cs`; client `lib/services/custom_targets_service.dart`, `lib/state/sky_atlas/custom_targets_state.dart`, `lib/services/profile_api.dart`.

**Related:** #1267 (closes the issue with parts 2–4), #1272 (the coordinate entry); tests `SavedTargetsValidatorTest`, `SavedTargetsStoreTest`, client `custom_targets_state_test`, `custom_targets_service_test`.

---

### 2026-10-18 — Target search: `GET /api/v1/targets/resolve` (offline index, then Simbad through the daemon) (#1267)

**Endpoint(s) or area:** new `GET /api/v1/targets/resolve?name=` → `TargetResolveResponseDto` `{query, status, source, main_id, aliases[], ra_deg, dec_deg, object_type, magnitude, message}`. Always 200 for a usable name; 400 for a blank name, one over 128 characters or one with control characters. `status` is `resolved`, `not_found` or `offline`; `source` is `index`, `cache` or `simbad` (empty on a miss). Positions are J2000 degrees.

**Decision:** the daemon resolves a name in three steps and stops at the first answer. (1) The **offline index**: built lazily from the sky-data the Data Manager has installed — HYG's HIP / HD / HR / Gliese / Bayer / Flamsteed / proper / variable-star columns ("AB Cas" is HYG's `var=AB con=Cas`) and the OpenNGC-layout files' Messier / NGC / IC / Identifiers / Common names columns — keyed case-, space-, leading-zero- and Greek-spelling-insensitively; rebuilt when a catalog lands. No internet. (2) The **cache** of earlier Simbad answers in the catalog database (`target_resolutions`), so a name resolved at home is still there at the dark site. (3) **CDS Sesame** (`-oxpIF/SNV`, Simbad then NED then VizieR), 5 s and 1 MB bound, HTTPS, no redirects, DTDs off; a found object is cached, a miss is `not_found` and never cached, an unreachable service is `offline` with a "needs internet" message. The client calls only the daemon: over the SBC's hotspot the lookup works whenever the SBC has an uplink, and the client keeps no internet dependency (the standing planning-compute rule is not touched: this is a data lookup, not planning).

**Client:** the planetarium search box resolves its local catalogs first as before; a miss asks the daemon. A resolved name the bundle already lists (by its main identifier or any alias, exact designation match) is that bundled row: the map pans to it (a `goto` carrying the row's name), no dialog (#1267 item 4, PR #1404). Any other resolved name opens the target dialog filled with the main identifier, the J2000 position and a provenance line (source, type, magnitude, aliases), from which it is framed or added to the plan like any catalog target. A `not_found` / `offline` answer falls through to the planetarium's own engine search with the daemon's reason in a snackbar.

**Reasoning:** issue #1267 part 2/3: a star such as AB Cas (HIP 12235, TYC 4320-505-1, …) could not be found by any of its names, and an online-only resolver would be a defect at the dark site (offline-first).

**Spec ref:** `OpenAstroAra.Server/Services/TargetNameIndex.cs`, `SesameClient.cs`, `TargetResolverService.cs`, `Endpoints/TargetEndpoints.cs`; client `lib/services/target_resolve_api.dart`, `lib/widgets/sky_atlas/custom_target_dialog.dart`.

**Related:** #1267 (parts 4 and 5, one row per object and profile-backed saved targets, follow separately), playbook §36.8; tests `TargetNameIndexTest`, `SesameClientTest`, `TargetResolverServiceTest`, `TargetEndpointsTest`, client `target_resolve_api_test`, `custom_target_dialog_test`.

---

### 2026-10-18 — Every device's Alpaca identity is re-read on connect and before a run; a swap is re-pinned, not refused (#1362)

**Endpoint(s) or area:** new read-only `GET /api/v1/profile/equipment-identity` (`equipment_identity` profile section: the last-seen `unique_id`, `name` and `seen_utc` per gear slot `camera`, `guidecamera`, `telescope`, `focuser`, `filterwheel`, `rotator`, `dome`, `switch`, `safetymonitor`, `observingconditions`, `covercalibrator`). `POST /api/v1/sequences/{id}/start` re-reads every connected device's identity before starting. `POST /guider/profile/push`'s #1308 refusal narrows (below).

**Decision:** on every Connected transition the daemon reads that device's `UniqueID` from its server's `configureddevices` and compares it with the slot's stored one. A first sighting is recorded quietly. A different id posts an Equipment inbox notice ("Guide camera changed: A → B", with what to re-check) and clears the calibrations measured through the old device: camera → focus, tracking and sky-model calibrations; focuser → focus; mount → tracking and the guider's calibration; guide camera → dark library / defect map `valid: false` and the guider's calibration. An unreachable management API, a server that does not list the device, or a blank id leaves the profile as it is. The section is carried on profile select, reset on profile create from the live store, and stripped from a share export.

**Supersedes part of 2026-10-11 #1308:** "a pinned camera at another number or missing is refused (422)" now reads: a pinned camera **at another number** is refused (it moved; the user picks again), and so is one whose number **nothing answers at**; a pinned camera that is gone while a **different, non-main camera answers at its number** is a swap and is **not** refused — the push goes ahead, and the connect-time reconcile re-pins the slot's camera, re-reads its pixel size and tells the user. The main-camera refusal stands unchanged.

**Reasoning:** 2026-10-15 a QHY5III585M replaced the guide camera in the same slot; the profile kept 3.75 µm and an empty pin because the wizard was never re-run. The wizard's autofill is a one-off; the driver's facts have to be re-read each time the gear comes up, and the one thing the driver cannot tell us (focal length, offsets, backlash) is what the notice asks the user to check.

**Spec ref:** `OpenAstroAra.Server/Services/EquipmentIdentityReconciler.cs`, `EquipmentIdentityReconcile.cs` (pure decision + invalidation advice), `GuiderService.CameraReconcile.cs`, `GuiderCameraPinCheck.cs` (`IsMainCamera`), `EquipmentEventPublisher.Transition`.

**Related:** #1362, #1391 (the guide-camera half), #1308; tests `EquipmentIdentityReconcilerTest`, `EquipmentIdentityStoreTest`, `GuiderCameraReconcileTest`, `GuiderCameraPinCheckTest`.

---

### 2026-10-16 — No storage disk, no run: `POST /sequences/{id}/start` refuses without one (#1374)

**Endpoint(s) or area:** `POST /api/v1/sequences/{id}/start` gains a second 409.

**Decision:** when the save directory sits under the store mount (`/media/openastroara`) and nothing is mounted there, or the save volume is critically low under an `OnDiskSpaceCritical` policy of `abort` or `pause`, the start answers 409 Problem `title: store_unavailable` with `detail` in words for the user ("No storage disk: nothing is mounted at …"). The client shows that text in a dialog with a jump to Options → Storage. A host whose mount table cannot be read (no `/proc`) never refuses. Mid-run, a sequence capture into an ejected store parks the run `paused_awaiting_user` (Resume retakes the frame) instead of failing it, and a failed, skipped or disabled exposure no longer counts toward Dither or Autofocus after N exposures.

**Reasoning:** 2026-10-16 the store drive fell off mid-run; every frame was refused at once by the §29 pre-capture check, the run carried on under ContinueOnError, and each refused frame still fired the dither — half an hour of "settling" with nothing saved. Asking the same question once before the run (and stopping when it turns false) costs nothing and tells the user while they are still at the rig.

**Spec ref:** `OpenAstroAra.Server/Endpoints/SequenceEndpoints.cs` (`StartSequenceAsync`), `ICameraService.StoreRefusal`, `OpenAstroAra.Core/Model/StoreUnavailableException.cs`, `TakeExposure.Execute`, `ItemUtility.ExposureTaken`.

**Related:** #1374; tests `NoStorageDiskTest`, client `sequencer_run_controls_test`. The auto-flats run started at the end of a session goes through `StartAsync` directly, not the endpoint, so it meets the mid-run pause rather than the start refusal.

---

### 2026-10-17 — The Guiding Assistant from Ara (#1364 item 4)

**Endpoint(s) or area:** new `GET /api/v1/equipment/guider/live/assistant`, `POST …/live/assistant/start`, `POST …/live/assistant/stop`, `POST …/live/assistant/apply`; WS `guider.event` gains kinds `guiding_assistant_progress` and `guiding_assistant_done` (payload `state`, `error?`).

**Decision:** PHD2's Guiding Assistant runs headless in openastro-guider (#83: `guiding_assistant_start/stop/get_status/apply`); Ara drives it through the daemon, never client → guider (§63.20). `GET …/assistant` is always 200 with `GuidingAssistantStatusDto { connected, state, duration_sec?, measure_backlash?, elapsed_sec?, samples?, pixel_scale?, ra_drift_px_min?, dec_drift_px_min?, ra_rms_px?, dec_rms_px?, high_freq_rms_px?, ra_peak_px?, dec_peak_px?, ra_peak_peak_px?, ra_max_drift_px_sec?, snr?, star_mass?, polar_align_error_arcmin?, polar_align_error_is_lower_bound?, backlash_status?, backlash_result?, backlash_ms?, backlash_sigma_ms?, backlash_px?, recommendations?, error? }` — `state` is `idle` | `measuring` | `measuring_backlash` | `done` | `failed`; a figure the guider left out (idle; fewer than two samples; no backlash test) is null, never 0; `connected` is false (state `idle`) with no guider **or a guider that does not answer the method** (an older build), so the client says "not available" rather than "ready". `recommendations { min_move_ra_px?, min_move_dec_px?, min_move_seeing_based?, exposure_sec?, exposure_max_sec?, drift_limiting_exposure_sec?, dec_backlash_ms?, dec_guide_mode?, dec_algorithm?, messages[{id, text}] }` is present once `done` (every figure optional: the guider leaves out what it did not measure). `POST …/start { duration_sec?, measure_backlash }` (duration 10–3600, null = the guider's 120 s default, sent as `duration_s` only when given) and `POST …/stop` answer the status they leave the guider in (200); `POST …/apply { which[] }` writes the named recommendations (`exposure`, `min_move_ra`, `min_move_dec`, `dec_backlash`, `dec_guide_mode`, `dec_algorithm`) to the guider's current profile and answers `{ applied[] }`. Refusals: 409 no guider; 400 a duration out of range or an empty `which`; 422 the guider refused, in its own words (no star selected, not calibrated, already running, a polar-alignment tool holding the star, dark build). The two WS kinds tell a client to re-read the status; the figures are never duplicated on the wire.

**Reasoning:** the epic's last item: the measurement behind PHD2's min-move / exposure / backlash advice was reachable only from a desktop dialog; with the guider's headless RPC in place, Ara is the only screen needed.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1guider~1live~1assistant` (…`~1start`, `~1stop`, `~1apply`); `Contracts/EquipmentDtos.cs` (`GuidingAssistantStatusDto`, `GuidingAssistantRecommendationsDto`, `GuidingAssistantStartRequestDto`, `GuidingAssistantApplyRequestDto`), `Services/GuiderLiveService.cs` (`MapGuidingAssistant`), `Equipment/MyGuider/PHD2/PHD2Guider.GuidingAssistant.cs`, `PHD2Methods.GuidingAssistant.cs`; client `widgets/imaging/guiding_assistant_dialog.dart`, `state/guider/guiding_assistant_state.dart`, `models/guiding_assistant.dart`.

**Related:** #1364, openastro-guider #83; tests `GuiderLiveServiceTest` (mapping, refusals without a guider), `GuiderLiveFakeIntegrationTest` (the four methods over the wire, named params, 400/422, a silent older guider), `PHD2GuiderMarkerEventTest`, `GuiderStepPayloadTest`; client `guiding_assistant_dialog_test`, `guiding_assistant_test`.

---

### 2026-10-16 — The guider's graph history for a client that connects mid-session (#1364 item 5)

**Endpoint(s) or area:** new `GET /api/v1/equipment/guider/history?max_points=` (`GuiderHistoryDto`).

**Decision:** always 200: `{connected, steps[{age_sec, ra_raw_px?, dec_raw_px?, ra_arcsec?, dec_arcsec?, ra_duration_ms, dec_duration_ms, pixel_scale_arcsec?, snr?, star_mass?}]}`, oldest first, from the guider's own graph history (`get_guide_history`). `max_points` (snake-case query name) is 1–400, default 400; a value outside that range is clamped into it, not refused. The step fields match `guider.step` (pulse durations negative = East / South). `age_sec` is how long ago the step was taken, measured on the daemon's clock — the guider's own when they run on the same host, as on the SBC (a remote guider whose clock differs skews the ages) — so a client places history on its own time axis without the SBC's and the client's clocks having to agree; a point without a plausible Unix-millisecond stamp (an older guider build) is spaced 2 s back from the newest. `connected` false or no history → `steps` empty. The client fills an empty guiding graph from it at start-up and when the guider connects, once, never over live steps.

**Reasoning:** a client that connects (or reconnects) mid-run started with an empty graph and no RMS for minutes, though the guider had the whole session.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1guider~1history`; `Contracts/EquipmentDtos.cs` (`GuiderHistoryDto`, `GuiderHistoryStepDto`), `Endpoints/EquipmentEndpoints.cs` (`GetGuiderHistoryAsync`), `Services/GuiderLiveService.cs` (`GetHistoryAsync`, `MapHistory`), `Equipment/MyGuider/PHD2/PHD2Guider.LiveView.cs` (`GetGuideHistoryAsync`); client `state/guider/guide_step_state.dart` (`seedFromHistory`, `historySteps`).

**Related:** #1364; tests `GuiderLiveServiceTest` (`History_maps_to_guide_steps_with_ages_and_signed_pulses`), `GuiderLiveFakeIntegrationTest` (over the wire, silent refusal), `EquipmentEndpointsGuiderHistoryTest` (the `max_points` binding over HTTP), client `guide_step_history_test`.

---

### 2026-10-16 — One guider state everywhere: `looping`, `star_selected`, `settling` (#1364 item 6)

**Endpoint(s) or area:** `GET /api/v1/equipment/guider` — `runtime.state` (`GuiderStateDto.State`).

**Decision:** `runtime.state` is one of `stopped` | `looping` | `star_selected` | `calibrating` | `guiding` | `settling` | `paused` | `star_lost` | `assistant`. `assistant` (2026-10-18) is what Guiding, Looping or Selected read while the Guiding Assistant measures (PHD2 says "Guiding" with corrections off): no polar-alignment residual is measured and the lost-star watch stands down for the run; the flag follows the assistant RPCs and the guider's own Progress/Done events. `looping` (exposures looping, no star chosen) and `star_selected` (looping with a star chosen) used to read `stopped`; `settling` is `guiding` while the guider settles after a dither or a start. `dithering`, listed before, was never sent and still is not. A token a client does not know is read as an unknown phase — the Ara client shows *Unknown* (the guiding strip, *connected*) — never as `stopped`. Clients use one wording for every phase (the Live strip in lower case, the guider Settings page's connection card and the Sequence tab) and, while `calibrating`, append the leg from `calibration_*`.

**Reasoning:** field night 2026-10-16: the strip said "stopped" while the guide camera was looping (and refused a single frame for guide-camera focus), and the Run tab said nothing while the guider calibrated for five minutes.

**Spec ref:** `Services/GuiderService.cs` (`MapGuidingState`), `Contracts/EquipmentDtos.cs` (`GuiderStateDto`); client `models/guider_status.dart` (`GuiderRuntimeState`, `guiderRuntimeText`, `runtimeSummary`), `widgets/sequencer/run_dashboard_band.dart` (`RunGuiderLine`), `widgets/imaging/guiding_strip.dart`.

**Related:** #1364; tests `GuiderServiceTest` (`Every_guider_phase_has_its_own_state_token`), client `guider_status_test`, `run_guider_line_test`.

---

### 2026-10-16 — Every guider setting from Ara, kept in the profile (`/api/v1/profile/guider-advanced`) (#1364 item 3)

**Endpoint(s) or area:** new `GET`/`PUT /api/v1/profile/guider-advanced` (`GuiderAdvancedSettingsDto`), profile JSON `guider_advanced`; new `GET /api/v1/equipment/guider/advanced` (`GuiderAdvancedViewDto`); the guider push (`POST /api/v1/equipment/guider/profile/push`, both scopes, and the connect-time push) carries the section; profile select and clone (`ProfileStoreSnapshot`) now carry `unguided` and `guider_advanced`.

**Decision:** the guider's settings beyond Tune Guiding become a profile section with every field optional — null keeps the guider's own value, so a profile never overwrites a setting it did not set: `{multi_star_enabled, fast_recenter, tolerate_jumps, tolerate_jumps_threshold_px, min_star_hfd, max_star_hfd, min_star_snr, mass_change_threshold_enabled, mass_change_threshold, search_region_px, auto_exposure_min_ms, auto_exposure_max_ms, auto_exposure_target_snr, noise_reduction ("None" | "2x2 Mean" | "3x3 Median"), use_subframes, max_ra_duration_ms, max_dec_duration_ms, backlash_enabled, backlash_pulse_ms, backlash_floor_ms, backlash_ceiling_ms, assume_orthogonal, stop_guiding_when_slewing, cal_flip_requires_dec_flip, variable_delay_enabled, variable_delay_short_s, variable_delay_long_s, dither_scale_factor, dither_ra_only, ra_algorithm, dec_algorithm}`. It is kept apart from `phd2` so a client that PUTs that section without these fields cannot wipe them. A PUT replaces the whole section: a field left out of the body is cleared to null (back to the guider's own value), so a client sends every field it wants kept — GET, change, PUT. The PUT validates at the write boundary (400) with the guider's own ranges: every set number positive except the max RA/Dec pulse (0–1,000,000 ms; 0 = no correction on that axis), the backlash pulse 20–8000 ms, backlash on only with a pulse width (2026-10-18: an enable alone made the guider refuse the whole group on every push), min ≤ max for HFD, auto exposure, backlash and the variable delay, a known noise method, the variable delay as all three values or none, algorithm names non-blank (trimmed). Only set keys reach the guider, grouped as its setters take them (a profile saved before the backlash rule, on with no pulse, skips `set_backlash_comp` with a warning in the log so the other groups still land) (`set_guider_options`, `set_star_detection`, `set_auto_exposure`, `set_noise_reduction`, `set_use_subframes`, `set_guide_limits`, `set_backlash_comp`, `set_mount_options`, `set_variable_delay_settings`, `set_dither_settings`, `set_algo` per axis) on the tuning push — guiding continues; `set_algo` goes before Tune Guiding's `set_algo_param` aggression / minimum move, so those land on the newly chosen algorithm rather than on the one it replaces; `multi_star_enabled` rides `set_profile_setup`, which the guider accepts only with its equipment disconnected, so it lands on the next connect or full push. `GET /equipment/guider/advanced` is always 200: `{connected, current: GuiderAdvancedSettingsDto (the guider's values now, null where it did not answer), ra_algorithms[], dec_algorithms[]}`; its reads all run in parallel and a refused one posts no inbox error. Profile select and clone previously skipped `unguided` (#1264), so selecting a profile kept the previous rig's unguided values; both sections now follow the profile, and a profile without them reads the defaults (all-null for `guider_advanced`).

**Reasoning:** field night 2026-10-16: multi-star, star detection and the algorithms were reachable only on the guider's own page; the epic (#1364) puts the guider inside Ara, and the values belong to the rig, so they live in its profile.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1profile~1guider-advanced`, `…~1equipment~1guider~1advanced`; `Contracts/ProfileDtos.cs` (`GuiderAdvancedSettingsDto`), `Contracts/EquipmentDtos.cs` (`GuiderAdvancedViewDto`), `Services/GuiderAdvancedSettingsMapping.cs`, `Services/ProfileStoreSnapshot.cs`, `Endpoints/ProfileEndpoints.cs`, `Equipment/MyGuider/PHD2/PHD2Guider.AdvancedSettings.cs`, `PHD2Guider.GuiderEngineConfig.cs`; client `widgets/imaging/guider_advanced_dialog.dart`, `models/guider_advanced_settings.dart`, `services/guider_advanced_api.dart`.

**Related:** #1364, #1264; tests `GuiderAdvancedSettingsTest`, `ProfileStoreNullSectionTest` (`Unguided_and_guider_advanced_follow_the_profile_through_select`), client `guider_advanced_dialog_test`.

---

### 2026-10-16 — Guider calibration in Ara: step plot, stored result, verdict, clear (#1364 item 2)

**Endpoint(s) or area:** new `GET /api/v1/equipment/guider/live/calibration`, `POST …/live/calibration/clear`; WS `guider.event` kind `calibrating` gains `dx_px`, `dy_px`.

**Decision:** `GET …/live/calibration` is always 200 with `GuiderCalibrationViewDto { connected, run_state, steps[{direction, step, dx, dy, dist}], error?, data?, verdict, verdict_reason? }`. `run_state` is this session's calibration run: `none` | `running` | `complete` | `failed`; `steps` are every `Calibrating` event of the run (the star's displacement from the leg's start, guide px — PHD2's calibration plot; a step where the guider sent no `dx`/`dy` is left out of `steps`, and `guider.event` omits `dx_px`/`dy_px` for it), kept after completion and cleared when the next calibration starts; a run that stops before it finishes (loop stopped, guiding stopped, guider link lost — the guider sends no `CalibrationFailed` for these) ends `failed` with the reason in `error`. `data` is the guider's stored calibration (`get_calibration_data`): `calibrated`, `x_angle_deg`, `y_angle_deg`, `x_rate_px_per_sec`, `y_rate_px_per_sec`, `x_parity`, `y_parity`, `declination_deg`, and `orthogonality_error_deg` (how far the axes are from 90° apart); null when the guider does not answer. `verdict`: `redo` when the run failed (the reason adds that an earlier stored calibration stays in use, when the guider still holds one) or nothing is stored; `check` while running, when the axes are more than 10° from square, or when the Dec rate is more than 2× off the RA rate corrected for declination (checked only when the declination is known and within ±80°, where the cos(dec) correction is trustworthy); `good` otherwise — `verdict_reason` says why. `connected` false → `run_state` `none`, `verdict` `redo`. `POST …/clear` drops the stored calibration (`clear_calibration Mount`) so the next guide start calibrates, and resets the run (`none`, no steps): 204; 409 no guider; 422 the guider refused.

**Reasoning:** field night 2026-10-16: a five-minute calibration ran with nothing in Ara to show it, and no way to judge or redo the result without the guider's page.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1guider~1live~1calibration` (…`~1clear`); `Contracts/EquipmentDtos.cs` (`GuiderCalibrationViewDto`, `GuiderCalibrationStepDto`, `GuiderCalibrationDataDto`), `Services/GuiderLiveService.cs` (`GetCalibrationAsync`, `Judge`, `ToDto`), `Equipment/MyGuider/PHD2/PHD2Guider.Calibration.cs`, `PHD2Guider.cs` (`CalibrationRun`); client `widgets/imaging/guide_calibration_panel.dart`, `models/guider_calibration_view.dart`.

**Related:** #1364; tests `GuiderLiveServiceTest` (verdict, orthogonality), `PHD2GuiderMarkerEventTest` (run bookkeeping), client `guide_camera_panel_test`.

---

### 2026-10-16 — Live guide-camera view: the guider's picture, locked star, stars, star pick (#1364 item 1)

**Endpoint(s) or area:** new `GET /api/v1/equipment/guider/live`, `GET …/live/frame`, `POST …/live/loop`, `POST …/live/stop`, `POST …/live/select {x, y}`, `POST …/live/deselect`.

**Decision:** `GET …/live` is always 200 with `GuiderLiveDto { connected, app_state, frame_width?, frame_height?, lock_x?, lock_y?, search_region?, snr?, star_mass?, multi_star_enabled?, stars[{x, y, snr?, mass?, hfd?}], has_frame }`: `connected` false with nulls and no stars when no guider is connected; `app_state` is the guider's own phase (`Stopped` | `Looping` | `Selected` | `Calibrating` | `Guiding` | `LostLock` | `Paused`); coordinates are guide-camera pixels; `snr`/`star_mass` are the last guide frame's while `app_state` is `Guiding`, and the chosen star's own detected centroid (the one under the lock position, within the search region) while it is `Selected` (2026-10-18: a tap shows an SNR at once); null in every other state; `stars` are the guider's detected stars, brightest first, at most 12. Each field is read best-effort: one the guider refuses (no camera, no image yet) is null — never an inbox notification or an Error log line, since the dialog polls it — and the rest still answer. `GET …/live/frame` is the guider's own live JPEG fetched daemon-side (the client never talks to the guider, §63.20) — 204 until it has one, and 204 when the fetch fails or takes over 5 s (never a 500). The five reads behind `GET …/live` run in parallel. `loop` connects the guider's camera and mount first when they are not, then starts looping exposures; `stop` stops looping or guiding; `select {x, y}` locks the guider on the star nearest that guide-pixel position (`set_lock_position`, exact = false); `deselect` drops the star. Each action: 204; 409 when no guider is connected; 400 for a negative or non-finite position, or one outside the frame once the guider reports its frame size (without a size the guider judges the position); 422 when the guider refuses (no image, no star near the tap, could not connect its equipment).

**Reasoning:** field night 2026-10-16: choosing a guide star, watching it and seeing what the guider was doing all meant opening the guider's own web page; the epic (#1364) makes the guider part of Ara.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1guider~1live` (…`~1frame`, `~1loop`, `~1stop`, `~1select`, `~1deselect`); `Contracts/EquipmentDtos.cs` (`GuiderLiveDto`, `GuiderLiveStarDto`, `GuiderLiveSelectRequestDto`), `Services/GuiderLiveService.cs`, `Endpoints/EquipmentEndpoints.cs` (`GuiderLiveActionAsync`), `Equipment/MyGuider/PHD2/PHD2Guider.LiveView.cs`; client `widgets/imaging/guide_camera_panel.dart`, `state/guider/guider_live_state.dart`, `services/guider_live_api.dart`, `models/guider_live.dart`.

**Related:** #1364; tests `GuiderLiveServiceTest`, client `guide_camera_panel_test`.

---

### 2026-10-16 — Bahtinov star pick, cropped picture while locked, short-spike detector; guider calibration leg

**Endpoint(s) or area:** new `POST /api/v1/bahtinov-focus/pick {x, y}` and `DELETE …/pick`; `GET …/state` gains `frame_width`, `frame_height`, `frame_crop {left, top, size}?`, `pick_x?`, `pick_y?`; `GET …/frame` semantics; `GET /api/v1/equipment/guider` runtime gains `calibration_direction?`, `calibration_step?`, `calibration_distance_px?`; WS `guider.event` gains kind `calibrating`.

**Decision:** `pick {x, y}` in (binned) frame pixels locks the readout on that star → 204; 400 when `x` or `y` is negative or, once a frame has been analysed, outside `frame_width × frame_height`; allowed while idle (Start clears the pick). A picked star keeps its lock through misses (the lock falls back to the pick, never to the whole frame — unless the pick, made before the session's first frame, lies outside it: it is then dropped and the whole frame searched), is searched in a 32-px window (a brighter neighbour inside the default 96-px window would otherwise take the lock), and the crop is centred on the brightest star near the pick. `DELETE …/pick` → 204 forgets it. `GET …/frame` is the 256-px crop around the locked or picked star whenever there is one — with or without a fitted pattern, rendered with a sky-relative asinh stretch so faint spike arms show — and the whole frame (stars ringed) only when nothing is locked; `frame_crop` says which part of the frame the picture covers (absent = whole frame). Other stars inside the crop are masked out of the fit. The detector scores signed, radius-weighted line integrals and accepts a spike at ≥ 4 noise sigmas (MAD over all angles) with ≥ 12° between spikes, so a short-spike pattern on a small image scale (2.4″/px: spikes ~30 px, below the per-pixel noise beyond r ≈ 40 px) still fits. While the guider calibrates, its runtime carries the leg (`West`/`East`/`North`/`South`…), step and distance (px), null outside `calibrating` (cleared on `CalibrationComplete`, `CalibrationFailed`, `GuidingStopped`, `LoopingExposuresStopped`), and `guider.event` publishes `calibrating {direction, frame = step, distance_px}` once per step.

**Reasoning:** field night 2026-10-16: at 2.4″/px the mask pattern was a few dozen pixels across and the previous detector (per-pixel 2σ threshold, 3× median) found nothing all night; the whole-frame picture (3856 px into ~1000) made the spikes invisible; a tap on the shrunken frame lands tens of pixels off, hence the snap to the nearest bright star; the guider's own page said "guiding" during a five-minute calibration while the client said nothing.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1bahtinov-focus~1pick`; `Contracts/BahtinovFocusDtos.cs` (`BahtinovPickRequestDto`, `BahtinovFrameCropDto`), `Services/BahtinovFocusService.cs` (`Pick`, `ClearPick`, `CropAround`, `StretchSpikes`), `OpenAstroAra.Image/ImageAnalysis/BahtinovAnalyzer.cs` (`MinStrength`, `PickedSearchRadius`, `OtherStars`), `Endpoints/BahtinovFocusEndpoints.cs`; `Contracts/EquipmentDtos.cs` (`GuiderStateDto.Calibration*`), `Contracts/WsEvents/WsEventCatalog.cs`, `Equipment/MyGuider/PHD2/PhdEvents/PhdEventCalibrating.cs`; client `widgets/focus/bahtinov_focus_card.dart` (`tapToFramePixel`), `models/bahtinov_focus.dart` (`steady`), `models/guider_status.dart` (`calibrationText`).

**Related:** #1362, #1363, #1364; tests `BahtinovRealFrameTest` (fixture `fixtures/bahtinov-redcat51-2s-256.u16`), `BahtinovFocusServiceTest`, `PHD2GuiderMarkerEventTest`, `GuiderStepPayloadTest`, client `bahtinov_frame_pick_test`, `guider_status_test`.

---

### 2026-10-07 — #1298 manual filter wheel: hand-swap prompt, installed report, swap cancel

**Endpoint(s) or area:** `GET /api/v1/equipment/discover/filterwheel` (always lists the manual wheel); `GET /api/v1/equipment/filterwheel` (`manual`, `runtime.pending_slot`, `runtime.state = "awaiting_user"`); new `POST /api/v1/equipment/filterwheel/installed` and `POST /api/v1/equipment/filterwheel/swap/cancel`; new WS event `equipment.filter_wheel.manual_swap`.

**Decision:**
- Filter-wheel discovery always ends with the driverless manual wheel (`unique_id: "ara-manual-filter-wheel"`), whether or not any Alpaca wheel answers, so it can be chosen offline. Connecting it selects the manual wheel behind the same `/equipment/filterwheel` routes; its slots are the profile's filter labels.
- `FilterWheelDto` gains `manual` (default `false`). `FilterWheelStateDto` gains `pending_slot` (default null) and the state `awaiting_user`: a change was requested and the user has not yet reported that filter installed. A manual change waits with no time limit.
- `POST /filterwheel/installed` `{ "position": n }` records slot `n` as installed and resolves a pending change to it: `204`; out-of-range slot `400`; another wheel selected `409 not_manual_filter_wheel`; manual wheel not connected `409`. The slot is remembered across daemon restarts (`manual-filter-wheel.json` in the profile directory).
- `POST /filterwheel/swap/cancel` drops a standing prompt, failing a waiting change: `204` (also when nothing was pending); not the manual wheel `409 not_manual_filter_wheel`.
- `equipment.filter_wheel.manual_swap` payload: `{ device_type: "filterwheel", pending_slot, pending_name, current_slot, current_name }`. `pending_slot` set = a prompt was raised; null = resolved or cancelled. Clients show or drop the "install the X filter" prompt and re-read the wheel. The prompt also goes out as a §46 notification.

**Reasoning:** a filter drawer has no driver, so Ara has to ask for each swap and trust the user's answer. Putting the manual wheel in discovery and behind the existing routes keeps the chooser, the sequencer's `SwitchFilter` and the Imaging tab unchanged.

**Spec ref:** `Services/ManualFilterWheelService.cs`, `Services/FilterWheelRouter.cs`, `Services/ManualDeviceDiscoveryService.cs`, `Endpoints/EquipmentEndpoints.cs` (`ReportFilterInstalledAsync`, `CancelManualSwapAsync`), `Services/EquipmentEventPublisher.cs` (`ManualFilterSwap`), `Contracts/EquipmentDtos.cs`; tests in `ManualFilterWheelServiceTest`, `EquipmentEndpointsFilterWheelTest`.

**Related:** #1298, PR #1303, CHANGELOG [Unreleased]

### 2026-10-05 — #1230 pad bands are the primary∩secondary intersections; re-published once a late axis answers

**Endpoint(s) or area:** `GET /api/v1/equipment/telescope` (`capabilities.move_axis_rate_bands_deg_per_sec` and the derived `move_axis_rates_deg_per_sec`; no shape change).

**Decision:** each published band is a non-empty intersection of one primary-axis AxisRates band with one secondary-axis band, ascending by `min` (ties by `max`), deduped. A continuous primary `(0.002, 4.0)` over a discrete secondary `(0.002),(0.008),(0.033),(1–4)` publishes the four secondary steps, not `(0.002, 4.0)`. A thrown or honestly-empty secondary still applies no clip; when no intersection is non-empty (two discrete ladders with no step in common) the #1126 `[floor, ceiling]` clip is published instead (inside that window the secondary snaps but never refuses), and only when that is empty too are the primary bands published as-is (better a rate the secondary may snap than none). `move_axis_rates_deg_per_sec` is the endpoints of the same intersections. Separately, the capabilities are built on the first refresh pass whose reads answer; if the secondary's AxisRates threw on that pass, both rate lists are now re-published from the band cache on the pass where it answers (until the cache settles), so a client that connected early sees the real bands on its next status read rather than the unclipped primary list for the session.

**Reasoning:** #1126's `[floor, ceiling]` clip guaranteed no 409 on a diagonal press but not the same speed on both axes: inside the window the secondary still snapped a continuous-primary chip to its nearest step (0.2 °/s → 0.033, 6× slower N/S than E/W). Intersections publish exactly the rates both axes honour as given, so a chip is never snapped on either axis. The re-publish closes a pre-existing hole where the first-pass capabilities outlived the band cache they were derived from.

**Spec ref:** `Services/TelescopeService.cs` (`PadBandsFrom`, `RepublishPadBandsLocked`), `OpenAstroAra.TestHarness/Alpaca/ScriptedAlpacaDevice.cs` (`RespondWithQuery`); tests in `TelescopeMoveAxisClampTest` (`Pad_bands_are_the_intersections_with_the_secondary_bands_not_its_window`), `TelescopeAxisRatesRepublishTest`.

**Related:** #1230 (follow-ups of #1225), #1126, #1085, #1064, CHANGELOG [Unreleased]


---

### 2026-10-01 — §33 client-pushed update (#1122)

**Decision:** three routes under `/api/v1/server/update`. `POST ""` takes the `.deb` as a raw body (not multipart: one file, no fields, streamable from the client) with an optional `X-Update-Sha256`, returns 202 `ServerUpdateStagedDto { id, package, version, installed_version, size_bytes }`. `POST /{id}/apply` returns 202 `ServerUpdateStatusDto` with `status: "pending"`, emitting `server.restart_imminent { reason: "update", update_id, version, in_seconds }` only after systemd accepted the helper job; one apply at a time. `GET /{id}` returns the same DTO with `pending | applied | rolled_back | failed`, readable from the restarted daemon. Refusals are Problem responses whose `title` is a stable token: `not_packaged`/`helper_unavailable`/`update_in_progress` (409), `too_large` (413), `unknown_id` (404), `not_a_package`/`wrong_package`/`wrong_architecture`/`not_newer`/`checksum_mismatch`/`empty` (422).

The result lives on tmpfs, so after a **reboot** `GET /{id}` is 404: clients treat a 404 after an apply as "outcome unknown" and compare `/api/v1/server/info`'s version with the one they pushed.

**Why:** the restart happens between apply and the outcome, so the outcome has to outlive the process — it lives in the root helper's result file on tmpfs, keyed by the id the client already holds. A `.deb` rather than the §33.3 sketch's tarball keeps dpkg the owner of `/opt/openastroara`.

**Spec ref:** `Endpoints/ServerUpdateEndpoints.cs`, `Services/ServerUpdateService.cs`, `packaging/debian/opt/openastroara/scripts/{update-request,apply-update}.sh`, playbook §33.3.

---

### 2026-10-04 — Rotate camera by hand moves to the Plan screen: exposure, single shot or loop; no sequencer step

**Endpoint(s) or area:** `POST /api/v1/rotation-assist/start` (request gains `exposure_seconds`, `mode`, `binning`), new `POST …/confirm`, `GET …/state` (gains `mode`, `exposure_seconds`, `default_exposure_seconds`, `binning`, `auto_binning`, `max_binning`, `confirmation`; `state` gains `confirming | confirmed | not_confirmed`); the sequencer instruction `OpenAstroAra.Sequencer.SequenceItem.Rotator.RotateCameraByHand` is REMOVED (a saved sequence that still carries it deserialises as an unknown item and is skipped); the run builder no longer emits it.

**Decision:** the by-hand turn is a planning job, not a run step. The readout is driven from the Plan screen's docked **Rotate camera** panel against the framing dial's angle (the planetarium page posts `framing` events — on/off, centre, dialed angle, name — and a `rotateCamera` event from the Framing card's own button, over the loopback channel). `POST …/start {position_angle_deg, exposure_seconds?, mode?}`: `exposure_seconds` 0.01–60 (400 outside; default the profile's plate-solve exposure, reported as `default_exposure_seconds`), `mode` `loop` (default; solve after solve until `stop`) or `single` (one capture + solve, then `state: stopped` with the result kept; a failed solve ends in `error` at once). A start toward the SAME target angle keeps `latest`, `recent` and the frame (the client's keep-going / go-back advice needs the previous sample across single shots); a new target starts the history over.

**Binning and the Done check.** Readout frames run BINNED, Single and Loop alike: `binning` 1–4, never above the camera's maximum (`IAnalysisFrameSource.MaxBinning`, the smaller of Alpaca MaxBinX/MaxBinY; 400 outside), defaulting to that maximum capped at 4 (`auto_binning`; an unknown maximum allows up to 4). A protractor needs no resolution; binned frames download and solve several times faster, and the client suggests the loop exposure as the full plate-solve exposure ÷ bin² (floored at 0.2 s). `POST …/confirm` is the user's Done: the loop stops (its in-flight solve drains), ONE 1×1 frame is taken at the profile's full plate-solve exposure and solved; that solve becomes `latest` (so the picture and the scope box show the real pointing) and `confirmation`, and the readout ends `confirmed` when it sits within the rotation tolerance or `not_confirmed` when it does not — the approval always comes from the full-resolution solve, never the binned one. A solve failure ends in `error`; `start` and `confirm` are 409 while `confirming`, and `confirm` is 409 until a frame has solved toward the current target (none started, a new angle cleared the history, or every frame failed); `stop` cancels a confirmation (→ `stopped`). Everything else — the frame endpoint, the scope box on the sky — is unchanged.

**Reasoning:** parking a run inside a pause to turn the camera put the readout on the Run screen, with Resume as its only exit and no say over the exposure; in the field the turn happens while framing, before any run exists, and a single shot (turn, shoot, compare) is the natural rhythm when loosening a camera. The run keeps `Center and Rotate` for the centring.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1rotation-assist~1start` (…`~1confirm`); `Contracts/RotationAssistDtos.cs` (`RotationAssistModes`), `Services/RotationAssistService.cs` (`ResolveBinning`, `ConfirmAsync`), `Services/RotationFrameSolver.cs` (`IPositionAngleSolver.SolvePositionAngleAsync(exposureSeconds, binning, ct)`, `MaxBinning`), `Services/CameraService.Analysis.cs` (`IAnalysisFrameSource.MaxBinning`); client `widgets/sky_atlas/rotation_assist_panel.dart`, `state/sky_atlas/plan_framing_state.dart`, `assets/stellarium/index.html` (`postFraming`, `openRotateCamera`).

**Related:** the 2026-10-03 entry below (superseded for the sequencer step); tests `RotationAssistServiceTest`, client `rotation_assist_panel_test`, `plan_framing_state_test`.

---

### 2026-10-11 — Guide camera pinned by Alpaca UniqueID; the push re-resolves the device number (#1308)

**Problem:** the §63.17 guide-camera selection is the daemon choice string `Alpaca Camera [host:port/N]`, and AlpacaBridge numbers ZWO cameras in SDK enumeration order, which can change across a restart; after a flip the guider would open the imaging camera.

**Decision:** `Phd2SettingsDto` gains `guider_camera_alpaca_unique_id` ("" = unset / pre-#1308), filled by the client from `GET /equipment/guider/alpacadevicenames`, whose response gains `unique_ids` (`"type/N"` → UniqueID, additive; entries the server reports no id for are left out). Before a full `POST /guider/profile/push` the daemon reads that server's `configureddevices`: a pinned camera at another number or missing is refused (422, message names where it went); a guide camera that resolves to the daemon's main imaging camera, by UniqueID or by same server + number, is refused too. An unreachable management API skips the identity check (logged) and the number is pushed as stored. The §63.4 profile-switch follow is exempt like #1306. Tuning pushes are unaffected.

### 2026-10-11 — The daemon never reads a GPS receiver; the dongle lives on the client (#1305)

**Problem:** the USB-GPS self-sync worker opened every `/dev/ttyUSB*`/`ttyACM*` at 9600-8N1 every 2 min (unsynced) / 50 min (synced), including the port AlpacaBridge held for a serial mount: the mount link fault latched and the tty was left at the wrong speed. The SBC's USB ports are also the scarce resource on a rig.

**Decision:** the worker, the `gps-internal` source and the `internal_gps_available` field of `GET /server/time-sync` are removed. A USB GPS dongle goes on the computer running the client, which reads NMEA itself and relays the fix as `gps-client` (→ state `gps-external`, trust `high`); phones push `gps-mobile`. The client no longer skips a relay because the rig "already has its own dongle". A user who wants the Pi's clock disciplined with no client attached runs gpsd/chrony (§31.4.1); Ara stays out of serial ports it does not own.

### 2026-10-03 — Rotate camera by hand: the rotation readout, the run's target name and frame counter

**Endpoint(s) or area:** `POST /api/v1/rotation-assist/start`, `POST …/stop`, `GET …/state`, `GET …/frame`; the sequencer instruction `OpenAstroAra.Sequencer.SequenceItem.Rotator.RotateCameraByHand` (`PositionAngle`); `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` gain `frames_captured`, and `current_target_name` is now populated; frames' `target_name` for client-built runs.

**Superseded** by the 2026-10-04 entry above: the run builder no longer emits `Rotate camera by hand`, a framing angle now gives a lone `Center and Rotate`, and since #1446 the plain slew centres when "Center target after slew" is on. Kept as the record of the 2026-10-03 decision.

**Decision:** a rig without a rotator cannot act on a framing angle, so the run builder emits `Rotate camera by hand` (after the blind slew, before `Center and Rotate`) when a position angle is set and the rig has no rotator. The instruction starts the daemon's rotation READOUT and parks the run in `paused_awaiting_user` INSIDE the step (it awaits the pause gate itself), so Resume — or an abort — stops the readout before the next instruction takes the camera; with a rotator connected it is a no-op, and on a rig that cannot solve (no solver binary, no optics in the profile) it logs a warning and skips rather than parking the run on a readout that can only fail. The readout: `POST /rotation-assist/start {position_angle_deg}` (202; 400 non-finite; 409 already running / rig cannot solve) loops capture-through-the-analysis-seam → plate solve with the main optics → `RotationAssistStatusDto { active, state: idle|running|stopped|error, target_position_angle_deg, tolerance_deg (the profile's rotation tolerance), seq, started_utc, latest, recent[≤120], within_tolerance, error, consecutive_failures, has_frame, frame_seq }` where a sample is `{seq, solved_utc, solved_position_angle_deg, delta_deg, ra_deg, dec_deg, pixel_scale_arcsec, flipped, frame_width, frame_height}` — `delta_deg` is the signed shortest turn to the target folded into (−90°, +90°] because a frame rotated by 180° is the same framing. `GET …/frame` is the latest solved frame rendered (JPEG, auto-stretched with star rings, `X-Frame-Seq`; 204 before one exists). The loop ends in `error` after 5 consecutive failed solves; `POST …/stop` is 204 once the in-flight solve has drained. No WebSocket events: the client polls like the guide-focus loop. The client turns the delta history into advice relative to the user's last turn (the daemon cannot know which way "clockwise" turns the sky on a given optical train), draws north and the planned framing over the frame (`flipped` reverses the on-screen sense), and pushes each solve to the planetarium as a second "scope" box beside the planned framing.

The run state carries `frames_captured` (int, default 0): the count of frames filed under the run's §40 session, polled with the live status, so the band can show frames landing while a loop's single `TakeExposure` stays one instruction. `current_target_name` is derived from the live tree (nearest DSO target, else the client-built target block — the container carrying an altitude/horizon condition), and `TakeExposure` files its frames under that same name instead of the imaging loop's container name. `sessions.frame_count` increments per insert.

**Reasoning:** the by-hand turn needs a protractor, not a motor: the plate solver measures, the user turns, and the readout is a loop so the picture follows the hand. Sitting inside the pause is what guarantees the camera is free when `Center and Rotate` runs. Frames under the target and a live frame count are the two facts whose absence made a working run look dead on 2026-10-02.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1rotation-assist~1start` (…`~1stop`, `~1state`, `~1frame`); `Contracts/RotationAssistDtos.cs`, `Services/RotationAssistService.cs`, `Services/RotationFrameSolver.cs`, `OpenAstroAra.Sequencer/SequenceItem/Rotator/RotateCameraByHand.cs`, `OpenAstroAra.Sequencer/Utility/ItemUtility.ResolveTargetName`, `Services/SequencerService.LiveStatus.cs`; client `rotation_overlay.dart`, planetarium `scopeBox` command.

**Related:** §38 rotation fidelity, §45 capture-fetch, §59 analysis seam, §64 Live View; tests `RotateCameraByHandTest`, `RotationAssistServiceTest`, `SequencerLiveStatusTest`.

---

### 2026-10-12 — Safety policies: `meridian_refocus`, `meridian_guider_recal`, `unattended_window_*` (#1138)

**Endpoint(s) or area:** profile JSON `safety` (`SafetyPoliciesDto`): `meridian_refocus` ("always" | "if_temp_drifted" | "never", default if_temp_drifted), `meridian_refocus_temp_threshold_c` (default 2.0; the client clamps to 0.5–10), `meridian_guider_recal` ("auto_restore" | "full" | "skip", nullable: null = the pre-field behaviour — guiding restored when a guider is connected, the legacy `meridian_recal_guider` bool alone deciding whether the flight check requires one; the client leaves null in place until the dropdown is used), `unattended_window_start` / `unattended_window_end` (site-local "HH:mm", nullable, default null).

**Decision:** three §58.6 fields land as optional trailing constructor parameters, so an older `profile.json` keeps deserializing. `meridian_recal_guider` (bool) stays on the wire for older clients; the client writes it as `meridian_guider_recal != "skip"`. The executor decides from the store's policy, not the NINA profile's `AutoFocusAfterFlip` (which the store now mirrors for legacy readers). An unknown enum string reads as the default (same posture as `on_disk_space_critical`). The unattended window is two clock strings rather than minutes-from-midnight because that is what a user types and what the spec's example shows; both must parse and differ for the window to apply, otherwise the sun decides as before.

**Reasoning:** the refocus step already existed in `MeridianFlipExecutor` but was unreachable from the server profile; the spec's three-way policy (and its re-centre-before-re-focus order) replaces a flag nothing set. `if_temp_drifted` compares the focuser's temperature with the session's last completed autofocus (`IImageHistory.AutofocusPoints`); with no autofocus this session it re-focuses, with no temperature probe it keeps the focus. The §58.13 morning summary and the §58.12 per-action shutdown list are not in this change (the ladder stays fixed by design, see `UnattendedShutdownService`).

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/components/schemas/SafetyPoliciesDto`; `Services/MeridianFlipPolicy.cs`, `Services/MeridianFlipExecutor.cs`, `Services/UnattendedSeverity.cs`; client `safety_policies_state.dart`, `safety_policies_panel.dart`.

**Related:** #1138, §58.4 steps 3–4, §58.6, §58.10; tests `MeridianFlipExecutorTest` (policy cases), `UnattendedSeverityTest`.

---

### 2026-10-11 — `focus_calibration` carries the measured focuser backlash (#1139, §59.7)

**Endpoint(s) or area:** `GET /api/v1/autofocus/calibration` (`FocusCalibrationDto`), profile JSON `focus_calibration`; WS `autofocus.completed`.

**Decision:** two nullable fields, `backlash_up_steps` and `backlash_verified`, written by the §59.7 layer-1 probe the Classic sweep appends (three analysis frames at best focus: control below, straight return, compensated return). Only the UPWARD direction is measured and stored: the sweep and Smart Focus approach every position from above, so upward backlash is the only one that ever bites (the Smart bracket's + side). `autofocus.completed` carries the same pair when the probe measured; the probe's frames appear on the run record with phase `backlash`. A calibration written before the fields existed, or a probe that could not measure, reads null.

**Reasoning:** the playbook's layer 1 steps up through OUT corrections until HFR snaps back; inverting the sweep's own fitted V turns the one return shot into the distance directly, so the probe costs two or three frames instead of eight to ten. Backlash lives on `focus_calibration` rather than in the autofocus settings because it is derived data tied to the calibration's rig, invalidated by the same recalibrate. Layers 2 (passive refinement) and 3 (device-signature invalidation) are not in this change.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/components/schemas/FocusCalibrationDto`; `Services/AutofocusSweepService.Backlash.cs` (the probe that writes the pair).

**Related:** #1139, §59.7, §59.2; tests `AutofocusBacklashProbeTest`, `SmartFocusRunnerTest`.

**Addendum 2026-10-17 (#1385):** the fields are still measured and stored, but nothing compensates by them any more. The Smart bracket's + side, the one upward move, is now entered from above through an overshoot of one bracket (`AutofocusSweepService.Smart.cs`, `BracketMoves`), so no move depends on a measured backlash. The probe measures at the V's flat bottom, where it cannot see a shortfall: on 2026-10-17 it verified 0 on a ZWO EAF whose upward move then landed ≈ 40 steps short and the bracket read "no clear rise". `backlash_up_steps` / `backlash_verified` are informational (run record, client readout) until a later layer uses them.

---

### 2026-10-13 — Imaging → Unguided profile section (`/api/v1/profile/unguided`) (#1264)

**Endpoint(s) or area:** `GET`/`PUT /api/v1/profile/unguided` (`UnguidedSettingsDto`), profile JSON `unguided`.

**Decision:** the knobs behind the Tracking Probe, the Unguided Exposure Guard and Center After Drift become a profile section, shaped like `autofocus` / `polar_align`: `{guard_enabled, eccentricity_tolerance, good_eccentricity, step_down_after, step_up_after, probe_rungs_seconds[], recentre_drift_arcmin, recentre_solve_every_n, prior_tolerance_basis}` with constructor defaults equal to the shipped behaviour (true, 0.5, 0.3, 2, 10, [10,30,60,120], 10, 1, `max`), so a pre-#1264 profile back-fills unchanged. The PUT validates at the write boundary (400 problem details): tolerance in (0, 1], the round bar strictly below the trailed bar, counts ≥ 1, the ladder 1–10 strictly ascending positive exposures, drift > 0, basis ∈ {`max`, `pixel`, `seeing`}. The client builds the sequence nodes from this section (the guard is omitted when `guard_enabled` is false; the probe and the re-centre stay); the daemon reads only `prior_tolerance_basis`, when it turns the mount's spec sheet into a prior (`MountSpecSheet.Tolerance`: `max` = one pixel or half the seeing disc, whichever is larger; `pixel`; `seeing`). The probe's FWHM rule (1.2× the first usable rung) and the 2 × prior cap stay fixed.

**Reasoning:** the run is cooked from the rig on the client, so the knobs that shape nodes live where the nodes are built and ride on the body like every other node field; the one knob the daemon itself consumes (the prior's tolerance basis) lives in the same section so the user has one panel. The section is on the profile, not a client preference, because the guard thresholds are a property of the rig and must follow it between clients.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1profile~1unguided`; `Contracts/ProfileDtos.cs` (`UnguidedSettingsDto`), `Endpoints/ProfileEndpoints.cs` (`PutUnguidedSettings`), `Services/MountSpecSheet.cs`; client `unguided_settings_state.dart`, `imaging_run_body.dart` (`UnguidedOptions`), `imaging_unguided_panel.dart`.

**Related:** #1264 (issue's "Settings (registry + help entries per §61.4/§69.4)" box), PR #1318 (the nodes), PR #1334 (the spec-sheet prior); tests `UnguidedSettingsTest`, client `unguided_settings_state_test`, `imaging_run_body_test`.

---

### 2026-10-11 — Unguided exposure ceiling: `tracking_calibration`, `/api/v1/tracking/*`, `tracking_probe.*` (#1264)

**Endpoint(s) or area:** `GET /api/v1/tracking/calibration` (`TrackingCalibrationDto`, 404 = never measured), `POST /api/v1/tracking/recalibrate` (204, clears), profile JSON `tracking_calibration`; WS `tracking_probe.started` / `tracking_probe.rung_complete` / `tracking_probe.completed`; sequence `$type`s `OpenAstroAra.Sequencer.SequenceItem.Telescope.TrackingProbe` and `OpenAstroAra.Sequencer.Trigger.Telescope.UnguidedExposureGuard`.

**Decision:** the unguided tracking ceiling is daemon-owned derived data on the profile, shaped like `focus_calibration`: `{ceiling_seconds, measured_utc, polar_align_error_arcmin?, pixel_scale_arcsec?, seeing_fwhm_arcsec?, prior_seconds?, source: "measured"|"prior", rungs: [{exposure_seconds, eccentricity?, fwhm_px?, stars, usable, passed}]}`. The sequencer's `TrackingProbe` writes it; the client only reads it (readiness fact, advisor line) and may clear it. The probe's live stream mirrors the autofocus lifecycle: `started {target, rungs, prior_seconds?}`, `rung_complete {rung_index, exposure_seconds, eccentricity?, fwhm_px?, stars, usable, passed}`, `completed {ceiling_seconds?, prior_seconds?, source, usable_rungs, summary}`. The instruction carries `Rungs`, `MaxExposure`, `PriorSeconds` and, after it ran, `CeilingSeconds`; the trigger carries `Rungs`, `MaxExposure`, `EccentricityTolerance`, `GoodEccentricity`, `StepDownAfter`, `StepUpAfter`. Light frames' `eccentricity` column is now written by the §59.5 post-capture analysis (median star eccentricity, e = √(1 − roundness²)).

**Reasoning:** the run is cooked from the rig on the client, so the probe and guard are ordinary sequence nodes the client emits when `RigCapabilities.guider == false` rather than server-side injection — a saved or imported body stays self-describing, and a guided rig's body is byte-for-byte unchanged. Storing the measurement on the profile (not in the run) makes the next night's prior the measured one. The ceiling is applied by rewriting the imaging loops' `TakeExposure.ExposureTime` and `LoopCondition.Iterations` in the live tree, which the #1068 ETA estimator and the live status already read, so no new progress wire shape was needed. `usable`/`passed` ride on every rung so the client can draw the ladder like the autofocus probes. A separate `tracking` route group (not `autofocus`) keeps the two calibrations independently clearable.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1tracking~1calibration` (…`~1recalibrate`); `Contracts/TrackingCalibrationDtos.cs`, `Services/TrackingProbeService.cs`, `Services/FrameQualityFeed.cs`, `OpenAstroAra.Sequencer/SequenceItem/Telescope/TrackingProbe.cs`, `OpenAstroAra.Sequencer/Trigger/Telescope/UnguidedExposureGuard.cs`, `OpenAstroAra.Sequencer/Utility/UnguidedExposureWriter.cs`; client `imaging_run_body.dart`.

**Related:** #1264 (prior from the bridge's mount spec sheet is AlpacaBridge #815), §59.5 frame analysis, #1068 ETA, §45.13 polar-alignment log (the record's `polar_align_error_arcmin`); tests `UnguidedExposureCeilingTest`, `TrackingProbeServiceTest`.

**Client consumption (2026-10-12, #1264 client half):** `TrackingApi` reads `GET /tracking/calibration` (404 → none) and posts `/recalibrate`; the readiness card shows the ceiling as an informational mount fact, the sub advisor prints "Unguided: about N s" beside the sky-limited figure on a rig with no guider, the Sequence tab folds `tracking_probe.*` into a live ladder readout and the ticker, and Settings → Imaging → Unguided shows the record with Recalibrate. The client does NOT send the stored ceiling as the probe's `PriorSeconds` — the daemon starts from `tracking_calibration` on its own, and `PriorSeconds` is reserved for a real spec-sheet value so a Recalibrate is never overridden by a frozen body figure. No wire change.

**Stage 1 — spec-sheet prior (2026-10-13, #1264):** `TelescopeCapabilitiesDto.mount_specs` (`MountSpecSheetDto`: `driver_id`, `model`, `source` = manufacturer|measured|community, `worm_period_s?`, `periodic_error_pp_arcsec?`, `has_encoders`, `has_pec`, `mount_type?`, `max_payload_kg?`, `notes?`) carries the mount's spec sheet read once per connect from the bridge's custom Alpaca action `openastro:specs` (AlpacaBridge #815 shape, schema `openastro.mount-specs/1`); null when the driver lists no such action or the JSON does not parse. The tracking probe's prior resolution is now body → stored `tracking_calibration` → spec sheet turned into seconds for THIS rig (`MountSpecSheet.UnguidedPriorSeconds`: tolerance = max(1 px × pixel scale, 0.5 × site typical seeing), pe_rate = 2π·(pp/2)/worm (0 with encoders), pa_rate = 15.04·sin(latest polar-align error), t = tolerance/rate floored at 5 s), each with a named source for the divergence notification (`ITrackingProbeExecutor.ResolvePriorWithSourceAsync`). `GET /api/v1/tracking/prior` → `TrackingPriorDto {seconds?, source?}` serves that resolution (with no body value) so the client never recomputes it: the sub advisor prints "Unguided: about N s (from the mount's spec sheet (manufacturer); the first run measures it)" from this route, and shows the sheet itself as a mount readiness fact and on Imaging → Unguided. `source` on the sheet is null when the bridge did not say. The bridge-side afternoon lookup (`GET /openastro/v1/specs/telescope/{driverId}`) is not consumed yet. Wire: the new nullable capabilities field and the new `GET /api/v1/tracking/prior` route (openapi.yaml regenerated).

---

### 2026-10-12 — Center After Drift trigger re-ported; `InputCoordinates` declination sign (#1195)

**Endpoint(s) or area:** sequence `$type` `OpenAstroAra.Sequencer.Trigger.Platesolving.CenterAfterDriftTrigger` (NINA's `NINA.Sequencer.Trigger.Platesolving.CenterAfterDriftTrigger` maps to it on import); the `InputCoordinates` JSON shape every sequence body carries (`SlewScopeToRaDec`, `CenterAndRotate`, altitude conditions, this trigger).

**Decision:** the trigger carries NINA's fields unchanged — `Coordinates` (`InputCoordinates`), `DistanceArcMinutes` (default 10), `AfterExposures` (default 1) — and no runner: it re-centres through the daemon's §28 centering seam. Drift is measured from the saved lights themselves (the daemon plate-solves every Nth catalogued LIGHT from disk, seeded with the mount position), never from an extra capture; a frame that does not solve leaves the trigger quiet. The client's run builder attaches it to each imaging loop on a rig without a guider, aimed at the target. `InputCoordinates` follows NINA's shape for a negative declination: `DecDegrees` is SIGNED (`-5` for −5°24′) and `NegativeDec: true` marks the sign, which alone carries the −0°xx′ case. The client wrote an unsigned `DecDegrees` with the flag before this change; the daemon decoded that as `DecDegrees − M/60 − S/3600` (−5°24′ → −4.6°). The client now writes signed degrees and reads both shapes.

**Reasoning:** re-centre decisions belong in the sequence (NINA plans carry the trigger, and it is the unguided rig's only pull back onto the framing), while the solve belongs to the daemon, which owns the files and the solver. Reusing the light avoids a capture per cadence on a Pi that is already analysing every frame. The declination fix changes no daemon code: the daemon was NINA-faithful, the client was not.

**Spec ref:** `OpenAstroAra.Sequencer/Trigger/Platesolving/CenterAfterDriftTrigger.cs`, `OpenAstroAra.Sequencer/Interfaces/IFrameSolveFeed.cs`, `Services/FrameSolveFeed.cs`; client `trigger_catalog.dart`, `imaging_run_body.dart`, `util/input_coordinates.dart`, `sequence_field_editor.dart`.

**Related:** #1195, #1264 (periodic re-centre leftover), #1272 (custom coordinates — southern targets were affected); tests `CenterAfterDriftTriggerTest`, `input_coordinates_test.dart`.

---

### 2026-10-12 — API hardening: stale-session 409, WebSocket connection cap, frame files confined to the capture roots (#1194)

**Endpoint(s) or area:** every mutating `/api/v1/*` route (`X-Ara-Session` header), `GET /api/v1/ws` (503), `GET /frames/{id}/download|preview|thumbnail|histogram`, `POST /frames/bulk-delete|bulk-export`, the plate-solve frame loader.

**Decision:** (1) The §27 single-client policy is **advisory on REST for a request without `X-Ara-Session`**: a pre-§27 client, a script or a WILMA whose connect was refused keeps full REST access, as `ClientSessionService` has always documented. What is enforced is the stale holder: a non-GET/HEAD/OPTIONS request under `/api/v1` that presents a session id other than the current holder's is refused with **409 `https://openastro.net/errors/stale-session`**; `POST /server/connect` and `/server/disconnect` are exempt. With no holder at all nothing is stale. The client sends the header on its WebSocket upgrade today and not yet on REST; when it does, a taken-over client's commands are refused instead of racing the new holder. (2) The WebSocket endpoint admits at most **16** concurrent sockets (`WsConnectionGate`); the 17th gets **503 `ws-too-many-connections`** with `Retry-After: 30` before the upgrade, and Kestrel's `MaxConcurrentUpgradedConnections` (32) is the hard belt. (3) On the frame endpoints (download, preview, thumbnail, histogram, bulk export, bulk delete, the plate-solve loader) a catalogued `file_path` is served, exported or deleted only when it sits under a **capture root**: the profile's save directory (read live), the §29 store mount `/media/openastroara`, or the profile-dir `frames/` fallback the camera writes to when the save directory is unavailable. Paths are normalised first, and a file that is itself a symlink resolving outside the roots is refused (a linked night *directory* inside a root is followed: users legitimately link night folders to other disks). `POST /server/emergency-stop` is exempt from the stale-session check like connect/disconnect. A refused row reads exactly like a missing file (placeholder preview, `null` download, skipped in an export, deleted from the catalog but left on disk by a bulk delete) and logs one warning naming the path; a bulk delete of a FITS the user already removed by hand still sweeps its sidecars when the path is under a root. The daemon's own internal reads of frames it just wrote (the Center After Drift solve) are not routed through the check.

**Reasoning:** enforcing the session on header-less requests would break every pre-§27 client and the daemon's own scripts for no gain against the realistic failure, which is a client that WAS the holder and keeps driving after a takeover. The cap exists because each socket costs a per-subscriber channel and a send loop on a Pi and nothing bounded a reconnect storm. Frame paths come from the daemon's own writes, so confining reads to where the daemon writes costs nothing in the normal case and stops a corrupt or edited row from turning `/download` into a file server. The roots are read live so a changed save directory is honoured; frames captured under an earlier, hand-set save directory outside the store are the one case that stops being servable until that directory is configured again.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1ws` (503); `Endpoints/SessionGuard.cs`, `Endpoints/WebSocketEndpoints.Gate.cs`, `Services/ClientSessionService.cs` (`IsStaleSession`), `Services/SqliteFrameRepository.cs` (`ConfinedExistingPath`).

**Related:** #1194, §27, §28, §29, §60.9; tests `ApiHardeningTest`, `FrameFileConfinementTest`.

---

### 2026-10-12 — Store detection: one probe for `is_ara_store`, the storage numbers and the capture gate (#1231)

**Endpoint(s) or area:** `GET /api/v1/storage/devices` (`StorageDeviceDto.is_ara_store`), `GET /api/v1/storage/space` free/total bytes and the §29 disk-space monitor, the per-frame pre-capture store gate, the `storage.devices_changed` watcher.

**Decision:** `is_ara_store` is true only when lsblk shows the device at `/media/openastroara` AND the daemon's own mount probe says a filesystem of its own is mounted there; after a restart with no disk the root partition (systemd's self-bind of the empty directory) is no longer offered as the store. The storage free/total numbers and the disk-space monitor return "not resolvable" (null, no false alarm) for a save directory under an ejected store instead of the root disk's figures. The probe itself now reads `/proc/self/mountinfo` first: the store entry's `major:minor` must differ from the nearest ancestor mount's, or, when it is the same device, its `root` must not be the store path inside that ancestor (the systemd self-bind) — so an intentional `mount --bind /srv/frames /media/openastroara` and a btrfs subvolume are stores. A device number with no `/sys/dev/block/<maj:min>` entry (disk unplugged under a namespace-private bind) reads as ejected. `/proc/self/mounts` (the #1207 source-name rule) is the fallback when mountinfo is unreadable; neither readable is "unknown", which every consumer treats as "do not refuse" (capture proceeds, watcher skips the tick, lsblk's column stands).

**Reasoning:** three consumers had three different answers to one question; the probe was already the capture gate's and is the only one that reads the daemon's own namespace. mountinfo carries the two fields that make the decision exact (device number, bind root) where the mounts table only has a device name, which cannot separate a self-bind from a deliberate bind. The detached-disk case was the untested mid-session eject from the #1226 review; the sysfs check answers it without a VM reproduction (not run), by asking the kernel whether the block device still exists.

**Spec ref:** `Services/StoreMountProbe.cs` (`IsStoreMountedFromMountInfo`, `DeviceExists`), `Services/StorageDeviceService.cs` (`ParseDevices`), `Services/DiskSpaceMonitor.cs` (`StoreEjectedFor`), `Services/CameraService.cs` (`StoreEjected`), `Services/StorageDeviceWatcher.cs` (`BuildSnapshot`).

**Related:** #1231, #1226, #1207, §29; tests `StoreDetectionFollowUpsTest`, `StorageStoreMountProbeTest`.

---

### 2026-10-12 — Mosaic projects persist; panel progress is derived from the frame catalog (#1146, §47)

**Endpoint(s) or area:** `/api/v1/mosaics` (list/get/create/delete, `/{id}/panels`, `/{id}/progress`); `MosaicCreateRequestDto` gains `panels` (`[{ra_degrees, dec_degrees}]`, row-major, `panel_count_x × panel_count_y` of them) and `frames_per_panel`; `MosaicPanelDto` gains `frames_captured`; the placeholder's seeded "Veil nebula 3×2" fixture is gone.

**Decision:** `SqliteMosaicService` replaces `PlaceholderMosaicService`. Tables `mosaics` and `mosaic_panels` hold the plan and the per-panel J2000 centres; **completion is never stored**. A panel's light frames are the catalog rows whose `target_name` is the panel's target block, `"<name> i/N"` (1-based, row-major) — the name the client's run builder already gives each panel block — so `status` (`pending` / `in_progress` / `done` at ≥ `frames_per_panel` lights), `frames_captured`, `target_frame_id` (newest light) and the progress record (`completed_panels`, `current_panel_index` = lowest started-not-done panel, `frames_captured_total`) are computed at read time from `frames`. Centres come from the client when it sends the right number (the planetarium's drawn grid, now carried on the add-to-sequence event with `cols`/`rows`/`overlapPercent`); otherwise the daemon spreads a grid from the profile optics (FOV × (1 − overlap), rotated by the PA, tangent-plane deprojected), or puts every panel at the centre when no optics are configured. `crosses_ra_wrap` (§47.3) is set from the field width and declination; unknown width → false. The client registers a project best-effort after a connected mosaic run is created or appended (`frames_per_panel` = what one panel's block will capture: the run's frame count, or the sum of a filter plan's step counts; `center_ra_degrees` = the circular mean of the panel centres); a failed registration never fails the run. `panel_count_x`/`panel_count_y` are clamped to 32. The Library shows a Mosaics section (name, grid, panels done, lights, a dot per panel).

**Reasoning:** the run already encodes the mosaic as N named target blocks, and the catalog already files every light under its block's name; deriving progress from that keeps one source of truth and makes restarts, repeat nights and Library deletes correct for free. Storing completion would have meant a second bookkeeping path the sequencer has to update.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1mosaics`; `Services/SqliteMosaicService.cs` (`PanelTargetName`, `TryPanelIndex`, `GridCentres`, `CrossesRaWrap`), `Services/SqliteAraDatabase.cs` (`mosaics`, `mosaic_panels`); client `models/library/mosaic.dart`, `services/mosaic_api.dart`, `state/library/mosaics_state.dart`, `widgets/library/mosaics_section.dart`, `state/sequencer/create_imaging_run.dart` (`_registerMosaicProject`), `assets/stellarium/index.html` (`cols`/`rows`/`overlapPercent` on `addToSequence`).

**Related:** #1146, §47, §47.3, §38 (panel target blocks); tests `SqliteMosaicServiceTest`, `mosaic_test.dart`, `mosaics_section_test.dart`, `create_imaging_run_mosaic_project_test.dart`.

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

### 2026-10-18 — A manual slew during a sequence run is refused until the caller says so

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/slew` (`SlewRequestDto` gains `force?: bool`, default false; new 409 refusal); `ISequencerService.ActiveRunIdsAsync`.

**Decision:** while a sequence run is in progress (starting, running, paused, or parked awaiting the user; not terminal, not already aborting) a slew that is neither a sync nor forced is refused with 409, problem title `run_in_progress`, and a detail sentence naming the consequence and the way out ("… slewing now stops guiding and pauses the run awaiting you. Repeat with force to slew anyway."). A repeat with `force: true` is accepted as before (202). A sync never moves the mount and is never refused. Clients ask the user on that title and repeat forced on yes (the mount panel's dialog; the Planning page asks before it unparks, since its unpark is not gated). The refused request never reaches the telescope service.

**Reasoning:** rig night 2026-10-18: a Planning GoTo was accepted mid-dither, the guider stopped (stop-guiding-when-slewing) and the run parked awaiting the user, with nothing to say a run was active. Pausing is the safe outcome; the user should be told first.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1telescope~1slew`; `Endpoints/EquipmentEndpoints.cs` (`SlewAsync`, `SlewDuringRunRefusal`), `Services/SequencerService.cs` / `PlaceholderSequenceServices.cs` (`ActiveRunIdsAsync`); tests `EquipmentEndpointsSlewTest`, `SequencerServiceTest`, client `equipment_mount_panel_test`.

**Related:** CHANGELOG [Unreleased]; #1364 rig checklist.

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
      failed for the same frame_id. A frame the §29 pre-capture check refuses (no
      storage disk, disk full under abort/pause) gets a failed with NO started
      before it, the reason in the user's words (#1450); a client timer should still age out on
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
  guider.operation_complete { operation_id, operation, ok, error? }
      #1156 — the terminal status behind every guider 202 (operation =
      guider.start | guider.stop | guider.dither), bound to the 202's
      operation_id. ok:false is the guider saying no or a fault; error is
      "refused" (not guiding, already stopped), a named refusal — "the
      guider is calibrating", "the guider is paused" (2026-10-18: the
      sequencer parks on these instead of restarting guiding) — or the
      guider's own message when the dither RPC errored; anything else is
      the fault's text.
  guider.dither_complete { ok, operation_id?, error? }
      #1156 — one per dither, REST (operation_id set) or sequencer-driven
      (no id), published after SettleDone or the settle failsafe: ok is the
      SETTLE verdict, error the daemon's text or the timeout sentence, or
      the refusal text above when no dither was sent. A dither met by a
      lost star waits up to 20 s for it before it is sent or refused, so
      the terminal event can follow the 202 by that long.
  guider.event { kind, ... }   one per PHD2 session event other than a step —
      the things PHD2's own graph annotates. kind and its detail fields
      (omitted when PHD2 sent none):
        dithered            { dx_px, dy_px }
        settling            { distance_px, time_sec, settle_time_sec }
        settle_done         { status (0 = ok), error? }
        star_lost           { frame?, star_mass?, snr?, distance_px?, status?, error? }
        calibration_started | calibration_complete | calibration_failed { error? }
        calibrating         { direction, frame (= step), distance_px, dx_px, dy_px }  one per step
        guiding_started | guiding_stopped | paused | resumed | lock_position_lost
        guiding_assistant_progress { state }                 one per Guiding Assistant sample / backlash step
        guiding_assistant_done     { state, error? }         state = done | failed; figures via GET …/live/assistant
      A dither is drawn as dithered → (settling …) → settle_done; the client
      shades the settle window and marks the dither, exactly as PHD2 does.
  guider.pa_residual { id, status, started_utc, completed_utc?, sample_seconds,
                       target_seconds, frames, drift_arcsec_per_min?,
                       pa_error_min_arcmin?, uncertainty_arcmin?, reliable?,
                       hour_angle_hours?, dec_deg?, align_error_arcmin?,
                       align_ended_utc?, session_id?, reason? }
      #1311 — the polar alignment residual from the first 300 s of clean
      guiding of each guided run: status measuring (at the start, then every
      30 s of sample) → done | unavailable (reason). pa_error_min_arcmin is a
      lower bound. A dropped measurement re-publishes the previous result, or
      { status: "idle" }. The same object is runtime.pa_residual on
      GET /equipment/guider.
```

## 2026-10-19 — polar-align `binning` 0 = auto (#1133)

`PolarAlignSettingsDto.binning` (`GET`/`PUT /profile/polar-align`) gains the
value **0 = auto**, now the default (§45.12 "Binning: auto"): the server reads
the guide camera's frame size from the guider daemon once per run and picks the
§45.4 tier (≤ 2.5 MP → 1, ≤ 8 MP → 2, ≤ 16 MP → 3, else 4). 1–4 remain the
user's fixed override; `PUT` refuses anything else with 400. A profile.json
written before this keeps its explicit `1`. Nothing else on the wire changes:
the chosen binning is logged, not reported, because a run's binning is fixed at
its start and the client has nothing to do with it.

## 2026-10-20 — §36.10 solver database recommendation per camera (#1136, PR #1423)

**Endpoint(s) or area:** `POST /api/v1/data-manager/solver-recommendation` (new); `GET /api/v1/data-manager/packages` (two new solver ids `astap-d05`, `astap-d50`; `astap-d80` no longer `recommended` by default)

**Decision:** the request carries whatever the setup draft knows (`focal_length_mm`, `reducer_factor`, `sensor_width_px`/`sensor_height_px`, `pixel_size_um`, `guide_focal_length_mm`, `guide_pixel_size_um`, `guide_sensor_width_px`/`guide_sensor_height_px`, `guide_setup_type` = `oag` | `guide_scope`); every missing field falls back to the active profile, and a missing guide sensor is read from the connected guider (null → main camera only). The answer is one `packages` row per camera whose field is known (`role` = `camera` | `guide camera`, `fov_deg`, `band`, `package_id`/`package_name`/`size_bytes`/`is_installed`, or `too_narrow` + `reason`), deduplicated by package, with the headline `band` for the narrowest field. The ladder: W08 > 20°, G05 6–20°, D05 1–6°, D50 0.5–1°, D80 < 0.5°, nothing under 0.15°.

**Reasoning:** the profile does not hold the guide sensor and a draft is not yet a profile, so the client sends what it has and the daemon fills the rest; one row per camera lets the wizard queue two downloads for a guide scope and one for an OAG without the client knowing the ladder. `recommended` on the package list stays the main camera's pick (one flag per package, no camera context).

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1data-manager~1solver-recommendation`

**Related:** §36.10 of PORT_PLAYBOOK.md, PR #1423

