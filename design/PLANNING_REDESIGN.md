# Planning Redesign — from "a list beside a sky" to "your night, decided"

**Status: DRAFT for review. Senior-design pass over the Planning tab,
2026-07-21. No implementation until sign-off.**

## The honest critique

The bones are excellent — a full-bleed planetarium is inherently the most
beautiful screen in the app, and the data behind the panel (scores, framing
fit, filter advice, Glover subs, integration budget, banked hours) is a
moat no competitor has. But the presentation is a spreadsheet wearing a
dark theme:

1. **The story is hidden.** Planning answers three questions in order —
   *what's worth my night → how does it sit in my frame → commit it* — but
   Tonight's Sky hides behind a toggle button, the session planner behind
   an unlabeled calendar icon, and the two best sentences we compute
   ("Hours needed", "You have 6.8 h captured") are buried inside an
   expandable "Why?" that most users will never open.
2. **Everything is the same size.** Rows are caption-weight text stacked on
   caption-weight text; the score badge is the only color. Nothing is the
   hero; the eye has nowhere to land. Rank #1 should FEEL like rank #1.
3. **Numbers are prose that should be pictures.** Dark-window timing is a
   text line ("21:40–03:10 · transit 00:20") when it wants to be a tiny
   timeline strip. Banked-vs-needed hours is two sentences when it wants
   to be a progress ring. Framing fit is a word ("good fit") when it wants
   to be a miniature FOV box drawn around an object ellipse.
4. **No motion, no reward.** Rows pop in instantly, expansion snaps,
   "Add to Sequence" ends in a SnackBar. The moment a user commits a
   target is the emotional peak of planning — it deserves a beat.
5. **Dead chrome.** `TargetActionBar` and `PlanningTimeBar` are unmounted
   Aladin-era leftovers (their jobs moved into the Stellarium page).
   Delete them.

## Platform constraint (shapes every choice)

The native webview composites ABOVE Flutter — no Flutter overlay may sit
on the sky. All design happens in the panel's own rect, the top bar, and
in-page (which we do not restyle here). Panel width may grow (360–380) —
the sky keeps the rest.

## The design (slices, one commit each, shippable after every slice)

- **S1 — kill the dead chrome.** Delete `target_action_bar.dart` +
  `planning_time_bar.dart` (+ their tests/state if orphaned). Pure
  hygiene, zero behavior change.
- **S2 — tokens + panel skeleton.** Adopt AraSpace/AraText (the Run
  redesign's metrics) across the panel; width → 360; section header
  treatment ("TONIGHT" over the list, quiet all-caps like Run's palette);
  loading state → three shimmer skeleton rows instead of a bare spinner.
- **S3 — the hero card.** Rank #1 becomes a distinct card at the top:
  bigger name, its framing glyph (S5), its budget ring (S6), and a
  one-line verdict composed from data we already have ("Fills your frame ·
  SHO · window opens 21:40"). The rest of the list continues below in
  compact rows. The eye lands where the ranking says it should.
- **S4 — the dark-window strip.** Replace the timing text with a 4 px
  horizontal strip per row: the night span as a track, the object's dark
  window as a filled segment, transit as a tick, "now" as a dot, moon-up
  span as a faint underlay. All inputs already on the wire
  (windowStart/End, transit, moonUpFraction). Text remains as a11y label.
- **S5 — the framing glyph.** A ~44 px square vector: the sensor FOV
  rectangle with the object's catalog ellipse (sizeMaj/Min, posAngle)
  drawn to scale inside it, tinted by framing tier. Replaces the
  framing chip's word with the actual geometry — offline, cheap,
  and instantly legible ("that's why it says too small").
- **S6 — the budget ring.** Banked ÷ full-tier hours as a small Activity-
  style ring (accent fills as the target completes; a check when full tier
  is banked). The tier line moves OUT of "Why?" to the row proper, one
  line: "◐ 6.8 of ~9 h · faint impractical". The "Why?" keeps the long
  form. This is the product's crown jewel — it must be visible unexpanded.
- **S7 — chips get the Run treatment.** Framing/filter/moon chips adopt
  the category-hue system from the Run redesign (icon + tint), 22 px tall,
  consistent radii; score badge refined (thin ring, tabular numerals)
  instead of a filled blob.
- **S8 — commit moment.** "Add to Sequence" success: the row flashes a
  brief accent sweep and a compact confirmation card slides in at the
  panel bottom for ~5 s — target name, plan summary ("SHO · 3 filters ·
  ~4.1 h"), and a "View in Run →" action — replacing the SnackBar. The
  plan-chooser dialog restyles to match (S9 of the Run redesign's card
  look, already close).
- **S9 — "Plan my night" gets a real door.** The calendar icon becomes a
  full-width quiet button pinned under the list ("Plan my night — pick
  targets for 10pm–1am"). Same dialog behind it.
- **S10 — motion pass.** Rows stagger-fade on refresh (30 ms steps,
  reduced-motion respected), Why? expansion eases (200 ms easeOutCubic),
  the budget ring animates to its fraction on first build, hero card
  cross-fades when the ranking changes. Idle-cost zero; no loops.

## What deliberately does NOT change

- The Stellarium page and its in-page controls (separate workstream; AGPL
  boundary stays clean).
- The ranking math, budget math, plan-chooser logic — presentation only.
- The panel-beside-webview architecture (platform constraint above).

## Verification

Per slice: analyze + targeted widget tests (`tonight_sky_panel_test`
keeps passing — finders keyed on text that survives; new glyph/strip/ring
get their own tests), full suite before each commit; live visual pass
with the running app between slices. No PR until live-tested (standing
rule).
