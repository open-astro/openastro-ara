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

## 2026-11-16 — #1952 the instantiate 422 is problem details: `POST /sequences/templates/{name}/instantiate`

**Endpoint(s) or area:** `POST /api/v1/sequences/templates/{name}/instantiate`

**Decision:** supersedes the 422 shape in the 2026-11-15 #1859 entry. A body that fails the schema check answers 422 problem details (`application/problem+json`, title `Template body refused`, `detail` = the validator's reason). Only `TemplateBodyRefusedException` maps to 422; no other `ArgumentException` does. Nothing is created. An unknown name stays a 404.

**Reasoning:** the body now matches the route's `ProducesProblem(422)` and the committed `openapi.yaml`, which already described problem details. The create and PATCH routes still answer `{error}`; that is not changed here.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1sequences~1templates~1{name}~1instantiate`

**Related:** #1952, #1859

---

## 2026-11-15 — #1859 a template that fails the schema check answers 422: `POST /sequences/templates/{name}/instantiate`

**Endpoint(s) or area:** `POST /api/v1/sequences/templates/{name}/instantiate`

**Decision:** after the `{{token}}` substitution the body is checked with `SequenceSchemaValidator` (`requireCapturableInstruction: false`) before it is stored. A body that fails answers `422 {error}` with the validator's reason, and no sequence is created. An unknown template stays a 404.

**Reasoning:** the create route already refuses a body that fails the schema check with the same 422 (ADR 0018); instantiate stored whatever a disk template held, so a bad template became a stored sequence the engine could not load. The same status and `{error}` shape keeps one client error path. The capturable-instruction level stays off because a template may be a skeleton the user completes.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1sequences~1templates~1{name}~1instantiate`

**Related:** #1859

---

## 2026-11-14 — A version on every profile section: `ETag` and `If-Match` on the 24 section routes (#1807)

**Endpoint(s) or area:** the GET and PUT of the 24 client-writable profile sections under `/api/v1/profile/` (imaging-defaults, storage, notifications, site, custom-horizon, saved-targets, filenames, safety, autofocus, polar-align, unguided, guider-advanced, plate-solve, diagnostics-mode, phd2, optics, camera-electronics, filter-set, filter-wheel/labels, filter-wheel/policy, cooling-fan, dome, equipment-connection, stretch-defaults). Not the six sections only the daemon writes.

**Decision:** a 2xx GET or PUT answers a strong `ETag`, the SHA-256 of the stored section's JSON. A PUT may send `If-Match`: `*` or the tag it loaded passes; any other tag is a 412 problem, nothing is written and no `profile.updated` goes out. A PUT with no `If-Match` is still accepted until slice 4 makes it required. `PUT /profile/storage` moves onto the same check, so its tags change once (the hash now comes from the serializer context, not the reflection one) and its 400 rules are unchanged.

**Reasoning:** two clients or panels saving a whole section over each other silently drop the other's edit (ADR 0015). A tag of the stored content, not a write counter, gives equal content an equal tag. A write by the daemon itself moves the tag too; it publishes `profile.updated` like a route write (#1807 slice B, entry of 2026-11-20), so a client holding the section reloads it before its next save. A live-sync load (#1644) throws away open edits, so a 412 comes only when the event was missed.

**Spec ref:** `Endpoints/ProfileEndpoints.cs` (`WithSection`, `CheckSectionVersion`), `Services/ProfileSections.cs` (`ProfileSection.Read`), `Program.cs` (the `If-Match` parameter).

**Related:** #1807; ADR 0015; test `ProfileSectionVersionTest`.

---

## 2026-11-14 — #1875 the daemon serves the queued plan: `GET /sequences/{id}/plan`

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/plan` (`SequencePlanDto`, `SequencePlanTargetDto`, `SequencePlanRowDto`); `SequenceRunStateDto.plan_frames_done` and `.current_block_name`; the same two keys on the `sequence.progress` WebSocket payload.

**Decision:** the queued plan is read off the rig, so any client rebuilds Simple Mode's queue instead of relying on the client that queued it. The route returns `{sequence_id, run_id?, targets[]}`; each target is `{name, calibration, ra_deg?, dec_deg?, gain?, flats, dawn_provider?, rows[]}` and each row `{filter_name?, exposure_seconds?, frames_total, frames_done}`. Entries are the child containers of the body's top, in queue order; the root children named `Flats` and `Calibration` are entries with `calibration: true` and no position. A row is a loop container (its `LoopCondition.Iterations` is `frames_total`, its `CompletedIterations` clamped to that is `frames_done`; a negative count reads 0) or, in a calibration entry, a flat set (`FrameCount`, done once finished); its filter is the last `SwitchFilter` before it. `ra_deg`/`dec_deg` are J2000 degrees from the block's horizon condition (else a DSO container's target); an unset position is null. `gain` is the first light exposure's gain, null for the profile default (-1). `flats` is `none`, `after_target` (a flat set in the block) or `end_of_night`: the block holds an `Annotation` whose text is exactly `Flats: end of night`, the one place the rig records that choice. `dawn_provider` is the NINA `$type` of the block's `TimeCondition` provider. While a run holds a tree the route reads it (live edits show); an ended run still retained (completed, stopped or failed) serves the counts it left only while the stored body is unchanged since the run released its tree; else it reads the stored body (an edit after the run shows), so a queue that has not started is served too. An unknown sequence is a 404 problem. `plan_frames_done` is the sum of every served row's `frames_done`; `current_block_name` is the entry whose block holds the running leaf, null when none does (the run's own start and end steps), and unlike `current_target_name` it names a target-less block. A client re-reads the plan when either changes.

**Reasoning:** the session's frame count (`frames_captured`) says nothing about one loop's count (rig night 2026-11-13: a loop read 0/240 while the session had filed 16). The plan is a read-only view like `SequenceRunStateDto`, never a sequence format (ADR 0018): NINA's JSON stays the only one, and the marker is an ordinary NINA item. Two scalars on the existing progress frame need no new event.

**Spec ref:** `Services/SequencePlanBuilder.cs`, `Services/SequencerService.cs` (`GetPlanAsync`, `RunState.PlanSnapshot`), `Endpoints/SequenceEndpoints.cs`.

**Related:** #1875 slice 1; tests `SequencePlanTest`, `SequencerServiceTest.Progress_frames_carry_the_plans_own_count_and_the_block_it_runs`.

---

## 2026-11-12 — #1757 the dark and bias library belongs to one camera

**Endpoint(s) or area:** the `frames` catalog (`camera_name`, `camera_id`; `FrameDto.camera_name`, `FrameDto.camera_id`); `GET /api/v1/calibration/dark-library/status?camera=` (`DarkLibraryStateDto.camera`); `DarkLibraryEntryDto.camera`, `BiasLibraryEntryDto.camera` (also on `GET /calibration/dark-library/list`); `CalibrationSessionDto.camera`; the session coverage flags and the matching-flats generator.

**Decision:** every frame Ara captures records the main camera that took it: `camera_name`, the selected device's name (the value the FITS `INSTRUME` header is written from), and `camera_id`, the driver's unique id. Rows from before the columns, and foreign frames the drive scan catalogues, stay NULL. Dark and bias library groups add the camera to their key and carry it as `camera`; a group's `id` hashes the camera too, except for a NULL-camera group, which keeps its old id. `dark-library/status` lists one camera's library: the `camera` query, else the connected main camera; `camera` on the response says which. With no camera asked and none connected it lists every group (`camera: null`). A build's progress counts the same frames. A session's darks, bias and flats coverage, and what the generator shoots, count only calibration frames whose camera matches the lights'. `CalibrationSessionDto.camera` is the camera most of the session's lights were taken with, null when none recorded one. Matching uses the name.

**Legacy rule:** a NULL camera on either side matches any camera. Library groups with `camera: null` appear in every camera's status, as the flag that they predate the camera record, and they keep covering lights, so an existing library is not lost. NULL-camera lights are covered by any camera's frames.

**Reasoning:** darks and bias are a property of one sensor; a second camera's library silently "covering" lights would skip frames the user needs. Treating legacy frames as foreign would throw away every library built before this change, which costs a cloudy night to rebuild; the null `camera` lets the client label them. The name is the match key because it is what the user sees and what the FITS header carries; `camera_id` is stored for telling apart two cameras of the same model later.

**Spec ref:** `Services/SqliteAraDatabase.cs` (camera columns), `Services/CameraService.cs` (`RegisterFrameAsync`), `Services/SqliteDarkLibraryService.cs` (`GetStatusAsync`), `Services/SqliteCalibrationService.cs` (`SameCamera`, `ModalCamerasAsync`, flat coverage).

**Related:** #1757; tests `FramesSchemaWideningTest`, `AlbumFrameFieldsTest`, `SqliteDarkLibraryServiceTest`, `SqliteCalibrationServiceTest`.

---

## 2026-11-11 — #1786 Verify Position's 409 names its cause

**Endpoint(s) or area:** `POST /api/v1/platesolve/verify-position` (409).

