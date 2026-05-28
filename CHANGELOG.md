# Changelog

All notable changes to OpenAstro Ara are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html) —
pre-1.0, breaking changes can happen in any release per the playbook §0.6.

For Ara-specific release tagging convention (`v0.0.1-ara.N`), see
[design/PORT_PLAYBOOK.md §34.5](design/PORT_PLAYBOOK.md). Phase boundary tags
(`phase-N-complete`) are NOT release tags — they're internal milestone markers
used by the port's `port/ara → master` promotion cadence (playbook §22.0).

## How to update this file

Every PR that lands a user-visible change adds a bullet to the **[Unreleased]**
section as part of its diff (convention enforced by the port-driver skill, see
`.claude/skills/port-driver/SKILL.md` step 4). Pick the matching subsection:

- **Added** for new features, endpoints, settings, or capabilities.
- **Changed** for changes in existing behavior.
- **Deprecated** for soon-to-be removed features.
- **Removed** for now-removed features or files.
- **Fixed** for bug fixes.
- **Security** for vulnerability fixes.

Mechanical PRs (pure renames, pure deletes during demolition, doc-only edits to
internal design files) don't need a CHANGELOG entry. When in doubt, add one.

At release time (e.g. `v0.0.1-ara.1`), the **[Unreleased]** section is renamed
to `[0.0.1-ara.1] - YYYY-MM-DD` and a fresh `[Unreleased]` placeholder is added
at the top. This happens in the same commit that pushes the release tag.

---

## [Unreleased]

