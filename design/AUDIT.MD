# OpenAstroAra — Senior Engineering Audit

**Date:** 2026-07-11
**Scope:** Full repository (server daemon, equipment/sequencer core, Flutter client, imaging/astrometry stack, build/CI/licensing).
**Method:** Five parallel deep-dive audits by area, each reading source directly.
**Repo snapshot:** ~168k lines C# (excl. vendored SOFA/NOVAS31/Accord), ~83k lines Dart, ~1,530 client test cases + ~229 server test files, 8,600+ commits since 2016.

> **Resolution status (2026-07-11):** Non-auth findings are being remediated in the working tree (see per-finding status). **Network authentication is a deliberate WON'T-FIX**: the daemon is intentionally unauthenticated and binds all interfaces because ASCOM Alpaca does not implement authentication and adding it (or restricting the bind/CORS) would break device connections. Findings that depend on adding auth, loopback-only binding, or CORS lockdown are marked **WON'T-FIX (Alpaca)** below and left as documented, accepted risk for the LAN-trusted deployment model.

---

## Executive summary

OpenAstroAra is a **well-engineered, security-conscious codebase** — noticeably above the norm for a project of this size and lineage (a headless port of N.I.N.A. + a fresh Flutter client). Local component quality is high: file-handling paths are hardened against tar/zip-slip with size caps and SHA verification, error messages are deliberately sanitized, the WebSocket transport (both ends) is excellent, and build/CI/licensing hygiene is unusually disciplined (nullable + analyzers + warnings-as-errors repo-wide, SHA-pinned actions, 3-OS Flutter matrix, per-directory MPL/AGPL split with a CI license-freshness gate).

The problems cluster into a small number of **systemic themes**, not scattered one-offs:

1. **The daemon's front door is wide open (Critical).** No authentication, binds `0.0.0.0`, CORS `AllowAnyOrigin`. This is a documented "v0.0.1 LAN-only" posture, but it amplifies every other finding — a malicious webpage the operator merely *visits* can read profile secrets and command the physical mount.
2. **Untrusted-file ingestion is under-validated (High).** XISF/FITS loaders drive allocations and native reads off attacker-controlled header fields with no overflow/sanity checks.
3. **Inherited device-I/O edges have unbounded waits and unobserved exceptions (High).** The PHD2 guider client can hang the exact safety paths (meridian flip, abort) that must terminate, and one bug makes *resume* re-pause guiding.
4. **Cross-platform P/Invoke ABI landmines (High, latent).** NOVAS structs passed by value will segfault the first linux-x64 caller.
5. **"Copy-degradation" in the client state layer (Medium, broad).** Correct async guards exist in the flagship files and were cloned into ~25 siblings with the guards dropped.

None of these block the trusted-hardware happy path, but items 1–4 should be addressed before the daemon is exposed to any untrusted network or file source.

---

## Top priorities (ranked)

| # | Severity | Finding | Area |
|---|----------|---------|------|
| 1 | ~~Critical~~ **WON'T-FIX (Alpaca)** | No auth + `0.0.0.0` bind + `AllowAnyOrigin` CORS. Accepted: Alpaca is unauthenticated by design; auth/bind/CORS changes would break device connections. LAN-trusted deployment model. | Server |
| 2 | ~~High~~ **WON'T-FIX (Alpaca)** | Profile secrets over unauthenticated GET; bug-report/backup bundles include `profile.json`. Ties to the no-auth model; accepted as above (client reads tokens back to edit them). | Server |
| 3 | **High** | Untrusted XISF/FITS: zlib decompression bomb + integer-overflow on dimensions → OOM / native heap overwrite | Imaging |
| 4 | **High** | PHD2 guider: `Pause(false)` still sends *pause*; `WaitForAppState`/`SendQuery` ignore timeouts → safety paths (flip/abort) can hang forever | Core |
| 5 | **High** | NOVAS structs passed by value where C expects pointers → linux-x64 / Intel-mac segfault (latent) | Astrometry |
| 6 | **High** | `ExposureData` read-before-assign + `ImageArray` cache race → silent corruption of 32-bit captures | Imaging |
| 7 | **High** | Meridian-flip failure without a wired PauseGate does NOT halt the run (comment claims it does) → images past meridian, OTA-collision risk | Sequencer |
| 8 | **High** | WebSocket subscriber channel leaks permanently on client drop during resume replay → unbounded memory growth, unauthenticated | Server |
| 9 | **High** | Vendored SOFA/NOVAS31 sources absent from ALL license inventories → attribution gap when natives ship | Licensing |
| 10 | **Medium** | Loopback planetarium server: DNS-rebind guard present but direct-loopback CSRF can forge an `addToSequence` → real mount slew | Client |

