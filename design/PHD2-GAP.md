# PHD2-GAP.md — openastro-phd2 integration gap analysis

**Purpose:** Capture gaps between what ARA Core's §63 PHD2 lifecycle spec needs and what `openastro-phd2`'s current JSON-RPC surface provides. Each gap is either (a) worth adding to openastro-phd2 to improve ARA integration UX, (b) workable around in ARA v0.0.1, or (c) a documentation tightening rather than an API change.

**Source-of-truth for what exists:** `~/Documents/GitHub/openastro-phd2/doc/jsonrpc_api.md` (998 lines, 93 RPCs, 27 event types, comprehensive coverage).

**Status as of 2026-05-21:** identified during the §63 baking session. No issues opened on openastro-phd2 yet. Pick back up after ARA's other Tier 1 gaps are closed.

**Status update 2026-07-07 — Path A EXECUTED.** Upstream PR **open-astro/openastro-guider#57** (the repo was
renamed from openastro-phd2) implements all three real gaps + the 7 doc clarifications: `DarkLibraryBuild*` /
`DefectMapBuild*` progress events (started / per-frame / complete / failed, with per-artifact partial-count field
names), `EquipmentDisconnected`/`EquipmentReconnected` structured camera-fault events (with the reconnect-throttle
caveat documented), and `get_version` extended with `overlap_support` + `fork:"openastro-guider"` (the RPC already
existed undocumented; the `Version` event also gained a `Fork` key). Merge awaits maintainer review. Review-found
pre-existing daemon issues were filed as openastro-guider#58–61. ARA-side adoption of the new events (progress bar
in the calibration dialog, structured fault routing, get_version handshake) is follow-up work in THIS repo once
#57 merges — note the event-PascalCase vs RPC-snake_case fork-key casing.

---

## Summary

| Category | Count | Action |
|---|---|---|
| **Real gaps** (affect ARA v0.0.1 UX or implementation quality) | 3 | Recommend adding to openastro-phd2 before ARA Phase 12 integration |
| **Nice-to-haves** (workable around in ARA v0.0.1) | 7 | Defer; reconsider during ARA v0.0.1 implementation if friction emerges |
| **Documentation clarifications** | 7 | Cheap to fix in `jsonrpc_api.md`; update before ARA's Phase 12 |

---

## Real gaps

### Gap 1 — Progress events during `build_dark_library` and `build_defect_map_darks`

**Problem:** Both RPCs can take 2-3 minutes to complete. The current spec returns a final result payload but the event catalog has no `DarkLibraryProgress` or equivalent. So either the RPC blocks synchronously for ~2 minutes with no feedback, or it's async and the docs don't document the events.

**ARA UX impact:** the wizard's cover-the-scope modal sits at "Building..." for 2 minutes with no per-frame progress. Users assume it's frozen. Significantly degrades the §63.6 dark-library-build flow.

**Recommend adding (4 new event types per build operation):**

```
Event: DarkLibraryBuildStarted     { profile_id, planned_frame_count, planned_exposures_ms[] }
Event: DarkLibraryFrameComplete    { frame_index, total_frames, exposure_ms, this_frame_seconds }
Event: DarkLibraryBuildComplete    { ... existing return payload ... }
Event: DarkLibraryBuildFailed      { error, partial_frames_completed }

(same pattern: DefectMapBuild*)
```

**Or alternatively:** make `build_dark_library` return `0` immediately (kicked off) and surface progress + final via events — matches the existing `capture_single_frame` → `SingleFrameComplete` pattern.

**Implementation surface:** modest change in `src/event_server.cpp` to emit progress events from the dark-build worker thread.

---

### Gap 2 — Structured equipment-disconnect event

**Problem:** PHD2 emits `Alert` events for many issues, but the docs don't enumerate which conditions fire it or what payload shape it uses. Mid-session USB unplug of a guide camera is the canonical case ARA must handle distinctly (§42.2 equipment-fault matrix). String-parsing `Alert` payloads to detect "camera disconnected" is fragile.