### Added
- `CHANGELOG.md` at repo root (Keep-a-Changelog format) with backfilled history through `phase-10.5-complete` + the going-forward `[Unreleased]` convention. Convention is reminded by `.claude/skills/port-driver/SKILL.md` step "Open the PR".
- **Phase 11 scaffold** — Flutter WILMA client at `client/openastroara_client/` (`org.openastro.openastroara`, platforms = macos/windows/linux per §18.G mobile-deferred-to-v0.1.0). Pinned Flutter 3.44.0 via `.flutter-version` + pubspec `environment.flutter`. Runtime deps: dio, web_socket_channel, multicast_dns, riverpod, flutter_riverpod, flutter_secure_storage, file_picker. First-run skeleton: `lib/models/server.dart` (AraServer), `lib/services/server_discovery_service.dart` (mDNS scan `_openastroara._tcp.local`), `lib/services/server_api.dart` (dio /api/v1/server/info handshake), `lib/state/server_state.dart` (Riverpod 3.x Notifier-based providers), `lib/screens/first_run_screen.dart` (discovery list + manual entry + handshake panel), `lib/main.dart` (Material 3 dark theme entry). `flutter analyze` clean.
- **Phase 12a — App shell + global infrastructure.** Material 3 dark theme using the §25.2 color tokens (`lib/theme/ara_colors.dart` + `lib/theme/ara_theme.dart`). `AppShell` with `NavigationRail` (5 tabs: Imaging / Framing / Sequencer / Sky Atlas / Options, all stubbed pointing at Phase 12c-12h follow-ups), top equipment bar with §25.3 device-type chips (CAM / FW / FOC / MOUNT / ROT / GUIDE / FLAT / SW / WX / SAFE / DOME — disconnected until Phase 12c wires the Alpaca chooser), bottom status bar with global `StatusIndicator` + bug-report `?` icon stub. `StatusIndicator` widget (§51 health-pill + §53 a11y semantics). `EquipmentChip` widget — the per-device visual primitive. Saved-server persistence via flutter_secure_storage (`SavedServerService` + `SavedServersNotifier` AsyncNotifier); the new `_RootRouter` in `main.dart` routes to FirstRunScreen if no servers saved or AppShell otherwise. FirstRunScreen got a "Save & continue" button — taps it after a successful handshake, writes to the saved-server store, the router auto-swaps to AppShell. Storage hardened against keyring failures so Linux without libsecret degrades to "no saved servers" instead of stranding the user on the error route.
- **Phase 12b — Profile wizard shell.** §37 18-screen / 7-stage wizard scaffold. `ProfileDraft` model (`lib/models/profile_draft.dart`) covers every wizard field — telescope/camera/filter wheel/focuser/mount/rotator/guider/plate-solve/autofocus/file-saving/imaging-defaults/safety/site/sky-data. `WizardController` (`lib/state/wizard_state.dart`) is a Riverpod auto-dispose Notifier with `next` / `back` / `skipCurrent` / `jumpTo` / `snapshot`; tracks skipped screens per §37.8 + emits a `copyWith()` rebuild on terminal-step skip. `ProfileWizard.steps` static catalog maps step number → stage label + screen title. `WizardShell` (`lib/screens/wizard/wizard_shell.dart`) renders progress bar + screen body + bottom nav (Back / Skip / Next / Save Profile) + Save & Exit action; falls back to `Navigator.pop` when no `onComplete` is wired so the wizard route always dismisses. 18 placeholder screens (`lib/screens/wizard/wizard_screens.dart`) — each shows the stage label + title + a one-paragraph description of what the real form will collect (sourced from §37.1-§37.7). Per-screen forms land one-by-one in Phase 12b follow-up PRs. AppShell's bottom status bar gets a "Run profile wizard" entry point that pushes the wizard as a full-screen route.
- **Phase 12c.1 — Imaging + Framing tab cores.** Imaging tab (§25.5.1) now has: `_ImagingHeader` with title + always-visible §51 `StatusIndicator` ("Diagnostics: not connected" until 12c.2 wires the diagnostics stream), `FrameViewer` (placeholder — real `Image.network(previewUrl)` + InteractiveViewer zoom/pan + plate-solve overlay lands in 12c.2), `HistogramStrip` (empty-bin gradient placeholder), `ExposureControlsPanel` (right-side: exposure-seconds + gain + offset + bin + frame type + Take One button + Live View toggle, all Riverpod-backed via `ExposureController` in `lib/state/imaging/exposure_state.dart`). Framing tab (§25.5.2) now has: `_TargetSearchBar` (text input + Search button — bundled catalog lookup lands in 12c.2, query state in `TargetSearchQueryNotifier`), `_SkyChartPreview` (placeholder), `_FramingParamsPanel` (rotation slider, mosaic cols/rows steppers, overlap-pct slider, Set-as-Target button stub — all Riverpod-backed via `FramingController` in `lib/state/imaging/framing_state.dart`).
- **Phase 12h.2-autofocus — Editable Autofocus panel.** §37.11 Autofocus panel is now fully editable. New `AutofocusSettings` model + 12 fields: method (`AutofocusMethod` enum: HFR V-curve / brightest-star HFR / FWHM Gaussian fit), step count (clamped [3, 31] — V-curve needs ≥3 points to fit a parabola, 31 keeps runs short), step size, exposure seconds, binning, AF filter, run-after-filter-change toggle, temp-delta trigger °C, HFR-drift trigger %, every-N-hours trigger, abort-sequence-on-failure toggle, restore-position-on-failure toggle. Positive-value setters reject ≤0; HFR drift clamps to [0, 100]%. 8 new tests, total 128. Built on the shared `EditableTextRow`/`EditableNumberRow` from #95.
- **Phase 12h.2-filenames — Editable File Naming panel.** §29.2 File saving + naming panel made editable for the naming-specific options without duplicating storage's existing state. New `FilenamesSettings` model holds 2 fields: `dateSeparator` (DateSeparator enum: forwardSlash / underscore / dash) + `compressDarksAndBias` (bool, defaults on since dark/bias frames compress losslessly very well). Storage-owned fields (template / format / compression) are surfaced as read-only refs with a "Edit in Settings → Session → Storage" hint to keep one source of truth. The §29.2 token reference list (Date / Target / Frame / Sensor groups) stays. 3 new tests, total 120.
- **Phase 12h.2-site — Editable Site preferences panel.** §37.12 Site panel is now fully editable. New `SiteSettings` model + 10 validated setters: latitude [-90, 90]°, longitude [-180, 180]°, elevation [-500, 9000]m, Bortle class [1, 9], default horizon altitude [0, 90]°, typical seeing (0, 20]″; empty siteName / timeZone rejected. `TwilightDefinition` enum (civil/nautical/astronomical) dropdown. Custom-horizon switch hides the default-altitude row when on (custom horizon polygon import lands in 12h.2b with §36.8). 9 new tests, total 117. Built on the shared `EditableTextRow`/`EditableNumberRow` from #95.
- **Phase 12h.2-display-sync — Shared EditableTextRow/EditableNumberRow.** Extracts the round-1-CR fix from PR #94 into shared widgets at `lib/widgets/settings/editable_field.dart` so all three editable settings panels (Imaging Defaults, Storage, Safety Policies) handle rejected input the same way: setter rejects → `getCanonical` reads the unchanged state → controller text snaps back so the field never shows a value that didn't stick. `didUpdateWidget` syncs external state changes when the user isn't mid-edit. Imaging + Storage panels swap their local `_NumberField`/`_TextField` for the shared widgets; Safety Policies will pick up the shared widget in the next CR cleanup if needed (currently uses an in-panel copy with the same semantics).
- **Phase 12h.2-safety — Editable Safety Policies panel.** §35 Safety Policies panel is now fully editable. New `SafetyPolicies` model + 3 enums (`UnsafeAction`, `AltitudeLimitAction`, `GuiderLostAction`). Panel renders 4 dropdown actions + 3 numeric thresholds (resume delay, meridian pause, guider retry timeout) + 5 boolean toggles. Numeric setters reject negative values at the boundary; enum setters assign directly. 5 new tests, total 108.
- **Phase 12h.2-notifications — Editable Notifications panel.** §54 Notifications panel made fully editable. New `NotificationsSettings` model + `NotificationsSettingsNotifier` (5 channel toggles: in-app banner / OS desktop / sound alert / Pushover token / Telegram bot token; 7 trigger toggles: sequence-complete / sequence-paused / critical-diagnostic / safety-event / autofocus-failed / plate-solve-failed / disk-space-low). Token setters trim whitespace at the boundary. Panel renders 3 Switch channels + 2 token text fields (StatefulWidget controllers per PR #63) + 7 trigger switches with Save snackbar. 6 new tests, total 103.
- **Phase 12h.2-storage — Editable Storage panel.** §29 Storage panel is now fully editable: save directory + filename template (multiline `_TextField` with proper StatefulWidget controller disposal per PR #63 contract), `StorageFileFormat` dropdown (FITS / XISF / FITS+RICE / FITS+gzip), `StorageCompression` dropdown (Off / RICE / gzip). New `StorageSettings` model + `StorageSettingsNotifier` rejects empty strings on `setSaveDirectory` + `setFilenameTemplate` (downstream consumers can't get an empty path). 4 new tests, total 97. §29.1.3 SD-card wear banner stays at the top; free-space probe + §63 directory picker land in 12h.2b alongside daemon round-trip.
- **Phase 12h.2-imaging-b — Editable Imaging Defaults panel UI.** The Imaging Defaults panel (read-only stubs since Phase 12h.1) is now fully editable. Six `TextField` rows for exposure / gain / offset / bin / cooler target / ramp rate, a `DropdownButtonFormField` for default frame kind (Light/Dark/Bias/Flat), and a `Switch` for warm-up at session end. All wired to `imagingDefaultsProvider`. Per the round-1 CR contract from PR #63, the `_NumberField` widget is a `StatefulWidget` with `TextEditingController` + `FocusNode` allocated in `initState` and disposed in `dispose` — never built inline. Commit happens on focus-out and on submit so users can tab/click between fields without explicit enter. A Save button shows a snackbar; daemon round-trip via `/api/v1/profile/imaging-defaults` lands in 12h.2b.
- **Phase 12h.2-imaging — Imaging Defaults state.** New `ImagingDefaults` model + `ImagingDefaultsNotifier` (`lib/state/settings/imaging_defaults_state.dart`) holding the 8 §37.11 imaging-defaults fields: default exposure / gain / offset / bin / frame-kind + cooler target temp (°C) + ramp rate (°C/min) + warm-up at session end. Setters validate at the boundary matching the round-2 ExposureController pattern (no zero/negative exposure, no negative gain/offset, bin >= 1, cooler temp clamped to [-60, 30]°C, ramp 0..10°C/min). 8 new tests bring total to 93. The editable panel UI lands in Phase 12h.2-imaging-b — keeping the notifier + tests in their own focused PR so the form-field controller lifecycle doesn't get reviewed alongside business-logic changes.
- **Phase 12-tests-6 — saved_server_state coverage.** 3 more tests bringing total to 85. `saved_server_state_test.dart`: SavedServersNotifier.build returns persisted list, add appends to state on success, and **add keeps in-memory state even when the underlying service throws** — locks in the cleanup-1 contract (PR #72) that first-run isn't blocked by transient keyring/storage errors on Linux-without-libsecret. Uses a `_FakeSavedServerService` injected via `savedServerServiceProvider.overrideWithValue` so the test owns the throw vs success path.
- **Phase 12-tests-5 — framing + live-view + library-grouping coverage.** Adds 12 more Dart unit tests bringing the total to 82. `framing_state_test.dart` (5): FramingController defaults, setRotationDeg, mosaic params update independently, FramingParams.copyWith preserves untouched fields; TargetSearchQueryNotifier defaults + set. `live_view_state_test.dart` (3): LiveViewController defaults to false, toggle flips, set assigns. `library_grouping_test.dart` (3): LibraryGroupingNotifier defaults to bySession + cycles through all 3, librarySessionsProvider returns 7 fully-populated sessions.
- **Phase 12 CR cleanup round 6 — `package_info_plus` for app version.** Round-1 review of promotion PR #84 caught the hardcoded `_kAppVersion = '0.0.1-ara.0'` in help_dialog.dart — pubspec is the single source of truth at `0.0.1+1`, so the two could drift. Replaced with `package_info_plus ^8.0.2` (newly added dep) read via a `_appVersionProvider` FutureProvider; the diagnostics-copy path also calls `PackageInfo.fromPlatform()` so the clipboard payload always has the freshest version.
- **Phase 12-tests-4 — stats + sky atlas state coverage.** Adds 14 more Dart unit tests bringing the total to 70. `stats_state_test.dart` (9): `statsOverviewProvider` reports 7 sessions / 7 targets / 7 nights / 60 frames over the demo data, total integration is non-zero, avg HFR is in 1.0..3.0 (sanity range); `targetRollupsProvider` returns one rollup per target sorted by integration descending; `bestFramesProvider` returns at most 10 Light frames ranked by HFR ascending. `sky_atlas_state_test.dart` (5): mode notifier defaults to catalogView + toggles, search notifier defaults to empty + updates, `skyImageryAvailableProvider` returns the Phase 12e.1 stub `false`.
- **Phase 12i — §54 Help / Report-a-bug dialog.** AppShell's bottom-status-bar Help button (disabled since Phase 12a per CR's cleanup-4 ask) is now wired to a real dialog. The dialog shows: app version (read at runtime from `package_info_plus` per cleanup-6), active saved server, saved-server count, and links to the GitHub Issues + Wiki. A "Copy diagnostics" button puts a Markdown-formatted issue template on the clipboard with the build info pre-filled + sections for Steps-to-reproduce / Expected / Actual. Clipboard uses the round-2 async + try/catch + context.mounted pattern from PR #70's CSV export; PlatformException failures log via `dart:developer` and show a "Failed to copy" snackbar. Lives at `lib/widgets/help_dialog.dart`.
- **Phase 12 CR cleanup round 5.** 4 findings on promotion PR #80. **1 Major**: `LibrarySelectionNotifier.toggle` now publishes via `Set<String>.unmodifiable(next)` so external consumers can't mutate the selection in place and bypass the notifier (would cause stale UI drift). Added `library_selection_test` case locking in the unmodifiable contract — total 56 tests now. **3 Minor**: `bulk_action_bar.dart` wrapped in `SafeArea(top: false)` so the bottom bar doesn't underlap system gesture/navigation insets; CHANGELOG fixed `LibrarySelectionActiveProvider` → `librarySelectionActiveProvider` casing; PORT_PROGRESS.md "Current" updated to reflect promotion #80 in flight.
- **Phase 12-tests-3 — SequenceController coverage.** Adds 14 tests on top of Phase 12-tests-2 (total 55) for the most complex notifier in the client. `sequence_state_test.dart` covers: demo seed sanity, `findNode` for root + deep child + miss-returns-null, `load` swaps root + clears selection, `addChild` refuses instruction parents (12h.2 single-root invariant), `addChild` refuses `SequenceNodeKind.root` (12-cr-cleanup-2 contract), `addChild` appends + auto-selects, `addSiblingAfter` inserts at idx+1 + selects, `addSiblingAfter` refuses root, `moveSelectedUp` swaps with previous sibling, no-op on first sibling, `moveSelectedDown` swaps with next, `deleteSelected` removes + clears selection, no-op on root, `moveSelected` with null selection no-op.
- **Phase 12-tests-2 — wizard + exposure + tab-index notifier coverage.** Adds 18 more Dart unit tests on top of the 23 from Phase 12-tests, bringing the total to 41. `wizard_state_test.dart` (8) — locks in step clamping (next never exceeds totalSteps, back never < 1, jumpTo `clamp` correctness over [-5, totalSteps+100], 7]), skipCurrent advances + tracks skippedScreens, skipCurrent on the terminal step still emits a new state (so the §37 banner re-renders per CR finding on PR #56), ProfileWizard catalog covers all 18 steps. `exposure_state_test.dart` (7) — round-2 validation contract: setExposure rejects zero/negative, setGain/setOffset reject negative, setBin rejects < 1, setFilterSlot rejects empty, setFrameKind accepts any enum. `app_shell_state_test.dart` (3) — SelectedTabIndexNotifier defaults to 0, accepts 0..4, rejects negative + ≥5.
- **Phase 12-tests — initial unit tests + CI integration.** 23 Dart unit tests across 3 files seed test coverage for the most-critical state + model invariants — replaces "just `flutter analyze`" with executable verification. `library_selection_test.dart` (6 tests): notifier starts empty, toggle adds-then-removes, multi-id accumulates, clear drops everything (and is identity-no-op when already empty), contains/membership. `settings_search_test.dart` (9 tests): empty + non-positive limit guards, exact-label rank, "dither"→Guider, "park"→Mount + Safety Policies, group-label match, no-match, limit cap, registry covers all 22 panels. `sequence_node_test.dart` (8 tests): default + nested params/children are unmodifiable (the round-3 deep-freeze contract), deep equality + hashCode over nested lists, copyWith preserves untouched fields + sentinel clears nullable instructionType, isContainer matches kind. New `client-test` CI job (`.github/workflows/ci.yml`) runs `flutter pub get && flutter analyze && flutter test` against the pinned `.flutter-version`; Flutter setup uses `subosito/flutter-action` pinned to commit SHA per zizmor immutable-ref policy.
- **Phase 12f.3a — Library multi-select skeleton.** §40.8 selection mode for the Image Library. New `librarySelectionProvider` Notifier (Set\<String\> of frame ids) backs a `librarySelectionActiveProvider` derived bool. `FrameThumbnail` gains `selected` / `selectionMode` / `onLongPress` props — long-press any thumbnail enters selection mode and adds that frame; subsequent taps toggle selection (and tap-to-open-viewer is short-circuited while in selection mode). New `LibraryBulkActionBar` slides up from the bottom whenever the selection set is non-empty, showing a Material-elevation card with: "N selected" + Close (clears selection), and 5 disabled-for-now action buttons — Rate, Tag, Move to session, Export, Delete (last in `AraColors.accentBusy` to signal destructive). The action handlers wire up in Phase 12f.3b once `/api/v1/frames/bulk` lands.
- **Phase 12 CR cleanup round 4.** Five real findings from promotion PR #71 round-4 review. **3 Major**: `app_shell.dart` bottom status bar height bumped 32px → 40px so the TextButton.icon launchers don't clip at 110% text-scale; `image_library_screen.dart` AppBar `PreferredSize` bumped 48 → 64 to fit the dropdown's intrinsic 48px + 16px vertical padding; `diagnostics_mode_panel.dart` mode options wrapped in `Semantics(label, selected, button)` so screen readers announce the active mode (the Icon-based indicator alone is visual-only). **2 Minor**: `main.dart` error screen no longer interpolates raw exception text; uses generic message and logs internal details via `dart:developer`; `image_library_screen.dart` session header texts wrapped in `Expanded`/`Flexible` with `maxLines: 1` + `TextOverflow.ellipsis` so long target/site names don't blow out the row. The 2 round-4 "Criticals" about a missing `lib/settings/registry.dart` have CR hallucinating a file that was never part of the design — our settings registry lives at `lib/state/settings/settings_nav.dart` (settingsTree) and `lib/state/settings/settings_search.dart` (per-panel keyword corpus) per Phase 12h.1; replied on those threads.
- **Phase 12 CR cleanup round 3 — deep-freeze `SequenceNode.params`.** Round-3 CR review of promotion PR #71 caught that the round-2 switch to `DeepCollectionEquality` made comparison deep but the constructor only shallow-froze the top-level map (`Map.unmodifiable(params)`). Nested `List`/`Map` values could still be mutated through the wrapper, which would change `==`/`hashCode` after construction. New `_freezeParams` + `_freezeDeep` helpers recurse through Maps and Lists wrapping each with the unmodifiable variant.
- **Phase 12 CR cleanup round 2.** Round-2 promotion-PR #71 review surfaced 8 new findings (not duplicates of round-1; CR's expanded scan caught additional issues). **5 Major**: `SequenceNode` equality/hashCode used `mapEquals`/`Object.hashAllUnordered` which is shallow on nested params (the demo `ForEachFilter` loop's `filters: ['L','R','G','B']` list would make structurally-identical nodes compare unequal); now uses `DeepCollectionEquality` from `package:collection ^1.19.1` (newly added). `SelectedTabIndexNotifier.select` clamps to 0..4. `ExposureController` setters reject invalid values (negative duration, gain/offset, non-positive bin, empty filter slot). `CalendarHeatmap` aggregates only dates within the 49-day window so out-of-window sessions don't wash out cell contrast. `ImageLibraryScreen` header chips wrapped in `SingleChildScrollView` for narrow-window responsiveness (same pattern as sequencer_toolbar in round 1). **3 Minor**: `AppShell` Help button `onPressed: null` until §54 dialog lands. `FrameThumbnail` rating clamped to 0..5. `DataManagerModal` MPC asteroid catalog size `'placeholder'` → `'TBD'`.
- **Phase 12 CR cleanup.** Batch-fixes 13 of the 14 CR findings raised on promotion PR #71 (the 14th — adding a filter-slot control to ExposureControlsPanel — is deferred to PORT_TODO.md as a Phase 12c follow-up feature). **3 Critical**: `clamp(...)` returns `num`, not the receiver's type — `.toInt()` added to `wizard_state.dart` jumpTo, `.toDouble()` added to `histogram_strip.dart` alpha + `frame_quality_chart.dart` yMax. **4 Major**: `wizard_shell.dart` Save & Exit now always dismisses the route regardless of whether `onComplete` was wired; `saved_server_state.dart` `add` keeps in-memory state updated on persist failure so first-run isn't blocked by transient keyring errors; `sequence_state.dart` `addChild` + `addSiblingAfter` refuse `SequenceNodeKind.root` to preserve the single-root invariant; `calendar_heatmap.dart` anchor uses real `DateTime.now()` (not a hardcoded 2026-05-27). **6 Minor**: framing_tab `Set as Target` button disabled until 12c.2 wires it; imaging_tab status pill tap handler passes `null` instead of an empty closure; equipment_chip semantics report button affordance for either `onTap` *or* `onLongPress`; sequencer_toolbar buttons wrapped in `SingleChildScrollView` to prevent narrow-window overflow; guiding_rms_chart x-axis title corrected from "session #" → "Session date" to match the M/D tick labels; PORT_PROGRESS.md updated for this PR.
- **Phase 12g.3 — Stats CSV export.** Wires the previously-disabled "Export CSV" button on the Stats dashboard into a `PopupMenuButton` offering two flavors: **Sessions summary** (one row per session — date / target / site / frame count / integration minutes / avg HFR / guiding RMS RA-Dec) and **Per-frame details** (one row per frame — 17 columns covering the full §40.5 metadata grid). RFC-4180 quoting in `lib/services/stats_csv_export.dart` handles commas, quotes, and newlines in target/site names. Selecting a flavor copies the CSV to the clipboard via `flutter/services` `Clipboard.setData` and shows a snackbar with the row count. Real file-save dialog (file_picker `saveFile` API differs per platform) lands in 12g.4.
- **Phase 12d.5 — Sequencer Add Child / Add Sibling actions.** Two new icon-button menus in the InstructionEditor header: Add Child (`+`) is enabled for container nodes only; Add Sibling (`↳`) is enabled for any non-root node. Each opens a `PopupMenu` of 13 ready-made specs — 4 containers (sequential / parallel / conditional / loop), 1 target, 8 common instructions (SlewToTarget, AutoFocus, TakeManyExposures, SwitchFilter, WaitForAltitude, WaitForTime, DitherBetweenFrames, ParkMount). Backing `SequenceController` methods (`addChild`, `addSiblingAfter`) walk the immutable tree and rebuild via copyWith, stamp new nodes with a `node-{epochMs}-{counter}` id, and select the new node so the user can edit it immediately. Editable params + drag-and-drop reorder land in 12d.6.
- **Phase 12d.4 — Sequencer node reorder + delete.** Three new icon-buttons in the InstructionEditor (Move up, Move down, Delete) drive new `SequenceController` methods (`moveSelectedUp` / `moveSelectedDown` / `deleteSelected`). The internal `_withParentModified` helper recursively finds the selected node's parent and rebuilds the path back to the root with `copyWith` so the immutable tree's invariants stay clean. Buttons are disabled when the root is selected; Delete also clears the selection (since the id is dangling). Drag-and-drop reorder + Add Child / Add Sibling actions land in Phase 12d.5.
- **Phase 12d.3 — Sequencer conditional/loop UI.** The §38.5/§38.6 `loopContainer` + `conditionalContainer` kinds (added to the `SequenceNode` enum in 12d.1 but not previously demoed or rendered specially) now have visible UI. Demo sequence gains a `ForEachFilter` loop under M42 (iterates over L/R/G/B with `$$ITER$$` substitution) and an `IfCondition` conditional under NGC 7000 (skip-if-unsafe per §35 with explicit `elseBranch: 'skipTarget'`). `InstructionEditor` renders a blue condition banner showing the boolean expression + an "Else branch" section explaining what happens when false (skipTarget / skipArea / abortSequence / continue), and an amber loop banner showing iteration values + label template. Tree icons (`alt_route` for conditionals, `loop` for loops) were already wired in 12d.1 — Phase 12d.3 just gives them content to render.
- **Phase 12g.2 — Real fl_chart visualizations.** Replaces the four Phase 12g.1 placeholder cards in the §50 Stats dashboard with real charts via `fl_chart ^1.1.1` (added as a runtime dep). **Focus & Temperature** — `ScatterChart` plotting (sensor temp, focus position) per captured frame across all sessions; slope reveals the §37.4 temp-comp coefficient. **Guiding RMS Trends** — `LineChart` with one line per axis (RA blue, Dec amber) over chronological session ordering; sessions without guider data are skipped. **Frame Quality** — `BarChart` of per-session avg HFR, color-banded (blue ≤ 1.70, amber ≤ 2.00, red > 2.00). **Calendar Heatmap** — GitHub-style 49-cell tile grid (fl_chart has no native heatmap) over the rolling window ending at the demo "today"; cell shade scales with that night's integration minutes, with a Less→More legend. Shared `ChartCard` wrapper keeps title/subtitle/border treatment consistent. Demo library expanded from 2 sessions to 7 (spanning ~6 weeks) so the charts have enough data to be meaningful — added `CaptureSession.guidingRmsRa` / `guidingRmsDec` nullable fields populated for each demo session. Phase 12g.3 wires real `/api/v1/stats/*` data + per-target detail + CSV export.
- **Phase 12h.3 — §61 ⌘K smart search palette.** Global ⌘K / Ctrl+K shortcut anywhere in AppShell opens a centered command palette (`lib/widgets/command_palette.dart`). The Search settings (⌘K) button in the Settings shell header now also opens it. Indexes all 22 panels from the `settingsTree` registry built in 12h.1 + a 22-entry per-panel keyword corpus (`lib/state/settings/settings_search.dart`) so users can find panels by intent (e.g. "dither" → Guider, "cooler" → Camera + Imaging Defaults, "park" → Mount + Safety Policies). Scoring: exact label match (1000) > prefix (500) > contains (300) > group match (250/200) > keyword exact (180) > keyword prefix (120) > keyword contains (80). Top 20 results, ↑↓ to navigate, Enter to jump, Esc to close. Activating a result selects the Options tab + sets `selectedSettingsPanelProvider` via the new `selectedTabIndexProvider` (lifted from `_AppShellState` local state so external triggers can drive tab selection).
- **Phase 12h.2 — Remaining settings panels (read-only stubs).** Brings the §25.5.5 settings tree from 3 rendered panels up to **22** by adding 18 new read-only panel stubs (1 helper widget + 18 panel files). Equipment cluster (10): Camera (§52 connection + sensor + cooling), Mount (slew/track/meridian/limits), Focuser (movement + AF triggers), Filter Wheel (8 slots + per-filter focus offsets), Rotator, Guider (PHD2 connection + dithering + calibration), Flat panel (CoverCalibrator wiring per §10.6), Dome (slaving), Weather (thresholds + polling), Safety Monitor. Imaging extras (2): Autofocus (§37.11 algorithm + triggers + safety), Plate Solving (§37.10 solver + slew/sync). Session (2): File saving + naming (§29.2 template + 4 token groups), Notifications (§54 channels + triggers). Safety (2): Policies (§35 weather/meridian/altitude/guider/session-end), Site (§37.12 location + horizon + conditions). Sky Atlas (1): Sky Data — launches existing §36.2 DataManagerModal. Profile (2): Active profile (4 metadata fields + 5 action buttons), Run wizard again (launches the §37 18-screen WizardShell from Phase 12b). All 22 panels share a new `SettingsRow` / `SettingsSectionHeader` widget pair (`lib/widgets/settings/settings_row.dart`) so every label/value pair has consistent typography. Editable forms + persistence to `/api/v1/profile/*` land in Phase 12h.2b/c; §61 ⌘K smart search lands in 12h.3.
- **Phase 12h.1 — Settings shell + tree nav.** §25.5.5 Options tab now opens a real `SettingsShell` (left-side 6-group tree: Equipment / Imaging / Session / Safety / Sky Atlas / Profile, right-side selected panel). Tree backed by `settingsTree` const (`lib/state/settings/settings_nav.dart`) — single source of truth for every settable option in the app, which Phase 12h.3's §61 ⌘K smart-search will index. `selectedSettingsPanelProvider` Notifier owns the active panel id (defaults to `img.defaults`). Three panels are fully rendered: **Imaging Defaults** (`img.defaults`) — read-only stub for default exposure/gain/offset/bin/frame-type + cooling target/ramp/warmup; **Storage** (`session.storage`) — §29 form stub with the §29.1.3 SD-card-wear warning banner + save dir / free space / format / compression / filename template rows; **Diagnostics Mode** (`safety.diagnostics`) — §51 mode picker (notify only / pause on critical / abort on critical) with per-mode descriptions. All other panels show a generic placeholder that confirms the routing works and notes the form lands in Phase 12h.2. Panel header has a disabled "Search settings (⌘K)" button that wires up in 12h.3. Editable forms + persistence to the active profile JSON via `/api/v1/profile/*` endpoints land in Phase 12h.2.
- **Phase 12g.1 — Stats dashboard skeleton.** Full-screen `StatsDashboardScreen` (§50) reachable from AppShell's bottom status bar. **Overview tiles** — 6 stats computed live from `librarySessionsProvider`: Nights, Targets, Sessions, Frames, Total Integration (`Xh Ym`), Avg HFR. **Targets section** — `TargetRollup` cards listing per-target session count + frame count + integration time + avg HFR, sorted by integration descending. **Best Frames** — top 10 lights by lowest HFR, ranked with avatar number + filename + HFR + star count + filter + rating stars. **Visualizations section** — 4 placeholder cards (Focus & Temperature scatter, Guiding RMS trends, Frame Quality composite, Calendar heatmap) — real `fl_chart` renders land in Phase 12g.2. CSV export button (disabled until 12g.2). Riverpod providers: `statsOverviewProvider`, `targetRollupsProvider`, `bestFramesProvider` all derive from the library state.
- **Phase 12f.1 — Image Library skeleton.** Full-screen `ImageLibraryScreen` (§40) — sessions grouped By Session (toggle to By Target / By Date in 12f.2), each session card shows date + target + site + total integration + filter counts + thumbnail strip + Capture Matching Flats / Resume Target buttons (stubs until 12f.2). `FrameThumbnail` widget renders the filter label + rating stars over a placeholder square (12f.2 swaps in `Image.network`). `FrameViewerScreen` (§40.5) opens on thumbnail tap — shows filename + 5-star rating + preview placeholder + 12-field metadata panel (exposure/gain/offset/filter/bin/HFR/stars/median ADU/background/sensor temp/focus steps/captured time) + 6-button action bar (Rate/Tag/Open in App/Show in Folder/Download FITS/Delete, all disabled in 12f.1). `CaptureSession` + `CapturedFrame` data models with `totalIntegration` + `framesByFilter` getters. Demo data seeds two sessions (M42 with 6 L frames, NGC 6188 with 3×3 Hα/OIII/SII frames). AppShell bottom status bar gets an "Image Library" launcher between "Run profile wizard" and the help icon. Real backend (`/api/v1/sessions` + `/api/v1/frames`), stretch picker (§65), auto-rating + HFR drift display (§40.7), bulk operations (§40.8), backup UI (§43/§44) land in Phase 12f.2/.3.
- **Phase 12e.1 — Sky Atlas tab + Data Manager modal scaffold.** Sky Atlas tab body (§25.5.4 + §36) with `SegmentedButton` mode toggle (Catalog View / Tonight's Sky), universal search bar (`SkyAtlasSearchNotifier`), Data Manager launcher, and an Aladin placeholder that responds to mode + search query (real `webview_flutter` Aladin Lite embed in 12e.2). `DataManagerModal` is a full-screen 4-tab dialog (§36.2): Sky Imagery (21 surveys grouped by wavelength), Star Catalogs (Tycho-2 / GAIA DR3 / UCAC4 / HD / HIP / Bayer-Flamsteed), Thumbnails (Famous Targets Pack + per-target placeholder), Solar System (DE440 ephemerides + MPC asteroid v0.1.0 deferral note). All download buttons disabled until 12e.2 wires `/api/v1/data-manager/*`. `SkyDataMissingBanner` (§36.13) — amber strip across the top when no HiPS survey downloaded yet; "Open Data Manager" CTA jumps to §36.2 inline. Riverpod state: `skyAtlasModeProvider` + `skyAtlasSearchProvider` + `skyImageryAvailableProvider`.
- **Phase 12d.1 — Sequencer skeleton.** Tree-based sequence editor per §25.5.3 + §38: `SequenceNode` data model in `lib/models/sequence/sequence_node.dart` (root/area/target/4×container/instruction kinds, params map, recursive children list, value-equality + copyWith). `SequenceController` Notifier (`lib/state/sequencer/sequence_state.dart`) seeds an in-memory demo sequence (Tonight area → M42 + NGC 7000 targets → Slew/AutoFocus/Capture/Wait instructions) so the tree renders on first open. `SelectedNodeIdNotifier` tracks the selected node id; `findNode(root, id)` walks the tree. `SequencerTab` body has: `SequencerToolbar` (New / Load / Save / Validate / Run / Pause / Abort, all disabled until 12d.2 wires the `/api/v1/sequences` endpoints), `SequenceTree` (flat indented ListView with kind-icon + display name + instruction-type label, click-to-select with highlight), `InstructionEditor` (right pane showing selected node's kind + instructionType + params table + child summary; read-only in 12d.1, editable in 12d.2). NINA `.json` import flow + conditional/loop UI + template instantiation + Run wiring all land in Phase 12d.2/12d.3.
- **Phase 12c.2 — Live View Notifier + Diagnostic Panel.** Live View toggle lifted out of `_ImagingTabState` widget state into `liveViewControllerProvider` (`lib/state/imaging/live_view_state.dart`) so cross-component observation works (bottom status bar, sequence-runner pause, etc.). §51 always-visible Diagnostic Panel (`lib/widgets/imaging/diagnostic_panel.dart`) renders below the histogram — collapsed by default, expanding shows recent events with per-event status dot + source + timestamp. `diagnosticsStateProvider` is the data source — stubbed in 12c.2 as "Diagnostics: not connected"; real `/api/v1/ws` event stream (`diagnostics.*` events per §60.9) wires up in Phase 12c.3 alongside Take One + frame-fetch.

---

## Pre-release history (backfilled 2026-05-26)

The port started by forking `nighttime-imaging/NINA` and demolishing the WPF
client to leave a `.NET 10` daemon + Flutter client architecture (see
`design/PORT_PLAYBOOK.md` for the full plan). The work below is grouped by
phase boundary; each phase ends with a `phase-N-complete` git tag.

### [phase-10.5-complete] - 2026-05-26

#### Added
- `packaging/debian/` overlay tree for the arm64 `.deb` package — `DEBIAN/control` (Recommends alpaca-bridge + openastro-phd2), `DEBIAN/postinst` (creates `openastroara` user, joins dialout/video/plugdev groups, sets `CAP_SYS_TIME` on the binary, refreshes `systemd-tmpfiles`, validates the sudoers drop-in, enables + starts the service), `DEBIAN/prerm` (graceful stop), `DEBIAN/postrm` (cleanup on purge), hardened `openastroara-server.service` per playbook §13 (NoNewPrivileges + ProtectSystem=strict + ProtectHome + PrivateTmp + RestrictAddressFamilies + CapabilityBoundingSet=CAP_SYS_TIME + SystemCallFilter=@system-service), `sudoers.d/openastroara` (passwordless sudo limited to update.sh + configure-storage.sh), `logrotate.d/openastroara` (daily rotation w/ copytruncate), `tmpfiles.d/openastroara.conf` (creates `/var/run/openastroara` on boot), `etc/openastroara/server.env.example`. (PR #49)
- `packaging/build-deb.sh` — assembles the `.deb` from a `dotnet publish` output + version string. Validates `visudo -cf` and runs `dpkg-deb --build --root-owner-group`. (PR #49)
- CI: `server-build` job now builds the `.deb` on every push and uploads it as an artifact (`openastroara-server_<version>_arm64.deb`, 30-day retention). (PR #49, PR #51)

#### Changed
- systemd unit `PrivateDevices=false` rationale moved off the inline trailing comment to a dedicated comment line — systemd-analyze doesn't accept inline comments on key=value lines. (PR #51)
- CI `.deb` version detection tightened to `git describe --tags --exact-match --match 'v*'` so non-release tags like `phase-10.5-complete` don't get used as Debian Version fields. (PR #51)

### [phase-10-complete] - 2026-05-26

#### Added
- Repo-root `Dockerfile` for the arm64 daemon (`mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-arm64v8` base; `EXPOSE 5555` to match the daemon's actual default port; `USER 1000` per §13 hardening). (PR #46)
- CI `server-build` job: `dotnet build` + `dotnet publish OpenAstroAra.Server -c Release -r linux-arm64 --self-contained -p:PublishAot=false` + ELF-arch verification + arm64 Docker buildx via QEMU. Implements the long-promised Phase-0.5p + Phase-4 CI growth. (PR #46)
- `.claude/skills/port-driver/SKILL.md` — autonomous port-driver Claude Code skill that drives sub-PRs under `/loop /port-driver` per design/COMMIT-PR-RULES.md (orient → branch → CR poll/fix → merge → advance phase). Includes the 2026-05-26 `/review` fallback policy for CR rate-limit. (PR #44, clarifications in PR #48)

#### Changed
- `OpenAstroAra.Core/Utility/Notification/Notification.cs` — warning/error variants now route to `Logger.Warning`/`Logger.Error` (with `[CallerXxx]` attribute propagation so the original call site is preserved) instead of dropping operational issues silently. Info/Success stay no-op. (PR #43)
- `OpenAstroAra.Core/MyMessageBox/MyMessageBox.cs` `Show(...)` — affirmative defaults (Yes/OK) map to safe non-affirmative results (No/Cancel) so `SequenceHasChanged.AskHasChanged` no longer silently auto-detaches. Real user choice replaces this when the §35/§60.9 modal-event flow lands. (PR #43)
- `OpenAstroAra.Core.csproj` — added `System.Management 10.0.0` for WMI usage in `Logger.cs` + `SerialPortProvider.cs`. (PR #43)
- `OpenAstroAra.Astrometry/DatabaseInteraction.cs` — reduced to `GetUT1_UTC` stub (honors `CancellationToken`) + `GetDisplayAlias` (full Levenshtein preserved). All other EF6-backed methods removed because their return types referenced deleted schema. (PR #43)

#### Removed
- `OpenAstroAra.Equipment/Equipment/MyCamera/EDCamera.cs` (Canon EDSDK vendor impl, ~1162 lines). (PR #43)
- `OpenAstroAra.Equipment/Equipment/MyGuider/MGENGuider.cs` (MGEN guider, ~596 lines). (PR #43)
- `OpenAstroAra.Equipment/Utility/ASCOMInteraction.cs` (ASCOM COM enumeration factory). (PR #43)
- `OpenAstroAra.Equipment/Equipment/MyFlatDevice/{AllProSpikeAFlat,AlnitakFlatDevice,ArteskyFlatBox,PegasusAstroFlatMaster}.cs` (vendor flat-device impls). (PR #43)
- `OpenAstroAra.Test/FlatDevice/{AlnitakFlatDeviceConnectTest,AlnitakFlatDeviceTest,PegasusAstroFlatmasterTest}.cs` (orphaned tests). (PR #43)

#### Fixed
- Phase 6 ASCOM Alpaca SDK signature bug in `AlpacaEquipmentProvider.cs`: `deviceType:` → `deviceTypes:` + added missing `logger: null` param. Built-but-broken since PR #38 — CI was sanity-only so the build error never surfaced. (PR #43)

### [phase-9-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 9 endpoint surface — log/state + WebSocket + notifications + stats + system scaffold across ~141 endpoint registrations in 11 endpoint files. 75+ WS event tokens catalogued. `GET /api/v1/ws/catalog` returns the live catalog dump. (PR #41)

### [phase-8-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 8 endpoint surface — image + session + backup + diagnostics scaffold. ~15 image DTOs (incl. composite quality score per §50.10, HFR analysis time series, backup-claim DTO), 4 service interfaces, 16 image endpoints, 3 diagnostics endpoints. (PR #40)

### [phase-7-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 7 endpoint surface — sequence + calibration + mosaic scaffold. ~16 sequence records, 8 service interfaces, 16 sequence endpoints (incl. `PATCH /api/v1/sequences/{id}` for partial updates), 6 calibration endpoints, 6 mosaic endpoints (panel DTO includes §47.3 `crosses_ra_wrap` flag). (PR #39)

### [phase-6-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server` Phase 6 equipment scaffold + Alpaca discovery — 12 DTO records covering all device types, 12 service interfaces, functional `EquipmentDiscoveryService` (Alpaca UDP discovery on port 32227 via `ASCOM.Alpaca.Discovery.GetAscomDevicesAsync`), `GET /api/v1/equipment/discover/{type}` operational, per-device 501 stubs. Global `JsonStringEnumConverter` with `LowerCaseNamingPolicy`. (PR #38)

### [phase-5-complete] - 2026-05-26

#### Added
- `OpenAstroAra.Server/openapi.yaml` — full v0.0.1 API contract. 6 endpoint groups (Server / Equipment / Sequence / Image / Log / Stream). Cursor-based pagination per §60.2. `Idempotency-Key` header per §60.5. RFC 7807 `Problem` schema. `ServerStateSnapshot.ws_resume_token` for §60.9 resume. Full WebSocket protocol documented. OpenAPI 3.1 null unions throughout. (PR #37)

### [phase-4-complete] - 2026-05-25

#### Added
- `OpenAstroAra.Server` project (`Microsoft.NET.Sdk.Web`, `net10.0`, AOT in Release). Kestrel port resolution env → appsettings → 5555. `/healthz` + `/api/v1/server/info` operational. Scalar UI mounted at `/scalar/v1`.

### [phase-3-complete] - 2026-05-25

#### Changed
- `PHD2Guider.cs` now logs `openastro-phd2` vs upstream PHD2 detection via `PHDSubver`/`PHDVersion` substring match. Copyright mojibake fixed.

### [phase-2-complete] - 2026-05-24

#### Changed
- Equipment layer collapsed to Alpaca-only per §52. Added `IEquipmentProvider` per §6.2 with `DiscoverAsync` + `ConnectAsync<T>`.

### [phase-0.5p-complete] - 2026-05-24

#### Changed
- .NET 10 SDK pin (`global.json`) + csproj `TargetFramework` bumps. First build cleanup pass (Astrometry+Core+Test forced to `net10.0-windows` TFM, `UseWPF=true` restored where local build revealed deep WPF deps).
- Solution renamed `NINA.sln` → `OpenAstroAra.sln`. `.gitignore` rewritten.

#### Removed
- `OpenAstroAra.Core/Database/**` per §56 NINA-DB-greenfield (NINADbContext + BrightStars / Constellation / ConstellationBoundary / DsoDetail / EarthRotationParameters / VisualDescription / etc.).
- `OpenAstroAra.Core/Utility/Notification/CustomDisplayPart.xaml{,.cs}` (WPF toast UI).
- `OpenAstroAra.Core/MyMessageBox/MyMessageBoxView.xaml{,.cs}` (WPF dialog view).
- Various `Datatemplates.xaml{,.cs}` + `ProgressStyle.xaml` across Sequencer.

### [phase-0.5n-complete] - 2026-05-24

#### Changed
- Project renames: `NINA.Astrometry` → `OpenAstroAra.Astrometry`, `NINA.Profile` → `OpenAstroAra.Profile`, `NINA.Image` → `OpenAstroAra.Image`, `NINA.Equipment` → `OpenAstroAra.Equipment` (rename + cascade scrub of `using NINA.Equipment`), `NINA.Sequencer` → `OpenAstroAra.Sequencer`, `NINA.Platesolving` → `OpenAstroAra.PlateSolving`, `NINA.Test` → `OpenAstroAra.Test`.

### [phase-0.5g-complete] - 2026-05-24

#### Changed
- Project rename: `NINA.Core` → `OpenAstroAra.Core`.

### [phase-0.5f-complete] - 2026-05-24

#### Removed
- Stefan Berg / NINA branding stripped from user-facing strings.
- All non-English locale `.resx` files (en-US + en-GB only henceforth).

### [phase-0.5d-complete] - 2026-05-24

#### Removed
- ASCOM COM glue across `NINA.Equipment` (replaced by Alpaca in Phase 2).

#### Changed
- CI workflow hardened — `actions/checkout` pinned to commit SHA + `persist-credentials: false` per zizmor + CodeRabbit security findings on PR #12.

### [phase-0.5c-complete] - 2026-05-24

#### Removed
- All vendor SDK directories (Canon/Nikon/ZWO/QHY/etc.) and their vendor concrete equipment impls. Replaced by Alpaca-only providers in Phase 2.

### [phase-0.5b-complete] - 2026-05-23

#### Removed
- `NINA.MGEN`, `nikoncswrapper`, WiX setup projects, NINA Plugin scaffolding.

#### Added
- The four §1 tracking files (PORT_PLAYBOOK.md / COMMIT-PR-RULES.md / PORT_PROGRESS.md / PORT_TODO.md) added retroactively per playbook §1.

### [phase-0.5a-complete] - 2026-05-23

#### Removed
- `NINA/` WPF host project (~600+ files: View, ViewModel, Utility, Database, Resources, External, .sln deregister).
- `NINA.WPF.Base/` (~180+ files: ViewModel, Resources, Interfaces).
- `NINA.CustomControlLibrary/` (~67 files).
- Pure-delete WPF demolition, split across 6 sub-PRs (#4-#9) for CodeRabbit's 150-file-per-PR limit.

#### Added
- `prep-ci` baseline (PR #2): progressive `.github/workflows/ci.yml` placeholder, `.coderabbit.yaml` with `port/ara` in `auto_review.base_branches`, branch-naming Git-ref conflict fix (`port/ara/<name>` → flat `<name>`), AI merge authority granted.
- `rules-tighten` (PR #10): §19.1 merge-gate tightened (no merge on CR rate-limit), §22 periodic `port/ara → master` cadence added (replacing one-shot-at-Phase-15).
- The four §1 tracking files re-pinned in `tracking-files` (PR #11).

### Pre-Phase-0.5 — Design phase (2026-05-23, before any code change)

#### Added
- `design/PORT_PLAYBOOK.md` — 12k+ line port plan, baked across 6 design passes incorporating Tier-1 through Tier-3 decisions on AOT publishing, OpenAPI generation, FITS lib choice, systemd hardening, udev groups, watchdog model, port-number choice (5555), NTP strategy, WebSocket protocol, filename conventions, simulator pinning, API versioning, CORS policy, performance SLOs, Pi hardware matrix, USB cabling advisory, FITS corruption recovery, version compatibility matrix, network bring-up, GFS retention, equipment-DB deferral, NINA-DB greenfield decision, settings-registry gate, §73 exception policy, §8.1 DI mapping, §60.10 WS catalog, §74 CONTRIBUTING placeholder.
- `design/COMMIT-PR-RULES.md` — per-phase + sub-PR rhythm, branch naming, CR poll-and-fix loop, §19.1 merge-gate, `.coderabbit.yaml` config, Phase 12 8-sub-PR split, settings-registry + help-registry mechanical gates.
- `design/GAPS-ARA.md` — feature gaps between NINA and Ara's target.
- `design/PHD2-GAP.md` — PHD2 fork plan for `openastro-phd2`.
- `design/PORT_DECISIONS.md` — append-only decision log.
- `design/PORT_TODO.md` — append-only `TODO(port)` + deferred-CR-finding log.
- `design/API_CONTRACT.md` — early stub (superseded by `OpenAstroAra.Server/openapi.yaml` at Phase 5).

[Unreleased]: https://github.com/open-astro/openastro-ara/compare/phase-10.5-complete...HEAD
[phase-10.5-complete]: https://github.com/open-astro/openastro-ara/compare/phase-10-complete...phase-10.5-complete
[phase-10-complete]: https://github.com/open-astro/openastro-ara/compare/phase-9-complete...phase-10-complete
[phase-9-complete]: https://github.com/open-astro/openastro-ara/compare/phase-8-complete...phase-9-complete
[phase-8-complete]: https://github.com/open-astro/openastro-ara/compare/phase-7-complete...phase-8-complete
[phase-7-complete]: https://github.com/open-astro/openastro-ara/compare/phase-6-complete...phase-7-complete
[phase-6-complete]: https://github.com/open-astro/openastro-ara/compare/phase-5-complete...phase-6-complete
[phase-5-complete]: https://github.com/open-astro/openastro-ara/compare/phase-4-complete...phase-5-complete
[phase-4-complete]: https://github.com/open-astro/openastro-ara/compare/phase-3-complete...phase-4-complete
[phase-3-complete]: https://github.com/open-astro/openastro-ara/compare/phase-2-complete...phase-3-complete
[phase-2-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5p-complete...phase-2-complete
[phase-0.5p-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5n-complete...phase-0.5p-complete
[phase-0.5n-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5g-complete...phase-0.5n-complete
[phase-0.5g-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5f-complete...phase-0.5g-complete
[phase-0.5f-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5d-complete...phase-0.5f-complete
[phase-0.5d-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5b-complete...phase-0.5d-complete
[phase-0.5c-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5b-complete...phase-0.5c-complete
[phase-0.5b-complete]: https://github.com/open-astro/openastro-ara/compare/phase-0.5a-complete...phase-0.5b-complete
[phase-0.5a-complete]: https://github.com/open-astro/openastro-ara/releases/tag/phase-0.5a-complete