---

## Resolution log (2026-07-11)

All non-auth findings below were remediated in the working tree the same day. Full solution builds clean (`dotnet build OpenAstroAra.sln` → 0 errors, warnings-as-errors in force); `OpenAstroAra.Fits.Tests` (5) + `OpenAstroAra.Stretch.Tests` (50) pass; client `flutter analyze` clean with touched-area tests passing. The main C# suite (2331 tests) was run to catch regressions — an initial pass surfaced 31 failures traced to three over-reaching fixes, which were then **reverted** (below); the re-run is clean.

**Reverted after the regression pass (3 fixes withdrawn):**
- **BackupService SSRF denylist** — REVERTED. Blocking RFC-1918/loopback on `backup_source_url` breaks a *legitimate* feature: restoring a backup from another daemon or NAS on the user's LAN (the same LAN-trusted model that governs the auth decision). It also broke the restore tests (DNS-resolving a fake host). The SSRF concern does not apply to a deliberately LAN-open appliance — left as accepted, consistent with the auth posture.
- **Frame captures-root confinement** (`SqliteFrameRepository`) — REVERTED. This was a Low, no-live-vulnerability defense-in-depth guard (paths are server-minted); in practice it made legitimate frame deletes/previews soft-fail when a frame lived outside the *current* `SaveDirectory`. Not worth the behavioral regression.
- **PHD2Guider `Connect` returns false on init failure (#6) + listener token-linking (#7 partial)** — REVERTED these two sub-parts. `connected` is the socket-liveness contract that callers depend on; `initialized` already records init completion, so failing the whole connect when an optional init RPC throws wrongly reported a live guider as disconnected. Token-linking the session listener to the connect token would have killed a live session when a connect-timeout token fired. The valuable, safe parts of the guider work (#H2 `Pause(pause)` + failsafe, #H3 `WaitForAppState` timeout, dither `Settling` ordering, listener rethrow removal, Dispose→Disconnect) were **kept**.

**Server (5 kept):** WS subscriber-leak guarded (#8); batch-job atomic single-flight + final catch marks failed + CTS disposal + 10-min terminal-job prune + JobState locking (no cancel/complete mislabel). *SSRF denylist and frame captures-root confinement were reverted (see above). Auth/bind/CORS/secret-redaction/Origin/OpenAPI findings intentionally not touched — see WON'T-FIX above.*

**Core / Equipment / Sequencer (18 fixed):** NMEAGps timer faults the TCS (#H1); PHD2 `Pause(pause)` + failsafe + AppState null-guard (#H2); `WaitForAppState` honors its timeout via linked CTS (#H3); meridian-flip no-gate branch now calls `root.Interrupt()` to actually halt (#H4); `BasicQuery` EOF-on-zero + 30s read deadline (#H5); guider Connect returns false on init failure; listener rethrow removed + token linked; camera download logs instead of swallowing; duplicate `StopExposure` removed; camera Connect disconnects on Initialize failure; ExternalScript killed on abort; SequenceContainer lock made instance; dither `Settling` set before RPC; ConditionWatchdog CTS disposed + clean loop exit; SlewScopeToAltAz/SwitchFilter unsubscribe in Teardown; serial semaphore leak, MetaGuide CTS `using`, PHD2 Dispose→Disconnect.

**Imaging / FITS / Stretch / PlateSolving / Astrometry (19 fixed):** FITS NAXIS overflow validation (#H1); XISF decompression-bomb caps + capped inflate (#H2); XISF geometry/channel cross-check + write-ctor validation (#H3); `Create32BitData` assigned before use + clamp (#H4); `ImageArray` cache published once via `Interlocked.CompareExchange` (#H5); NOVAS structs passed `in`/pointer (#H6); float/double FITS scaled by observed min/max; astrometry status codes checked → NaN-poison; rise/set discriminant `>0`; decimal-comma DMS parse; RA-wrap in tracking rate; `cal_date` void + TT correction; plate-solve timeout token wired + process kill on cancel; XISF shuffle/LZ4/UInt16 malformed-input guards; unused mis-marshaled P/Invoke removed; header PadRight clamp; AllImageStatistics unsubscribe.

**Flutter client (7 fixed):** planetarium bridge now requires a per-run 256-bit `X-Ara-Token` on `/araevent`+`/aracmd` (closes the loopback CSRF → mount slew); BuildContext-across-async guards in 4 screens/widgets; `ref.mounted` added to the autoDispose list notifiers; **one shared `SettingsSyncMixin`** applied to all 15 settings notifiers (generation fence + serialized persist chain, replacing the copy-degraded boilerplate); site-location failure surfaces an error instead of defaulting to 0,0; `loadMore` failures now flagged; unguarded JSON casts use the FormatException idiom.

**Build / licensing / hygiene (fixed):** SOFA + NOVAS31 + vendored-Accord attribution added to `NOTICE.md`; license generator + CI freshness gate extended to vendored native sources; dead files deleted (`publish/push.bat`, two stale Accord csproj); doc drift reconciled (playbook/Dockerfile `5400→5555`, README tree). *Deferred as too large/risky: Central Package Management migration; StarDataImport SQL-injection (dormant unshipped dev tool — retained note); dependency-deprecation cleanups.*

**Deferred (noted, not auto-fixed):** client test-coverage gaps (app_shell, equipment_device_state, create_imaging_run, plate-solve vertical, imaging tab) remain as recommended follow-up work; frame captures-root guard confines to the *current* SaveDirectory (frames under an old directory soft-fail after a save-dir change).

---

## 1. Server & API (`OpenAstroAra.Server`)

**Assessment:** High-quality, security-conscious component engineering wrapped around one architectural decision that undermines it. File-handling is unusually well-hardened (tar/zip-slip guards, size caps, SHA-256 verify, atomic staging/rollback, symlink skipping), errors are sanitized to keep server paths off the wire, and the §70 profile-export strip is a well-tested allowlist-by-omission. The dominant risk is the open front door; secondary themes are advisory-only session enforcement and a racy in-memory job registry around an otherwise-solid centering service.

### Critical
- **No auth + `0.0.0.0` + `AllowAnyOrigin`** — `Program.cs:58` (`ListenAnyIP`), `:61-62` (CORS), `:826`, `:839-842`. `Access-Control-Allow-Origin: *` with no credentials lets any origin both send *and read* responses. A malicious page loaded on the observatory LAN can read `/profile/notifications` secrets, then POST `/telescope/slew`, `/server/emergency-stop`, `/server/restart`. **Fix:** bind loopback by default, require a shared token for control endpoints, replace `AllowAnyOrigin` with an explicit allowlist, add Host-header/`HostFiltering` validation.

### High
- **Plaintext secrets over unauthenticated GET** — `ProfileEndpoints.cs:84-88`, `ProfileDtos.cs:73-74`. `GET /profile/notifications` returns `PushoverToken`/`TelegramBotToken` in cleartext. **Fix:** redact on read (return `has_token`).
- **Secret exfiltration via bug-report/backup bundles** — `SystemEndpoints.cs:62-94, 361-432`; `BugReportService.cs`. `?acknowledge=pii` is a UX flag, not access control; both bundles include full live `profile.json` (creds, tokens, precise coordinates). **Fix:** gate behind auth.
- **WebSocket subscriber channel leak** — `WebSocketEndpoints.cs:187` vs unprotected sends at `:526-593`; `PlaceholderWsServices.cs:94-117`. A client disconnecting during resume-replay throws out of the handler with its subscriber (up to 1000 retained envelopes) still registered; `_subscribers` grows unbounded for the process lifetime, unauthenticated. **Fix:** wrap resume/replay sends in the drain loop's catch, or hand back a disposable subscription disposed in `finally`.

### Medium
- **Blind SSRF on restore** — `BackupService.cs:855-861, 922-957`. `restore-zip` accepts an arbitrary `backup_source_url`, validates only the scheme — no block on loopback/RFC-1918/`169.254.169.254`. **Fix:** egress allowlist / private-range denylist, re-validate resolved IP.
- **Single-client session not enforced on commands** — `EquipmentEndpoints.cs:144-157, 74-84`. §27 session is cooperative UI state; slew/park/abort/exposure don't check the controlling session, so two clients can issue conflicting mount commands. **Fix:** enforce session ownership (409 for non-holders) or document it as advisory.
- **No cap on concurrent WebSockets; no Origin check on upgrade** — `WebSocketEndpoints.cs:71-96`. Cheap DoS via many sockets; cross-origin page can stream the event feed. **Fix:** validate `Origin`, set `MaxConcurrentUpgradedConnections`.
- **Batch-job single-flight is check-then-act** — `InMemoryBatchJobService.cs:43-63`. Racing `POST /platesolve/center` both enqueue → redundant slew/solve, duplicate job ids. **Fix:** atomic `TryAdd`.
- **Non-allowlisted exception strands a job in "running" forever** — `InMemoryBatchJobService.cs:65-99`. Fire-and-forget worker catches only a narrow list; anything else faults the discarded task unobserved and skips the `failed` transition. **Fix:** final `catch (Exception)` marks job failed.
- **Job registry never pruned; per-job CTS never disposed** — `InMemoryBatchJobService.cs:31,62,95-98,137`. Accumulates for the process lifetime on an all-night daemon. **Fix:** dispose CTS in `finally`, evict terminal jobs after a retention window.

### Low
- Unsynchronized `JobState` reads → inconsistent snapshots; cancel/completion race mislabels a finished job "cancelled" (`InMemoryBatchJobService.cs:81-126`).
- Frame file I/O trusts DB `file_path` with no captures-root confinement — no live vuln (paths are server-generated), but a future import endpoint would make it arbitrary read/delete (`SqliteFrameRepository.cs:495-711, 848-904`).
- OpenAPI + Scalar docs exposed unauthenticated in all environments (`Program.cs:845-846`).

**Verified clean:** log-download traversal, tar-slip on sky-data install, zip-slip on restore, §70 export strip, shell-injection (all `UseShellExecute=false`, hardcoded verbs), centering mount serialization + cancellation plumbing, bounded WS send queue, error sanitization.

---

## 2. Core / Equipment / Sequencer

**Assessment:** New code (PauseGate, TakeExposure, MeridianFlipTrigger hardening, PHD2 listener rewrite) is noticeably more careful than the inherited code, with deliberate comments documenting known seams. The sequencer state machine (pause/resume/skip/abort) is consistent. Systemic weaknesses are all on **inherited device-I/O edges**: unbounded poll loops whose timeout parameters are ignored, exceptions thrown where they can't be observed (timer callbacks, discarded tasks), and catch-alls that hide device failures.

### High
- **GPS fix timeout throws inside `Timer.Elapsed` → `GetLocation` hangs forever** — `NMEAGps.cs:58-61,124`. `System.Timers.Timer` swallows the exception; `gotGPSFix` never completes. **Fix:** `gotGPSFix?.TrySetException(...)`.
- **PHD2 `Pause` always sends `true` → resume re-pauses guiding** — `PHD2Guider.cs:392-415`. `Pause(false)` re-sends pause; guiding stays paused after any pause/resume cycle, and the pause branch has no failsafe timeout. **Fix:** send `Parameters = new[]{ pause }`, add the failsafe, null-guard `AppState`.
- **`WaitForAppState` ignores its `receiveTimeout` → `StopGuiding` can hang the abort path** — `PHD2Guider.cs:543-559,795-798`. Several callers pass unbounded tokens (`RestartForLostShiftLock`, `DisconnectPHD2Equipment`); this sits on meridian-flip and park/abort. **Fix:** honor `receiveTimeout` as a linked-CTS deadline.
- **Meridian-flip failure without a wired PauseGate does NOT halt the run** — `MeridianFlipTrigger.cs:192-193` + `SequenceTrigger.cs:142-147`. The `InvalidOperationException` intended to halt is caught by `Run`, marked FAILED, and the sequence proceeds to image past the meridian. Production wires the gate; standalone/embedded runs have a defeated safety fallback. **Fix:** rethrow a designated fatal trigger exception, or call `root.Interrupt()` directly.
- **`BasicQuery.SendQuery` busy-spins forever on EOF, no read timeout** — `TcpRaw/BasicQuery.cs:61-71,79`. A closed planetarium peer makes `ReadAsync` return 0 forever in a CPU-pinning loop; SkytechX/TheSkyX/CdC callers pass `CancellationToken.None`. **Fix:** treat `length==0` as EOF, impose a default deadline.

### Medium
- Guider `Connect` returns `true` when post-connect init failed (`PHD2Guider.cs:255-270`).
- Listener rethrow into a discarded task; connect token doesn't stop a running listener (`PHD2Guider.cs:217,1241-1253`).
- `GenericCamera.DownloadExposure` swallows all exceptions with bare `catch {}` — no logging, cancel indistinguishable from SDK fault (`GenericCamera.cs:249-257`).
- `GenericCamera.AbortExposure` calls `StopExposure()` twice, first call unguarded inside a cancellation callback (`GenericCamera.cs:227-247`).
- `GenericCamera.Connect` leaves SDK connected when `Initialize()` throws → "device busy" until restart (`GenericCamera.cs:177-192`).
- Aborting an `ExternalScript` orphans the child process (`ExternalCommandExecutor.cs:71-77`).
- `SequenceContainer` uses a single process-wide **static** lock, serializing every container's Validate/CheckConditions (`SequenceContainer.cs:58`) — should be an instance field.
- Dither settle race: `Settling=true` set after the RPC returns → up to minutes of dead time per occurrence (`PHD2Guider.cs:356-362`).
- `ConditionWatchdog`: CTS leak per cycle + cancellation exits via unobserved throw; failing op retries every 5s with no backoff (`ConditionWatchdog.cs:52-77`).
- **Event-handler leaks**: sequence items subscribe to the long-lived profile service (`LocationChanged`/`ProfileChanged`) and never unsubscribe; every clone pins another instance for the daemon's lifetime (`SlewScopeToAltAz.cs:50`, `SwitchFilter.cs:58`).

### Low
- `SerialSdk.SendCommand` cache-hit path can leak the semaphore (`SerialSdk.cs:77-83`).
- `NMEAGps.Disconnect` fire-and-forgets `CloseAsync()` then immediately disposes (`NMEAGps.cs:135-140`).
- `MetaGuideListener` leaks two CTSs per datagram (`MetaGuideListener.cs:163-168`).
- `AsyncObservableCollection` is a plain `ObservableCollection` in the headless daemon — no ambient sync context (`AsyncObservableCollection.cs:30-46`).
- `PHD2Guider.Dispose` doesn't cancel the listener or unhook subscribers (`PHD2Guider.cs:62-65`).

**Verified clean:** `PauseGate` (lock discipline, cancellation wins over pause), `SendMessage` CTS-bound reads, `SequenceItem.Run` 2-minute stuck-cancellation escape hatch + exactly-once FAILED contract, §57-adjacent safety interrupts cannot hang.

---

## 3. Flutter Client (`client/`)

**Assessment:** Well-engineered, clearly above typical: an excellent WS transport (resume tokens, capped backoff, §27 takeover semantics, leak-free teardown), a uniformly disciplined Dio layer (timeouts everywhere, typed errors, one base-URL source), tolerant JSON parsing, zero undisposed controllers/timers across 118 UI files, and a thoughtfully hardened loopback server. The dominant defect mode is **copy-degradation**: correct patterns (mounted guards, generation fences, edit-version chains) exist in the flagship files and were cloned into ~25 siblings with the guards dropped. Test volume (~1,530 cases) is high but **inverted against risk**.

### Medium
- **Loopback CSRF → real mount slew** — `stellarium_server.dart:100-103`. DNS-rebind guard is correct, but a plain `fetch('http://127.0.0.1:<port>/araevent', {mode:'no-cors', method:'POST'})` from any page carries the real loopback Host and passes; a forged `addToSequence` drives `createImagingRun` → mount slew (`stellarium_view.dart:198`). Held to Medium by ephemeral port + Chrome/Safari PNA blocking (Firefox does not block). **The only client finding with a physical-world consequence.** **Fix:** per-run secret token in the page URL echoed as a header, or reject non-loopback `Origin`.
- **BuildContext-across-async in screens/widgets** (capture-before-await idiom dropped): `sequencer_toolbar.dart:276` (save succeeds server-side, editor stays dirty), `imaging_tab.dart:85` (`_toggleLiveView` unguarded — `_takeOne` in the same file is correct), `screen_equipment_discovery.dart:403` (setState after modal sheet), `image_library_screen.dart:261,278,295` (filter pills after dialog).
- **`autoDispose` state-after-await family** (real `StateError` crashes fired by WS debounce timers during ordinary navigation): the list notifiers copied the generation guard but dropped the `ref.mounted` half — `sequence_list_state.dart:78`, `live_library_state.dart:100,130`, `calibration_state.dart:91,120,169`, `fault_feed_state.dart:74,96`.
- **Settings hydrate/persist boilerplate** (~14 files): `hydrateFromServer`/`persistToServer` uniformly lack the edit-version/serialization protections `filter_wheel_labels_state.dart` added after review caught exactly this stale-echo bug (a hydrate/PUT-echo landing after a user edit silently reverts it). `equipment_connection_state.dart:106-120` additionally fire-and-forgets unserialized PUTs (out-of-order persistence) with fully silent `catch(_){}`.
- **Site-location fetch failure → lat/lon 0,0** — `site_location_state.dart:60-63`. `DioException → null` conflates "fetch failed" with "no site configured"; the planetarium loads at 0,0 and Tonight's Sky plans for the Gulf of Guinea after a transient hiccup.
- **State layer is bimodal (structural)** — one `RefreshableListNotifier` mixin + one hydrate/persist mixin (the fix already exists in `filter_wheel_labels_state.dart`) would close most Medium findings in a single high-leverage PR.

### Low
- `loadMore()` `catch(_){}` swallows all failures with no user feedback (`live_library_state.dart:131-134`, `calibration_state.dart:121-125`, `fault_feed_state.dart:97-99`).
- Unguarded JSON casts throw raw `TypeError` vs the repo's diagnosable idiom (`autofocus_api.dart:25`, `mount_status.dart:39`).
- Hardcoded `http://`/`ws://` scheme (already TODO'd for §67.4); profile PUT bodies carry tokens in LAN cleartext.
- Asset-serving routes have no Host guard (public AGPL assets only) (`stellarium_server.dart:105-223`); `dispose()` never called (`:307-311`, known #611 follow-up).
- `diagnostics_state.dart:293-330` lazily first-built after the shell holds the WS open → misses accept-time snapshot.
- `SavedServersNotifier.add` and `backup_stream_state` post-await writes lack `ref.mounted` (low blast radius — root providers).

### Test coverage gaps (highest risk, untested)
1. `app_shell.dart` (346 ln) — root navigation shell every feature mounts through.
2. `equipment_device_state.dart` (315 ln, 10 timers) — timer-driven device connect/poll state machine.
3. `create_imaging_run.dart` (284 ln, 13 awaits) — sequence→live-run orchestration.
4. **Plate-solve vertical** (`solve_state` + `plate_solve_api` + `solve_panel`) — entire feature, zero tests, fronts the just-shipped §28/§18.I centering.
5. **Imaging tab + frame pipeline** (`imaging_tab.dart`, `frame_viewer`, `fault_panel`, `frames_api`, `camera_exposure_api`) — primary live-capture surface, only exposure controls covered.

**Verified clean:** WS reconnect (well-tested), `stellarium_server` (tested), wizard screens (mostly tested), sequencer editor widgets (9/10), JS-bridge validation (type-checked dispatch, textContent not innerHTML, 64KB body cap, traversal rejected), `flutter analyze` zero `use_build_context_synchronously`, all controllers/timers disposed across 118 UI files.

---

## 4. Imaging / FITS / Stretch / PlateSolving / Astrometry

**Assessment:** A competent, mostly faithful NINA port (MPL headers intact) with careful new work at the daemon boundary (the `OpenAstroAra.Fits` LibraryImport wrapper, atomic-write path, StarDetector, Skia interop are well-bounds-checked). The weakest area by a clear margin is **ingestion of untrusted files**; the second theme is **cross-platform P/Invoke correctness inherited from Windows-only NINA**.

### High
- **Integer overflow on FITS dimensions** — `FITS.cs:117-121`, `CFitsioFITSReader.cs:61-63`. `NAXIS1/2` cast `long→int`, `width*height` computed in 32-bit; a crafted header wraps `nelem` → undersized buffer handed to cfitsio `fits_read_pix` → native heap overwrite. **Fix:** validate `>0` and `(long)w*h <= ceiling` before any cast, keep pixel count `long`.
- **XISF decompression bomb + uncapped allocations** — `XISF.cs:75-82,199-207,416-441`. `headerLength`/block `size`/`UncompressedSize` drive `new byte[...]` unchecked; `ZlibStream.UncompressBuffer` inflates unboundedly. A few-KB bomb OOM-kills the daemon. **Fix:** validate every size vs `fs.Length` and `w*h*bpp`; inflate through a byte-capped stream.
- **XISF geometry never cross-checked against decoded pixel count** — `XISF.cs:107-115,226-229` → `CFitsioFITS.cs:32`. `img.Length != w*h` → cfitsio reads past the buffer into the written FITS. **Fix:** reject unless `img.Length == (long)w*h*channels`.
- **`Create32BitData` read-before-assign** — `ExposureData.cs:78-84`. Property assigned *after* the branch that reads it; every `int[,]` frame wraps via `(ushort)` — 32-bit camera mode silently corrupts every frame. **Fix:** assign before the branch, clamp not cast.
- **`ImageArrayInt.FlatArray` publishes a partially-filled cache to concurrent readers** — `ImageArray.cs:62-83`. Assigned before the fill loop, no lock/volatile, read concurrently by stats/render/detect/save → non-deterministically wrong statistics and saved files on 32-bit captures. **Fix:** fill a local, publish once.
- **NOVAS structs passed by value where C expects pointers** — `NOVAS.cs:236-258`. `make_object`/`app_planet`/`geo_posvel` work by ABI accident on win-x64/arm64; on System V x86-64 (linux-x64, Intel mac) → garbage/segfault. Latent (no callers today). **Fix:** `ref`/`in` (as `NOVAS_Place:193` already does).

### Medium
- Float/double FITS pixels assumed normalized 0..1 → real ADU float frames load as garbage (`CfitsioNative.cs:292-306`).
- `place`/`iauAtco13`/`iauAtoc13` status codes discarded → missing `JPLEPH` silently yields RA/Dec = 0 (moon/sun/altitude wrong) (`AstroUtil.cs:585,616`, `BasicBody.cs:66`, `Coordinates.cs:242`, `TopocentricCoordinates.cs:102`).
- Rise/set discriminant compared against 1 instead of 0 → high-latitude grazing events dropped (`RiseAndSetEvent.cs:115-140`).
- Decimal-comma DMS/HMS misparsed 10×–1000× (`AstroUtil.cs:502-521`).
- `SiderealShiftTrackingRate.Create` has no RA wrap at 0h/24h → grossly wrong custom tracking rate (`SiderealShiftTrackingRate.cs:33`).
- `BasicBody.Calculate` feeds UTC JD where TT expected (~35″ Moon error); `cal_date` declared returning `double` but C returns `void` (`NOVAS.cs:75-77,180-181`, `BasicBody.cs:43,66`).
- DUT1 permanently stubbed to 0 (~13″ topocentric/LST error) (`DatabaseInteraction.cs:46-51`).
- **Plate solving:** 10-minute timeout is dead code — original token passed instead of `cts.Token`, wedged solver awaited forever (`CLISolver.cs:69-72`); solver process not killed on cancel → orphaned ASTAP processes (`CLISolver.cs:175-176`).
- XISF shuffle `ItemSize` → `DivideByZeroException`/zeroed image (`XISFData.cs:269-285`); LZ4/checksum failures only logged, corrupt buffer used (`XISF.cs:426-430`).
- Per-frame LOH churn with no pooling across Stretcher/Debayer/StarDetector at Live-View cadence → gen-2 pauses on the Pi.

### Low
- `fits_get_img_size` P/Invoke declares `out int[] naxes` (wrong marshaling, unused) (`CfitsioNative.cs:153-154`).
- Header-write `PadRight` can throw on over-long keyword/value (`FITSHeaderCard.cs:123`).
- `OutputDataReceived` subscribed but `BeginOutputReadLine()` never called; stderr not redirected → solver diagnostics lost (`CLISolver.cs:159-173`).
- `AllImageStatistics` subscribes to `PropertyChanged`, never unsubscribes (only true handler leak in scope) (`AllImageStatistics.cs:34`).
- Assorted SOFA/NOVAS P/Invoke return-width mismatches (`short` vs `int`), source-epoch B1950/J2050 treated as J2000, `Atan` not `Atan2` in position-angle, unnormalized sidereal-time getters (`SOFA.cs`, `NOVAS.cs`, `Coordinates.cs`, `AstroUtil.cs`).

**Command-injection risk is low:** `UseShellExecute=false`, single `Arguments` string, image paths from `Path.GetRandomFileName()` in a controlled dir. (Note if the `LocalPlateSolver` Cygwin `bash -c` path ever takes a user-supplied filename.)

**Verified clean:** XXE (DTDs prohibited), Skia interop bounds + disposal, StarDetector bounds math, stream `using` scopes, `OpenAstroAra.Fits` wrapper (validates w/h, atomic write + dir-fsync).

---

## 5. Build, CI, Tests, Licensing, Hygiene

**Assessment:** Unusually good cross-cutting shape. The build enforces nullable + full analyzer catalog + warnings-as-errors across all 14 first-party projects; CI is layered (analyzer gate, runtime smoke probes, qemu arm64 e2e, 3-OS Flutter test + native-build matrices, zizmor, SHA-pinned actions, 4-ecosystem Dependabot); licensing is handled with more care than most commercial projects (documented per-directory split, generated third-party inventory with a CI freshness gate, AGPL §13 disclosure for the Stellarium engine). Findings are mostly edge gaps.

### High
- **SOFA & NOVAS31 vendored sources missing from ALL license inventories** — neither `NOTICE.md` nor `3rd-party-licenses.txt` mentions them (the generator only walks NuGet/pub graphs, so vendored C is structurally invisible). Source redistribution is compliant today (SOFA terms travel in each `.c`), but the moment `libsofa`/`libnovas31` land in the `.deb`, ARA ships SOFA-derived binaries with zero attribution. **Fix:** add SOFA (with the required acknowledgement + unmodified note) and NOVAS31 to `NOTICE.md`, and teach `generate-3rd-party-licenses.py` about vendored native sources — *before* the §14e native-shipping PR.

### Medium
- **`StarDataImport`**: orphaned project on EOL `net6.0-windows7.0`, outside the solution (escapes analyzer gate + CI), references old `System.Data.SQLite 1.0.116` (same CVE class the repo was pinned against), and string-interpolates network-fetched catalog data into SQL (injection into the generated star DB). Dormant NINA artifact. **Fix:** port to net10 + add to solution, or delete.
- **Vendored Accord.Imaging (LGPL-2.1) not named in `NOTICE.md`** — the full source tree is vendored and was reformatted by `dotnet format` (288 files) without LGPL §2(a) change notices, while the notice claims "does not modify Accord." Compliance mechanics are fine (separate assembly, source in-repo, headers intact) but the claim is contradicted. **Fix:** declare the vendored tree, qualify the claim, exclude it from future format sweeps.
- **Test poll-loop pattern** — 139 `Task.Delay`/`Thread.Sleep` sites in the server suite (mostly the *good* bounded-poll variant, but structural). **Fix:** prefer `TaskCompletionSource` signaling / virtual `TimeProvider` for new async tests.

### Low
- SQLitePCLRaw CVE-2025-6965 pin (3.50.3) is present and **still required** — keep, re-verify on each Microsoft.Data.Sqlite bump.
- No central package management — versions duplicated across csproj (e.g. `System.Data.SqlClient 4.9.0` in 8 projects, itself deprecated); a partial Dependabot bump risks version skew. **Fix:** adopt `Directory.Packages.props`.
- Prerelease production deps: `Accord.Math 3.8.2-alpha` (abandoned upstream — the NU5104 carve-out), `file_picker ^12.0.0-beta.7`.
- Dead csproj files: `Accord - Backup.Imaging (NETStandard).csproj` + legacy `Accord.Imaging.csproj` (targets .NET 3.5–4.6.2).
- CI gaps: CodeQL non-gating + `build-mode: none`; Alpaca sim jobs non-required; README promises iOS/Android but CI never compiles mobile.
- Legacy `publish/push.bat` still pushes `*.nupkg` (incl. a `NINA.Accord.Imaging` id) to nuget.org — contradicts branding/release posture. **Fix:** delete.
- Doc drift: Dockerfile vs playbook §11.2 (`EXPOSE 5400` vs 5555), README repo tree omits newer projects.

**Verified clean:** secrets scan (no committed credentials; `Accord.snk` is upstream's public strong-name key), workflow secrets referenced only via `secrets.*` context, per-directory MPL/AGPL split consistently documented, Stellarium AGPL §13 obligation genuinely satisfied, CHANGELOG per-PR convention followed.

---

## Recommended sequencing

**Before any untrusted exposure (network or file):**
1. Server auth + loopback bind + CORS allowlist (Critical #1) — unlocks safe handling of #2, #8.
2. Redact profile secrets on read; gate bug-report/backup behind auth (#2).
3. Validate untrusted XISF/FITS sizes/geometry before allocation or native handoff (#3, #6).

**Before linux-x64 / Intel-mac deployment:**
4. NOVAS by-value → `ref`/`in` (#5); stop swallowing astrometry status codes.

**Correctness on the capture/safety hot path (any release):**
5. `ExposureData` read-before-assign + `ImageArray` cache race (#6).
6. PHD2 `Pause(false)`, `WaitForAppState`/`SendQuery` timeouts (#4); meridian-flip halt fallback (#7).
7. Plate-solve timeout token + process kill on cancel.

**Before shipping native binaries in the `.deb`:**
8. SOFA/NOVAS31 attribution in `NOTICE.md` + license generator (#9).

**High-leverage cleanup (single PRs):**
9. Two shared client mixins (RefreshableListNotifier + hydrate/persist) to close the state-layer copy-degradation family.
10. Central package management; delete dead `StarDataImport`/`push.bat`/backup csproj.

---

*This audit was read-only. Findings are ranked by the auditors' severity judgement; line numbers reflect the repository at the audit date and should be re-confirmed before acting.*