**ARA UX impact:** ARA's hot-reconnect logic (§42.3) needs reliable disconnect detection. Generic `Alert` works but isn't robust.

**Recommend adding:**

```
Event: EquipmentDisconnected   { device_type: "camera"|"mount"|"aux_mount"|"ao"|"rotator", driver_info, reason }
Event: EquipmentReconnected    { device_type, driver_info }
```

ARA would route these directly to `equipment.fault` notifications (§46.3) without parsing `Alert` strings.

**Implementation surface:** add hooks in PHD2's per-device disconnect paths to emit the structured event in addition to whatever `Alert` already fires.

---

### Gap 3 — Direct `get_version` RPC

**Problem:** The catch-up event stream sends `Version` on connect, but there's no synchronous RPC to query it. ARA's §63.9 fork-vs-upstream identification (which surfaces a warning if connected to stock PHD2 instead of openastro-phd2) currently has to wait for the catch-up event to arrive — awkward for a "connect, check version, decide" sequence.

**ARA UX impact:** ARA's connection handshake (§63.2 state machine) wants version info immediately. Workable via the catch-up event but messier.

**Recommend adding:**

```
RPC: get_version
  result: {
    phd_version: "2.6.13",
    fork_identifier: "openastro-phd2",
    build_info: "...",
    api_version: 1
  }
```

5-10 lines in the dispatch table. Tiny addition with clear value.

---

## Nice-to-haves (workable around in ARA v0.0.1)

These would simplify ARA's code but ARA ships fine without them. Promote individually to v0.1.0 if/when implementation reveals friction.

| Missing | ARA's workaround in v0.0.1 | What an API would help with |
|---|---|---|
| **`apply_profile_bundle`** — atomic profile push | ARA sequences `set_connected(false)` → N setters → `set_connected(true)` itself | Single transaction RPC; less round-trip latency and no partial state on errors |
| **`calculate_calibration_step`** — recommended cal step from focal length + pixel + dec | ARA replicates PHD2's GUI calculator math in Dart/C# | Server-side helper would be authoritative + always correct |
| **`get_session_stats`** — rolling RMS / settle / dither aggregate | ARA accumulates from `GuideStep` events into its own session DB for §50 Stats | Saves ARA from maintaining its own buffer; avoids ARA-vs-PHD2 stat divergence |
| **`log_message`** — inject markers into PHD2's log | ARA logs cross-reference timestamps with PHD2's log | Direct injection would simplify forensic debugging across the two log files |
| **`get_calibration_quality`** — derived quality score (RA/Dec orthogonality, step consistency) | ARA computes from `get_calibration_data` vectors client-side | Authoritative server-side score; surfaces in §50 Stats Guiding view without ARA-side math |
| **`Cooler*` push events** | `get_cooler_status` polled every 10 s | Push events on temp change would reduce poll overhead during §28 cooler stabilization |
| **`get_server_health`** — richer than `get_app_state` | ARA pings `get_app_state` for liveness | Process-health view (uptime, RPC count, event queue depth) would improve §63.3 hung-process detection |

---

## Documentation clarifications

Not API changes — things ARA's integrator (and any other downstream consumer) would want spelled out in `jsonrpc_api.md`:

1. **Is `build_dark_library` synchronous or async?** If sync, doc should say it blocks for approximately `frame_count × max(exposure_ms)`. If async, document what events fire and the completion contract.
2. **`Alert` event payload shape** — currently mentioned by name but the payload isn't documented. Suggested: `{ level: "info"|"warning"|"error", message, source, recoverable: bool }`.
3. **`Version` event payload shape** — confirm whether it includes fork identifier (openastro-phd2 vs upstream PHD2) or just PHD2 version number.
4. **List of writable `algo_param` names per axis** — the doc says "use `get_algo_param_names`" but a static reference list (`Aggressiveness`, `MinMove`, `Hysteresis`, `RA-LowpassCutoff`, etc. per axis) would help wizard implementers.
5. **Catch-up event delivery on reconnect** — is `Version` re-sent on every reconnect, or only on the first connect to a fresh server? Affects ARA's reconnect handling.
6. **`Alert` vs other event channels for disconnects** — does cable-pull on a USB camera produce `Alert` or `StarLost` or both? Documenting the canonical event for each fault would let ARA route reliably.
7. **`shutdown` RPC semantics** — does this trigger systemd's `Restart=on-failure` (clean) or does ARA need to handle it as a deliberate stop? Affects ARA's restart-vs-shutdown intent disambiguation.

---

## Decision points

The 3 real gaps + the doc clarifications cluster into a single small workstream. Three paths:

**Path A — Add the 3 real gaps to openastro-phd2 + update the doc.** Best long-term outcome. Modest engineering work (~1 small PR for the RPC + events + doc).

**Path B — Skip the API additions; just update the doc.** ARA works around the gaps. Doc clarifications still ship.

**Path C — Defer everything.** ARA implements workarounds; this doc is the record for future revisit.

**Recommended:** Path A, scheduled after ARA's other Tier 1 gaps close. The additions are small, they meaningfully improve ARA's UX, and they help any future PHD2 client (NINA included).

---

## Decision (2026-05-23): Path A, deferred to post-Phase-12

**Status: settled.** Path A is the plan — file an upstream PR to `openastro-phd2` adding the 3 real gaps (`DarkLibraryProgress` event family, `EquipmentDisconnected`/`EquipmentReconnected` events, `get_version` RPC) plus the 7 doc clarifications.

**Timing: defer the actual PR filing until ARA Phase 12 integration is in place.** Reasoning:
- Phase 12 builds the WILMA wizard's dark-library + equipment-fault flows that consume these events; integration will inform exactly what payload shape + event semantics are most useful
- Specifying RPC shapes ahead of integration risks shipping something subtly wrong upstream that's painful to revise once other PHD2 clients depend on it
- The 3 nice-to-haves + 7 doc clarifications can also be batched into the same upstream PR — total ~1 small PR's worth of work
- ARA v0.0.1 doesn't block on these gaps; the workarounds in the "Nice-to-haves" table above are fine for v0.0.1

**Triggers for filing the upstream PR:**
- ARA Phase 12 sub-PR 12c (Imaging + Framing tab — owns the §51 Health Indicator + §42 fault recovery surface) is merged AND
- Phase 12 sub-PR 12b (Wizard — owns the dark-library build flow per §63.6) is merged

Once those two land, the AI files the upstream openastro-phd2 PR with the validated event/RPC shapes informed by actual integration. The user reviews + merges the upstream PR; ARA then switches its internal workarounds to use the new events/RPCs in a follow-up ARA PR.

**v0.1.0 alignment:** the upstream openastro-phd2 PR ships within ARA's v0.1.0 window. ARA v0.0.1 ships with workarounds; ARA v0.1.0 switches to the new events.

---

## Cross-references

- `design/PORT_PLAYBOOK.md` §63 — PHD2 lifecycle integration spec (assumes current RPC surface; will reference the gaps once they're either filled or worked-around)
- `design/PORT_PLAYBOOK.md` §63.9 — version detection logic that would benefit from Gap 3
- `design/PORT_PLAYBOOK.md` §63.6 — dark library build flow that would benefit from Gap 1
- `design/PORT_PLAYBOOK.md` §42 — equipment fault recovery that would benefit from Gap 2
- `openastro-phd2/doc/jsonrpc_api.md` — source-of-truth API doc
- `openastro-phd2/src/event_server.cpp` — RPC implementation site
- `openastro-phd2/scripts/phd2_rpc_smoke.py` — reference RPC client implementation