**Decision:** the 409 problem details carry a stable `reason`: `no-camera` (nothing connected to look with, or the device was lost mid-exposure), `camera-busy` (another exposure holds the camera; the sentence says to try again once it has read out) or `gate-stuck` (the capture gate's wait expired; only a daemon restart clears it). `detail` is unchanged. The 422 causes carry no reason.

**Reasoning:** the three causes need three different actions (open Camera settings, wait and Check again, restart the daemon), and a client cannot tell them apart reliably from the sentence. A client that sees a 409 without a `reason` (an older daemon) treats it as `no-camera`.

**Spec ref:** `Endpoints/PlateSolveEndpoints.cs` (`VerifyPositionAsync`); `openapi.yaml` (`VerifyMountPosition`). Tests `VerifyPositionServiceTest`.

---

## 2026-11-11 — #1649/#1650 a leased MoveAxis, the mount's tracking rate, and the solve's angle in Verify Position

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/moveaxis` (optional `lease_ms`); new `POST /api/v1/equipment/telescope/trackingrate`; `GET /api/v1/equipment/telescope` (`runtime.tracking_rate`); `POST /api/v1/platesolve/verify-position` (`position_angle_degrees`, `rotator_synced_degrees`, and a rotator sync).

**Decision:**
- `moveaxis` takes an optional `lease_ms`, clamped to 250–5000. A non-zero rate with a lease starts or renews a deadline for that axis. When the deadline passes with no renewal, the daemon sends MoveAxis with rate 0 for that axis and logs it. A rate-0 request, a request without `lease_ms`, AbortSlew, a park and a disconnect cancel the deadline. A request without `lease_ms` behaves as before. Validation and the 400/409 refusals are unchanged.
- `trackingrate` takes `{ "rate": "Sidereal" | "Lunar" | "Solar" | "King" }` in any case. Any other value is a 400 before the driver. Not connected, or a rate missing from the capability's `supported_sidereal_rates` (when that list is not empty), is a 409 with the daemon's sentence. It sets ASCOM `TrackingRate` only: tracking on/off is unchanged. `runtime.tracking_rate` reports the current rate by the same name, null when the mount can't say.
- `verify-position` returns the solve's `position_angle_degrees` (null when the solve failed). On a good solve it syncs a connected, still rotator to that angle, as Center and Rotate does, and reports the angle in `rotator_synced_degrees` (null when no rotator was synced). The mount is still never synced or moved.

**Reasoning:** Simple Mode's direction pad must stop the mount when the link drops mid-press (a sleeping iPad, lost Wi-Fi): only the rig can do that. The tracking rate had no REST surface; only a plan's Set Tracking step reached it. Syncing the rotator moves nothing and keeps its sky angle honest, whereas a mount sync stays a deliberate user action (Sync after Check).

**Spec ref:** `Services/MoveAxisLease.cs`; `Services/TelescopeService.cs` (`MoveAxisAsync`, `CheckLeases`, `SetTrackingRateAsync`, `ReadRuntime`); `Endpoints/EquipmentEndpoints.cs` (`MoveAxisAsync`, `SetTrackingRateAsync`); `Services/VerifyPositionService.cs` (`SyncRotator`, `Describe`). Tests `TelescopeMoveAxisLeaseTest`, `TelescopeTrackingRateTest`, `VerifyPositionServiceTest`.

---

## 2026-11-11 — #1765 a copy marker names its drive, and an erase through ARA voids it

**Endpoint(s) or area:** `GET /api/v1/storage/contents` (`frames_not_copied`); `POST /api/v1/storage/configure` with `format: true`; the `.ara-last-copy` marker the disk-to-disk copy leaves on the store.

**Decision:** the marker's content is the filesystem UUID of the drive that received the copy; its modification time still dates the copy. Before an erase starts, `POST /storage/configure` with `format: true` appends the drive's UUID and the time to `{profileDir}/erased-drives.log`; when that write fails, the erase is refused with 500 and the drive is not touched. `frames_not_copied` treats a marker as valid only when it names a drive and that drive has no erase logged at or after the marker's time. A missing, unreadable or empty marker (the format before this entry) counts every frame as not copied; so does a log that cannot be read. The response shape is unchanged.

**Reasoning:** the 2026-11-10 entry promised a count that may over-report and never under-reports, and a marker that outlived the drive it vouched for broke that promise. The log lives on the rig, not on a drive, so it also voids markers on stores that were unplugged during the erase. It is written before the wipe so a format that fails partway still counts. Not covered: a target drive lost, or erased or changed outside ARA; the marker cannot see that. Content tracking was decided against (#1764 item 6).

**Spec ref:** `Services/StorageEraseLog.cs`; `Services/StorageCopyService.cs` (`CopiedUntil`, `MarkCopied`); `Endpoints/SystemEndpoints.cs` (`GetStorageContents`, `ConfigureStorageDevice`).

---

## 2026-11-11 — Calibration sessions say which one holds Take One frames: `manual_capture`

**Endpoint(s) or area:** `GET /api/v1/calibration/sessions` and `GET /api/v1/calibration/sessions/{id}` (`CalibrationSessionDto.manual_capture`).

**Decision:** `manual_capture` is true when every light in the session has the target `Manual capture` (`CameraService.ManualCaptureTarget`), the shared session Preview's Take One files its frames into; false for a plan's session and for a session that mixes both. Nothing else changes: the session is still listed and calibrated like any other.

**Reasoning:** the bucket was indistinguishable from a plan session, so a client could not label or set it apart. The flag is derived from the lights, the same rows the session header already reads, so no schema change is needed.

**Spec ref:** `Services/SqliteCalibrationService.cs` (`ManualCaptureColumn`), `Services/CameraService.cs` (`ManualCaptureTarget`).

**Related:** tests `SqliteCalibrationServiceTest.A_session_is_marked_manual_capture_only_when_every_light_is_a_manual_capture`.

---

## 2026-11-11 — #1675 calibrate per-filter focus offsets: `/equipment/filterwheel/offsets/*`

**Endpoint(s) or area:** new `POST /api/v1/equipment/filterwheel/offsets/calibrate`, `GET …/offsets/calibration`, `POST …/offsets/calibration/cancel`, `…/save`, `…/discard`; `POST /api/v1/equipment/focuser/autofocus`, `POST /api/v1/bahtinov-focus/start` and `POST /api/v1/equipment/guider/focus/start` gain a 409.

**Decision:** `calibrate {positions?: int[]}` → 202 + `BatchJobDto` (job type `filter_offset_calibration`, one step per autofocus). Without `positions` the daemon picks every named slot whose name does not contain "dark". The reference is the slot named like the profile's `autofocus.af_filter`, else a slot named `L` or `Lum`, else the lowest pick; it is always measured. 400 for a slot the wheel does not have. 409 Problem `title: offset_calibration_refused` with the reason in `detail` when: another calibration, an autofocus run, the Bahtinov readout or guide-camera focus is running; anything on Rig Occupancy holds the main camera or the focuser (a sequence that is not terminal, polar alignment, centering; the detail names it, e.g. "a sequence is running"); the camera, focuser or filter wheel is not connected (named); the wheel is the manual one; the focuser is relative; no filter besides the reference is picked. The job autofocuses the reference, each other pick in slot order, then the reference again; each autofocus is a normal run record with `trigger: "offset calibration"` and its filter. `calibration` → `FilterOffsetCalibrationDto { state: running|complete|failed|cancelled, job_id, reference_position, reference_name, reference_first_position, reference_second_position, cfz_steps, drift_checked, drift_warning, current_position, run_index, run_count, reason, filters[] }`, a filter `{ position, name, status: pending|measured|failed|skipped, best_position, offset, r_squared, previous_offset, failure_reason }`; 404 when there is none. `offset` = best position − the mean of the reference positions that measured, rounded; the reference reads 0. When the reference fails both times the run ends `failed` with a reason and no offsets. `drift_warning` is true when the two reference positions differ by more than `cfz_steps`; `drift_checked` is false when the second reference failed or the CFZ is unknown. Once the reference has measured, every ending puts it back in the wheel and moves the focuser to its latest best position. `cancel` 204 while running; `save` writes each `measured` filter's offset into the profile's filter list and into the new profile section `filter_focus_offsets` (`{ offsets: [{ position, offset }] }`, in the profile snapshot, stripped from share files), and clears the results; `discard` clears them; each 409 Problem `title: no_offset_calibration` when there is nothing to act on. While a calibration runs, including between its autofocus runs, `POST /equipment/focuser/autofocus`, `POST /bahtinov-focus/start` and `POST /equipment/guider/focus/start` return 409 `offset_calibration_refused`.

**Reasoning:** the daemon owns the run so a sleeping laptop loses nothing; the results stay until Save or Discard so the user can decide later. `cfz_steps` is twice the half-CFZ step the Classic sweep seeds from (2.2 µm × N² over the focuser's µm per step), so it is null when the focuser does not report µm per step or the optics lack focal length or aperture; the autofocus result carries no CFZ of its own. The profile's filter list is the one a filter change applies offsets from (#1667), but it is held in memory and rebuilt from the wheel on connect, so the saved offsets also live in `filter_focus_offsets`: on connect a saved offset wins over the driver's for its slot (a slot without one keeps the driver's), and `GET /equipment/filterwheel` `slots[].focus_offset` reports that effective offset on every read, for a motorised wheel too. The manual wheel already reads its offsets from the profile's filter list.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1filterwheel~1offsets~1calibrate`; `Services/FilterOffsetCalibrationService.cs`; `Endpoints/EquipmentEndpoints.cs` (`MapOffsetCalibration`, `RunAutofocus`).

**Related:** #1675, #1667; tests `FilterOffsetCalibrationEndpointsTest`, `FilterFocusOffsetsStoreTest`, `CompositionRootSmokeTest.Offset_calibration_resolves_from_the_composition_root`.

---

## 2026-11-11 — A pause stops the plan's work in progress

**Endpoint(s) or area:** `POST /api/v1/sequences/{id}/pause`, and the daemon's own safety pause.

**Decision:** a pause now stops what the plan has in progress, not only what comes next: an instruction (a wait, a slew, centring, cooling, an autofocus instruction) or a trigger (autofocus, dither) in progress is cancelled, the run suspends, and on Resume that instruction runs again and the triggers are asked again. A frame in progress (an exposure item), a flat set (`FlatPanelFlats`, `SkyFlats`, which take their frames inside one instruction) and a meridian flip still finish first. An instruction's own wait for the user (a prompt) does not interrupt anything. The response and the `sequence.paused` event are unchanged; `Paused` is still reported only once the run has suspended.

**Reasoning:** with NINA's semantics (the current instruction always finishes) a paused plan kept focusing for minutes, which reads as a pause that did nothing (user, 2026-11-11). A frame keeps "Pause after this frame"; a flip is never cut short with the mount mid-move.

**Spec ref:** `OpenAstroAra.Sequencer/Utility/PauseGate.cs` (`InterruptToken`), `Container/ExecutionStrategy/SequentialStrategy.cs`, `Trigger/SequenceTrigger.cs` (`RunsThroughPause`), `Trigger/MeridianFlip/MeridianFlipTrigger.cs`.

**Related:** #1724, #1747; tests `PauseInterruptsPlanWorkTest`, `SequencerServiceTest.A_pause_stops_the_wait_in_progress_and_resume_runs_it_again_then_completes`.

---

## 2026-11-11 — #1747 the meridian wait's seconds left; a refused flip has its own reason code

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` gain `meridian_wait_seconds_left`; `sequence.progress` also gains `reason_code` and `reason_text` (the wait starts mid-run, and the client re-reads the state only on lifecycle events); `reason_code` gains `meridian_refused`.

**Decision:** `meridian_wait_seconds_left` is the whole seconds left while the run waits out the meridian (rounded up), set only with `reason_code` `meridian`, null in every other case, and cleared together with the reason. A flip the pre-flip flight check refused now reports `reason_code` `meridian_refused` (it was `meridian`, the same code as the wait). `reason_text` is unchanged for both. Clients treat a run as waiting only when the code is `meridian` and the seconds are present; the code without seconds is an older daemon, shown by its text. Run state stays `running`. No new routes (ADR 0009 rule 1).

**Reasoning:** with one code for both, a client branching on `meridian` showed a refused flip as a countdown. Breaking change for such a client: a refusal no longer reads `meridian`. The Full UI shows `reason_text` for both and is unaffected.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml` (`SequenceRunStateDto.meridian_wait_seconds_left`); `Services/MeridianFlipExecutor.cs` (`WaitStatus`, `RefusedSource`), `Services/SequencerService.cs` (`SetReason`, `EmitAsync`); test `SequencerServiceTest.Progress_frames_carry_the_meridian_wait_and_clear_it_at_the_flip`.

**Related:** #1596, #1597, #1724; tests `SequencerServiceTest` (`The_meridian_wait_reports_its_seconds_left_until_the_flip_starts_and_not_after_the_run`, `A_refused_flip_reports_meridian_refused_with_no_seconds_left`), `MeridianFlipExecutorTest` (`The_passing_meridian_countdown_carries_its_whole_seconds_left`).

---

## 2026-11-17 — #1769 the OS disk's other partitions: `system_disk_level` and `media_kind` on `GET /api/v1/storage/devices`

**Endpoint(s) or area:** `GET /api/v1/storage/devices` → `StorageDeviceDto` gains `system_disk_level` (`none` | `os_partition` | `os_disk_data`) and `media_kind` (`emmc` | `nvme` | `sd` | `unknown`); `POST /api/v1/storage/configure` with `format: true`.

**Decision:** `os_partition` is a device that carries `/`, `/boot` or `/boot/firmware`, or an ancestor of one (md, LVM, dm-crypt, the physical disk). `os_disk_data` is any other partition on that physical disk. `is_system_disk` stays true for both, so an older client hides exactly what it hides today. `media_kind` is read from sysfs `device/type` (`SD`, `MMC` = eMMC) and lsblk's NVMe transport; anything else, or a failed read, is `unknown`. Both fields are additive; a client that does not know them reads `none` and `unknown`. `POST /storage/configure` with `format: true` on a device of either level answers 422 Problem `title: refused`, `detail: system_disk`, before the erase is noted and before the helper runs. Mount, check and eject on an `os_disk_data` partition are not refused while it is unmounted or mounted at the store; mounted anywhere else (a separate `/var`, `/home` or `/opt`, or the copy path), each answers 422 `title: device_busy`, `detail: mounted_elsewhere`, the code the copy target already uses. The copy target keeps its own check, which accepts a partition that is unmounted or already at the copy path. The root helper refuses every mode on an `os_partition` and the erase of an `os_disk_data` partition.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml` (`StorageDeviceDto`); `Services/StorageDeviceService.cs` (`ParseDevices`, `ProbeMedia`, `RefuseEraseOnOsDiskAsync`), `Endpoints/SystemEndpoints.cs` (`/storage/configure`), `packaging/debian/opt/openastroara/scripts/configure-storage.sh` (`refuse_if_os_partition`, `refuse_if_os_disk`, `refuse_if_os_disk_mounted_elsewhere`).

**Related:** #1769, ADR 0017; tests `StorageOsDiskTest`, `StorageCopyEndpointTest`, `scripts/tests/test_configure_storage_switch.py`.

---

## 2026-11-10 — #1579 what erasing a drive would lose: `GET /api/v1/storage/contents`

**Endpoint(s) or area:** `GET /api/v1/storage/contents?uuid=` → `StorageContentsDto { readable, frames, newest_frame_utc, frames_not_copied }`; the disk-to-disk copy (`POST /api/v1/storage/copy`) now leaves a hidden `.ara-last-copy` marker on the store.

**Decision:** the route reads only a drive that is mounted (the store, or a drive mounted elsewhere); any other drive answers `readable: false`, never `frames: 0`, so the client can say it cannot see the drive instead of claiming it is empty. Frames are the files the capture scan counts as frames (FITS and XISF), walked as the copy walks (hidden entries and symlinks skipped). `frames_not_copied` counts frames modified after the marker; with no marker, every frame. A whole-store copy that finishes with no failed and no conflicting file stamps the marker with the time it listed its files, less 2 s for exFAT's mtime granularity, so a frame saved while the copy runs still counts as not copied. A copy of chosen folders never stamps it. The walk runs off the request thread and stops when the client goes away.

**Reasoning:** the frame database records no drive per frame, so only a walk of a mounted drive can answer; mounting a drive just to look would need a new root helper mode. Stamping at plan time, not at the end, keeps the count on the safe side: it can over-report frames copied another way (a laptop, a folder copy), and it never hides a frame that exists on no other drive. Limit (decided 2026-11-11, #1764 item 6): the count is dated by file time, not content. A frame placed on the store from elsewhere with its original modification time, older than the marker, counts as copied although no copy of it may exist. Frames ARA saves itself are counted correctly. Content tracking (a list of copied files, or hashes) was decided against.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1storage~1contents`; `Services/StorageCopyService.cs` (`ContentsOf`, `Summarize`, `MarkCopied`), `Endpoints/SystemEndpoints.cs` (`GetStorageContents`), `Contracts/StorageCopyDtos.cs` (`StorageContentsDto`).

**Related:** #1579, #1760; tests `StorageCopyServiceTest` (`Contents_count_the_frames_and_date_the_newest`, `A_clean_copy_of_the_whole_store_counts_its_frames_as_copied`, `A_frame_saved_while_the_copy_runs_still_counts_as_not_copied`, `A_copy_of_chosen_folders_leaves_the_store_not_copied`, `A_drive_that_is_not_mounted_cannot_be_read`).

---

## 2026-11-09 — #1597 the next meridian flip in the run status; #1723 clipped stars on the frame histogram

**Endpoint(s) or area:** `GET /api/v1/sequences/{id}/state` and WS `sequence.progress` gain `meridian_flip`; the frame histogram response gains `clipped_stars`.

**Decision:** `meridian_flip` is `{expected_utc, state, target}`, null when no run is active. `state` is `due_at`, `waiting_for_exposure` (due, held until the running item ends), `flipping`, `auto_flip_off` (this run has no flip trigger, or Auto flip is off in the profile) or `none_tonight` (the mount cannot be evaluated, this target already flipped, or the flip falls after the run's estimated end); `expected_utc` is null unless a time applies. It comes from `MeridianFlipTrigger.Forecast`, which shares the trigger's own window maths, including its early fire when the next item would overrun. `clipped_stars` is the count of candidates the detector rejected only for a saturated peak, and is not part of `stars`; a share is `clipped_stars / (stars + clipped_stars)`. No new routes (ADR 0009 rule 1).

**Reasoning:** the preflight forecast is an estimate from transit, not the trigger's time. A clipped-pixel fraction hides a few blown-out cores; a star count does not.

**Spec ref:** `MeridianFlipTrigger.Forecast`, `SequencerService.FlipStatus`, `StarDetector.Measure`, `SqliteFrameRepository.UpdateStarClippingAsync`.

**Related:** #1538, #1724.

---

## 2026-11-09 — #1557 stop after the current frame

**Endpoint(s) or area:** `POST /api/v1/sequences/{id}/stop-after-frame` (202); `stop_after_frame` on `GET …/state` and WS `sequence.progress`.

**Decision:** a new lifecycle op, not a body on `/stop`: an older daemon answers 404 instead of ignoring the body and stopping at once, which would lose the frame. With an exposure running, the run arms the pause gate and ends as Stopped when the gate lands, at the boundary after that exposure. With no exposure running (a slew, a cool-down, a wait, an already-paused run) it stops at once, since no frame would be saved by waiting.

**Reasoning:** the state lives on the daemon, so a sleeping iPad or a dropped link can't lose the request.

**Spec ref:** `SequencerService.StopAfterFrameAsync`, `OnPauseEntered`.

**Related:** #1557.

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

### 2026-11-14 — Session Live Stack: `POST/GET /equipment/camera/live-stack…` (#1787)

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/live-stack/start`, `POST …/live-stack/reset`, `POST …/live-stack/stop`, `GET …/live-stack`, `GET …/live-stack/frame`.

**Decision:** a colour stack of the sequencer's saved lights, apart from the Live View stack (own engine, own routes, no shared state). `start` is 202 and idempotent (a stack that already holds lights keeps them); `reset` empties the stack and keeps the switch on, `stop` switches off and empties it; both 204, idempotent. The switch is in memory, off at daemon start, and survives Sessions and a camera disconnect. **Known limit:** on a camera disconnect (or a connect that replaces it) the stack clears and the switch stays on, so a reconnect mid-night resumes stacking; there is no event for it. Only lights saved by the sequencer's background save are fed (never a manual capture, dark, flat or bias). A light restarts the stack when its target name, filter name, catalogue Session or frame size differs from the previous one. A light is stacked only when the camera reports a Bayer pattern and the light was taken at 1×1 binning; otherwise `last_outcome` is `not_colour`. The cap is 12 MP counted on the superpixel grid (width/2 × height/2). One stacker works while one light waits; a third is `skipped_busy` (still saved and catalogued), counted only while the switch is on. A light that does not match the reference is retried turned 180° (a meridian flip) and reads `flipped`. `GET …/live-stack` returns `SessionLiveStackStatusDto { enabled, unsupported, session_id, target_name, filter_name, stack_seq, lights_stacked, lights_unregistered, lights_skipped_busy, rejected_samples, width, height, last_outcome: reference|stacked|flipped|unregistered|wrong_size|too_large|not_colour|skipped_busy|dropped, last_light_at_utc }`. `unsupported` is additive and computed from the camera capabilities only: `no_camera`, `not_colour` or `too_large`, else null. `lights_stacked` counts the reference light; `stack_seq` never goes back. `GET …/live-stack/frame` is the colour stack as `image/jpeg` (≤ 1024 px), headers `Cache-Control: no-store`, `X-Stack-Seq`, `X-Stack-Lights`, 204 until a light is stacked. A camera service without the capability answers 501 `not-implemented`.

**Reasoning:** the stack follows the Session's lights, not Live View's loop, so it needs its own switch and status. The reason a camera cannot stack is decided once on the server from the capabilities, so an offline colour camera never reads as mono.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1camera~1live-stack`; `Endpoints/SessionLiveStackEndpoints.cs`, `Services/CameraService.SessionLiveStack.cs`, `Services/LiveStack/LiveStackEngine.cs` (`AddColour`), `Contracts/SessionLiveStackDtos.cs`.

**Related:** #1787, #1169; tests `CameraServiceSessionLiveStackTest`, `LiveStackEngineColourTest`, `SessionLiveStackEndpointsTest`.

### 2026-11-13 — Storage settings carry an `ETag`; `PUT` honours `If-Match` (#1807)

**Endpoint(s) or area:** `GET /api/v1/profile/storage` and `PUT /api/v1/profile/storage` (`storage` profile section).

**Decision:** `GET` and a successful `PUT` answer with an `ETag` header: a strong tag, the SHA-256 of the stored (normalized) section's JSON, in quotes. Nothing is stored for it; equal content gives an equal tag. `PUT` takes an optional `If-Match` header. When it is sent and is neither `*` nor the current tag, the answer is `412` Problem and nothing is written; a `412` is a rejected write, so no `profile.updated` is published. When `If-Match` is absent the write is accepted as before (refusing a write without it is a later slice of #1807). The tag check runs before the existing `400` validation. `POST /api/v1/storage/configure` also writes this section (the new save directory); since #1807 slice B that write publishes `profile.updated { section: "storage" }` like any daemon write of a client section, and the configuring client still loads the section again after a successful configure. The tag compare and the store write are two steps, not one atomic step (`ProfileEndpoints.PutStorageSettings`): two saves that arrive together with the same tag can both pass. That is accepted because one client controls the rig at a time (ADR 0005), so two writes of the same section racing within that window is not a case the daemon has to arbitrate; the tag is there for the common case of a stale copy on a second device.

**Reasoning:** the client saved the Storage section from its own defaults when the load had failed, and saved over a newer copy another device had written. ADR 0015 makes a client load a section from the rig before it saves it and send back the tag of the copy it loaded. A derived hash needs no version column and no migration; a stored version counter was the alternative, and is left for when every section moves.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1profile~1storage`; `OpenAstroAra.Server/Endpoints/ProfileEndpoints.cs` (`GetStorageSettings`, `PutStorageSettings`, `StorageETag`); client `lib/state/settings/loaded_section.dart`, `lib/services/profile_api.dart`.

**Related:** #1807, ADR 0015, ADR 0012, ADR 0005; tests `ProfileStorageEndpointTest`, client `loaded_section_test`, `storage_load_before_save_test`.

---

### 2026-11-13 — Live guide-camera loop: refused while the Guider is busy, and it takes the camera from setup activities (#1809)

**Endpoint(s) or area:** `POST /api/v1/equipment/guider/live/loop` (amends the 2026-10-16 live-view entry).

**Decision:** besides 409 when no guider is connected, `live/loop` answers a plain 409 (the reason in `detail`, e.g. "the guider is guiding — stop guiding before …") while the guider is guiding, calibrating, paused or has lost its star. When polar alignment or guide focus holds the Guide Camera, `live/loop` stops that activity first and then loops; the later request wins. Guiding later takes the camera from the loop without stopping it. `stop`, `select` and `deselect` are unchanged.

**Reasoning:** ADR 0016 puts every hand-over of the Guide Camera in one custody module (`GuideCameraCustody`). Looping under an active guide would break guiding, so it is a state conflict the client can explain (409), not a 500. Stopping the older setup holder matches how polar alignment and guide focus already take the camera from each other.

**Spec ref:** `Services/GuideCameraCustody.cs` (`TakeAsync`), `Services/GuiderLiveService.cs` (`LoopAsync`), `Endpoints/EquipmentEndpoints.cs` (`GuiderLiveResultAsync`).

**Related:** ADR 0016, PR #1879.

---

### 2026-11-11 — The dome follows the mount: `POST /equipment/dome/follow`, `GET/PUT /profile/dome`, and `following` on the dome status (#1689)

**Endpoint(s) or area:** new `POST /api/v1/equipment/dome/follow` (body `{"enabled": bool}`); new `GET` and `PUT /api/v1/profile/dome` (`dome` profile section); `runtime.following` on `GET /api/v1/equipment/dome`.

**Decision:** "follow the mount" is a per-profile setting (`follow_mount` in the `dome` section, so it survives a restart and a profile switch); the follow command writes it and answers `200 {"enabled": <state>}` (not 202: nothing runs in the background that the caller must wait for). Turning it on is refused `409` Problem, with the reason as `detail`, when the dome is not connected, cannot set its azimuth, or the profile has no dome radius. Turning the dome by hand (`slew`, `park`, `findhome`, `abort`, `disconnect`, or a plan's dome steps), an unsafe-weather reaction, an emergency stop, and a geometry that puts the scope outside the dome for the mount's pointing turn `follow_mount` off. The `dome` section holds the geometry the azimuth math reads (`dome_radius_mm`, `scope_position_north_south_mm` / `_east_west_mm` / `_up_down_mm`, `mount_type` = `equatorial` | `fork_on_wedge`, `gem_axis_mm`, `lateral_axis_mm`, `dec_offset_horizontal_mm`), `azimuth_tolerance_degrees`, `synchronize_during_mount_slew` and `dome_sync_timeout_seconds`. `PUT` keeps the stored `follow_mount` whatever the body says, so only the follow command changes it (a client saving geometry with a stale copy cannot turn following back on, or off). `PUT` is refused `422` with a reason for a scope outside the dome radius, a bad mount type, an out-of-range tolerance or timeout, and for a zero radius while following is on. The new endpoint is an action beside `slew` and `park` (the dome's other commands), rather than a field on one of them, because it changes a setting and starts no motion.

**Reasoning:** the follower was a placeholder that reported "not following" and refused to start, so the dome stayed where the user left it while the target moved 15 degrees an hour. The status flag lets Simple Mode say "Following the mount…" when the follower, not the user, is turning the dome (`state: slewing` with `following: true`).

**Spec ref:** `OpenAstroAra.Server/Services/DomeFollower.cs`, `DomeSettingsValidator.cs`, `DomeService.cs` (`SetFollowingAsync`), `Endpoints/EquipmentEndpoints.cs`, `Endpoints/ProfileEndpoints.cs`; client `lib/models/dome_settings.dart`, `lib/screens/simple/simple_device_windows.dart`, `lib/screens/settings/panels/equipment_dome_panel.dart`.

**Related:** #1689; tests `DomeFollowerTest`, client `simple_dome_window_test`, `equipment_dome_panel_test`.

---

### 2026-11-05 — Apply multi-star to a connected guider, and find the guide star (#1592)

**Endpoint(s) or area:** new `POST /api/v1/equipment/guider/multi-star/apply`, new `POST /api/v1/equipment/guider/live/find_star`.

**Decision:** `POST …/multi-star/apply` writes the active profile's `guider_advanced.multi_star_enabled` (null = on) to the connected guider, which means briefly disconnecting and reconnecting the guider's equipment (`set_profile_setup`). No body; 204 on success. 409 when no guider is connected (problem type `https://openastro.net/errors/guider-not-connected`) or when the guider is guiding, calibrating, has lost its star or is paused (a plain 409; stop guiding first), or while a sequence run is active (a plain 409: "A sequence is running; applying multi-star would release the guider's equipment under it. Apply it when the run has ended."); 422 when the guider refuses the setting or its equipment does not reconnect afterwards. `POST …/live/find_star` asks the guider to pick its best star in the current frame; 200 `GuiderLiveLockDto {x, y}` (where it locked, guide pixels, rounded to 0.01); 409 (a plain problem, not the typed one) when no guider is connected; 422 with the guider's reason when there is no image or no usable star. Neither takes a body or an idempotency key.

**Reasoning:** multi-star only reached a guider on its next connect or full push, so toggling it in Guider settings during a session did nothing until a reconnect; and a user who had looped an exposure had no way to ask the guider for its own star pick.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1guider~1multi-star~1apply`, `…~1equipment~1guider~1live~1find_star`; `Endpoints/EquipmentEndpoints.cs` (`ApplyGuiderMultiStarAsync`, `GuiderLiveFindStar`), `Services/GuiderService.ProfilePush.cs`, `Services/GuiderLiveService.cs` (`FindStarAsync`), `Equipment/MyGuider/PHD2/PHD2Guider.AdvancedSettings.cs` (`ApplyMultiStarFromProfileAsync`).

**Related:** #1592.

---

### 2026-11-03 — The guide camera's model and versions: `GET /equipment/driver/guidecamera`

**Endpoint(s) or area:** new `GET /api/v1/equipment/driver/guidecamera` → `DeviceDriverDto`, which gains an optional `name` (the model).

**Decision:** the profile's guide camera, when it is an Alpaca pick (`… [host:port/N]`), is looked up on that server like `/driver/{type}` (name from its management listing). Otherwise the connected guider is asked which camera it has open (`get_current_equipment`); its answer usually carries the same `[host:port/N]`, which is looked up the same way, and else the model alone is answered (the `Alpaca Camera [..] - ` lead and `(driver)` tail dropped). No guider camera known → 404. Declared before `/driver/{type}` so the literal wins.

**Reasoning:** the test rig's profile leaves the guide camera blank while the guider has one open; Simple Mode titles the Guider window with the camera's model, like Camera's.

**Spec ref:** `Endpoints/EquipmentEndpoints.cs` (`GetGuideCameraDriverAsync`), `PHD2Guider.GetConnectedCameraNameAsync`, `IGuiderService.TryGetGuideCameraNameAsync`.

**Related:** tests `DeviceDriverInfoTest`, `GuiderLiveFakeIntegrationTest`.

---

### 2026-11-03 — A device's driver and SDK versions: `GET /equipment/driver/{type}`

**Endpoint(s) or area:** new `GET /api/v1/equipment/driver/{type}` → `DeviceDriverDto { driver_version?, sdk_version?, firmware?, serial_number? }`.

**Decision:** for the selected (remembered) device of `{type}` (a `DeviceType` token; `guider` and unknown types → 400; nothing selected → 404), the driver version is its Alpaca `driverversion`, and the SDK version and firmware come from its server's `/management/v1/configureddevices` entry (`SdkVersion`, `Firmware` and `Config.serialNumber`, which OpenAstro AlpacaBridge adds; null elsewhere). Both reads use the management client's 3 s timeout, in parallel; a silent server answers nulls. ZWO cameras report no firmware.

**Reasoning:** Simple Mode's device windows show the versions support asks for, at their foot.

**Spec ref:** `Endpoints/EquipmentEndpoints.cs` (`GetDeviceDriverAsync`), `Services/AlpacaManagementClient.cs`; client `services/equipment_driver_api.dart`.

**Related:** tests `DeviceDriverInfoTest`.

---

### 2026-11-03 — Every version on the rig: `/server/versions` gains the board, the guider, the Alpaca servers and ASTAP

**Endpoint(s) or area:** `GET /api/v1/server/versions` (`ApiVersionsDto`): new optional `board_model`, `guider_version`, `alpaca_servers`, `astap_version`.

**Decision:** `board_model` is the device tree's model string ("Raspberry Pi 5 Model B Rev 1.1"), null without one. `guider_version` is the connected guider's version from its handshake (`get_version`, else the `Version` event), null while disconnected. `alpaca_servers` is `[{ "host", "port", "server_name"?, "manufacturer"?, "version"? }]`, one entry per distinct host:port among the remembered devices (IP when known, else host name), each asked `GET /management/v1/description` in parallel with the existing 3 s management-client timeout; a server that doesn't answer is listed with nulls. `astap_version` is parsed from the configured solver's `-h` banner ("CLI-2026.09.01"), cached per path, null when the binary is missing or silent. The star database already has its own route (`GET /api/v1/platesolve/database`). All four are optional and additive (v1 protocol-minor rule); older daemons omit them.

**Reasoning:** support asks for every version first; Simple Mode's Settings › Rig and the About panel show them. Alpaca does have a version call for the server itself (`ManufacturerVersion` in the management description), unlike for devices; that is what AlpacaBridge reports.

**Spec ref:** `Services/RigVersions.cs`, `Services/PlaceholderServerStateService.cs`, `Services/AlpacaManagementClient.cs` (`GetDescriptionAsync`), `PHD2Guider.GuiderVersion`.

**Related:** design/briefs/simple-mode-settings.md; tests `RigVersionsTest`.

---

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

**Decision:** the resume route accepts an optional `{ "recenter": bool, "refocus": bool }` body. When the run is still paused on the SAME target it paused on (reference identity of the RUNNING target (a DSO container, or a Simple Mode block with its slew or AboveHorizonCondition; for Simple Mode blocks, with none running, the first block that has not run, while the deep-sky path keeps its first-container fallback), snapshotted in OnPauseEntered), the daemon plate-solves + re-centers — and on request runs an autofocus sweep — BEFORE releasing the pause gate, while the engine is suspended and the rig idle. Absent body = `recenter=true, refocus=false`, so pre-§38.10 clients gain the pointing refinement transparently. The choice/prompt lives CLIENT-side pre-resume (dialog on the Resume tap): a daemon-side prompt would hold the gate hostage to a client that may be gone. Refinement is bounded (5-min re-center cap), best-effort (no solver/equipment → skip + honest notification, mirroring §35's verify-pointing messages), single-flight per run (a double-tapped Resume neither re-runs it nor yanks the gate open mid-solve), cancelled by Abort/Stop, and ALWAYS ends in a gate release. The §35 safety auto-resume and the star-lost recovery re-center through `GetActiveTargetCoordinatesAsync`, which finds a Simple Mode block the same way.

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

**Decision:** each guided run (guiding started → stopped) is measured once, over its first 300 s of clean guiding (frames inside a dither settle or a guider pause are left out; a dither, settle, pause or lost star closes a segment and each segment gets its own intercept). The daemon rebuilds the uncorrected Dec motion as the Dec offset minus every Dec pulse already applied × the calibration's Dec rate (`get_calibration_data` yRate, read once per run), fits its slope, and reports `pa_error_min_arcmin = |drift ″/min| / (3600 ω)` ≈ 3.809 × drift. Payload (`PaResidualDto`): `{ id, status: measuring|done|unavailable, started_utc, completed_utc?, sample_seconds, target_seconds, frames, drift_arcsec_per_min?, pa_error_min_arcmin?, uncertainty_arcmin?, reliable?, hour_angle_hours?, dec_deg?, align_error_arcmin?, align_ended_utc?, session_id?, reason? }`. `measuring` is published at the start and every 30 s of sample; `done` once. `reason` on `unavailable`: `lock_shift` (comet tracking moves the star in Dec on purpose), `ao` (an adaptive-optics unit is connected, so the Dec corrections are AO steps, not mount pulses; checked with `get_current_equipment` at the run's start), `no_calibration`, `no_pixel_scale`, `no_fit`, `guiding_not_holding` (an axis ran away: 10 consecutive steps with the pulse pinned at its maximum and the distance growing), `settle_failed` (a settle ended in an error other than "Stopped capturing"), `mount_not_tracking` (the mount was not tracking when the run started); the first reason set is kept. `reliable` is false when the 1σ uncertainty (least squares, widened by the residuals' lag-1 autocorrelation) exceeds max(1′, half the value), or when the value less 2σ is more than 2′ above `align_error_arcmin` (#1731: a lower bound cannot exceed the total error Polar Align measured; Dec pulses eaten by lost motion read as drift, and under closed-loop guiding the share that reached the star cannot be fitted). Guiding that stops after 120 s of sample still reports; earlier, the measurement is dropped and the previous result is re-published (`{ status: "idle" }` when there is none); a run already reported `unavailable` stays so whenever guiding stops. `align_error_arcmin` is the newest Polar Align result with a measured error (complete, or stopped while adjusting) in the 12 h before. Hour angle and Dec are the mount's at the end. A `done` result is logged to the `pa_residuals` table with the running imaging session (`ActiveRunSessionRegistry.Current`); the list route returns rows newest first, `?sessionId=` narrows to one session, `limit` defaults to 20 (1–500).

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

**Decision:** `start {exposure_sec 0.05–30 (default 1), binning? 1–4 (default 1)}` → 202; 400 out of range; 409 already running, or the camera is busy (an autofocus run, or a Rig Occupancy routine that holds the main camera: a sequence that is not terminal, polar alignment or centering; the detail names it, e.g. "the camera is busy: polar alignment is running"). The loop captures through the §59 analysis seam and fits the mask pattern on the brightest star (locked to that star after the first fit; after 5 misses the lock falls back to the user's pick, or to the whole frame when there is none — see the 2026-10-16 entry). `state` → `BahtinovFocusStatusDto { active, state: idle|running|stopped|error, exposure_sec, binning, seq, started_utc, latest, recent[≤120], best_offset_px, zone_px, zone_from_optics, zone_um, focal_ratio, within_zone, error, consecutive_failures, has_frame, frame_seq }`; a sample is `{ seq, captured_utc, detected, problem: no_star|near_edge|no_pattern|null, offset_px, defocus_um, within_zone, star_x, star_y, peak_adu, overlay? }`, with `overlay { crop_size, lines[3] { role: outer|central, x1, y1, x2, y2, angle_deg }, intersection_x, intersection_y, spread_deg }` on `latest` only, in crop pixels. `offset_px` is the central spike's signed distance from the outer spikes' crossing; its sign convention is fixed by the session's first fit (which way that is on the focuser depends on the mask and the optical train, so the client gives advice relative to the user's last move). `defocus_um = offset × pixel size × binning × 3πN/4` (N = working focal ratio, reducer applied); `zone_um` is half the CFZ the autofocus step sizing uses (2.2 µm × N² / 2) and `zone_px` its offset equivalent, else a fixed 0.5 px when the profile lacks focal length, aperture or pixel size. A frame without a pattern is a sample, never an error; 3 consecutive capture faults end the readout in `error`. `frame` is the 256-px crop around the locked (or picked) star, with or without a fit, rendered with a sky-relative spike stretch, or the whole frame (stars ringed) when no star is locked — `frame_crop` says which (2026-10-16 entry); JPEG with `X-Frame-Seq`, 204 before one. No WebSocket events: the client polls, as for the guide-camera focus loop. While the readout is active, `POST /equipment/focuser/autofocus` and `POST /sequences/{id}/start` return 409 Problem `title: bahtinov_focus_active`.

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

**Decision:** the guider's settings beyond Tune Guiding become a profile section with every field optional — null keeps the guider's own value, so a profile never overwrites a setting it did not set (one exception, 2026-11-05: `multi_star_enabled` null means on, `GuiderAdvancedSettingsMapping.ToEngine`): `{multi_star_enabled, fast_recenter, tolerate_jumps, tolerate_jumps_threshold_px, min_star_hfd, max_star_hfd, min_star_snr, mass_change_threshold_enabled, mass_change_threshold, search_region_px, auto_exposure_min_ms, auto_exposure_max_ms, auto_exposure_target_snr, noise_reduction ("None" | "2x2 Mean" | "3x3 Median"), use_subframes, max_ra_duration_ms, max_dec_duration_ms, backlash_enabled, backlash_pulse_ms, backlash_floor_ms, backlash_ceiling_ms, assume_orthogonal, stop_guiding_when_slewing, cal_flip_requires_dec_flip, variable_delay_enabled, variable_delay_short_s, variable_delay_long_s, dither_scale_factor, dither_ra_only, ra_algorithm, dec_algorithm}`. It is kept apart from `phd2` so a client that PUTs that section without these fields cannot wipe them. A PUT replaces the whole section: a field left out of the body is cleared to null (back to the guider's own value), so a client sends every field it wants kept — GET, change, PUT. The PUT validates at the write boundary (400) with the guider's own ranges: every set number positive except the max RA/Dec pulse (0–1,000,000 ms; 0 = no correction on that axis) (superseded by #1748: a variable delay may be 0, and more limits apply, see the 2026-11-09 entry), the backlash pulse 20–8000 ms, backlash on only with a pulse width (2026-10-18: an enable alone made the guider refuse the whole group on every push), min ≤ max for HFD, auto exposure, backlash and the variable delay (superseded by #1748: HFD is strictly min < max, and backlash is floor ≤ pulse ≤ ceiling, see the 2026-11-09 entry), a known noise method, the variable delay as all three values or none, algorithm names non-blank (trimmed) (superseded by #1748: a fixed list per axis, see the 2026-11-09 entry). Only set keys reach the guider, grouped as its setters take them (a profile saved before the backlash rule, on with no pulse, skips `set_backlash_comp` with a warning in the log so the other groups still land; superseded by #1748: the group is left out and listed in `not_applied`, see the 2026-11-09 entry) (`set_guider_options`, `set_star_detection`, `set_auto_exposure`, `set_noise_reduction`, `set_use_subframes`, `set_guide_limits`, `set_backlash_comp`, `set_mount_options`, `set_variable_delay_settings`, `set_dither_settings`, `set_algo` per axis) on the tuning push — guiding continues; `set_algo` goes before Tune Guiding's `set_algo_param` aggression (superseded by #1748: the name and scale are resolved per algorithm, see the 2026-11-09 entry) / minimum move, so those land on the newly chosen algorithm rather than on the one it replaces; `multi_star_enabled` rides `set_profile_setup`, which the guider accepts only with its equipment disconnected, so it goes with the connect-time and full push and, for an already connected guider, with `POST /equipment/guider/multi-star/apply` (2026-11-05). `GET /equipment/guider/advanced` is always 200: `{connected, current: GuiderAdvancedSettingsDto (the guider's values now, null where it did not answer), ra_algorithms[], dec_algorithms[]}` (superseded by #1748: it also returns `not_applied`, see the 2026-11-09 entry); its reads all run in parallel and a refused one posts no inbox error. Profile select and clone previously skipped `unguided` (#1264), so selecting a profile kept the previous rig's unguided values; both sections now follow the profile, and a profile without them reads the defaults (all-null for `guider_advanced`).

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

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1telescope~1slew`; `Endpoints/EquipmentEndpoints.cs` (`SlewAsync`), `Services/MoveAdmission.cs` (`RunActiveRefusal`, #2139), `Services/SequencerService.cs` / `PlaceholderSequenceServices.cs` (`ActiveRunIdsAsync`); tests `EquipmentEndpointsSlewTest`, `SequencerServiceTest`, client `equipment_mount_panel_test`.

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

### 2026-11-03 — Live stacking (server side): `POST/GET /equipment/camera/liveview/stack…`

**Endpoint(s) or area:** `POST /api/v1/equipment/camera/liveview/stack/start`, `POST …/stack/reset`, `POST …/stack/stop`, `GET …/stack`, `GET …/stack/frame`.

**Decision:** stacking is opt-in per Live View session and rides the running loop. `stack/start` (202, no body) enables it for the session that runs now; 409 when Live View is not active, when the session is a one-shot-colour camera at 1×1 binning (a later slice stacks colour), or when the session's frame size is known and over 12 MP. `stack/reset` empties the stack and keeps it enabled, `stack/stop` disables and empties it; both are 204 and idempotent. The stack also clears and turns off when Live View stops, self-stops, disconnects or restarts, so a new session never stacks until `stack/start` is called again. `GET …/stack` returns `LiveStackStatusDto { enabled, session_id, stack_seq, frames_stacked, frames_unregistered, rejected_samples, reference_stars, width, height, last_offset_x, last_offset_y, last_rotation_deg, last_outcome: reference|stacked|unregistered|wrong_size|too_large|dropped, last_frame_at_utc }`; `enabled` is true only for the running session, and `stack_seq` counts frames added and never goes back. `GET …/stack/frame` is the stretched stack as `image/jpeg` (≤ 1024 px, `Cache-Control: no-store`, headers `X-Stack-Seq`, `X-Live-Session`, `X-Stack-Frames`), 204 until a frame is stacked. The existing `/liveview` and `/liveview/frame` are unchanged (the live JPEG stays the raw frame). A camera service without live stacking answers 501 `not-implemented`.

Each frame is aligned to the first one with the stars the §59 detector finds (shift and rotation, no scale) and folded into a per-pixel running mean with 3-sigma clipping, so satellite trails and cosmic rays drop out. Frames that cannot be matched are counted in `frames_unregistered` and never stacked; after 10 in a row the stack restarts on the new field. The work runs on the Live View loop thread after the live JPEG is published and outside the capture gate: with stacking on, the live frame rate drops (detection, resample and a second JPEG per frame; annotated Live View runs the detector twice), and a manual capture is not delayed by it.

**Reasoning:** an always-on stack would cost a 12 MP float pass per frame for users who only focus. Hanging the engine on the camera service through its own interface keeps the shared service interface and the DI wiring untouched. A client polls `stack` and `stack/frame` the way it polls Live View.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1camera~1liveview~1stack`; `Endpoints/LiveStackEndpoints.cs`, `Services/LiveStack/LiveStackEngine.cs`, `Services/CameraService.LiveStack.cs`, `Contracts/LiveStackDtos.cs`.

**Related:** §64 Live View, §64.17 live stacking; tests `LiveStackEngineTest`, `LiveStackEngineLimitsTest`, `LiveStackEndpointsTest`, `CameraServiceLiveStackLifecycleTest`.

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
      (rms_total/ra/dec in px, rms_*_arcsec when the scale is known;
      frames taken while the guider settles are left out, #1749).
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
                       align_ended_utc?, session_id?, reason?, above_align? }
      #1311 — the polar alignment residual from the first 300 s of clean
      guiding of each guided run: status measuring (at the start, then every
      30 s of sample) → done | unavailable (reason). pa_error_min_arcmin is a
      lower bound. A dropped measurement re-publishes the previous result, or
      { status: "idle" }. The same object is runtime.pa_residual on
      GET /equipment/guider.
  profile.updated { section }
      #1644 — after any successful write under /api/v1/profile/<section>
      (a PUT, or the first-flip re-arm POST); section is the first path
      segment ("site", "optics", "filter-wheel", "safety", ...). A rejected
      write (4xx/5xx) publishes nothing. Another device refetches that
      section; the one that wrote may ignore it. #1807 — also when the
      daemon itself changes a client section outside those routes (time
      sync, plate solve, a connect-time fill, the flip executor, auto
      flats, the guider, the dome follower, the filter-wheel label mirror,
      storage configure), once per section whose ETag moved. A profile
      select and the daemon-only sections publish nothing.
  client_settings.updated { keys, updated_utc }
      #1644 — after a successful PUT or PATCH /api/v1/client-settings; keys
      are the top-level keys the write carried (PATCH: the patch's keys,
      removed ones included; PUT: the keys of the stored object) and
      updated_utc is the new stamp, equal to the updated_utc in the write's
      own response so the writer can tell its own event from another
      device's. A rejected write publishes nothing.
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


## 2026-11-07 — Switch ports carry the device's step size: `step` on `SwitchPortDto` (#1680)

**Endpoint(s) or area:** `GET /api/v1/equipment/switch` (and every response that carries `SwitchDto.ports`): each port gains `step` (number, additive).

**Decision:** `step` is the port's ASCOM `SwitchStep`, read with the rest of the port snapshot the daemon already keeps for the sequencer's `SetSwitchValue.Validate`. `0` means unknown; older daemons omit it and clients read that as `0`. Clients snap a slider to `min + k × step` (rounded to the step's decimals; `k` stops at the last step at or below `max`, so a `max` off the grid is never sent) and give it one stop per step; a step that would make more than 1000 stops over the range is treated as unknown, and an unknown step keeps each client's previous rounding. A 0–1 port stays an on/off whatever its step. The write route doesn't validate against the step: the device stays the authority.

**Reasoning:** without it the client guessed (whole numbers over a range of 10 or more, else hundredths), so a slider could send 37 to a light box that takes steps of 10, and the device either refused it or stored something else than the label said.

**Spec ref:** `OpenAstroAra.Server/Contracts/EquipmentDtos.cs` (`SwitchPortDto`), `SwitchService.ProjectPorts`; client `SwitchPort.step`, `snapToSwitchStep` / `switchSliderDivisions`.

**Related:** #1680, #1651 (PR #1678); tests `SwitchMediatorTest.ProjectPorts_carries_each_ports_step_to_the_rest_dto`, `switch_port_layout_test`, `simple_switches_window_test`.

## 2026-11-07 — A switch device's versions: `GET /api/v1/equipment/driver/switch/{id}` (#1651)

**Endpoint(s) or area:** `GET /api/v1/equipment/driver/switch/{id}` (new); `ISwitchService.GetDevice`.

**Decision:** answers a `DeviceDriverDto` (driver version, SDK version, firmware, serial number; each null when its Alpaca server doesn't report it) for one KNOWN switch, looked up by its id from the discovery record the switch service holds, from the same Alpaca management calls as `/driver/{type}`; 404 for an unknown id. `/driver/{type}` stays one device per type, so `/driver/switch` (the first remembered switch) is not what a client should ask for a given switch.

**Reasoning:** every other device window lists its versions in Simple Mode's Settings; switches are many at once (one per power box), which the per-type route, built on the single equipment selection, can't address.

**Spec ref:** `OpenAstroAra.Server/Endpoints/EquipmentEndpoints.cs` (`GetSwitchDriverAsync`, shared `DriverOfAsync`); client `deviceDriverProvider('switch/<id>')`.

**Related:** #1651 (PR #1678); test `DeviceDriverInfoTest.The_switch_route_answers_that_switchs_driver_by_its_id`.

## 2026-11-07 — Polar Align in three stages: coarse at Home, fine, three-point check

> Superseded in two points by the 2026-11-08 entries below: the coarse stage now measures the axis with a seed turn instead of reading the camera's pointing at Home as the axis (#1727), and an unknown `stage` on `start` is a 400, not a 409.

**Endpoint(s) or area:** `POST /api/v1/equipment/polaralign/start` (optional body `PolarAlignStartRequestDto { stage? }`), new `POST /api/v1/equipment/polaralign/next`, `GET …/polaralign/status` (`PolarAlignStateDto` gains `stage`, `waiting_for_home`, `check_solves_done`, `check_result`), `GET`/`PUT /api/v1/profile/polar-align` (`seed_rotation_deg` default 30 → 60; new `check_step_deg` 30, `coarse_exposure_seconds` 3, `use_home` true), WS `polar_align.stage`, `polar_align.needs_home`, `polar_align.checked`; `started` and `progress` carry `stage`.

**Decision:** the routine is three stages the user walks through with `next`. **Coarse** (`stage: coarse`, the default): the mount goes Home (Find Home when `CanFindHome`, else state `homing` + `waiting_for_home` + `needs_home` until `next`), tracking goes off, and every main-camera solve (the Verify Position capture-and-solve, blind fallback on, `coarse_exposure_seconds`) is read as the RA axis itself and compared with the apparent pole — `progress` with `stage: coarse`; a rough readout (cone error is not removed) that shows how far and on which side. **Fine** (`stage: fine`): the §45 guide-camera routine as before, except a pointing within 3° of the pole (the mount at Home) is first slewed to Dec 85° so the two-point chord exists. **Check** (`stage: check`): Home, Dec 85°, three guide-camera solves with two RA turns of `check_step_deg` away from the meridian, `PolarAlignGeometry.FitAxisThreePoint` (the circle through three pointings — no commanded rotation, no mirror ambiguity), then state `checked` (terminal, like `failed`: a later Stop keeps it) with `check_result { altitude_error_arcmin, azimuth_error_arcmin, total_error_arcmin, cone_half_angle_deg, turn_mismatch_deg, fine_error_arcmin?, fine_delta_arcmin?, verdict, measured_at }`, the `checked` event, a `polar_alignments` row with outcome `checked` (which the #1311 cross-check and the tracking prior now read), and the lease released. `next` is 409 unless the routine waits on the user (`waiting_for_home`, or `adjusting`/`paused`); `start` is 409 for an unknown stage, or for `coarse` on a daemon without the main-camera solver. New error reason `home_failed`. A `stage` absent from `started`/`progress` is an older daemon's one-stage routine; clients treat it as `fine`.

**Reasoning:** #1591 — the two-point seed and the live loop can agree with each other and still be degrees off; a measurement that shares none of their inputs (fresh frames, Home, an exact three-point fit) says how good the alignment really is, and `fine_delta_arcmin` is the number the triage asked for. The coarse stage answers the first question of a night at a new site — which way, and how far — with the camera the user already focused. 60° for the turn: §45.16's own numbers (1.9× noise at 30°, 1.0× at 60°); from Home the tube spins about the axis line, so the turn cannot reach the pier.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/paths/~1api~1v1~1equipment~1polaralign~1next`; `PolarAlignService.Stages.cs`, `PolarAlignGeometry.FitAxisThreePoint`, `MainCameraPointingSolver`.

**Related:** #1591, #1686 (the plate-solve routine this builds on), §45.16 (the 2-point call, now revisited for the check); tests `PolarAlignServiceTest` (`Coarse_stage_*`, `Check_stage_*`, `Next_*`), `PolarAlignGeometryTest` (`Three_point_fit_*`), client `polar_align_state_test` (stages), `polar_align_panel_test`.
## 2026-11-08 — Polar Align coarse stage measures the axis (seed turn) instead of reading the camera's pointing at Home as the axis (#1727)

**Endpoint(s) or area:** `POST /api/v1/equipment/polaralign/start` (`stage: coarse`), `GET …/polaralign/status`, WS `polar_align.started` / `polar_align.stage` / `polar_align.progress` with `stage: coarse`.

**Decision:** the coarse stage runs the §45 seed-and-adjust routine on the main camera: Home (as before), the slew to Dec 85°, one RA turn of `seed_rotation_deg` with a main-camera solve at each end, the two-point axis fit, then the live loop with tracking off. Its states are now the fine stage's — `homing` → `seeding` → `adjusting`/`paused` — where before it went `homing` → `adjusting` and never slewed; a run started with `use_home: false` opens in `seeding`, not `adjusting`. The readout's meaning is unchanged (alt/az error of the axis), only its source: a measured axis, never a pointing. Two consequences for the other stages: the fine stage that follows a coarse stage goes Home again first (`polar_align.stage` with `stage: fine`, `state: homing`, then `seeding`) — the coarse turn and its tracking-off loop left the mount an hour angle from Home, and a second turn outward could reach a GEM's limit; with `use_home` on every fine start homes first too, so it opens in `homing` (a mount without Find Home is asked, `needs_home`, as in the other stages); with `use_home: false` a fine start begins from where the user put it (`seeding`). A check-only start with `use_home: false` opens in `checking`, never `seeding`. `POST …/polaralign/start` with `stage: coarse` is refused (409, the usual preflight) when the main camera is not connected, before the mount moves; an unknown `stage` is a 400, not a 409. `POST …/polaralign/complete` on a routine in the terminal `checked` state ends it: state `stopped`, `polar_align.stopped` published, no second session row (the check wrote its own); a `stop` there still leaves `checked` standing. With `use_home: false`, every stage after the first slews back to the run's start pointing before its turn (the turns never add up toward a limit). `check_result.fine_delta_arcmin` is the distance between the check's axis and the fine stage's last axis (alt and az both), not the difference of their sizes. `next` cancels a capture in flight, so the next stage starts at once; the live frame ids and `live_iterations` run on across the stages within one run. The check refuses a verdict (`polar_align.error` with `axis_fit_failed`) when the solved turns disagree with the mount's reported turns by more than 5° or the three pointings sit under 1° from the axis — three solves of one field must never read as "excellent". After a Home the user set by hand (no Find Home), the off-axis move is 5° of Dec relative to the mount's reported pointing, which is stale then. `PUT /api/v1/profile/polar-align` clamps `seed_rotation_deg` (5–120), `check_step_deg` (5–60) and `coarse_exposure_seconds` (0–60] into range instead of refusing the whole document (#1736: the app has no control for them yet); the coarse start also refuses (409) when the main optics are unset, before the mount moves — but only an explicit `stage: coarse`: a start that names no stage (Simple Mode, the Preflight pilot) begins at the fine stage instead when the coarse stage cannot run, and `started` says `stage: fine`. With `use_home` on, every fine start goes Home first (a mount without Find Home is asked, `needs_home`), so "Adjust again" after a check never turns further out. After the user's Continue on `needs_home` a `polar_align.stage` event (`state: homing`, `waiting_for_home` off) follows at once. `stop` clears `waiting_for_home`. The check's turn mismatch tolerates a 180° RA fold in the mount's reading (a synced mount reporting Dec past 90°). `polar_align.started` is published before the run begins, so it always precedes the run's own events (`needs_home` first of all). A new stage clears the previous stage's readout on the daemon too: `GET …/status` reports no `current_error_arcmin` (and no `last_frame`) between a stage's start and its first solve, and a Stop there writes a session row without a final error. The coarse preflight also refuses (or, unnamed, falls back to fine) when the main camera is busy with a live view or a sequence. After a hand-set Home the seed and check hints are the mount's RA at Dec ±85°, not its stale reading. The turn and rough-exposure knobs are clamped on every read as well as on PUT (`PolarAlignSettingsClamp`; a stored 0° turn from an older build can no longer reach the mount). `check_result.total_error_arcmin` is rounded before the verdict is chosen, so the two always sit in one band. Live frame ids count every frame of the run, failed ones included. `POST …/polaralign/complete` is refused (409) while the coarse stage runs — the rough readout is never recorded as a completed alignment; use `next` or `stop`. `GET /api/v1/profile/polar-align` returns the clamped knobs the daemon will use. Emergency Stop stops a running routine in any stage (it asks the service's `IsActive`, not a list of states). The coarse live loop solves each frame with one near look (no staged search, no blind fallback); only its two seed frames keep the blind fallback.

**Reasoning:** on the first real night the camera-as-axis zero sat 75′ from the fine stage's seed and the three-point check agreed with the fine stage; a camera is a degree off the axis at Home on an ordinary rig, and a readout shown to the arcminute must not carry that. No new fields on the wire — clients already render `seeding` for the fine stage and the Simple panel's phase text covers it (the clamped `GET /profile/polar-align`, the 409 for Complete in the coarse stage and the 400 for an unknown stage are the behaviour changes of this entry).

**Spec ref:** `PolarAlignService.Stages.cs` (`RunSeededAdjustAsync`, shared by `RunCoarseStageAsync` and `RunFineStageAsync`).

**Related:** #1727, #1591; test `PolarAlignServiceTest.Coarse_stage_homes_the_mount_measures_the_axis_with_a_main_camera_seed_turn_and_Next_moves_to_fine`.

## 2026-11-08 — Emergency Stop reports `mount_stopped`, not `park_requested` (#1672)

**Endpoint(s) or area:** `POST /api/v1/server/emergency-stop`, WebSocket `safety.action_taken` with `action: emergency_stop`

**Decision:** Emergency Stop no longer parks the mount. It sends AbortSlew and then Tracking off. `EmergencyStopResultDto.park_requested` becomes `mount_stopped`, the `safety.action_taken` payload changes the same way, and the failed-rung token `park` becomes `stop_mount`.

**Reasoning:** A park is a slew, and it runs the code that may be at fault. This is a breaking change inside `/api/v1/`, permitted within v0.x, and needs no shim: an older client reads the missing field as false and only drops its "mount told to park" line. An older client shows an unknown failed rung through its generic `{step} FAILED` text. The §35.4 unsafe-weather reaction still parks and keeps `park_requested`.

**Spec ref:** `OpenAstroAra.Server/openapi.yaml#/components/schemas/EmergencyStopResultDto`

**Related:** PORT_PLAYBOOK.md §35.3 step 3, #1671

## 2026-11-08 — Out-of-step Guider: `out_of_step_reason` and the guider-out-of-step 409 (#1665)

**Endpoint(s) or area:** `GuiderStateDto.out_of_step_reason`; `POST /equipment/guider/darklibrary/build` and the defect-map build; `POST /equipment/guider/focus/start`; `polar_align.error`

**Decision:** `out_of_step_reason` is an optional field on the guider runtime: null when the Guider holds the active profile's settings, or when that is not known yet. While it is set, the dark library and defect map builds answer **409 `https://openastro.net/errors/guider-out-of-step`**, the focus loop answers 409, polar alignment fails with reason `guider_out_of_step`, and Start guiding ends with a failed `guider.operation_complete`. Each carries the same text. The 422 of `POST /equipment/guider/profile/push` is unchanged.

**Reasoning:** The field is additive and needs no version change. A client can show the reason before the user presses Start. A separate problem type keeps this apart from `guider-not-connected`.

**Spec ref:** PORT_PLAYBOOK.md §63.4

**Related:** #1661, #1664

## 2026-11-09 — The cooling fan is designated in the profile: `GET`/`PUT /profile/cooling-fan` (#1700)

**Endpoint(s) or area:** `GET`/`PUT /api/v1/profile/cooling-fan`, `CoolingFanDto`, `ProfileSnapshotDto.cooling_fan`

**Decision:** The profile names the switch that runs the camera's cooling fan by its `device_id` (Alpaca `UniqueId`) and `port_id`. Both null, or the field absent from an older profile, means no fan is designated and there is no interlock. A half-set designation is a 422. Profile sharing strips the field, because a `UniqueId` names the donor's own switch. The daemon never infers a designation from a device name.

**Reasoning:** Alpaca has no fan capability and ADR 0003 rule 2 forbids branching on a device name, so the choice has to come from the user. Two nullable fields keep one schema and one GET/PUT, like the other profile sections.

**Spec ref:** `Services/CoolingFanInterlock.cs`, `Services/SwitchService.cs` (`SyncFanToCoolingCameraAsync`)

**Related:** #1065, #1076, #1695

## 2026-11-09 — A profile switch changes the connected devices: select response, 409s (#1710)

**Endpoint(s) or area:** `POST /api/v1/profiles/{id}/select` (now `ProfileSelectResultDto`), `POST /api/v1/profiles` with `activate` true or absent, `DELETE /api/v1/profiles/{id}` of the active profile, `IEquipmentSelectionStore` (one remembered-device list per profile)

**Decision:** The select response is `ProfileSelectResultDto`: the former `active_id` and `profiles` plus `device_messages`, a list of `{device_name, message}` for each device with a problem. A device that fails does not fail the switch. Select, create-and-activate and delete-of-the-active-profile return 409 problem+json while anything on Rig Occupancy runs (#2141: a sequence that is not terminal, aborting included; polar alignment; centering), with the detail naming it ("a sequence is running — stop it before switching profiles"), and leave the active profile and the rig as they were. On success each runs the same device switch: Safety Monitor off first, then every device the new profile does not remember, then its remembered devices under their connect-on-boot toggles. Create and delete keep their response bodies, so a device problem on those paths is logged only.

**Reasoning:** A profile owns its devices. A simulated Safety Monitor left connected after a switch could park a real mount, and a device change under a run would pull equipment from it.

**Spec ref:** `Endpoints/ProfilesEndpoints.cs`, `Services/ProfileDeviceSwitch.cs`

**Related:** #1710

## 2026-11-09 — Filter focus offsets switch and the unattended shutdown default (#1667, #1712)

**Endpoint(s) or area:** `FilterWheelPolicyDto.use_filter_wheel_offsets` on the filter wheel policy; `unattended_shutdown_wait_minutes` in the safety policies

**Decision:** `use_filter_wheel_offsets` is a new boolean, default false. When true, a filter change moves the focuser once by `offset(new) - offset(old)` after the wheel lands, on REST and sequencer changes and on Alpaca and manual wheels. The default of `unattended_shutdown_wait_minutes` changes from 10 to 30 for a profile that does not store a value. A stored value is kept.

**Reasoning:** The offsets were read and shown but never applied, and the switch was not exposed. Ten minutes was too short for a user who has stepped outside.

**Spec ref:** `Services/FilterFocusOffset.cs`, `Services/UnattendedShutdownService.cs`

**Related:** #1667, #1712

## 2026-11-09 — Emergency Stop failed rungs, and frames on `sequence.progress` (#1738)

**Endpoint(s) or area:** `POST /api/v1/server/emergency-stop` (`failed_rungs`), WS `safety.action_taken`, WS `sequence.progress`

**Decision:** `failed_rungs` holds zero or more of these tokens: `abort_runs`, `halt_rotator`, `abort_exposure`, `stop_guide_focus`, `stop_bahtinov`, `cancel_autofocus`, `stop_polar_align`, `stop_centering`, `stop_guiding`, `stop_mount`, `flat_panel_light_off`, `cooler_off`. A device that is not connected is not a failed rung. `frames_captured` (int) is also on WS `sequence.progress`, with the same meaning as on the polled run state.

**Reasoning:** Clients read both fields, and neither was written down in full.

**Spec ref:** `Services/EmergencyStopService.cs`, `Services/SequencerService.cs`

**Related:** #1738

---

## 2026-11-09 — What a guider settings push did not land: `not_applied`; the Guider's limits at save (#1748)

**Endpoint(s) or area:** `GET /api/v1/equipment/guider/advanced` gains `not_applied` (`GuiderAdvancedViewDto`, new `GuiderNotAppliedDto`); `PUT /api/v1/profile/guider-advanced` refuses more values; the guider push (connect-time, `POST /api/v1/equipment/guider/profile/push`, both scopes). This changes the 2026-10-16 entry for the guider-advanced section.

**Decision:** the view is `{connected, current, ra_algorithms[], dec_algorithms[], not_applied[{setting, reason}]}`. `setting` is the name the imager sees, `reason` plain text; for a refusal it is the Guider's own message. The list holds the latest push only, each push replaces it, and it is empty when nothing failed, when no push has run and when the Guider is not connected. A refused message from the push is logged and listed, and raises no notification.

The PUT answers 400 outside these limits (`GuiderAdvancedLimits`): search region whole, 7–50; mass change threshold above 0, at most 1 (a fraction on the wire); min and max star HFD each above 0, min strictly below max when both are set; tolerate-jumps threshold and dither scale above 0, at most 100; backlash pulse 20–8000, floor at least 20 and at most the pulse, ceiling at most 8000 and at least the pulse, floor at most ceiling; variable delays whole seconds, short 0–10, long 0–120, short at most long (0 is now allowed; null still means unset); RA algorithm one of Hysteresis, Lowpass, Lowpass2, Resist Switch, Predictive PEC, ZFilter; Dec algorithm the same list without Predictive PEC. Names are checked after trimming.

The push applies the same limits to values already saved: a bad value is left out of its group and listed, and the rest of the group is sent. An inconsistent backlash group goes whole, which replaces the earlier log-only skip of an enable with no pulse. The variable delay group also goes whole when any of its values is bad, because the guider takes the three together, and a min and max star HFD that conflict are both left out. Aggressiveness resolves per axis after any `set_algo` in the same push, from `get_algo_param_names`: `aggression` or `reactiveWeight` as Ara's 0..1 fraction, `aggressiveness` as the fraction × 100, none of these with other names present not sent and listed. A question that failed, or only `algorithmName` back, sends `aggression` as before.

**Reasoning:** an imager with one refused setting got a false Critical on every connect, an aggressiveness change did nothing on four algorithms, and one bad value cost its whole message. The limits are the Guider's own. They are declared in the Equipment project because the push lives there and the Server project maps onto them.

**Spec ref:** `GuiderAdvancedLimits`, `PHD2Guider.ResolveAggressiveness`, `PHD2Guider.LastPushNotApplied`, `GuiderLiveService.GetAdvancedAsync`

**Related:** #1364, #1592

---

## 2026-11-10 — Stop the focuser: `POST /equipment/focuser/halt`; each-night guider calibration in plans (#1542, #1716)

**Endpoint(s) or area:** new `POST /api/v1/equipment/focuser/halt` (202); `CalibrateEachNight` on the Start Guiding instruction; `RecalibrateGuider` on the Meridian Flip trigger; diagnostic `guider.calibration.failed`.

**Decision:** the halt has the rotator's shape: the 202 means the stop reached the driver, and a focuser that is not connected answers 500. Both new sequence fields default to false, so a body without them behaves as before. Simple Mode sets both. `CalibrateEachNight`: the night's first start (a night starts at local noon) recalibrates, later starts that night reuse the calibration. `RecalibrateGuider`: guiding comes back after the flip on a new calibration, unless `meridian_guider_recal` is `skip`. When a calibration fails, the daemon calls the Guider's `restore_calibration` and guides on the earlier calibration. The diagnostic `guider.calibration.failed` (yellow) stays open until a calibration works. With nothing to restore, or a Guider without the call, the start fails and the run pauses awaiting the user.

**Reasoning:** the Guider erases its calibration before it calibrates, so a failed run left none. An unattended plan should not end the night for that.

**Spec ref:** `GuiderService.RecalibrateAndGuide`, `GuiderService.StartGuidingCalibratedTonight`, `MeridianFlipExecutor.MeridianFlip`, `FocuserService.HaltAsync`

---

## 2026-11-11 — The camera status names its own dew heater: `dew_heater` (#1663)

**Endpoint(s) or area:** `GET /api/v1/equipment/camera` gains `dew_heater` (`CameraDto`, new `DewHeaterDto`).

**Decision:** `dew_heater` is `{device_id, port_id}` (the heater switch's Alpaca UniqueId and the port's id) or null. It is additive: a client that does not know it ignores it, and a body without it reads as null. The daemon fills it while the main camera is connected and a connected switch carries the same serial after `_SN_` in its UniqueId and a port named `DewHeater` (`ZWO_SN_x` with `ZWO_DEW_SN_x`, the ToupTek family's `TOUPTEK_THERMALSW_SN_x`, `PLAYERONE_SW_SN_x`). A missing serial on either side, no such port, a switch that is not connected, or a failed switch-list read gives null; the status itself never fails for it. Only the main camera is paired, never the guide camera, and never by name or order. The level, range and write permission stay on the switch list, and a write goes through `POST /equipment/switch/{id}/value`; the camera adds no write path.

**Reasoning:** the heater is a separate Switch device and nothing said which camera it belongs to. Pairing by serial does not depend on the order devices were found in.

**Spec ref:** `Services/DewHeaterPairing.cs`, `Endpoints/EquipmentEndpoints.cs` (`GetCameraWithHeaterAsync`), `OpenAstroAra.Test/DewHeaterPairingTest.cs`

**Related:** #1651, #1065

---

## 2026-11-11 — Adding devices to a fresh AlpacaBridge: `GET /equipment/alpacabridge/catalog`, `GET …/cameras`, `POST …/devices` (#1580)

**Endpoint(s) or area:** new `GET /api/v1/equipment/alpacabridge/catalog?host=&port=`, `GET /api/v1/equipment/alpacabridge/cameras?host=&port=&vendor=`, `POST /api/v1/equipment/alpacabridge/devices?host=&port=`. The paths follow `guider/alpacadevicenames`: the AlpacaBridge the Connect screen found is `host` and `port` in the query, validated like every management call (non-empty host, a valid host name or IP, port 1..65535; else 400).

**Decision:** the daemon forwards to AlpacaBridge's management routes and keeps nothing. `catalog` → `200 {"device_types":[{"device_type":"camera","drivers":[…]}]}`: the `devicecatalog` drivers whose `available` is true, grouped by device type in the order first seen, each driver exactly as AlpacaBridge sent it (its camelCase keys and field schema are not renamed). A read that fails, a server that is not an AlpacaBridge, or one without the catalog answers `200` with an empty list (best-effort like the name lookup): the client offers Add your devices only when the list is not empty. `cameras` (`vendor` is a short token, else 400) → `200 {"cameras":[{"index","name","serial"}]}`; a vendor with no lister (AlpacaBridge answers a non-zero `ErrorNumber`) or any failure is an empty list. `devices` takes AlpacaBridge's flat body (`vendor`, `deviceType` and the catalog field keys; both required, else 400) and sends it to `configuredevice` unchanged; the body is never logged. Zero `ErrorNumber` → `200 {"message","warning"?}` (AlpacaBridge's text; `warning` is what follows its "Device registered successfully. Warning: " lead). A non-zero `ErrorNumber` → `422` problem `alpacabridge-refused` whose detail is AlpacaBridge's message (the hardware-allowlist refusal comes as its own HTTP 400, same path). No usable answer (refused connection, timeout, a body that is not an Alpaca envelope) → `502` problem `alpacabridge-unconfirmed`: the device may or may not have been added. This is the one place the management client is loud; the reads stay best-effort. The add waits 30 s (the reads keep 3 s); the 1 MiB response cap stays. Basis: registering a device can open the vendor SDK, so ten times the read cap; nothing measured it yet: rig check pending with the board (time a ZWO camera add on the Pi's AlpacaBridge). Secrets: values of top-level body keys that sound secret (password, key, secret, token, psk) are blanked out of AlpacaBridge's message before it reaches a problem, and nothing about the body reaches a log. `POST …/devices` is a mutating `/api/v1` route, so a stale `X-Ara-Session` is refused with 409 by the existing guard.

**Reasoning:** a fresh rig has an AlpacaBridge with no devices and Retry detection cannot help; the add must be an Ara route because the client cannot always reach the bridge and Ara keeps no device facts. The attached camera fills `serialNumber` and `cameraName` (what AlpacaBridge's own web picker saves: the entry then binds by serial), not `cameraIndex`.

**Spec ref:** `Endpoints/AlpacaBridgeSetupEndpoints.cs`, `Services/AlpacaManagementClient.Setup.cs`, `Contracts/EquipmentDtos.cs` (`AlpacaBridgeCatalogResponseDto`, `AlpacaBridgeCamerasResponseDto`, `AlpacaBridgeAddDeviceResponseDto`); tests `AlpacaBridgeSetupTest`; mock `design/preview/alpacabridge-add-devices-mock.html`.

---

## 2026-11-17 — `GET /equipment/guider/alpacadevicenames` answers `serials` (#1580)

**Endpoint(s) or area:** `GET /api/v1/equipment/guider/alpacadevicenames?host=&port=` (one optional field on the existing response).

**Decision:** the response gains `serials`: `"type/N"` → the device's serial number (additive, like `unique_ids`; entries the server reports no serial for are left out; absent on an older daemon, which the client reads as no serials). The daemon takes it from the entry's `Config.serialNumber` that an OpenAstro AlpacaBridge already reports.

**Reasoning:** the add-your-devices list hides a camera the bridge already offers. Two cameras of one model have the same name, so the name cannot tell them apart; the serial can. The client falls back to the name only when either side has no serial.

**Spec ref:** `Contracts/EquipmentDtos.cs` (`AlpacaDeviceNamesResponseDto.Serials`), `Services/AlpacaManagementClient.cs` (`SerialsOf`), `Endpoints/EquipmentEndpoints.cs` (`GetAlpacaDeviceNamesAsync`); test `AlpacaManagementClientTest`.

---

## 2026-11-11 — Preference sync through the rig: `PATCH /client-settings`, `profile.updated`, `client_settings.updated` (#1644)

**Endpoint(s) or area:** `PATCH /api/v1/client-settings`; WebSocket `profile.updated`, `client_settings.updated`.

**Decision:** the existing `ClientSettingsDto` resource gains a top-level-key merge on the same path, as `PATCH` with the same `{ "settings": { ... } }` body as the `PUT`. Only the given top-level keys change (the value is replaced whole, not deep-merged); a key whose value is JSON `null` is removed; every other key is left alone. It takes the same write gate as the `PUT`, applies the object-only check (400) and the 256 KiB cap (400) to the merged result, writes with the same temp-file rename, and answers 200 with the stored object and its new `updated_utc`. The `PUT` is unchanged. The two WebSocket events are documented in the WebSocket section above; neither carries the preference values, only what changed. `profile.updated` is published by an endpoint filter on the whole `/api/v1/profile` group, so a section added later is covered without extra code; daemon-internal profile writes (calibration stamps, the connect-time identity record) are not client edits and publish nothing (superseded in part by #1807 slice B: a daemon write of a section a client can load does publish, see the #1807 entry of 2026-11-20).

**Reasoning:** two devices saving different keys at the same moment must both land, which a read-modify-replace on the client cannot guarantee. The merge runs inside the daemon's write gate. The `updated_utc` in the event lets the device that wrote skip the refetch of its own save. An older daemon answers 404/405 to the `PATCH` and sends neither event; the client then keeps its preferences local.

**Spec ref:** `Services/ClientSettingsService.MergeAsync`, `Endpoints/ClientSettingsEndpoints.cs`, `Endpoints/ProfileEndpoints.PublishUpdatedAfterWrite`, `Contracts/WsEvents/WsEventCatalog.cs`

---

## 2026-11-11 — Tonight's progress: `last_result` on the polar-align state, `permanent_pier` on the site block (#1565)

**Endpoint(s) or area:** `GET /api/v1/equipment/polaralign/status` (`PolarAlignStateDto`); `GET`/`PUT /api/v1/profile/site` (`SiteSettingsDto`).

**Decision:** `PolarAlignStateDto` gains `last_result { error_arcmin, completed_utc }`, null until a routine has stopped or finished with an error reading. The daemon keeps it in memory for the boot, so a restart clears it (a restart may mean the mount moved); a new run does not clear it, and a routine that never produced an error reading (a stop before the first solve, a failed run) or was stopped in the rough stage (its readout is not an alignment; Done there is accepted only inside the profile tolerance, #1875, and then records it) leaves the previous value. `SiteSettingsDto` gains `permanent_pier` (bool, default false, so an older `profile.json` and an older client body still parse; an older client's PUT carries no `permanent_pier`, so it writes false: the PUT is a plain replace, as for any defaulted field a client predates). The focus half of #1565 needs no daemon change: `GET /api/v1/autofocus/state` already carries the last run's state and `completed_utc`.

**Reasoning:** the client decides "polar aligned tonight" and "focused tonight" from the rig instead of its own memory, so a relaunch or a second device agrees. The observing-night rule (local date minus 12 h) stays in the client, which knows the user's clock. `permanent_pier` follows the profile so every device and a reinstall skip polar alignment on an observatory mount.

**Spec ref:** `Services/PolarAlignService.RecordLastResultLocked`, `Contracts/EquipmentDtos.cs` (`PolarAlignLastResultDto`), `Contracts/ProfileDtos.cs` (`SiteSettingsDto.PermanentPier`)

---

## 2026-11-13 — Focuser zero and travel calibration through the driver's actions: `POST /equipment/focuser/setzero`, `…/calibrate`, `…/calibrate/abort`; `can_set_zero`, `can_calibrate`, `calibration` (#1856)

**Endpoint(s) or area:** new `POST /api/v1/equipment/focuser/setzero`, `POST …/calibrate` and `POST …/calibrate/abort` (202 + `OperationAcceptedDto`); `FocuserCapabilitiesDto` gains `can_set_zero` and `can_calibrate`; `FocuserStateDto` gains `calibration`.

**Decision:** ASCOM's focuser interface has no zero and no calibration, so each route calls one Alpaca driver action: `SetZero`, `Calibrate` and `AbortCalibration`. The daemon reads the driver's `SupportedActions` on connect, matching names case-insensitively. `can_set_zero` is true when the focuser is absolute and lists `SetZero` (a relative focuser has no position to zero). `can_calibrate` is true when the driver lists both `Calibrate` and `CalibrationStatus`. `calibration` comes from `CalibrationStatus` on each refresh, only on a focuser that can calibrate: `calibrating`, `calibrated` or `not_calibrated`; null when it cannot calibrate or the driver answers something else or fails. A route answers 409 Problem with the reason in `detail` when the focuser is not connected, when the driver does not offer the action, or, for `setzero` and `calibrate` only, while the focuser moves, compensates backlash or calibrates. `calibrate/abort` is never refused for motion. A zero clears the daemon's backlash offset. The full UI's Focuser panel and Simple Mode's Focuser window in Settings both offer the two actions.

**Reasoning:** the actions are named by the drivers that offer them (AlpacaBridge's ZWO EAF, ToupTek AAF and iOptron iEAF for `SetZero`, the Celestron focuser for the calibration set), and ADR 0003 rule 2 says to decide from what the driver lists, not its name. The refusals are state conflicts the client can explain, so they are 409, not 500. A zero or a calibration in the middle of a move would leave the reported position meaningless, so those wait; a stop must always get through.

**Spec ref:** `FocuserService.SetZeroAsync`, `FocuserService.CalibrateAsync`, `FocuserService.AbortCalibrationAsync`, `FocuserService.ReadCalibration`, `EquipmentEndpoints.FocuserActionAsync`, `Contracts/EquipmentDtos.cs` (`FocuserCapabilitiesDto`, `FocuserStateDto`)

---

## 2026-11-16 — The rig's Polar Aligner: `GET /equipment/polaraligner`, `POST …/stop`, `POST …/home`; `stop_polar_aligner` (#1972, #1973)

**Endpoint(s) or area:** new `GET /api/v1/equipment/polaraligner` (`PolarAlignerDto`, `PolarAlignerCapabilitiesDto`), new `POST /api/v1/equipment/polaraligner/stop` and `POST …/home` (202 + `OperationAcceptedDto`, `Idempotency-Key` header); `POST /api/v1/server/emergency-stop` `failed_rungs` gains `stop_polar_aligner`.

**Decision:** a Polar Aligner is a connected Alpaca Switch, or the connected Telescope, whose driver lists `PolarAlign:Info` in `SupportedActions` (any letter case; read once per connect). No profile field names it: a Switch wins over the mount, and among several such switches the lowest `UniqueId` (ordinal) wins. `GET` always answers 200: `present` false (every other field null or false) when no connected device offers the actions. Otherwise `carrier` is `switch` or `mount`, `device_id` the switch's UniqueId (null for the mount), `name` the device's name. `capabilities` comes from `PolarAlign:Info` (read once per connection; null when it fails); `drivable` is true when Info lists tier 1 and describes both axes; `not_drivable_reason` is `device_runs_its_own_loop` (tier 2 only, or an axis missing), `info_unreadable`, or null. `moving`, `azimuth_position_arcmin`, `altitude_position_arcmin`, `azimuth_fault` and `altitude_fault` come from `PolarAlign:Status` on each `GET`; a position or fault the device does not report, or a Status that fails, is null, never 0. A device failure never fails the `GET`. `stop` sends `PolarAlign:Stop` (both axes), `home` sends `PolarAlign:Home`; each answers 409 Problem with the reason in `detail` when no Polar Aligner is connected (`no Polar Aligner is connected`) or when the device refuses, carrying the device's text. The emergency stop sends Stop to a present Polar Aligner right after its first mount stop; a failure adds `stop_polar_aligner` to `failed_rungs`, and no Polar Aligner is not a failure.

**Reasoning:** the interim PolarAlign actions are the only interface such devices offer until the ASCOM aligner interface is final, and ADR 0003 rule 2 says to decide from what the driver lists, not its name. A separate base is added for this job on purpose, so it wins over a mount that also offers the actions. The refusals follow the focuser action routes (409 Problem) while ADR 0022 is only proposed.

**Spec ref:** `Services/PolarAlignerService.cs` (`PolarAlignerService`, `IPolarAlignerService`), `Services/PolarAligner.cs` (`PolarAlignActionsAligner`), `SwitchService.ActionsFor`, `TelescopeService.MountActions`, `EmergencyStopService`, `EquipmentEndpoints` (`/polaraligner`), `Contracts/EquipmentDtos.cs` (`PolarAlignerDto`, `PolarAlignerCapabilitiesDto`)

**Related:** #1971, #1972, #1973

---

## 2026-11-16 — The Polar Align Routine drives the Polar Aligner: `correction` in the status, `polar_align.correction`, stop and resume, `polar_aligner_auto` (#1974, #1975)

**Endpoint(s) or area:** `GET /api/v1/equipment/polaralign/status` gains `correction` (`PolarAlignCorrectionDto`, null when the rig has no Polar Aligner Ara can drive or the run has not measured yet); new WebSocket event `polar_align.correction` with the same object on every change; new `POST /api/v1/equipment/polaralign/correction/stop` and `POST …/correction/resume` (202 + `OperationAcceptedDto`, 409 Problem when there is nothing to stop or resume); `GET/PUT /api/v1/profile/polar-align` gains `polar_aligner_auto` (default true).

**Decision:** in the live stages the routine turns the Polar Aligner after each measurement: one test move on each axis (+2′, or a quarter of the device's smaller `maxMove` when that is less, never under its resolution), then 80 % of the offset per axis, clamped to the device's `maxMove`, until the total error is inside `target_tolerance_arcmin`. `state` is `manual` (`polar_aligner_auto` off), `probing` / `moving` (with `axis` `azimuth` | `altitude` and the signed `arcmin` sent), `settling` (`seconds`), `measuring`, `converged` or `stopped` (`cause`, and `device_text` / `axis` when there is one); `moves` counts the moves sent. `arcmin` is in the wire convention: positive azimuth increases the azimuth coordinate (clockwise seen from above), positive altitude raises the axis. `cause` is one of `wrong_direction`, `wrong_scale`, `device_fault`, `not_calibrated`, `device_refused`, `diverging`, `timeout`, `link_lost`, `mount_tracking`, `mount_slewing`, `stopped_by_user`. A stop sends the device Stop and leaves the routine running by hand; `converged` does not end the routine, and the motors correct again when the error leaves the tolerance. The `polar_alignments` table gains `aligner_moves` (NULL on older rows).

**Reasoning:** the client must tell a stopped move from a finished one, and say why in the device's own words. The numbers follow N.I.N.A.'s Polar Alignment Correctors plugin, so one device behaves the same under both clients: a +2′ test move, 80 % per correction, and a test move that must land between half and twice the size asked. For an axis whose `Info` does not set `backlashHandled` (absent reads as false) the floor is one fifth, not one half, because such a device loses part of a first move to its own backlash. A stop that comes from outside the routine (the Emergency Stop, `POST /equipment/polaraligner/stop`) marks the run `stopped_by_user` before the device is stopped, and `POST /equipment/polaraligner/home` answers 409 while a routine drives the Polar Aligner, from its first measurement with one until it is stopped (resting inside the tolerance counts, with or without a move made: the motors would correct the Home move away). The cause is a token in the state, not a Refusal: ADR 0022 has no code yet, and a stop is not a refused command.

**Spec ref:** `Services/PolarAlignService.Aligner.cs`, `PolarAlignGeometry.AlignerMove`, `Contracts/EquipmentDtos.cs` (`PolarAlignCorrectionDto`), `Contracts/WsEvents/WsEventCatalog.cs` (`PolarAlignCorrection`), `Contracts/ProfileDtos.cs` (`PolarAlignSettingsDto.PolarAlignerAuto`), `EquipmentEndpoints` (`/polaralign/correction/*`)

**Related:** #1971, #1974, #1975

## 2026-11-16 — Guider faults use `equipment.fault` and `equipment.fault_action_taken`; `guider.fault_action_taken` retired (#1588)

**Endpoint(s) or area:** WebSocket events `equipment.fault`, `equipment.fault_action_taken`; retired WS event `guider.fault_action_taken`.

**Decision:** the three guider faults (link down, guide camera dropped, guide star lost for more than the 30 s grace) now publish `equipment.fault` with `device_type: "guider"`, `device_name: "PHD2"` and `details` `link_down` | `equipment_disconnected` | `star_lost` (`kind` `disconnected` for the first two, `tracking_lost` for the third). The reaction is `equipment.fault_action_taken` with the shared verbs: `sequence_paused` (`paused_runs`), `recovered`, `gave_up` (abort: `terminal: "abort"`, `aborted_runs`, no park), `released`, `notify_only`, and a new `skipped` (`skipped_runs`) for `on_guider_lost: skip_target`. `guider.fault_action_taken` is removed from the catalog: its `auto_resume` and `runs_affected` payloads have no replacement; the resume shows as `recovered`. The `on_guider_lost` profile tokens do not change. A repeat guide-camera drop while the first fault is open adds nothing; a drop after the recovery is a new fault with its own fault-log row, and a flapping camera now starts a new episode per drop.

**Reasoning:** the guider had its own reaction and event, so its faults never reached the client's fault panel and chips, and "pause the runs, then resume the ones I paused" existed twice. Nothing in the client reads the retired event, so there is no compatibility shim.

**Spec ref:** `Services/FaultPolicyMatrix.cs` (`ResolveGuider`), `Services/FaultReactionService.cs` (`RunGuiderEpisodeAsync`), `Services/EquipmentFaultHub.cs` (`SubscribeRecovered`), `Services/GuiderService.cs` (`PublishFaultLocked`), `Contracts/WsEvents/WsEventCatalog.cs`

**Related:** #1588

---

## 2026-11-17 — Polar Corrector is renamed Polar Aligner; the Routine's side is the correction (#1971)

**Endpoint(s) or area:** `/api/v1/equipment/polarcorrector` (and `/stop`, `/home`) is `/api/v1/equipment/polaraligner`; `PolarCorrectorDto` and `PolarCorrectorCapabilitiesDto` are `PolarAlignerDto` and `PolarAlignerCapabilitiesDto`. `POST /api/v1/equipment/polaralign/corrector/stop|resume` is `POST /api/v1/equipment/polaralign/correction/stop|resume`; the status field `corrector` (`PolarAlignCorrectorDto`) is `correction` (`PolarAlignCorrectionDto`); the WebSocket event `polar_align.corrector` is `polar_align.correction`. The `failed_rungs` value `stop_polar_corrector` is `stop_polar_aligner`. The profile field `polar_align.polar_corrector_auto` is `polar_align.polar_aligner_auto`.

**Decision:** the device takes ASCOM's name. The draft interface `IAlignerV1` (Alpaca device type `aligner`) calls a polar device an Aligner whose `AlignerPurpose` is `PolarAligner`. What the Routine does with the device is the correction, so the Routine's routes, status field and event say `correction` and do not read "polar align aligner". The old routes, the old event name and the old profile key are dropped with no alias. A profile saved with `polar_corrector_auto: false` reads as the default (on). The two entries of 2026-11-16 above are rewritten with the new names, because the old names never shipped.

**Reasoning:** one name for the device in Ara, in the standard and on a driver's page. The old names existed on master for two days and in no release, so an alias would be code kept for nobody. The cost is that a daemon and an app built from master on 2026-11-16 or 2026-11-17 must be updated together; the changelog says so.

**Spec ref:** `Endpoints/EquipmentEndpoints.cs`, `Contracts/EquipmentDtos.cs`, `Contracts/ProfileDtos.cs`, `Contracts/WsEvents/WsEventCatalog.cs`, `Services/EmergencyStopService.cs`, `openapi.yaml`, `ws-events.json`

**Related:** #1971, #1988, #1989

---

## 2026-11-17 — Restart when idle says queued or restarting, and can be cancelled: `status`, `DELETE /server/restart-on-idle`, `pending_restart`, `server.restart_cancelled` (#1951)

**Endpoint(s) or area:** the 202 body of `POST /api/v1/server/restart-on-idle` becomes `RestartOnIdleAcceptedDto` (the `OperationAcceptedDto` fields plus `status`); new `DELETE /api/v1/server/restart-on-idle` (204, no body); `GET /api/v1/server/state` fills `pending_restart` while a request is queued; new WebSocket event `server.restart_cancelled` `{ reason, cancelled_utc }`.

**Decision:** `status` is `queued` when anything on Rig Occupancy holds the restart (#2141: a sequence run that is not terminal, so running, paused or aborting; a polar alignment; a centering), and `restarting` when nothing is on and the 2 s notice (`server.restart_imminent`) starts at once. A second POST while a request is pending reports the current state, not the first answer: `queued` while the request can still be cancelled, otherwise `restarting` (the notice has started; if a run starts inside the 2 s notice, the daemon waits for that run too, with `pending_restart` null and `DELETE` a no-op, and a repeat POST still answers `restarting`). While a request is queued, `pending_restart` carries its `reason`, `requested_utc` and `restart_on_idle: true` (`planned_at_utc` null); it goes back to null when the request is cancelled or its notice starts. `DELETE` cancels a queued request: the daemon does not restart when the run ends, `pending_restart` goes null and `server.restart_cancelled` is published once with the request's `reason`. With nothing queued, or once the 2 s notice has started, `DELETE` answers 204 and does nothing. "Restart now", its notice and the exit code 75 restart (ADR 0017) do not change.

**Reasoning:** the client must tell the operator whether the daemon is about to go away or is waiting for the run, and offer a way back from a request made by mistake. A cancel with nothing to cancel has already reached the state the caller wants, so it is a 204 no-op, not a 404 or 409: two clients that both press Cancel both succeed. Once the notice has started, clients are already told the daemon is going, so the cancel no longer applies. A paused run holds the restart because the operator may resume it (maintainer decision on #1865: any state that is not terminal), and an aborting run still saves its last frames, so it holds the restart until it is terminal.

**Spec ref:** `Services/PlaceholderServerStateService.cs` (`RestartOnIdleAsync`, `CancelRestartOnIdleAsync`, `RunIsOn`), `Services/RigOccupancy.cs` (`Holder`, `RegisterSession`), `ISequencerService.HasUnfinishedRun`, `Contracts/ServerStateDtos.cs` (`RestartOnIdleAcceptedDto`, `PendingRestartDto`), `Contracts/WsEvents/WsEventCatalog.cs` (`ServerRestartCancelled`), `ServerStateEndpoints` (`/restart-on-idle`)

**Related:** #1865, #1951

---

## 2026-11-18 — #1847 A declined request carries a declared `reason` (ADR 0022)

**Endpoint(s) or area:** the pointing endpoints: `POST /api/v1/platesolve/verify-position` (Check), `POST /api/v1/platesolve/frames/{id}/solve` and `POST /api/v1/platesolve/center`. First slice of #1847; the other endpoint families follow.

**Decision:** when the rig declines a request for a reason the user can fix, the answer is a 409 or 422 `application/problem+json` whose `reason` is one of the `RefusalReason` values in `openapi.yaml`, `title` is a human sentence (never a token) and `detail` is the English sentence. This slice declares eight causes: `no-camera`, `camera-busy`, `gate-stuck`, `storage-unavailable`, `center-in-progress` (`job_id`), `optics-not-set` (`missing`: a list of `focal_length` and `pixel_size`), `solver-not-set` and `capture-settings-unsupported`. Later entries add `end-night-in-progress` (#1901), `below-horizon-limit` (#2044), `mount-position-unverified` (#2070), `counterweight-limit` (#2073) and `mount-clock-not-verified` (#2071). Modules throw `RefusalException` with a cause from `RefusalCatalog`; `RefusalHandler` is the one place that writes the answer. Check's other `InvalidOperationException` is no longer a 409 `no-camera`: it is a 500, as for every other route. A 422 on Check and on solving a frame now carries a `reason`; Center's 409 moves its token from `title` (`center_in_progress`) to `reason` (`center-in-progress`). Supersedes the 409 part of 2026-11-11 #1786 and the 409 shape of 2026-10-06 #1149.

**Reasoning:** a client that picks the fix from the words of `detail` or from the status breaks when the sentence changes, and it cannot word the answer in the user's language. A declared token the app maps to its own sentence (English and pt-BR) and its own fix button does not. The old catch-all turned any fault of the capture path into "no camera", so a daemon bug sent the person to Camera settings; only a camera that is not there says `no-camera`. An app newer than the daemon gets a 422 without `reason`: it shows the daemon's `detail` and offers no fix button, since it does not guess one.

**Spec ref:** `Contracts/Refusals/` (`RefusalCatalog`, `RefusalException`), `Endpoints/RefusalHandler.cs`, `Program.cs` (the `RefusalReason` schema), `Services/VerifyPositionService.cs`, `Services/PlateSolveService.cs`. Tests `RefusalHandlerTest`, `PointingRefusalTest`; client `lib/services/refusal.dart`.

**Related:** #1847, #1786, #1149, ADR 0022

---

## 2026-11-17 — Focus settings tiers: `GET /autofocus/step-size`; Bahtinov and guide focus take the AF exposure when `exposure_sec` is omitted (#1544)

**Endpoint(s) or area:** new `GET /api/v1/autofocus/step-size`; `exposure_sec` is now optional on `POST /api/v1/bahtinov-focus/start` and `POST /api/v1/equipment/guider/focus/start`.

**Decision:** `step-size` answers `AutofocusStepSizeDto { step_size, source }`, the step the next sweep would use and where it comes from (`manual` | `measured` | `cfz` | `default`), from the same resolver the sweep runs, so Settings can show the derived value next to the stored override. A focuser that reports no step size, or that cannot be read, only leaves the `cfz` rung out. A start request that omits `exposure_sec` (or sends null) uses the profile's `autofocus.exposure_seconds`, clamped to the readout's range (0.05-30 s); with no profile value the old defaults hold (Bahtinov 1 s, guide focus 2 s). A request that sends `exposure_sec` is unchanged, including its 400 for a value out of range. The schema loses its `default` for `exposure_sec` and gains `null`.

**Reasoning:** exposure existed three times on the wire with three defaults; the client no longer repeats a default of its own, and the profile's AF exposure is the one source. An older client that always sends the field behaves as before.

**Spec ref:** `Services/BahtinovFocusService.cs` (`ExposureOrDefault`), `Services/GuideFocusService.cs` (`ExposureOrDefault`), `Endpoints/AutofocusEndpoints.cs` (`StepSizeAsync`), `Contracts/FocusCalibrationDtos.cs` (`AutofocusStepSizeDto`)

**Related:** #1544

## 2026-11-19 — #2070 An unverified mount position: `position_unverified` on the telescope runtime, `POST /equipment/telescope/confirm-home`, the `mount-position-unverified` refusal (ADR 0022)

**Endpoint(s) or area:** `GET /api/v1/equipment/telescope` (`runtime`); `POST /api/v1/equipment/telescope/slew`, `/park`, `/home`, `/moveaxis` (a polar-alignment lease move); new `POST /api/v1/equipment/telescope/confirm-home`; `POST /api/v1/equipment/polaralign/start`; the `RefusalReason` schema in `openapi.yaml`.

**Decision:** the daemon marks a mount's position **unverified** when the first read of a session reports AtHome while still (unless the last pose this process trusted for that device was a trusted Home), when that first read is more than 2° in Dec from the last trusted pose (a pose the mount had settled at; a session that ended mid-move keeps none), or, inside a session, when AtHome turns true or Dec jumps more than 2° between two still ticks with no Ara mount command or slewing tick in the last 30 s. It applies to every mount. The runtime carries `position_unverified` (bool, default false) and `position_unverified_reason` (`at_home_on_connect` | `pose_jump_on_reconnect` | `at_home_without_command` | `pose_jump_in_session`, null when trusted), read from the trust state at request time. While unverified, a slew (not a sync; `force` does not bypass it), a park, a find-home, a polar-alignment start and a polar-alignment lease move answer 409 `application/problem+json` with `reason` `mount-position-unverified` and the extension `position_unverified_reason`; the same check before a session's first position read answers `not_read_yet`. The sequencer's GoTo, Park, Find Home and meridian flip fail the instruction (no op fault); a refused sequencer Park stops tracking first, so the mount is not left tracking unattended. A sync, a pad move with no lease owner, Stop, tracking and Unpark are not checked. `POST /confirm-home` answers 204 and trusts the position again when the mount reports AtHome now; 409 when it is not connected or does not report AtHome. The flag is kept per device id in `{profileDir}/mount-pose.json`, so a reconnect or a daemon restart does not clear it; only `confirm-home` does. The safety and fault reactions stop the mount's motion (AbortSlew, which also ends any axis lease) and its tracking, and post a Critical notification, when their park is refused this way. The eleventh cause of the `RefusalReason` enum (after `below-horizon-limit`).

**Reasoning:** a mount with no absolute encoders (the iOptron HAE29C) wakes from a power loss reporting Home and Dec 90 wherever it points. A GoTo, Park, model Home or polar alignment from that false zero drives the tube to the wrong place (RC51, 2026-11-19: Polar Align slewed below the pole, and Home was a no-op). Parking by a wrong model can hit the tripod, so a refused safety park stops tracking instead. The extension is not named `reason`, which ADR 0022 keeps for the cause. A sensor zero search route waits for the AlpacaBridge contract (open-astro/AlpacaBridge#2420).

**Spec ref:** `Services/MountPoseTrust.cs`, `Services/MountPoseStore.cs`, `Services/TelescopeService.PoseTrust.cs`, `Services/TelescopeService.cs`, `Services/TelescopeService.Mediator.cs`, `Services/PolarAlignService.cs`, `Services/SafetyReactionService.cs`, `Services/FaultReactionService.cs`, `Endpoints/EquipmentEndpoints.cs` (`ConfirmHomeAsync`), `Contracts/EquipmentDtos.cs`, `Contracts/Refusals/RefusalCatalog.cs`. Tests `MountPoseTrustTest`, `TelescopePositionUnverifiedTest`, `RefusalHandlerTest`; client `lib/services/refusal.dart`, `lib/models/mount_status.dart`.

**Related:** #2070, #2048, #2044, #1847, ADR 0022, open-astro/AlpacaBridge#2420

---

## 2026-11-19 — #2044 `POST /equipment/telescope/slew` refuses a target under the horizon: `below-horizon-limit` (ADR 0022)

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/slew`; the `RefusalReason` schema in `openapi.yaml`.

**Decision:** a slew (not a sync) whose target is under the profile's horizon limit at its azimuth (the flat `default_horizon_altitude_deg`, or the custom skyline when `use_custom_horizon` is on) answers 409 `application/problem+json` with `reason` `below-horizon-limit` and the extensions `altitude_deg` and `limit_deg` (degrees), and dispatches nothing. `force` does not bypass it; it only bypasses `run_in_progress`, which keeps its `title` token. A profile with no site, or a failed altitude transform, lets the slew through. The tenth cause of the `RefusalReason` enum (after `end-night-in-progress`).

**Reasoning:** a GoTo under the horizon points the telescope at the ground or a building. The check is the one the Resume refinement and the re-center already used (`HorizonGuard`), so the three callers cannot disagree about where the horizon is.

**Spec ref:** `Services/HorizonGuard.cs`, `Endpoints/EquipmentEndpoints.cs` (`SlewAsync`), `Contracts/Refusals/RefusalCatalog.cs`. Tests `GotoHorizonRefusalTest`, `RefusalHandlerTest`; client `lib/services/refusal.dart`.

**Related:** #2044, #1847, ADR 0022

## 2026-11-19 — #2071 GoTo, Sync, Center and polar alignment wait for a verified mount clock: `mount-clock-not-verified` (ADR 0022)

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/slew` (GoTo and Sync), `POST /api/v1/platesolve/center`, `POST /api/v1/equipment/polaralign/start`; the `RefusalReason` schema in `openapi.yaml`. Not `POST /equipment/telescope/park` or `/home`.

**Decision:** after a time sync of medium or high trust (`client`, `gps-client`, `gps-mobile`; never `manual`), and on every mount connect, the daemon writes the mount's Alpaca `UTCDate` from the trusted time (a client sync carried on the monotonic clock, so it does not expire after the §31.1 hour; or, while the kernel reports the clock NTP-synchronised by a read-only `adjtimex` with `STA_UNSYNC` clear and `maxerror` within 2 s, the OS clock itself, so no client is needed after a daemon restart; an unsynchronised kernel clock is never trusted), reads it back and checks the mount: its `UTCDate` within 5 s of the synced time, its `SiderealTime` within 5 s of the LST computed for the profile's longitude, and its site within 0.01° of the profile's. A driver that cannot report `UTCDate` (not implemented) is checked by its `SiderealTime`; a `UTCDate` read that fails otherwise is not verified. Until the check passes, a GoTo, a Sync, a Center and a polar-alignment start answer 409 `application/problem+json` with `reason` `mount-clock-not-verified` and the English reason in `detail`, and no motion or sync command reaches the mount (the daemon still writes its `UTCDate`). A REST GoTo or Sync, or a Center, sent while a clock push is running (the app's time sync right after it connects) or a host clock step is about to start one waits for it, up to 10 s, as a sequencer GoTo and the meridian flip do, and is refused only if the check still fails (Center checks before its job is enqueued); when the push it waited for is still waiting for the mount to stop slewing (up to about 3 min) before it sets the clock, `detail` says so ("Ara is waiting for the mount to stop slewing before it sets the mount's clock"). A move that waited is sent to the mount connected when the wait ends, never to a device a reconnect replaced. Each mount reading (`UTCDate`, `SiderealTime`) is compared with the trusted time taken right after it, so a slow bridge's round trips do not count as clock error. With no NTP, a client sync over a day old stays trusted but logs a warning once a day (it drifts with the Pi's clock); the wire does not change. The check is redone after a host clock step and, while it fails, about every 30 s. Park and Home are never gated (iOptron parks and homes by encoder; safety, fault, shutdown and end-of-run parks must not wait). The nineteenth cause of the `RefusalReason` enum (after `first-flip-not-completed`).

**Reasoning:** on RC51 (2026-11-19) the §31 push corrected the Pi's clock but the mount kept the time AlpacaBridge had sent from the stale boot clock, 6 h 48 min slow, and every GoTo pointed about 102° away from its target. Ara's own sky maths read the Pi's clock, so nothing showed the error.

**Spec ref:** `Services/TelescopeService.MountClock.cs`, `Services/TimeSyncService.cs` (`TryGetTrustedUtc`, `TrustedTimeApplied`), `Services/KernelClockSync.cs` (`IKernelClockSyncProbe`), `Contracts/Refusals/RefusalCatalog.cs`. Tests `MountClockTest`, `MountClockRefusalTest`, `RefusalHandlerTest`; client `lib/services/refusal.dart`.

**Related:** #2071, AlpacaBridge#2419, AlpacaBridge#2426, #1847, ADR 0022

---

## 2026-11-19 — End Night: `POST /end-night`, `GET /end-night/status`, `POST /end-night/cancel`, `end_night.progress`, the `end-night-in-progress` refusal (#1901)

**Endpoint(s) or area:** new `POST /api/v1/end-night` (optional body `{ "shut_down_host": true }`, default true), `GET /api/v1/end-night/status`, `POST /api/v1/end-night/cancel`; the WebSocket event `end_night.progress`; the refusal cause `end-night-in-progress`. Slice 2 of #1901 (the daemon); the client screen is slice 3.

**Decision:** one daemon action puts the rig to bed, open to both shells (ADR 0009 rule 1; it is not the Emergency Stop, rule 3). `POST` answers 202 at once with the status and the work runs on the daemon, so it carries on when the client sleeps or loses Wi-Fi. The steps run in this order, each best-effort and reported as `pending`, `running`, `done`, `skipped` or `failed`: `abort_run` (aborts the run and waits, up to 60 s, until it is over so its Session is closed), `stop_guiding`, `find_home` (skipped when the mount cannot), `park_mount` (or stops tracking when it cannot park), `disconnect_devices` (filter wheel, focuser, rotator, flat panel), `warm_cooler` (the profile's ramp, then cooler off), `disconnect_camera` (skipped when the warm-up failed or was cancelled, so the cooler is never left cold), `notify`, `host_poweroff`. A step that fails is reported and the next safe step still runs. `host_poweroff` runs only when `shut_down_host` is true and the mount ended parked or with tracking off; otherwise it is `skipped` with the reason in `detail`. The status is `{ state: idle|running|complete|cancelled, run_id, shut_down_host, cancellable, cancel_requested, started_utc, ended_utc, steps: [{ id, state, detail }] }`; the finished action stays readable until the next start. `POST /end-night/cancel` stops at the next step boundary (the warm-up checks between ramp steps) and leaves the rig as it is; it never takes effect once the poweroff has started, and then `cancellable` is false. It answers 200 with the status whether or not it took effect. `end_night.progress` carries `{ run_id, state, step, step_state, detail, cancellable }`: `step` and `step_state` are null for a change of the action itself (started, cancel requested, finished). A `POST` while one runs is a 409 with `reason: end-night-in-progress`.

**Reasoning:** the unattended shutdown already parks, warms and disconnects in the right order; End Night shares those steps (`ShutdownLadder`) and adds the run abort, Find Home and the host poweroff around them. The poweroff is the one step that cannot be undone and the Pi then cannot report back, so it is gated on the mount being safe, and a cancel cannot race it. The poweroff goes through the root-owned helper unit of ADR 0017; the daemon only asks systemd to start it.

**Spec ref:** #1901; `Services/EndNightService.cs`, `Services/ShutdownLadder.cs`, `Endpoints/EndNightEndpoints.cs`, `Contracts/EndNightDtos.cs`, `Contracts/WsEvents/EndNightPayloads.cs`, `Services/HostPoweroffStarter.cs`. Tests `EndNightServiceTest`.

**Related:** #1901, ADR 0009, ADR 0014, ADR 0017, ADR 0022, ADR 0023

## 2026-11-19 — Polar Align measures with motor moves and three solves, NINA's way (10° steps)

**Endpoint(s) or area:** `GET`/`PUT /api/v1/profile/polar-align` (`seed_rotation_deg` default 60 → 10, clamp 5–120 → 5–30; `check_step_deg` default 30 → 10, clamp 5–60 → 5–30); the routine's mount moves; `polar_align.error` reason `slew_failed` now also covers a refused, stalled or slow motor move.

**Decision:** every stage measures the axis like NINA's Three Point Polar Alignment: three solves with two RA turns of `seed_rotation_deg` between them, and the axis is the circle through the three pointings (`FitAxisThreePoint`, each solve moved to the last one's time by the Earth's turn) — the two-point seed and its reliance on the mount's reported turn are gone; the reported turns only cross-check the fit (over 5° of mismatch, or a cone under 1°, is `axis_fit_failed`). No polar-align move is a GoTo any more: the tilt off the axis from Home (5° of Dec) runs the Dec motor and each turn runs the RA motor (`MoveAxis` through the telescope service's leased call, 3 s lease), at 3°/s and 1°/s for the last 3°, stopping on the mount's position read from the driver four times a second; a motor that runs the wrong way is reversed once; a move with no progress for 10 s, or over three times its slow travel time, stops and fails as `slew_failed`; Stop and every failure stop the motor. The return to the start pointing with `use_home: false` is still a GoTo.

**Reasoning:** 2026-11-18 on the HAE29C: the GoTo from Home to Dec 85° left the firmware to choose a pier side, and at Home (hour angle ±6 h) it chose the flipped one and swung the tube toward the pier; the 60° seed turn had already been reported as alarming. A motor move cannot change the pier side, and 10° steps keep the whole measurement within 20° of Home. Rig check the same night: rough, fine and Verify ran with motor moves only; Verify read 0.79′ against the fine stage's 0.18′ (0.62′ apart), turn mismatch 0.05°.

**Spec ref:** `Services/PolarAlignService.Stages.cs` (`MoveAxisUntilAsync`, `FitThreePointOrFail`), `Services/PolarAlignSettingsClamp.cs`, `Services/TelescopeService.cs` (`ReadPointingNowAsync`), `Contracts/ProfileDtos.cs`.

**Related:** #2048, #1591, #1735

## 2026-11-19 — Polar Align: Stop really stops (`stop_tracking_when_done`, `mount_did_not_stop`) (#2048)

**Endpoint(s) or area:** `POST /api/v1/equipment/polaralign/stop`, `…/complete` and the sequence start that ends an open routine; `GET`/`PUT /api/v1/profile/polar-align` gains `stop_tracking_when_done` (default true); `polar_align.error` gains the reason `mount_did_not_stop`; the telescope service's `slew_aborted` event carries `reason: "polar_align"` for the routine's own aborts.

**Decision:** every end of a run stops the mount, as NINA's Three Point Polar Alignment does (the catch around its RA move sends MoveAxis 0, its finally turns tracking off when StopTrackingWhenDone). Stop, Done and a sequence start first fence the run and send AbortSlew (when a GoTo, Find Home or motor move of the routine may run, or the mount reads as slewing), then MoveAxis 0 on the primary and the secondary axis, before they wait on any lock or on the run; the request's token never cancels it. The run's own exit — after a Stop, a failure, an exception, a time bound, the check's verdict or daemon shutdown — sends the same round again and confirms it: two fresh reads at least 1.5 s apart, at least 2 s after the last command, both not slewing and under 0.05° apart in RA and in Dec; the round is resent at +2 s and +4 s, and the confirmation gives up after 8 s. Then tracking: with `stop_tracking_when_done` on (the default) it is turned off; off, a run that finished (Done, or the check's verdict) gives back the tracking it found, unless another caller wrote tracking since the run's last write; a Stop, a sequence start, a failure or a shutdown always leaves tracking off. Guiding that takes the guide camera from an open run (custody, ADR 0016) ends it like a Stop but hands back the tracking the run found, whatever the setting: guiding needs tracking, and unlike a GoTo it does not turn it on. A failure is reported after the stop, with "Ara could not confirm that the mount stopped. Cut the mount's power now, …" appended when the stop was not confirmed. A Stop whose stop was not confirmed ends in `failed` with `polar_align.error` `mount_did_not_stop`, never `stopped`. The hand-back (Done with the setting off, or Guiding) applies only to a mount whose stop was confirmed; otherwise tracking goes off. A Stop whose run does not unwind within its grace (stuck in a call that ignores cancellation) confirms the stop itself and turns tracking off at once. An end request (Stop, Done, a sequence start, shutdown) that lands while a `start` still holds the routine's lock (taking the guide camera, stopping the guider's loop) means that `start` launches no run: it answers 409 "polar alignment was stopped while it was starting" and gives the camera back. A motorised unpark is fenced and aborted like the other motion. `stop` and `complete` therefore answer once the stop is confirmed (2 s at least, 47.25 s at most for a stuck run: the daemon's `EndWorstCase`); the app waits up to 60 s for them, which a test checks against that bound.

**Reasoning:** on RC51 (iOptron HAE29C on AlpacaBridge) PA's Stop only cancelled the routine: a Find Home or GoTo under way kept moving, a motor move stopped only in the run's `finally` (or when its lease ran out), and the exit turned tracking back on (the incident log shows `:Q#` then `:ST1#`). A Stop that waited on `_opLock` with the request's token never reached the mount at all once the client gave up.

**Spec ref:** `Services/PolarAlignService.MountStop.cs`, `Services/PolarAlignService.cs` (`EndRoutineAsync`, `RunRoutineAsync`, `FailRoutineAsync`, `Dispose`), `Services/PolarAlignService.Stages.cs` (`BeginMotion` before each Find Home, GoTo and motor move), `Services/TelescopeService.cs` (`AbortSlewAsync(reason)`, `ReadSlewingNowAsync`), `Contracts/ProfileDtos.cs`. NINA: `PolarAlignment/Instructions/PolarAlignment.cs` 791-805 and 1037-1041. Tests `PolarAlignServiceTest.Stop.cs`.

**Related:** #2048

## 2026-11-19 — `CameraStateDto.cooler_not_responding` (#2076)

**Endpoint(s) or area:** `GET /api/v1/equipment/camera` (`runtime`): `CameraStateDto` gains the optional `cooler_not_responding` (bool, default false).

**Decision:** true while a cooler that Ara switched on does not respond. That is: `CoolerOn` has read back off for 1 min without a break, or it reads on at ≤ 1 % power with the sensor more than 2 °C above the set-point and not closing on it for 1 min. An unknown `CoolerOn` read neither sets nor clears it. It clears when the cooler responds, when the cooler is switched off, and on a reconnect. Only Ara's own cooler-on arms it; a cooler that another client or the driver's session restore switched on is not watched. When it is set, the daemon publishes one `value_mismatch` camera fault (notify-only Warning: no retry, pause or escalation) whose details start "Cooler not responding: is the camera's 12 V connected?". The daemon never re-sends the cooler write. A sequencer Cool Camera step whose cooler reads off for 1 min (in the ramp or the wait) fails through the normal step failure with the same hint, instead of after the 10-min regulating bound.

**Reasoning:** 2026-11-19: the Uranus-C Pro had no 12 V. The driver accepted `CoolerOn = true` and read it back false at 0 % power for hours, and nothing told the user. The drift monitor (§42.2) disarms while `CoolerOn` reads off, so it could not see this.

**Spec ref:** `Services/CoolerResponseWatch.cs`, `Services/CameraService.cs` (`ReadTick`, `SetCoolerAsync`), `Services/CameraService.Mediator.cs` (`RegulateTemperatureAsync`), `Contracts/EquipmentDtos.cs`; client `models/camera_status.dart`, `screens/simple/simple_shell.dart`.

**Related:** #2076, #1076, #1210

---

## 2026-11-20 — #2073 `POST /equipment/telescope/slew` turns tracking on first and refuses a counterweight-up target: `counterweight-limit` (ADR 0022)

**Endpoint(s) or area:** `POST /api/v1/equipment/telescope/slew`; the `RefusalReason` schema in `openapi.yaml`.

**Decision:** the daemon turns the mount's tracking on (when it reads off) right before it sends the GoTo, as the sequence GoTo and Park already do; the answer stays 202, and a GoTo the mount then refuses is logged with the driver's reason in the message. A slew (not a sync) to a target below the pole within 2 h of the lower meridian (|hour angle| > 10 h, which is a counterweight angle over #2048's 60° default by the shared model φ = 90° − the distance from the hour angle to the nearer of 0 h and 12 h) answers 409 `application/problem+json` with `reason` `counterweight-limit` and the extensions `hour_angle_hours`, `counterweight_angle_deg` and `limit_deg`, and dispatches nothing. The check runs only on a German equatorial mount: the daemon reads ASCOM `AlignmentMode` once per connect, skips the check for `algAltAz` and `algPolar` (fork), and checks a mount that does not answer (logged once). The horizon check runs first. `force` does not bypass it. A profile with no site lets the slew through. The limit is a constant until #2048 adds its setting. The twelfth cause of the `RefusalReason` enum (after `mount-position-unverified`).

**Reasoning:** on 2026-11-19 four GoTos from the Mount panel were refused by the mount ("SlewToCoordinatesAsync requires Tracking to be true") and Ara showed no reason. Two of them were at hour angle ≈ −11.1 h, counterweight-up on the HAE29C, and only the tracking refusal stopped them. Turning tracking on removes that accidental barrier, so the counterweight check ships with it. Above the pole a target near the meridian also has φ near 90°, but that is the normal meridian-flip zone, so only targets below the pole are checked.

**Spec ref:** `Services/CounterweightGuard.cs`, `Services/TelescopeService.cs` (`SlewInBackground`), `Endpoints/EquipmentEndpoints.cs` (`SlewAsync`), `Contracts/Refusals/RefusalCatalog.cs`. Tests `GotoCounterweightRefusalTest`, `TelescopeRestGotoTrackingTest`, `RefusalHandlerTest`; client `lib/services/refusal.dart`.

**Related:** #2073, #2048, #2070, ADR 0022

---

## 2026-11-20 — #2044 meridian timing on the wire, and a stop before the meridian with the flip off: `telescope.limit_reached`

**Endpoint(s) or area:** profile JSON `safety` (`SafetyPoliciesDto`): `minutes_after_meridian` (int, default 5), `max_minutes_after_meridian` (int, default 10), `stop_before_meridian_min` (int, nullable, default null). WebSocket `telescope.limit_reached` (typed payload `TelescopeLimitReachedPayload`: `limit`, `minutes`, `tracking_stopped`, `runs_paused`, `detail`).

**Decision:** `minutes_after_meridian` and `max_minutes_after_meridian` are NINA's `MinutesAfterMeridian` and `MaxMinutesAfterMeridian`, applied to the flip settings with the existing `meridian_pause_min` (negative values read as 0; minutes-after is clamped to the max). The defaults are NINA's, so an older profile flips as before. A non-zero `meridian_pause_min` still overrides both (a hard stop before the meridian). Simple Mode's "At the meridian" writes one signed value onto the three: N min before → pause N, after 0, max 5; on it → all 0 but max 5; N min after → pause 0, after N, max N + 5. `stop_before_meridian_min` is read only with `meridian_flip_auto` false: the daemon's `MeridianStopWatch` (15 s tick) stops tracking and pauses the active runs once when the mount tracks and its pointing is that many minutes or less before the upper meridian, then publishes `telescope.limit_reached` with `limit` `stop_before_meridian`. It runs with or without a plan (`runs_paused` 0), never slews or parks, re-arms when the pointing leaves the window, and does nothing with the flip on, with null or 0, with no site or with no position. `tracking_stopped` is false when the mount did not take the command.

**Reasoning:** with the flip off nothing but the mount's own firmware stopped a mount tracking into the pier; the flip trigger is inert with AutoFlip off. The two flip-window values existed in the daemon but not on the wire, so the client could not set when a flip runs after the meridian.

**Spec ref:** `Contracts/ProfileDtos.cs`, `Services/StoreBackedProfileService.cs` (`ApplyMeridian`), `Services/MeridianStopWatch.cs`, `Contracts/WsEvents/TelescopePayloads.cs`. Tests `MeridianStopWatchTest`, `StoreBackedProfileServiceMeridianTest`; client `state/settings/safety_policies_state.dart`, `screens/simple/simple_mount_limits.dart`.

**Related:** #2044, #1589, ADR 0014

---

## 2026-11-20 — Polar Align starts like NINA: no Home, no GoTo, a confirm step, parked refused; `use_home` retired (#2048)

**Endpoint(s) or area:** new `GET /api/v1/equipment/polaralign/start-plan?stage=` → `PolarAlignStartPlanDto { stage, direction: east|west, step_deg, turns, max_sweep_deg }`; `POST /api/v1/equipment/polaralign/start` body `PolarAlignStartRequestDto` gains `confirmed` (default false) and `direction`; two Refusal causes, `polar-align-mount-parked` (409) and `polar-align-unconfirmed` (409, with `direction`); `GET`/`PUT /api/v1/profile/polar-align` loses `use_home`; `polar_align.needs_home` is retired from the event catalog, the `homing` state is no longer published, and `waiting_for_home` is always false.

**Decision:** the routine starts from where the user pointed the mount, as NINA's Three Point Polar Alignment started from the current position. No stage calls Find Home, no stage slews (the `use_home: false` return GoTo is gone), and the routine never unparks: a parked mount is the `polar-align-mount-parked` Refusal at `start-plan` and at `start`, before the camera is taken or anything moves. `start` moves the mount only with `confirmed: true` and the `direction` the user was shown; otherwise, or when the direction read from the mount now differs (the hour angle crossed the meridian since), it is the `polar-align-unconfirmed` Refusal, carrying the direction the run would turn now. The first turn's direction is unchanged (away from the meridian, read once at `start`). With no Home between the stages, each later stage keeps its turns inside the band the user confirmed (`max_sweep_deg` = 2 × the larger of `seed_rotation_deg` and `check_step_deg`, or 2 × `check_step_deg` for a check-only start): it turns back over the arc when it then ends no more than 2° past the start, away when it then ends inside the band, and otherwise first turns back to the start with the RA motor; the offset is the hour-angle change since the start. `started` opens in `seeding` (coarse, fine) or `checking` (check). A stored or PUT `use_home` is an unknown member and is ignored.

**Reasoning:** RC51 went into the pier after Find Home: AlpacaBridge answers "at Home" from a fresh status read, and the routine took that as proof of where the mount was. NINA never homes or parks (PolarAlignment.cs 165-169, 510-526) and refuses a parked mount (1108-1110). Ara starts from the user's pose instead of NINA's alt/az GoTo, so the user must see where the RA axis will turn before it does; that GoTo waits for the mount-clock check (#2071), AlpacaBridge #2395 and a pier-side read after the slew.

**Spec ref:** `Services/PolarAlignService.cs` (`MountPreflight`, `PlanStartAsync`, `GetStartPlanAsync`, `StartAsync`), `Services/PolarAlignService.Stages.cs` (`PlanStageTurns`, `TurnDirectionForStageAsync`), `Contracts/Refusals/RefusalCatalog.cs`, `Endpoints/EquipmentEndpoints.cs`; client `lib/widgets/imaging/polar_start_confirm.dart`. Tests `PolarAlignServiceTest.Start.cs`.

**Related:** #2048, #2071, #2073, ADR 0022

## 2026-11-20 — Polar Align bounds the RA sweep across runs from a session origin (#2089)

**Endpoint(s) or area:** `PolarAlignStartPlanDto` gains `new_origin` (bool), `origin_offset_deg` (signed, + east) and `sweep_limit_deg`; `PolarAlignStartRequestDto` gains `new_origin` (default false), which must equal the plan's (else `polar-align-unconfirmed`); new Refusal cause `polar-align-sweep-limit` (409, with `offset_deg` and `limit_deg`) at `start-plan` and `start`.

**Decision:** *(Superseded in part by the 2026-11-20 #2066 fresh-reads entry below: a failed driver read is now `pointing_unknown`, not `mount_fault`.)* with no Home between runs, each Start built its band from wherever the mount was, so "Adjust again" after Verify turned 20 → 40° from the first start. The daemon keeps a sweep origin per mount session: the pose of the first confirmed start after the mount connects. A later start reads its offset from the origin and turns back toward it (within 2° of it, the origin's first direction); a start whose turns would take the RA axis more than 30° from the origin (`MaxSweepFromOriginDeg`; a run whose own band is wider keeps to its band plus 2°) is refused. Only RA differences the mount reports are used, less the Earth's turn over the daemon's monotonic clock — never the mount clock, LST or absolute RA (#2071, #2070). The origin is forgotten when the mount session changes (`ITelescopeService.SessionEpoch`: connect, disconnect, trip), on a daemon restart (it lives in memory), and when the mount moved more than 1° from where Polar Align's own moves last left it beyond what tracking explains (the smaller of the reading's change, still while tracking, and the mechanical turn, still while not: `MovedByOtherMeansDegBetween`), or its pier side changed: the plan then says `new_origin: true`, and the start must carry it, so the user confirms the pose as a new start. The direction at a new origin is unchanged (away from the meridian). Tracking that stays on, or stays off, between runs is not a move by other means (a run that hands tracking back would otherwise lose its origin within minutes and the next run would walk on); tracking switched on or off between runs can read as one, since neither the reading nor the axis then stands still, and the next start is a new origin the user confirms. The offset from the origin still counts the tracking turn, so a start after long tracking may be refused until the mount is slewed back. The confirm step says a continued start's turns go back toward the origin only when they do: within 2° of it they turn the origin's first way. Every reading of a motor move comes from the driver; a failed one stops the motor and fails the run (`mount_fault`), since progress read from the cached (0, 0) placeholder could reverse the motor away from the origin.

**Reasoning:** reviewers of #2089 found the cross-run walk toward the pier. Differences of the mount's own readings hold even when its clock or zero is wrong; the confirm step keeps the user in the loop whenever the origin is reset.

**Spec ref:** `Services/PolarAlignService.SweepOrigin.cs` (`PlanSweep`, `MovedSince`, `MechanicalTurnDeg`), `Services/PolarAlignService.cs` (`PlanStartAsync`, `StartAsync`), `Services/TelescopeService.cs` (`SessionEpoch`); client `lib/widgets/imaging/polar_start_confirm.dart`. Tests `PolarAlignServiceTest.Sweep.cs`.

**Related:** #2048, #2089, #2070, #2071, ADR 0022

## 2026-11-20 — Emergency Stop latches first; any stop from outside ends Polar Align; `polar-align-moving-mount` (#2066, #2067)

**Endpoint(s) or area:** new Refusal cause `polar-align-moving-mount` (409, no parameters) at `POST /equipment/telescope/slew` (not a sync), `/moveaxis` (a non-zero rate), `/home` and `/park`; new `polar_align.error` reason `slew_aborted_externally` (replaces `slew_failed` for a stop from outside); `POST /server/emergency-stop` gains a step 0. Server-internal: `ITelescopeService.FenceMotion(reason)` and `AbortReasonSince(epoch, except)`; `IPolarAlignService.StopForTakeoverAsync(by)` and `IsMovingMount`.

**Decision:** Emergency Stop's first step, before its event and every rung, counts the stop in the mount's `AbortEpoch` with no command to the mount, so a polar alignment between two turns cannot start the next one, or turn tracking on, while a slow rung (the dome, the run abort) holds the ladder. A REST Park and Find Home count as a stop too; a REST GoTo accepted just before a Park, whose move has not gone out yet, is then dropped (the park wins), as one accepted before a Stop Mount already was. A Find Home reads the count at its request, as a Park and a GoTo do, and is dropped (its 202 stands) when a stop lands before the homing goes out. Polar Align reads `AbortEpoch` once, when Start is called (it read it afresh at each motor move, so a Stop Mount during a solve became the next turn's baseline and the turn started after it), and checks it before every motor move, Polar Aligner move and tracking switch-on, after every seed frame and each live frame, between the stages and on every poll of a move. A count it did not raise (its own AbortSlew sends are counted) fails the run with `slew_aborted_externally`, worded by what raised it: Stop Mount (`user_request`), Emergency Stop, the meridian-flip watchdog, a park or a Find Home. After Stop Mount or the watchdog the run's exit stops the mount and confirms it, as on any end. After Emergency Stop, a park or a Find Home the mount is the sender's: the exit sends no stop of its own (an AbortSlew would abort the park) and writes no tracking. Emergency Stop's Polar Align rung, the safety park and the equipment-fault park end a running polar alignment with `StopForTakeoverAsync`: the run's first stop round goes out before any wait, as for Stop, but no confirmation follows (up to 8 s, which held the rung past its 10 s bound and reported it as failed); Emergency Stop then stops the mount and the Polar Aligner again, and the two parks stop the Polar Aligner and then park, each step bounded at 10 s. A leased motor renewal is checked against its abort count before the renewal itself, so a stop that cancels no lease (the latch) is never outlived by a renewal that only extends one; the refused lease runs out and stops the axis. While a live run's motor move is under way, or its stop is not confirmed yet, the telescope endpoints answer a GoTo, a pad move, Home or Park with `polar-align-moving-mount`; a sync, a pad stop and Stop Mount always go through, and a run that ended with its stop unconfirmed no longer blocks them. `openapi.yaml` now declares what `/park`, `/home` and `/moveaxis` answer: 202 (they were listed as 200), and 409 problem (the Refusals above, and the pad's not-connected or unusable-rate 409); `/moveaxis` also its 400.

**Reasoning:** the review of the Polar Align motion-safety cluster (#2048 ARA-2, #2066, #2067) found the one window left where Polar Align could start motion after a stop: the per-move abort reading, Emergency Stop latching only at its first AbortSlew (after up to two 10 s rungs), and the safety park parking around a running routine. Stop still answers after its unwind (no immediate 202), as #2086 chose.

**Spec ref:** `Services/EmergencyStopService.cs` (`FenceMountQuietly`), `Services/TelescopeService.cs` (`FenceMotion`, `AbortReasonSince`, `BumpAbortEpochUnlessAbortedSince`, `MoveAxisAsync`), `Services/PolarAlignService.MountStop.cs` (`ThrowIfStoppedFromOutside`, `StopForTakeoverAsync`, `IsMovingMount`), `Services/SafetyReactionService.cs` and `Services/FaultReactionService.cs` (`StopPolarAlignQuietlyAsync`), `Services/PolarAlignService.Aligner.cs` (`CorrectOnceAsync`), `Endpoints/EquipmentEndpoints.cs` (`ThrowIfPolarAlignMovesTheMount`). Tests `PolarAlignServiceTest.ExternalStop.cs`, `EmergencyStopServiceTest`, `SafetyReactionServiceTest`, `FaultReactionServiceTest`, `TelescopeMoveAxisLeaseTest`, `TelescopeRestGotoTrackingTest`, `EquipmentEndpointsSlewTest`, `EquipmentEndpointsMoveAxisTest`.

**Related:** #2048, #2066, #2067, #2086, #2089, ADR 0022

## 2026-11-20 — Guider fault details `dec_runaway` (#1589)

**Endpoint(s) or area:** WebSocket event `equipment.fault` (`device_type: "guider"`).

**Decision:** a fourth guider `details` value, `dec_runaway`, with `kind` `tracking_lost`. The daemon publishes it when the Dec error grows for 10 guide frames in a row with the Dec pulses at their maximum, and stops guiding. The reaction is the existing guider one (`on_guider_lost`), and its `recovered` comes once guiding is started again and has settled. No new field, event or kind.

**Reasoning:** `details` is free text that the client shows but does not map, so an added value needs no client change.

**Spec ref:** `Services/GuiderService.DecRunaway.cs`, `Services/GuideAxisRunaway.cs`, `Services/FaultReactionService.cs` (`RunGuiderEpisodeAsync`). Tests `GuiderDecRunawayTest`, `GuideAxisRunawayTest`.

**Related:** #1589, #1588, #1716

## 2026-11-20 — A daemon write of a client section publishes `profile.updated`; the first-flip re-arm answers an `ETag` (#1807)

**Endpoint(s) or area:** WebSocket `profile.updated { section }`; `POST /api/v1/profile/safety/first-flip/rearm`.

**Decision:** when the daemon changes a section a client can load (one of the routes that answer an `ETag`) outside the profile routes, it publishes the same `profile.updated { section }` with the route name a route write uses ("site", "optics", "safety", "phd2", "guider-advanced", "camera-electronics", "filter-wheel", "dome", "storage", ...). One listener on the store's change event compares each section's `ETag` with the last one it saw and publishes each section whose tag moved, once, whoever wrote it: time sync's location, plate solve's learned focal length, the camera and mount connect-time fills, the flip executor's first-flip confirmation (since the 2026-11-20 #1589 entry below, its completed-flip record; the user's confirm is a route write), the auto-flats capture choice, the guider's camera reconcile and guiding assistant, the dome follower, the filter-wheel label mirror, storage configure. A route write still publishes once: the route announces its own section while it runs, so the listener skips it; a second section the route write changes too (a real optics change clears `first_flip_confirmed`) is published as well. A whole-profile apply (select, the delete fallback, the boot load) publishes nothing, as before. A section with no client route (calibration records, the identity record) publishes nothing. The first-flip re-arm carries the safety section's metadata, so its 200 answers the stored safety section's `ETag`; it still takes no `If-Match`.

**Reasoning:** a daemon write moved the tag with no event, so a client holding the section kept the old tag and its next Save was a 412 that said the settings changed on the rig (after a time sync, a plate solve, a flip, a guiding assistant run). One listener on the store covers every writer, present and future, instead of a call at each.

**Spec ref:** `Services/ProfileSectionChangePublisher.cs` (`ProfileSectionChangePublisher`, `ProfileWriteScope`), `Endpoints/ProfileEndpoints.cs` (`PublishUpdatedAfterWrite`, `PublishDaemonProfileWrites`, `SectionRoutes`), `Services/ProfileStoreSnapshot.cs` (`Apply`), `Program.cs`.

**Related:** #1807, #1644; ADR 0015; tests `DaemonProfileWriteEventTest`, `PreferenceSyncWiringTest`.

## 2026-11-20 — Polar Align stop contract: a lost mount gets its stop on reconnect, a pad stop ends the run, a takeover fences the run (#2066)

**Endpoint(s) or area:** `POST /equipment/telescope/moveaxis` with rate 0 while Polar Align turns the mount now also counts a stop in `AbortEpoch` with the reason `pad_stop` (still 202); `polar_align.error` `slew_aborted_externally` gains the pad's wording; `POST /server/emergency-stop`'s `stop_polar_align` step answers within 9 s. Server-internal: `IPolarAlignService.StopForTakeoverAsync` is bounded (`TakeoverStopBound`, 9 s); a REST GoTo whose PUT returns after a stop landed is aborted again.

**Decision:** the abort contract of #2048 had five holes left after #2097. A stop to a mount that is not connected gave up at once with `mount_did_not_stop`; a round that cannot reach the mount is now not a send, and it is tried again on every poll until the client reconnects, inside the same 8 s window. Polar Align counted any move of the stop count during its own AbortSlew as its own, so a Stop Mount that landed while that send was refused (not connected) was hidden from the run-wide watch and from the next Start's baseline; it now counts only the counts the telescope service tagged `polar_align`. A takeover (Emergency Stop's rung, the safety and fault parks) waited for the routine without bound inside the caller's 10 s, and a stuck run, or an earlier Stop still confirming its stop, then sent its stop round after the park had started and aborted it; past 9 s the takeover now fences the run (no stop of it goes out any more) and answers, and an end that abandons a run stuck past its 15 s grace fences it the same way. A pad stop during a turn stopped the axis without a count, and the routine's next lease renewal started the motor again; it is now counted first, the renewal is refused and the run ends with `slew_aborted_externally`. A REST GoTo whose PUT was still on the wire when Emergency Stop latched (or Stop Mount aborted) could reach the mount after the stop; it is aborted again under the stop's reason, unless the latest count since its reading is a park or Find Home (that move goes on; one that a later stop stopped does not count), or a GoTo, park or Find Home (REST, sequencer or mediator) was sent after that latest stop: the newer move goes on. A REST command counts as sent after the stop only when its own request's reading is (an older one is dropped by the stop anyway), from its request on (it may still be waiting for the guider); a sequencer or mediator one, by the count when it is noted, before its pose check. A newer command whose call has not returned yet holds the second abort back: the abort goes out if that command is refused or dropped and no other as new is left, and is dropped once one reaches the mount or another AbortSlew goes out.

**Reasoning:** the stop-contract PR of the #2048 plan (PR 1), from the #2066 acceptance and ADR draft rule 3 (`Abort_racing_a_delayed_slew_PUT_aborts_again`). The 9 s bound keeps the fence up before every caller's 10 s bound runs out, so Emergency Stop's `stop_polar_align` step is no longer reported as failed on a stuck run.

**Spec ref:** `Services/PolarAlignService.MountStop.cs` (`StopMountMotionAsync`, `SendMountStopAsync`, `CountOwnAborts`, `StopForTakeoverAsync`, `FenceStops`, `PadStop`), `Services/PolarAlignService.cs` (`EndRoutineAsync`, `RunRoutineAsync`'s exit), `Endpoints/EquipmentEndpoints.cs` (`MoveAxisAsync`), `Services/TelescopeService.cs` (`SlewInBackgroundAsync`, `NoteMotionDispatched`, `NoteMotionRequested`, `NoteMotionSent`, `NoteMotionFailed`, `CountedSince`). Tests `PolarAlignServiceTest.StopContract.cs`, `PolarAlignServiceTest.PadStop.cs`, `PolarAlignServiceTest.StopContractTimings.cs`, `TelescopeRestGotoTrackingTest` (`A_newer_GoTo_after_the_stop_is_not_aborted_by_the_raced_GoTo`, `A_newer_GoTo_the_mount_refuses_still_leaves_the_raced_GoTo_aborted`, `A_GoTo_still_stopping_the_guider_is_not_aborted_by_the_raced_GoTo`, `A_sequencer_slew_after_the_stop_is_not_aborted_by_the_raced_GoTo`).

**Related:** #2048, #2066, #2067, #2097, ADR 0022

## 2026-11-20 — Named stop reasons; an unconfirmed Polar Align stop leaves the pose unverified (`stopped_by`, `stop_unconfirmed`) (#2107, #2066, #2065)

**Endpoint(s) or area:** `polar_align.error` with reason `slew_aborted_externally` gains `stopped_by` (string, left out when the daemon did not keep the stop's reason): `user_request` (Stop Mount), `emergency_stop`, `watchdog`, `park`, `findhome`, `pad_stop`, and the new `fault_reaction` and `pose_unverified`; any `polar_align.error` gains `stop_unconfirmed` (true, left out otherwise) when the run's exit could not confirm its stop after the failure. The telescope runtime's `position_unverified_reason` and the `mount-position-unverified` Refusal's `position_unverified_reason` gain `stop_unconfirmed`. The `slew_aborted` event's `reason` can now be `fault_reaction` or `pose_unverified`. Server-internal: `MountStopReasons` (the reason constants), `ReactionMountStops` (the safety and fault reactions' shared quiet stops), `ITelescopeService.MarkPositionUnverified(reason)`, `MountPoseTrust.StopUnconfirmed` and `Flag`.

**Decision:** every sender counts its stop in the mount's stop count under its own name. The equipment-fault reaction and the safety reaction, when their park is refused because the mount's position is unverified, now stop the mount with `fault_reaction` and `pose_unverified` (they counted as `user_request`, so a polar alignment they ended said "Stop Mount"). Polar Align's `slew_aborted_externally` carries the name as `stopped_by`, and the app words the end from it in its own language (en and pt-BR; the daemon's `message` stays English, for the log and an older app). That message ends with the cut-power sentence when the run's exit could not confirm its stop, and no `mount_did_not_stop` follows it; `stop_unconfirmed` says so on its own, and the app then adds the cut-power warning in its language. A routine's lease renewal refused after a stop names the stop in its sentence. A park or Find Home dropped because a stop came after it was accepted (a second park in flight among them) names that stop in its log line. A polar alignment whose stop was not confirmed (`mount_did_not_stop`), or whose mount session changed while it ran (a reconnect, or a power cycle the daemon sees as one), marks the mount's position unverified with `stop_unconfirmed`, as #2070 does after a power loss: GoTo, Park, Find Home, polar alignment and its motor moves answer 409 `mount-position-unverified` until `POST /confirm-home`; the flag is kept per device across reconnects and restarts. `GET …/polaralign/start-plan` checks the flag before it reads the pointing, as `start` does, so a mount that wakes up reporting Home at the pole after a power cycle answers `mount-position-unverified`, not `polar-align-on-axis` (#2082). A session change is checked at each of the run's stop checks, so the flag is set while the run is still open. The run's exit and the stop path record and read the not-confirmed finding under one lock: when the exit finds it after the stop path read its finding, it is still reported, once, and the state becomes `failed` (it could be lost when the stop path had fenced the run).

**Reasoning:** #2107 (the reactions' stops read as Stop Mount), the #2066 abort contract step 4 and the #2065 ARA-10 flag (required: "the pose-unverified flag is set"), and the report race found in review of #2112. The flag reuses the #2070 trust state and its refusal, so no new store or cause is needed.

**Spec ref:** `Services/MountStopReasons.cs`, `Services/ReactionMountStops.cs`, `Services/SafetyReactionService.cs` and `Services/FaultReactionService.cs` (`StopPolarAlignQuietlyAsync`, `StopTrackingInsteadOfParkAsync`), `Services/MountPoseTrust.cs` (`StopUnconfirmed`, `Flag`), `Services/TelescopeService.PoseTrust.cs` (`MarkPositionUnverified`, `PositionUnverifiedDetail`), `Services/TelescopeService.cs` (`StoppedSinceWords`), `Services/PolarAlignService.MountStop.cs` (`StoppedFromOutsideMessage`, `NotePoseUnverifiedAfterRun`, `RunEnd`), `Services/PolarAlignService.cs` (`ExitMountOnceAsync`, `EndRoutineAsync`, `FailRoutineAsync`, `PublishErrorAsync`, `ClaimNotStoppedReportLocked`); client `lib/util/polar_align_error_text.dart`, `lib/state/polar_align/polar_align_state.dart` (`errorStoppedBy`), `lib/util/friendly_error.dart`, `lib/models/mount_status.dart` (`positionUnverifiedByStop`), `lib/screens/settings/panels/equipment_mount_panel.dart`, `lib/screens/simple/simple_device_windows.dart`. Tests `PolarAlignServiceTest.NamedStops.cs`, `TelescopePositionUnverifiedTest`, `TelescopeMoveAxisLeaseTest`, `TelescopeRestGotoTrackingTest.Two_parks_in_flight_send_one_park_and_name_the_dropped_one`.

**Related:** #2107, #2066, #2065, #2070, #2048, ADR 0014, ADR 0022

## 2026-11-20 — Polar Align says which number to trust: tracking off confirmed, `readout_caution`, `above_align` (#2072)

**Endpoint(s) or area:** `GET /api/v1/equipment/polaralign/status` (`PolarAlignStateDto`) and the `polar_align.progress` payload gain the optional `readout_caution` (string, null when sound; today only `altitude_weak`); `polar_align.error` gains the reason `tracking_not_off`; `PaResidualDto` (`guider.pa_residual`, `runtime.pa_residual`, `GET …/guider/pa-residuals`) gains the optional `above_align` (bool; null with no Polar Align result to compare, and on stored rows).

**Decision:** the live readout's §45.8 reverse projection needs a camera fixed in alt/az. The routine now sends tracking off and waits until the mount reports it off (`TrackingConfirmTimeout`, 10 s) before the first live frame, and reads it on every live frame; a mount that refuses the write (and still reads tracking on, or is not connected: a disconnected mount reads tracking off, which confirms nothing) fails at once, and one that keeps tracking or reads slewing, or starts either mid-loop, ends the run `failed` with `tracking_not_off` (the message says which). Before, the off write's answer was thrown away and nothing read it back. `readout_caution: "altitude_weak"` is set from each seed, once tracking is confirmed off, when |cos ψ| < 0.2; the status shows it only while adjusting or paused; ψ the camera's azimuth from the pole's (`PolarAlignGeometry.ReadoutCosPsi`): the readout amplifies altitude noise 5× or more there and, under the 0.1 floor, under-reads the altitude bolt. `above_align` is the #1731 rule on its own (value − 2σ > Polar Align + 2′); `reliable` is unchanged and still false when it holds. The client takes `above_align` when present and applies the same rule itself for an older daemon or a stored row; its wording names both causes (Dec backlash inflates the guiding figure; a shifted mount or a wrong reference deflates the Align figure) and says to run Polar Align again. Logs: EventId 4529 adds each live solve's JNOW RA/Dec and the tracking state; 4594 logs the seed pointing and cos ψ; the "PA residual from guiding" line adds the Dec rate, the Dec pulses added back (net and total ms), the hour angle and Dec, the Polar Align figure and `above_align`.

**Reasoning:** on 2026-11-19 the live readout said 0.2′ and the guiding residual two hours later said ≥ 16′. A mount that kept tracking through the live loop circles the camera about the axis at about 1.3′ a minute 5° from it, and the readout would read that as knob motion while the user chased it to zero; nothing confirmed tracking was off. The #1731 text blamed backlash alone, which cannot be told from a shifted mount without new evidence. σ propagation is out of this change (PR A, after #2082).

**Spec ref:** `Services/PolarAlignService.Readout.cs`, `Services/PolarAlignService.cs` (`SolveLoopAsync`, `GetStatusAsync`, `PublishProgressAsync`), `Services/PolarAlignService.Stages.cs` (`RunSeededAdjustAsync`), `Services/PolarAlignGeometry.cs` (`ReadoutCosPsi`), `Services/GuiderService.PaResidual.cs`, `Services/PaResidualEstimator.cs`, `Contracts/EquipmentDtos.cs`; client `lib/models/pa_residual.dart`, `lib/widgets/imaging/polar_error_rating.dart`, `lib/widgets/imaging/guiding_strip.dart`, `lib/widgets/imaging/polar_align_panel.dart`, `lib/screens/simple/simple_polar_panel.dart`.

**Related:** #2072, #1731, #1983, #2082, #2083, #2048

## 2026-11-20 — Polar Align makes one measurement per Start: `/polaralign/next` retired, `polar-align-on-axis` (#2082)

**Endpoint(s) or area:** `POST /api/v1/equipment/polaralign/next` always answers 409; `POST /api/v1/equipment/polaralign/start` with no `stage` starts `fine` (was `coarse`, or `fine` when the coarse stage could not run); `GET /api/v1/equipment/polaralign/start-plan` gives `max_sweep_deg` = 2 × the stage's own step (`seed_rotation_deg` for coarse and fine, `check_step_deg` for check); new Refusal cause `polar-align-on-axis` (409, no parameters) at `start-plan` and `start`; new `polar_align.error` reason `on_axis`; `check_result.fine_error_arcmin` and `fine_delta_arcmin` keep their names and now compare with the last run's readout.

**Decision:** *(Superseded in part by the 2026-11-20 #2066 fresh-reads entry below: `mount_fault` is now `pointing_unknown`; a Dec past ±90° is refused, not folded.)* a Start makes one measurement and never leads into another. `coarse` and `fine` seed (three solves, two RA-motor turns) and then run the live adjust loop until Stop or Done; `check` measures and ends `checked`. The coarse → fine → check chain, its in-run turn-back (`PlanStageTurns`) and `next` are gone: `next` answers 409 every time, and a run's live loop no longer returns on it. Each Start's turns go the way its plan says (`PlanSweep`), so the session's 30° sweep limit (#2089) holds across repeated Starts and Checks. The Dec tilt (`LeaveTheAxisAsync`, 5° off the pole with the Dec motor) is deleted: a pointing within 3° of the pole (|Dec| > 87°, folded) is the `polar-align-on-axis` Refusal before the camera is taken or anything moves ("point it 2–5° from the pole"), and a run whose first reading finds the mount there since fails with `on_axis` before any turn. That first reading comes from the driver; a failed read fails the run as `mount_fault` (it used to fall back to the cache). A `start` with `stage: check` while the routine adjusts (`adjusting` or `paused`) is the adjust screen's Check: the check is planned and its confirmation checked while the loop still runs (a refusal, such as `polar-align-sweep-limit`, leaves the loop running), then the loop ends like `stop` (`polar_align.stopped`, an `aborted` session row, its readout kept), then the check starts as a new run (`polar_align.started` with `stage: check`). The loop's end is the one end request that `start` makes itself: any other (a Stop, Done, a sequence start, Guiding taking the camera, shutdown) since the call means no check starts, as for any start. That call can take as long as a Stop, so the app waits as long for it. The check keeps the loop's reading of the mount's stop count, so a stop from outside that the loop had not seen yet ends the check before it moves. Any other `start` while a run is active stays a no-op accept. The readout a check compares with is the last run's (any run that ended with one: Stop, Done, a failure, a check's verdict), in memory only like `last_result`; null when none since the daemon started. That choice is the plan's proposal; Joey may overrule it.

**Reasoning:** each Start could run up to three full measurements, each with its own turns, a Next tap apart; NINA's Three Point Polar Alignment makes one per Start and has no Dec move (#2082). A Check through the start plan is refused at the sweep limit, where the old in-run check only kept to its own run's band.

**Spec ref:** `Services/PolarAlignService.cs` (`StartAsync`, `EndAdjustForCheckAsync`, `PlanStartAsync`, `NextAsync`, `RememberRunReadoutLocked`), `Services/PolarAlignService.Stages.cs` (`StartPointingOffTheAxisAsync`, `RunCheckStageAsync`), `Contracts/Refusals/RefusalCatalog.cs`, `Endpoints/EquipmentEndpoints.cs`; client `lib/widgets/imaging/polar_align_panel.dart`, `lib/screens/simple/simple_polar_panel.dart`, `lib/services/refusal.dart`. Tests `PolarAlignServiceTest.FakeGem.cs`, `PolarAlignServiceTest.Sweep.cs`, `PolarAlignServiceTest.cs`, `RefusalHandlerTest`.

**Related:** #2082, #2048, #2089, #2072, ADR 0022

## 2026-11-20 — Polar Align camera and frames per point: `polar_align.camera`, `polar_align.frames_per_point`, `measurement_noisy` (#2082)

**Endpoint(s) or area:** `GET`/`PUT /api/v1/profile/polar-align` (`PolarAlignSettingsDto`) gains `camera` (`guide` | `main`, default `guide`) and `frames_per_point` (int, default 3, clamped to 1–5 on PUT and GET); a PUT with any other `camera` answers 400 (a body without the key keeps `guide`). `GET /api/v1/equipment/polaralign/start-plan` (`PolarAlignStartPlanDto`) gains `camera`. `GET /api/v1/equipment/polaralign/status` (`PolarAlignStateDto`) gains `camera` (null before the first run) and `measurement_noisy` (bool). `polar_align.started` gains `camera`; `polar_align.progress` gains `measurement_noisy`; `check_result` and `polar_align.checked` gain `noisy`. All fields are appended; an older client ignores them.

**Decision:** `camera` picks the camera of a measurement, its live loop and the Check. `guide` is the guide camera through the guider's single-frame capture, as before. `main` is the main camera with its own plate solver at `coarse_exposure_seconds`, the frames the coarse stage already used; the stage keeps its own meaning (the rough step's Done waits for the tolerance). The coarse stage ("Rough first (main camera)", offered by the app only after `seed_solve_failed`, Joey 2026-11-20) always uses the main camera, whatever the setting. A start whose camera is `main` with the main camera not connected, or with no main-camera solver, is refused (409) before the camera is taken or anything moves. The start plan's `camera` says which camera the run uses, so the confirm step can say so; the run's turns, the sweep plan (`PlanSweep`) and the confirm are the same as for any start. While a run shoots the main camera, `GET /api/v1/equipment/polaralign/frame.jpg` gives no picture (the guider's last frame is not this run's). Each point of a measurement (the three seed points, the three check points) takes `frames_per_point` frames with the mount still, averages the solves as unit vectors, and refers the point to the mean time of its solves. A frame that does not solve is retried, up to 3 failures per set; a set with no solve fails the run as before (`seed_solve_failed` or `capture_rejected`). The log has each point's solve count, failures, mean and rms scatter about the mean in arcseconds. A point whose scatter is over 3″ is retaken once (a fresh set of frames); when the retake is over 3″ too, the run goes on with the retake's mean and `measurement_noisy` is true for the rest of the run (and `noisy` on a check's verdict). A retake with no solve (a cloud) does not fail the run: the first set's mean stands for the point, flagged noisy the same way. Several frames per point at long exposures keep the mount tracking at each point for many minutes, which the start's plan cannot see: before each turn the daemon reads the RA axis's offset from the session's sweep origin (counting the tracking turn, like `PlanSweep`), and a turn that would take it past the run's limit plus 2° fails the run with the new `polar_align.error` reason `sweep_limit` before the motor runs. A single frame per point is the solve itself, scatter 0. A hand-turn run (#2083, `hand_turn`) averages its points the same way: the first point where the mount points, and points 2 and 3 once the turn is taken (the wait's still solves), as a fresh set of `frames_per_point` frames whose ids go on from the wait's; with one frame per point the solve that took the turn is the point. The hand-turn run has no motor turn, so the per-turn `sweep_limit` check does not apply to it: its own band check (`hand_turn_too_far`) stands.

**Reasoning:** each point was one plate solve, so one noisy solve went straight into the axis fit, and nothing told a noisy point from a good one (#2082: about 1.3′ rms of axis error per 1″ of solve noise at 10° turns). Averaging three frames cuts that noise, and the logged scatter is what #2072's σ will be built on. The camera setting lets a rig whose guide camera cannot solve the pole field measure on the main camera every time, not only after a failure.

**Spec ref:** `Contracts/ProfileDtos.cs` (`PolarAlignSettingsDto`, `PolarAlignCameras`), `Contracts/EquipmentDtos.cs`, `Services/PolarAlignSettingsClamp.cs`, `Endpoints/ProfileEndpoints.cs` (`PutPolarAlignSettings`), `Services/PolarAlignService.cs` (`CameraFor`, `SolveSeedFrameAsync`, `SolvePointSetAsync`, `GetLiveFrameJpegAsync`), `Services/PolarAlignService.Stages.cs` (`RunMainCameraStageAsync`, `RunCheckStageAsync`), `Services/PolarAlignGeometry.cs` (`MeanDirection`); client `lib/models/polar_align.dart`, `lib/services/profile_api.dart`, `lib/screens/settings/panels/imaging_polar_panel.dart`, `lib/widgets/imaging/polar_align_panel.dart`, `lib/widgets/imaging/polar_start_confirm.dart`, `lib/screens/simple/simple_polar_panel.dart`. Tests `PolarAlignServiceTest.FramesPerPoint.cs`, `PolarAlignGeometryTest`, `ProfilePolarAlignEndpointTest`.

**Related:** #2082, #2072, #2048, #1807, ADR 0012, ADR 0015

## 2026-11-20 — The user confirms a completed meridian flip: `POST /profile/safety/first-flip/confirm`, `first_flip_completed_utc`, the `first-flip-not-completed` refusal; `on_altitude_limit` unused (#1589)

**Endpoint(s) or area:** new `POST /api/v1/profile/safety/first-flip/confirm` (no body, no `If-Match`); `SafetyPoliciesDto` gains `first_flip_completed_utc` (date-time or null, default null) and `first_flip_pier_side_changed` (bool, default false), both appended; `POST /api/v1/profile/safety/first-flip/rearm` and `PUT /api/v1/profile/safety` change; new Refusal cause `first-flip-not-completed` (409, no parameters); `on_altitude_limit` is marked unused.

**Decision:** `first_flip_confirmed` is set only by the user, after a flip ran to the end. The flip executor no longer writes `true` when its 60 s announce ends: every flip on an unconfirmed profile announces and waits. When a flip on an unconfirmed profile returns success, the executor writes `first_flip_completed_utc` (the instant) and `first_flip_pier_side_changed` (true when the pier side changed; false when the mount reported it unknown, or the same with flip safety off, where a driver that misreports pier side would otherwise never let the user confirm) and posts a Warning notification asking the user to confirm. A failed or cancelled flip writes nothing (with flip safety on, an unchanged pier side fails the flip). The client offers Re-arm whenever a flip is confirmed or a completed flip is on record, so the user can drop a record that predates a rig change. The confirm answers 200 with the safety policies, the safety section's `ETag` and one `profile.updated { safety }`, sets `first_flip_confirmed` and keeps the record; on a confirmed profile it writes nothing; with no completed flip on record it answers 409 `reason: first-flip-not-completed`. The two new fields are daemon-owned like `first_flip_confirmed`: a PUT keeps the stored values; re-arm, a share (they are stripped), an optics-train change and a profile create (`POST /api/v1/profiles`, a clone or a body) clear all three. A stored `first_flip_confirmed: true` with no `first_flip_completed_utc` loads as false (the section's heal), since the earlier executor wrote it before any flip ran. `on_altitude_limit` stays on the wire and in the profile, but no daemon code reads it and the client no longer shows it; a later change removes it.

**Reasoning:** the old flag was written before tracking stopped or the mount slewed, so a flip that then failed still counted as confirmed, and nothing proved a flip had cleared the rig. Recording the completed flip and asking the user keeps a human check on every new rig (#1589). The unused `on_altitude_limit` promised a reaction the daemon never ran (CONTRIBUTING: fields need consumers); removing it from the wire now would break older clients' PUTs.

**Spec ref:** `Services/MeridianFlipExecutor.cs` (`AnnounceUnconfirmedFlip`, `VerifySideOfPier`, `RecordCompletedFlip`), `Endpoints/ProfileEndpoints.cs` (`ConfirmedFirstFlip`, `PreserveDaemonOwnedSafetyFields`), `Services/ProfileSections.cs` (`WithoutFirstFlip`, the safety heal), `Services/FileProfileStore.cs`, `Services/InMemoryProfileStore.cs`, `Contracts/Refusals/RefusalCatalog.cs`; client `lib/screens/settings/panels/safety_policies_panel.dart`, `lib/state/settings/safety_policies_state.dart`. PORT_PLAYBOOK §58.5, §58.8, §58.16.

**Related:** #1589, #1807, ADR 0022; tests `MeridianFlipExecutorTest`, `FirstFlipConfirmationResetTest`, `DaemonProfileWriteEventTest`, `RefusalHandlerTest`.

## 2026-11-20 — Polar Align moves only from fresh, timestamped mount reads: `polar-align-pointing-unknown`, `polar-align-pose-ambiguous`, `pointing_unknown`, `pose_ambiguous` (#2066)

**Endpoint(s) or area:** new Refusal causes `polar-align-pointing-unknown` and `polar-align-pose-ambiguous` (409, no parameters) at `GET /api/v1/equipment/polaralign/start-plan` and `POST …/start`; `polar_align.error` gains the reasons `pointing_unknown` (replaces `mount_fault`, which is retired) and `pose_ambiguous`. Server-internal: `IMountMotionControl` (`AbortEpoch`, `SessionEpoch`, `AbortReasonSince`, `AbortSlewAsync(reason)`, the leased `MoveAxisAsync`, `PositionUnverifiedReasonAsync`, `ReadNowAsync` → `MountReading`, and `ReadPositionNowAsync`, the same reading with RA and Dec only) moves from the test project into the daemon; `ITelescopeService` extends it and Polar Align takes it instead of casting its mount.

**Decision:** every pointing Polar Align plans or moves from is a `MountReading` read from the driver now: RA and Dec as the driver reports them (never folded over the pole), Slewing (null when that read failed), pier side, the time the read began and the mount session it came from. A reading is not trusted when there is none (not connected, a failed read), it is the (0, 0) placeholder, it is older than 2 s (twice AlpacaBridge iOptron's 1 s cache), or, inside a run, it comes from another mount session than the run's (the session of the reading the user confirmed). At a start (the plan and the start itself) that is the Refusal `polar-align-pointing-unknown`, before the guide camera is taken or anything moves; in a run it fails the run with `pointing_unknown` before the next motor command (the motor is stopped and the exit confirms the stop, as for any failure). A Dec past ±90° (the Dec axis over the pole, which a synced mount can report) leaves the pier side and hour angle ambiguous: `polar-align-pose-ambiguous` at a start, `pose_ambiguous` in a run, with nothing moved. The #1536 fold (RA + 180°, 180° − Dec) is no longer applied to a reading; it stays only in the turn arithmetic (`FoldTolerantSignedTurnDeg`). The run-wide watch that ends a run on a stop from outside also ends it with `pointing_unknown` when the mount session changes (a connect, a disconnect, a fault trip), in the adjust loop too: a reconnected mount may have been moved, re-homed or swapped. A park, Find Home or Emergency Stop that came with the reconnect wins: the run ends with `slew_aborted_externally` and leaves the mount to it (its exit sends no stop that would abort the park). The stop confirmation counts a missing, placeholder or stale reading as "not confirmed". A turn's polls read the position only (`ReadPositionNowAsync`: no Slewing or pier-side round trip to stretch the poll); its start and end readings, the plan and the stop confirmation take the full reading. The pier side Polar Align keeps for its sweep origin is a fresh reading's, never the cache's. The daemon's refresh cache (`GetCurrentPosition`, `GetInfo`) is never read by Polar Align while it turns the mount.

**Reasoning:** #2048 ARA-5. After one failed status poll the daemon's cache answers the (0, 0) placeholder; a turn planned or stopped on it, or on a reading of a mount that reconnected (and may have been power-cycled to a false Home, #2070), moves the mount a way nobody confirmed. A reading older than the driver's cache cannot say where a moving mount is. A Dec past the pole read as its folded sky point hides which side of the pier the tube is on, the input every counterweight guard needs.

**Spec ref:** `Services/IMountMotionControl.cs`, `Services/TelescopeService.FreshReads.cs` (`ReadNowAsync`), `Services/PolarAlignService.FreshReads.cs` (`ReadFreshPointingAsync`, `StartPointingJnowAsync`, `RunPointingJnowAsync`, `ThrowIfStoppedFromOutside`, `ThrowIfMountReconnected`), `Services/PolarAlignService.MountStop.cs` (`ThrowIfAbortedFromOutside`, `ReadStillnessAsync`), `Services/PolarAlignService.SweepOrigin.cs` (`PlanSweep`, `NoteMountLeft`), `Contracts/Refusals/RefusalCatalog.cs`.

**Related:** #2066, #2048, #2081, #2067, #1536, #2075, ADR 0022; tests `PolarAlignServiceTest` (`M18_*`, `Dec_over_90_reading_refuses_turns`, `The_cache_is_never_read_while_the_mount_moves`, `A_park_with_a_reconnect_*`, `A_stale_reading_never_confirms_the_stop`, `A_placeholder_reading_never_confirms_the_stop`, `A_turn_polls_the_mount_with_the_position_only_read`, `A_pier_flip_the_refresh_cache_missed_is_a_new_start`), `TelescopeMoveAxisLeaseTest` (`ReadNow_*`, `ReadPositionNow_*`), `RefusalHandlerTest`.

## 2026-11-20 — One motion owner: Polar Align holds the mount for its whole run; `polar-align-mount-busy`, `mount_busy` (#2066, #2065)

**Endpoint(s) or area:** the Refusal cause `polar-align-moving-mount` now also answers `POST /equipment/telescope/slew` (a sync too), `/park`, `/home` and `POST /plate-solve/center` for Polar Align's whole run, not only while it turns the mount (`/moveaxis` with a non-zero rate keeps the narrower rule); new Refusal cause `polar-align-mount-busy` (409, no parameters) at `GET /api/v1/equipment/polaralign/start-plan` and `POST …/start`; `polar_align.error` gains the reason `mount_busy`. Server-internal: `IMountMotionControl.MotionOwner` and `AcquireMotion(owner)` (`MotionOwners.PolarAlign`), implemented by `TelescopeService`; `ITelescopeService.ParkUnlessMotionOwnedAsync` (the `/park` endpoint's park); `IGuideCapture.AppState`.

**Decision:** Polar Align takes the mount's single motion owner before its start reading (so the turns and the sweep limit start from a pose read under it) and gives it back once the run's exit has stopped the mount (a failure's exit included; an end that gives up on a stuck run gives it back after its own stop; the next Start drains a run that never unwound). Joey decided on #2048 (2026-11-20) that it is held for the whole run, the adjust loop included, rather than only while Polar Align moves the mount as ARA-2 first said. While it is held, the REST GoTo, sync, Park, Home and Center answer `polar-align-moving-mount`; every mediator GoTo, park and Find Home (the sequencer's GoTo, Park Scope and Find Home, Center's GoTo, the meridian flip, checked before its pier-side write, an end-of-sequence park, a failed flip's safe rest) fails with that sentence before anything reaches the mount, a refused park leaving tracking as it is; and the mediator sync (Center's) is a clean "not synced". A sync is refused because it shifts the RA reading a turn steers by. The owner check and the send are one step: a move is counted while it is sent, Polar Align cannot take the owner meanwhile (its start is refused with `polar-align-mount-busy`), a REST GoTo or sync accepted before the run took the owner is dropped when it would go out, and a REST Park or Home whose checks (the position check, the guider stop) were still running when the run took the owner answers `polar-align-moving-mount` before it ends the leases, stops the guider or reaches the mount. Nothing that stops the mount is refused: Emergency Stop, Stop Mount and every AbortSlew, a pad stop (MoveAxis rate 0) and tracking off. The safety and fault parks call the telescope service's `ParkAsync`, which never checks the owner (`/park` calls `ParkUnlessMotionOwnedAsync`); they, and End Night's and the unattended shutdown's Find Home and park (the shutdown ladder, through the mediator), end Polar Align through its takeover first, which gives the owner back. A start is refused with `polar-align-mount-busy` when its fresh reading says the mount slews or gives no Slewing answer, or when the guider guides or calibrates (Guiding, Calibrating, Paused, LostLock: the custody module's definition of busy), before the guide camera is taken. Before each turn the run reads the mount twice, 1 s apart: both readings must say not slewing and agree within 0.05° in RA and in Dec, and the guider must not guide or calibrate; otherwise the run fails with `mount_busy` before the motor starts (a GoTo by the hand controller or another program, which the owner cannot see, is caught here). The second reading is where the turn starts. A reading the run cannot trust still fails it as `pointing_unknown` or `pose_ambiguous`.

**Reasoning:** #2048 ARA-2 and ARA-7. A GoTo accepted while Polar Align solved a frame between its turns ran beside the next turn: two commands drove the RA motor at once, and the turn was measured on a mount the GoTo was moving. Holding the owner only while a motor runs left that window open; Joey chose the whole run. A guider that starts guiding by its own controls would chase the turn.

**Spec ref:** `Services/IMountMotionControl.cs` (`MotionOwner`, `AcquireMotion`, `MotionOwners`), `Services/TelescopeService.MotionOwner.cs`, `Services/TelescopeService.Mediator.cs` (`RunMountCommandAsync`, `Sync`, `MeridianFlip`), `Services/TelescopeService.cs` (`SlewInBackground`), `Services/ShutdownLadder.cs` (`EndPolarAlignFirstAsync`), `Services/PolarAlignService.MotionOwner.cs` (`EnsureMountSteadyAsync`, `ThrowIfBusyAtStart`, `AcquireMountForRun`), `Services/PolarAlignService.cs` (`StartRunAsync`, `EndRoutineAsync`, `RunRoutineAsync`), `Endpoints/EquipmentEndpoints.cs` (`ThrowIfPolarAlignMovesTheMount`), `Endpoints/PlateSolveEndpoints.cs` (`CenterAsync`), `Contracts/Refusals/RefusalCatalog.cs`.

**Related:** #2066, #2065, #2048, #2097, ADR 0022; tests `PolarAlignServiceTest` (`Polar_Align_holds_the_motion_owner_for_the_whole_run_and_a_GoTo_meanwhile_is_refused`, `A_GoTo_accepted_mid_solve_does_not_run_beside_the_next_turn`, `A_run_that_fails_by_itself_gives_the_motion_owner_back`, `M21_*`, `M10_*`, `A_mount_moved_between_the_two_readings_*`, `A_guider_that_starts_guiding_mid_run_*`, `A_start_on_a_slewing_mount_*`, `Polar_Align_reads_its_start_pose_only_once_it_holds_the_mount`, `A_Stop_of_a_stuck_run_*`, `A_Start_that_drains_a_run_*`, `The_two_readings_before_a_turn_*`), `TelescopeMotionOwnerTest`, `TelescopeMeridianFlipSlewTest` (`A_flip_while_Polar_Align_holds_the_mount_*`), `EquipmentEndpointsSlewTest`, `CenteringEndpointTest`, `GuideCaptureSeamTest`, `RefusalHandlerTest`.

---

## 2026-11-20 — #2071 the mount clock recheck writes again after a failed write; a polar-align start waits for a clock push

**Endpoint(s) or area:** `POST /api/v1/equipment/polaralign/start` (every stage, and the Check from the adjust loop); the daemon's mount clock check (`mount-clock-not-verified`). No wire shape changes.

**Decision:** the 30 s recheck of an unverified mount clock now writes the mount's `UTCDate` again, from the trusted time only, whenever no trusted time has landed in the mount's clock: never written (the mount was slewing, or no trusted time had reached it), a write that failed (a transient bridge or driver error; a driver that does not implement the write is only read), or a mount that still cannot report its `UTCDate` (AlpacaBridge's "not established"). Before, only the first two were written again, so a connect whose write failed once kept the gate closed for the whole session on an NTP-only rig with no app. A host clock step counts as a push from the moment the check goes unverified, so a GoTo, Sync or Center in that instant waits for the push instead of being refused. A polar-align start now waits for a clock push in flight, up to 10 s, as GoTo, Sync and Center do, then answers 409 `mount-clock-not-verified` only if the check still fails; with no push in flight a closed gate refuses at once. A Check pressed in the adjust loop while the gate is closed answers 409 and leaves the loop running: the mount is not stopped.

**Reasoning:** the #2087 review: a read-only recheck could never open a gate the first write failed to open, and the polar-align start was the one move that did not wait for the push the app's connect starts.

**Spec ref:** `Services/TelescopeService.MountClock.cs` (`MountClockCheck.WriteOwed`, `WatchMountClock`), `Services/PolarAlignService.cs` (`StartRunAsync`, `EndAdjustForCheckAsync`). Tests `MountClockTest`, `PolarAlignServiceTest.MountClock.cs`.

**Related:** #2071, #2087, ADR 0022

---

## 2026-11-20 — Polar Align turns RA by hand: `hand_turn` on start, `polar_align.hand_turn`, `hand_turn_wait` (#2083)

**Endpoint(s) or area:** `POST /api/v1/equipment/polaralign/start` body gains `hand_turn` (boolean, default false); `GET /api/v1/equipment/polaralign/status` gains `hand_turn` (the current or last run turned by hand) and `hand_turn_wait` (`PolarAlignHandTurnDto`, null unless a hand-turn run waits for the user's turn); new WebSocket event `polar_align.hand_turn { point, direction, target_deg, turned_deg?, waiting }`; `polar_align.started` gains `hand_turn`; `GET /api/v1/equipment/polaralign/start-plan` gains `hand_turn_available` (bool; an older daemon leaves it out, and a client must then offer no hand turn, because that daemon would ignore `hand_turn` and use its motor); new Refusal cause `polar-align-already-running` (409, no parameters) at `POST /api/v1/equipment/polaralign/start`, the twenty-second cause of the `RefusalReason` enum (after `mount-clock-not-verified`). New Refusal cause `polar-align-clutch-turned` (409, no parameters) at `POST /api/v1/equipment/polaralign/start` (a motor start, and the adjust screen's motor Check), the twenty-third cause of the `RefusalReason` enum (after `polar-align-already-running`); new `polar_align.error` reason `hand_turn_too_far`. Server-internal: `IMountMotionControl.SyncEpoch` (the syncs the mount accepted; `TelescopeService` counts them, and Polar Align reads it through its motion seam).

**Decision:** NINA's manual mode, chosen per run in the confirm step, never stored in the profile. A start with `confirmed` and `hand_turn` true makes the same one measurement (#2082), fit and live loop as a motor run, but the user turns the RA axis by hand (the hand pad, or the clutch open) about the step between the three solves. The run asks for each turn (`polar_align.hand_turn`, `waiting` true, the way the user confirmed in the plan), solves frame after frame and reads the mount's RA after each solve. A solved RA change is an angle about the celestial pole, not about the RA axis: with a polar error of a few degrees a true 10° turn can read anywhere from backwards to 40°, so the solves never give the turn's angle. When the mount's reading has moved 1° or more (beyond tracking) since the last point, the axis turned with it (the hand pad): `turned_deg` is the reading's turn that way (negative: the other way), and the turn is taken at 80 % of the step. When the reading stands (the clutch is open), `turned_deg` is null (no angle is claimed, and the app shows no "other way"), and the turn is taken once the solved pointing has moved at least what 80 % of the step moves it at the least distance from the axis the run allows for (its distance from the pole less 3°, at least 1°). Either way the mount must have stopped: with the hand pad two solves in a row agree within 0.1°; with the clutch open, where near the pole a small nudge moves the solves that far, three in a row must, and 5 s must have gone since the turn was asked. A frame whose mount read fails is not measured (a failed read says nothing about the clutch: only a reading read at both ends that stood is a clutch turn), nor is a frame where the mount's tracking changed. Each frame's mount read is a fresh position read (#2066), bounded at 3 s: one that times out, gives no answer, the (0, 0) placeholder or a reading older than 2 s is such a failed read; a mount that reconnected since the start ends the run (`pointing_unknown`), and a Dec past ±90° fails it (`pose_ambiguous`), as for a motor run. A last event with `waiting` false says the point is in. A turn that takes the axis more than the measurement's two turns plus 2° from its first frame, either way, fails the run with `hand_turn_too_far`: by the reading with the hand pad, and with the clutch open by the least turn the solved distance needs with the camera at its furthest from the axis (its pole distance plus 3°). A clutch turn leaves the mount's reading off by the user's turns: Polar Align forgets the session's sweep origin and where it left the mount (both are readings), and refuses a start that turns RA with the motor with `polar-align-clutch-turned` until the mount accepts a sync (a plate solve's, Center's) or reconnects; a hand-turn start goes ahead; the run's exit does not record where it left the mount while that clutch turn stands. A Stop, Done or Guiding taking the camera clears `hand_turn_wait` from the status with the run. It waits as long as the user needs; Stop ends it. Both turns go the way the start's plan says (`PlanSweep`, #2089), as a motor run's do; `point` is 2 or 3. Such a run sends the mount no motor move, no GoTo and no Find Home, never drives a Polar Aligner's motors (with a host-driven Polar Aligner the correction shows `manual`, as with auto off), never turns tracking on while it runs. A start within 3° of the pole is refused with `polar-align-on-axis`, and a run whose first reading finds the mount there fails with `on_axis` before anything is asked, as for a motor run (one refusal, one failure reason for both). It still turns tracking off for the live adjust loop (the §45.8 readout needs the camera still in alt/az), and its exit is every run's: it stops the mount, and gives back the tracking the run found only where every run does (a finished run with `stop_tracking_when_done` off, or Guiding taking the camera); otherwise tracking ends off. A Start while a run is active stays a no-op accept when it confirms the same way of turning, and is refused with `polar-align-already-running` when it confirms the other (`hand_turn` true while the motor turns the mount, or the reverse): two devices, or a stale Start card. A Check from the adjust loop (`stage: check` while adjusting or paused) is a new run, not a retry: it ends the loop as a motor Check does, and the check turns by hand or with the motor as its own confirmation says, whatever the loop's run did. The fit leaves out the cross-check against the mount's reported turns (the mount's reading does not move with the clutch open); the pointings must still sit 1° or more off the axis and each solved turn be 1° or more. The check's `turn_mismatch_deg` is 0 in a hand-turn run. The mount must still be connected and the start confirmed, as for a motor run.

**Reasoning:** a rig whose mount cannot be trusted to move itself (or the first night on a new mount) can still be polar-aligned with no automated motion at all, as in NINA. Per run, so it does not touch the polar-align profile section another change is moving to the section pattern (#1807).

**Spec ref:** `Services/PolarAlignService.HandTurn.cs` (`ThreePointsByHandAsync`, `WaitForHandTurnAsync`, `MeasureHandTurn`, `HandTurnDeg`, `FitThreePointByHandOrFail`, `NoteClutchTurned`, `ThrowIfClutchTurned`), `Services/IMountMotionControl.cs` and `Services/TelescopeService.cs` (`SyncEpoch`), `Services/PolarAlignService.SweepOrigin.cs` (`NoteMountLeftAfterStopAsync`), `Services/PolarAlignService.Stages.cs` (`RunSeededAdjustAsync`, `RunCheckStageAsync` branch on `RunBand.HandTurn`), `Services/PolarAlignService.Aligner.cs` (`CorrectAsync`), `Contracts/EquipmentDtos.cs` (`PolarAlignStartRequestDto.HandTurn`, `PolarAlignStartPlanDto.HandTurnAvailable`, `PolarAlignHandTurnDto`), `Contracts/Refusals/RefusalCatalog.cs` (`PolarAlignAlreadyRunning`, `PolarAlignClutchTurned`), `Contracts/WsEvents/WsEventCatalog.cs` (`PolarAlignHandTurn`); client `lib/widgets/imaging/polar_start_confirm.dart`. Tests `PolarAlignServiceTest.HandTurn.cs`, `EquipmentEndpointsPolarAlignTest`.

**Related:** #2083, #2082, #2048, #2089

## 2026-11-21 — #1895 `GuiderStateDto.calibration_target_px`: how far each calibration leg has to go

**Endpoint(s) or area:** `GET /api/v1/equipment/guider` (`runtime`): `GuiderStateDto` gains the optional `calibration_target_px` (number, nullable, default null). No WebSocket event changes.

**Decision:** while `state` is `calibrating`, the distance in guide-camera pixels the guider is configured to move on each leg, so a client reads "11.9 of 25 px". Null outside `calibrating`, when the guider reports no `calibration_distance`, or when the read fails. The daemon reads it once per calibration run (at `StartCalibration`, or at the first step when it connected mid-run) through the guider's `get_profile_setup`, best-effort and silent (no inbox error), keeps it with the leg fields and clears it where they are cleared (`CalibrationComplete`, `CalibrationFailed`, loop or guiding stopped, a dropped link). The state read stays an in-memory snapshot. Every leg of a run carries the same target; nothing identifies a backlash-clearing leg. Ara sets no calibration distance. An older client ignores the field; a newer client against an older daemon reads null and shows today's "11.9 px".

**Reasoning:** #1875 asked for "West step 3 · 11.9 of 25 px"; #1884 shipped the moved distance without the target because the daemon did not send it. The guider's JSON-RPC document lists `calibration_distance` among the `get_profile_setup` result fields.

**Spec ref:** `Contracts/EquipmentDtos.cs` (`GuiderStateDto`), `Services/GuiderService.cs` (`GetAsync`), `Equipment/MyGuider/PHD2/PHD2Guider.cs` (`ReadCalibrationTargetAsync`), `Equipment/MyGuider/PHD2/PHD2Guider.LiveView.cs` (`ReadProfileSetupAsync`). Tests `GuiderFakeIntegrationTest`; client `models/guider_status_test`. `openapi.yaml` does not list `GuiderStateDto`, so it does not change.

**Related:** #1895, #1875, #1884, ADR 0009 (rule 1), 0014, 0020
