# Run Section Redesign — "Mission Control, not a spreadsheet"

Design plan (2026-07-18) for making the Run tab feel Apple-grade: focused,
alive, and enjoyable. Written against the current implementation
(`sequencer_tab.dart` + `lib/widgets/sequencer/*`).

## Design diagnosis

The Run tab today is a faithful NINA-editor port: toolbar + three dense
monochrome text panes. It *works*, but nothing about it says "you are about
to run a night under the stars":

- Run progress is ONE grey ellipsized string in the toolbar ("Veil — Running
  — 3/12 instructions"). No progress bar, no live highlight of the executing
  instruction, no timers, no frame counts, no thumbnails, no color.
- State color exists (the Load dialog's RunStateBadge) but not on the Run
  surface itself — running, paused, and failed all look identical there.
- Every button is the same neutral TextButton; Abort/Delete carry no
  destructive styling; Run carries no primary styling.
- The palette and tree are uniform 12–13px text lists — no per-type color,
  no descriptions, no hierarchy cues beyond indent.
- Completion/failure of a whole NIGHT'S run is a transient SnackBar.

Apple's principles, applied here: **one clear focus per moment** (editing vs
running are different moments), **direct manipulation with immediate,
beautiful feedback**, **depth and hierarchy over uniform density**, and
**celebrate the user's outcome** (a finished run is an event, not a log line).

## The big idea: two moods, one surface

The Run tab should morph between two states:

- **Compose mood** (no active run): the editor — palette, tree, inspector —
  but with real visual hierarchy and a hero "Run" affordance.
- **Live mood** (run active): the editor recedes; a **run dashboard** takes
  the stage — progress ring, current instruction spotlight, elapsed/remaining,
  latest frame thumbnail, event ticker. One glance from across the room
  answers "is my night going well?"

---

## The plan (itemized, roughly in build order)

### Phase 1 — Run lifecycle looks and feels like running
1. **Hero Run button**: replace the toolbar's plain-text Run with a prominent
   filled pill (accent green, play glyph, keyboard shortcut hint). Pause →
   amber; Resume distinct; **Abort styled destructive (red outline)** with a
   confirm affordance. Lifecycle buttons become a segmented cluster, visually
   separate from file operations.
2. **Live instruction spotlight in the tree**: the currently-executing node
   gets an animated accent bar + subtle pulse, auto-scrolled into view;
   completed nodes get a quiet checkmark tint; a failed node gets the error
   accent. (Run-state already streams via `sequenceRunStateProvider` — this
   is presentation only.)
3. **Progress header**: a slim always-visible band under the toolbar during a
   run: sequence name, colored state chip (reuse RunStateBadge — it lives in
   the wrong place today), determinate progress bar (completed/total leaves),
   elapsed + estimated-remaining (planner overhead model already exists),
   and "needs your attention" surfaced in red with a tap-to-jump.
4. **Completion moment**: a run finishing deserves a card, not a SnackBar —
   summary sheet with duration, frames captured per filter, skipped/failed
   steps, and a "View in Library" action. Failure gets the same sheet in
   error dress with the failing instruction named.

### Phase 2 — the editor gets hierarchy and personality
5. **Per-category instruction color + iconography**: each palette category
   (Camera, Guider, Telescope…) gets an accent hue used consistently as the
   tile's icon tint and the tree row's leading icon — the tree stops being a
   wall of grey text and becomes scannable by kind.
6. **Palette tiles → cards with descriptions**: two-line tiles (name +
   one-line "what this does"), hover elevation, and a search/filter field at
   the top. Drag-from-palette-to-position (already noted in code as a
   planned slice) replaces tap-inserts-at-selection as the primary gesture.
7. **Tree rows breathe**: 36–40px rows, container nodes styled as grouped
   "cards" with a soft header band (loop/trigger chips inline), rounded
   selection, and the move/delete affordances revealed on hover instead of
   permanently cluttering the selected row.
8. **Inspector polish** (right pane): title case + category color echo,
   grouped field sections with the Options-panel row idiom, and friendly
   empty states ("This instruction runs as-is — nothing to configure").
   Kill raw-model copy like enum names and "no editable fields here".

### Phase 3 — delight and flow
9. **Frame-thumbnail strip during runs**: latest captured frame (the daemon
   already serves frames to Live/Library) as a small live thumbnail in the
   progress header — the single most reassuring pixel in astrophotography.
10. **Event ticker**: a quiet, reverse-chronological strip of run events
    (autofocus ran · dithered · filter → Ha · meridian flip in 24 min)
    replacing "watch the grey string" as the sense of heartbeat.
11. **Empty state that invites**: "No sequence loaded" becomes an inviting
    zero-state: big glyph, one-line pitch, three buttons — New sequence,
    Load, **Plan tonight** (deep-link to the session planner, which can now
    hand its plan to the editor).
12. **Micro-interactions**: 150–200ms eases on selection, drag-chip shadow,
    progress-bar spring, palette hover states, and the run-start transition
    (editor panes gently dim/slide as the dashboard takes focus). Reduced-
    motion respected.
13. **Keyboard + power flow**: Space = pause/resume, ⌘R = run, Delete =
    remove node, arrows navigate the tree, ⌘Z undo for tree edits (model is
    already client-side — an undo stack is cheap). Discoverable via the ⌘K
    palette.

### Consistency ground rules (all phases)
- One spacing scale (8/12/16/24) and the Options-shell type ramp everywhere.
- AraColors accents only through semantic roles (state chips, category hues,
  destructive) — never decoration for its own sake.
- Copy in product voice: verbs, sentence case, no NINA/daemon enum leakage.
- Every state (idle/offline/draft/running/paused/attention/failed/done) has
  a designed look — no state falls back to "grey text".

## Suggested PR slicing
- PR A: Phase 1 items 1–3 (lifecycle styling + spotlight + progress header)
- PR B: item 4 + 11 (completion sheet + zero state)
- PR C: Phase 2 (editor hierarchy: palette cards, category color, tree/
  inspector polish)
- PR D: Phase 3 (thumbnails, ticker, micro-interactions, keyboard)
