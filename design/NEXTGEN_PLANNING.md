# Next-generation planning & exposure intelligence — design backlog

> **Status: SLICES 1–4 SHIPPED (2026-07-01) — the core epic is live.** Picked up 2026-07-01
> (maintainer decision), full scope, in the suggested slice order, all four merged the same day:
> **slice 1** — the Optimal-Sub calculator (`OptimalSubCalculator`, Glover floor + saturation
> ceiling, PR #624); **slice 2** — profile setup (`camera_electronics` + `filter_set` sections,
> `optics.aperture_mm`, connect-time electronics auto-capture, `GET /api/v1/planning/optimal-sub`,
> Settings panels + registry, PR #625); **slice 3** — Tonight's Sky filter/emission-aware advice
> (chip + Why?-breakdown reason + per-approach Optimal-Sub figure, advice-only, PR #626);
> **slice 4** — the sequence-editor "Optimal Sub" advisor filling the standard `ExposureTime`
> (NINA-fidelity preserved, PR #627). Dr. Glover is attributed in every user-facing surface per
> the recorded permission (§2).
> **Still deferred:** §6 (native sequence model), adaptive/runtime Glover (§5's second fork),
> the star-detectability + satellite-trail bounds (§3), and the ±score-nudge (recorded in
> `design/PORT_TODO.md`).
> **Tier 1 sensor QE library SHIPPED (2026-07-02):** `SensorQeLibrary` (16 rows, sensor→peak-QE
> fractions rounded to 0.05 — vendor figures vary ±5–10% and the model is forgiving, so coarse
> honest values over false precision) fills `QuantumEfficiencyPeak` in the connect-time
> electronics auto-capture, keyed as a substring of the driver's free-form `SensorName`, ONLY
> when unset — user-entered values always win, unknown sensors stay unset (the calculator's
> documented generic default applies). Electronics-owned specs deliberately excluded per the
> table below. The wizard electronics/filter capture steps also shipped earlier (#646/#647).
> Originally captured 2026-06-29 and bookmarked behind the v1 cross-platform release; see
> `design/PORT_DECISIONS.md` (2026-06-29) for that context.

This is the design record for turning Tonight's Sky (§36.8, shipped: `design/TONIGHT_SKY.md`)
from "what's worth shooting tonight" into "what's worth shooting tonight **with your rig and your
time budget**" — adding filter intelligence, Glover sub-exposure optimisation, and camera-aware
exposure feasibility. It also records a strategic direction for the sequencer format.

The guiding principle stays **advise, don't dictate** (the existing Tonight's Sky philosophy):
surface the trade-off and the recommended number, never hide a target or hard-gate the user.

---

## 1. Filter-type / emission-aware planning

**Goal: be thoughtful of the user's time.** Shooting an emission target like the Veil (SNR) in
broadband LRGB is possible but needs *far* more integration than narrowband Hα/OIII — narrowband
emits in narrow lines, so it punches through light pollution. Broadband-continuum targets
(galaxies, star clusters, reflection nebulae) gain little from narrowband. So the planner should
weigh **target emission character × the user's filters × their sky (Bortle)**.

- **Target emission class** — derive from the OpenNGC `Type` column: emission-line
  (HII regions, supernova remnants, planetary nebulae) → narrowband-responsive; continuum
  (galaxies, open/globular clusters, reflection nebulae) → broadband.
- **User's filters** — a **new profile field** (does not exist today; the profile's filter config
  is empty). Declared once at setup: broadband (mono L/R/G/B, or OSC/DSLR) and/or narrowband
  (Hα, OIII, SII, or dual/tri-band like L-eXtreme / L-eNhance). Planning runs offline, so this is
  a profile setting, *not* read from the connected filter wheel at plan time.
- **Sky** — `site.BortleClass` already exists and already feeds the worth score (surface-brightness
  term). The gap is **capturing Bortle (or an SQM mag/arcsec² value) in the setup wizard** and using
  it in the *filter* advice (the broadband penalty on emission targets is worst under bright skies —
  exactly when narrowband earns its keep).

**Output (advice, optional soft score nudge):** per target, a recommended filter approach
("Narrowband — efficient even under your sky", "Broadband LRGB", "OSC + dual-band") plus a
time-efficiency signal ("broadband only: expect many hours; narrowband would cut this
dramatically"). Open call: advice-only vs. a soft score nudge (lean: both — a tag plus a gentle
nudge, never a hard filter).

---

## 2. Glover optimal sub-exposure (`t = 10·R² / P`)

> **Attribution & permission (2026-06-30).** The read-noise-limited sub-exposure criterion was
> *popularised* by **Dr. Robin Glover** (author of SharpCap) in his "How to Get Perfect Subexposures"
> talk; Glover notes the approximation itself likely predates him, so we credit it as the criterion he
> popularised, not one he invented. He gave **explicit permission** to use the equation in Open Astro
> (email, 2026-06-30) and offered the engineering caveats folded into §3 below. He also endorsed the
> project's mission — he welcomed *"an open source implementation rather than the manufacturer lock-in"*
> of closed competitors. (His fuller remark named a specific commercial product; that clause is
> intentionally omitted here to avoid embedding a named legal allegation in a public doc.) Attribute him
> in any user-facing "Optimal Sub" UI.
> The **original permission email is retained by the maintainer** for independent verification (kept out
> of the repo to avoid publishing personal contact details); a redacted copy can be attached to an issue
> on request.

The criterion for the **sub-exposure length** where read noise stops mattering:

- `R` = camera read noise (e⁻), `P` = sky-background flux (e⁻/s/pixel).
- The `10` encodes "let read noise add ≤ ~5% to total noise": `e_sky = R²/((1.05)²−1) ≈ 10·R²`,
  then `t = e_sky / P`. The acceptable-noise-increase is a **tunable knob** (3% → ≈ `16·R²/P`).
- **`P` model:** `P ∝ 10^(−0.4·skyMag) × aperture_area × pixel_solid_angle × QE × filter_passband`
  — i.e. it ties together Bortle/SQM (skyMag), f-ratio + pixel scale (optics), QE (sensor), and the
  filter bandwidth (narrowband collapses `P` by roughly the bandwidth ratio; a 3 nm Hα vs a ~100 nm
  L ≈ 30× less sky flux).

**Note — NINA "Smart Exposure" is NOT Glover.** NINA's Smart Exposure is a *sequencer convenience*
(switch filter → take N exposures → dither/AF on trigger); its exposure time is a plain
user-typed number, noise-blind. Glover's method lives in **SharpCap's Smart Histogram / "Brain"**.
ARA implementing this would be giving users something NINA core lacks. See §5 for how the two relate.

**`t = 10·R²/P` is the floor, not the whole story.** It sets the *minimum* useful sub. "What should
I spend the whole night on" is the *total-integration-to-target-SNR* question — the same noise model,
one step further, also using the target's surface brightness (which Tonight's Sky already carries).

**Glover's caveat (2026-06-30) — the read-noise limit is only ONE bound, and it's the *subtle* one.**
He stresses that the read-noise criterion sits inside a set of *other* practical limits, and that the
others are *obvious when they happen* whereas the read-noise problem is invisible (you can't see when
a sub is too short to swamp read noise, and — Glover's own wording — going *past* the floor yields no
further read-noise gain yet quietly costs dynamic range / extra trail exposure; this is diminishing
returns, NOT a read-noise-derived ceiling). So the tool's unique
value is surfacing the **subtle read-noise sweet spot inside the obvious bounds** — see §3.

---

## 3. Camera-aware exposure feasibility (the sub-exposure *window*)

Glover gives the *read-noise* floor; **full-well capacity gives the obvious ceiling** — the longest
sub before bright cores/stars **clip**. But per Glover's own caveat (§2, 2026-06-30) the read-noise
limit is only one of several bounds. The **practical usable window** is the read-noise sweet spot
*intersected* with the obvious practical limits:

```
usable sub window =
  [ MAX( read-noise floor (Glover t=10R²/P),      // subtle — invisible when violated
         star-detectability floor,                 // enough stars per sub to register/align/plate-solve
         data-volume floor )                        // min sub length to keep sub count storable/processable
    …
    MIN( star/core saturation ceiling (full well vs object brightness),
         sky-background saturation ceiling,         // the BACKGROUND itself clipping under heavy LP
         satellite-trail-tolerance ceiling ) ]      // longer subs catch more trails → more rejected data
```

**The read-noise floor is the only *subtle* bound** (you can't see a sub too short to swamp read noise,
nor — per Glover — that going past the floor buys no further read-noise gain while quietly costing
dynamic range; that's diminishing returns, not a read-noise ceiling). The other bounds announce
themselves (blown cores, trailed or rejected frames, no stars to solve, a full disk). So the tool computes the precise read-noise sweet
spot and presents it *within* the obvious bounds, flagging when an obvious bound is the real
constraint. v1 can implement the read-noise floor + the full-well/background saturation ceiling
(all derivable from the sky + camera model); star-detectability and satellite-trail bounds are
refinements.

- Faint emission target in narrowband under a dark-ish sky → `P` tiny → long Glover floor, full well
  rarely the limit → wide window.
- Bright target / rich starfield → saturation ceiling drops; full well decides whether you can even
  *reach* the Glover-optimal sub without clipping. This is where extended full-well modes matter.

**Critical correction (user, 2026-06-29): the camera electronics matter, not just the sensor.** The
same sensor behaves differently per camera: e.g. ZWO ASI2600MM ≈ 50 ke⁻ full well vs ToupTek 2600
up to **100 ke⁻ in High Full Well mode** — same IMX571 sensor, different readout electronics. So the
spec model splits along a real seam:

| From the **sensor** (shared, stable) | From the **camera electronics** (per-model, per-mode) |
|---|---|
| pixel size, QE curve | **full well**, read noise, e⁻/ADU, gain & HFW modes |
| small ~20-entry sensor library | **varies even on the same sensor** |

**Data strategy — never curate hundreds of cameras:**
1. **Read electronics from the connected camera (primary).** ASCOM/Alpaca exposes
   `Camera.FullWellCapacity` (e⁻, reported *for the current readout mode* — so HFW vs standard falls
   out automatically), `Camera.ElectronsPerADU`, `Camera.Gain`/`Gains`. Capture once on connect,
   cache into the profile for offline planning. The 50-vs-100 ke⁻ difference comes for free.
2. **Sensor → QE + pixel** from a small ~15–20-row sensor library (IMX571, IMX455, IMX533, IMX294,
   IMX585, …), keyed off `Camera.SensorName`, or a generic "modern CMOS" default.
3. **Read noise** is the one spec **not** in standard ASCOM — default it by gain regime (the model is
   forgiving: the read-noise-contribution curve is flat near the optimum), user-editable from the
   manufacturer's gain/read-noise chart, or import a SharpCap sensor-analysis row.

| Tier | Source | Effort | Covers |
|---|---|---|---|
| 0 | generic CMOS defaults | none | everyone, instantly |
| 1 | ~20-row **sensor** library (QE + pixel) | small, one-time | most modern cams |
| 2 | auto-captured electronics from connected camera | none (runtime) | exact, your gear |
| 3 | manual / SharpCap-analysis paste | user | perfectionists |

Net: **zero camera database required.** Ship Tier 0 day one; the rest is self-populating or optional.

### 3.1 Star-detectability floor — design proposal (2026-07-02, awaiting maintainer sign-off)

> Status: PROPOSAL. The Glover floor + saturation ceiling shipped in slice 1; this section pins
> down the first "refinement" bound so it can be built design-first rather than improvised. No
> code exists yet — the model choice below wants a maintainer yes/no before implementation.

**What it computes.** The shortest sub `t_stars` such that a sub is *usable by the pipeline*:
enough stars register above the detection threshold to align/stack (and, for solve-per-sub
workflows, to plate-solve). Below `t_stars`, subs are individually well-exposed for the target
but the night's data won't integrate. Two ingredients:

1. **Limiting magnitude for a sub of length `t`** — pure rig+sky math, no new data:
   a star of magnitude `m` delivers `S(m) = F₀·10^(−0.4m)·A·QE·t` electrons through aperture
   area `A` (atmospheric + optical losses folded into a single documented transmission factor);
   detection needs `SNR = S/√(S + n_pix·(B·t + R²)) ≥ k` with the sky rate `B` already computed
   by `OptimalSubCalculator` from Bortle/SQM, `R` the read noise, `n_pix` the seeing-disc
   footprint from the profile's `TypicalSeeingArcsec` + pixel scale. Solve for `m_lim(t)`;
   `k = 5` (the conventional detection threshold; registration wants SNR-10-ish centroids, so
   present both). Every input already lives in the profile. **This half is uncontroversial.**

2. **Star count above `m_lim` in the FOV** — the contentious half. Options considered:
   - **(a) Analytic galactic star-count model** (Bahcall–Soneira-family approximation:
     `log₁₀ N(<m)` per deg² as a smooth function of `m` and galactic latitude). Pros: no data
     dependency, covers any FOV/depth. Cons: it is an *invented curve* unless validated —
     exactly the "garbage catalog" failure mode this project refuses.
   - **(b) Empirical counts from installed catalogs.** HYG (installed via the Data Manager)
     is honest data but caps near mag 9 — far too shallow (a 1° FOV needs `m_lim` 11–14 for
     useful counts). ASTAP's D50/D80 star databases go deep enough but are the solver's binary
     format; parsing them couples us to the fork's internals for an advisory number.
   - **(c) RECOMMENDED — analytic model, validated against HYG where HYG is complete.** Ship
     (a)'s curve, but with a unit test that compares its predicted `N(<9)` against actual HYG
     counts over a grid of galactic latitudes (the catalog is complete to ~mag 9): the model is
     only trusted because the test proves it against real data in the range we CAN check, and
     the extrapolation beyond mag 9 is labelled as such in the "Why?" reason string. If the
     validation shows >2× error anywhere on the grid, the model is wrong and doesn't ship.

**Presentation (advise-don't-dictate, as everywhere):** `t_stars` joins the window as a floor
bound with a reason tag ("~12 stars/sub at 30 s — thin for registration (+0)"); it never gates.
When `t_stars` exceeds the Glover floor, the window message flags that stars, not read noise,
are the binding constraint (the design's "flagging when an obvious bound is the real
constraint").

**Slice plan (post-sign-off):** 1) `m_lim(t)` solver + tests against hand-computed cases;
2) the count model + the HYG-validation test (the go/no-go gate for the whole feature);
3) wire into the §3 window + Tonight's Sky reason tags.

**Satellite-trail ceiling: explicitly NOT proposed.** A defensible trail-rate model needs
constellation-shell density by sky position, season and local time, and the publicly available
rates go stale year over year. A wrong number here would masquerade as engineering. Revisit only
if a maintained public trail-rate source appears; until then the ceiling stays a prose note in
the §3 window text, not a computed bound.

---

## 4. Profile-setup additions implied by §1–§3

To feed the above, profile setup (the wizard) needs to capture:
- **Bortle class (or SQM mag/arcsec²)** — already stored in `site.BortleClass`; add/confirm a
  setup step with a plain-language scale.
- **Filter set** — the missing field (broadband L/R/G/B, OSC/DSLR; narrowband Hα/OIII/SII; dual/tri-band).
- **Camera electronics** — read noise, full well, e⁻/ADU, gain/mode (auto-captured on connect where
  possible; defaults + manual override otherwise) + **aperture / f-ratio** (profile has focal length
  + reducer but not aperture, which the `P` flux term needs).

---

## 5. NINA "Smart Exposure" vs Glover — how they coexist

They are **different layers**, not competitors:
- **NINA Smart Exposure = the container** (workflow: filter → N exposures of length `T` → dither/AF).
  Owns *how* to run; `T` is a plain noise-blind number.
- **Glover = the number that should go in `T`** (the optimal sub for that filter + sky + camera).

**Hard constraint:** ARA must import/export NINA sequences faithfully (the import-fidelity epic;
Smart Exposure is the most common real-world instruction). So Glover **cannot replace** Smart
Exposure — it must be an **advisor that fills the standard `T`**, keeping the sequence NINA-valid:
- Tonight's Sky advice ("shoot the Veil in Hα — ~600 s subs").
- Sequence editor: a per-filter **"Optimal Sub"** suggestion the user can one-click *apply* (writes a
  plain number into the normal field → still exports as valid NINA).
- A standalone calculator.

**Naming:** don't overload "smart" — NINA's stays **"Smart Exposure"**; ARA's Glover number is
**"Optimal Sub"** / "Suggested exposure".

**The real fork in the road (decide later):**
- **Static/advisory Glover** — compute `T`, drop it into Smart Exposure's fixed field. NINA-compatible,
  predictable, exports clean. **Recommended first step (≈90% of the value, keeps fidelity).**
- **Adaptive/runtime Glover** — SharpCap-Brain-style: measure the *actual* sky background from frames
  during the run and set the sub live (tracks moon, transparency, twilight). More correct, but a
  **new ARA-only execution behavior with no NINA equivalent** → would not round-trip to NINA. A later
  ARA-native option for users who don't care about NINA export.

---

## 6. Strategic direction — own the sequence model, keep NINA as an importer (DEFERRED)

ARA has diverged substantially from NINA (bug fixes, multi-switch, conformance, the §38 native editor,
the `.araseq.json` share format). A future direction worth recording:

**ARA should own its sequence *model*, and NINA JSON becomes one *supported import format* — an adapter
at the edge, not the master shape.**

- **Canonical ARA model** — a clean, `schemaVersion`'d format designed for ARA's features. Drop NINA's
  `$type` / .NET-class-name coupling (brittle; leaks NINA internals + plugin assembly names into data).
  ARA-native nodes (multi-switch targeting, Optimal-Sub/Glover, filter-aware exposure) become
  first-class instead of being crammed into NINA's shape. This also dissolves the §5 tension — Glover
  is just a node type the NINA importer maps into.
- **NINA import = adapter** — the import-fidelity epic *becomes* the NINA→ARA translation layer; users
  keep their JSON.

**Decisions to make deliberately if/when this is picked up:**
1. **Engine vs. format** — strong lean: own the *model + format + editor + UX* but keep NINA's proven
   **execution engine** (containers/triggers/conditions) under the hood. Most value, least risk; a
   native runner can come later if ever.
2. **Export** — one-way NINA import only (simplest, honest) vs. best-effort **lossy** NINA export that
   warns when an ARA-native node has no NINA equivalent.
3. **Schema** — versioned, stable discriminators, forward-compatible (the thing NINA serialization is
   bad at).

**Why deferred (2026-06-29):** the priority is shipping the cross-platform release, not a sequencer
rebuild. This is explicitly a *next-generation* effort, done incrementally (native model behind the
existing editor, NINA import green throughout, version-stamped migration) — **not now**.

---

## Suggested slice order (when this epic is picked up, post-release)

1. **Optimal-Sub calculator** — pure `t = 10·R²/P` floor + full-well ceiling → the sub *window* per
   filter/gain. Standalone, unit-testable against known SharpCap numbers. No planner/sequencer changes.
2. **Profile/equipment setup** — Bortle/SQM, filter set, camera electronics (auto-capture + defaults),
   aperture/f-ratio.
3. **Filter/emission-aware advice** in Tonight's Sky — recommended filter approach + efficiency signal.
4. **Optimal-Sub advisor in the sequence editor** — per-filter suggestion that fills NINA's `T`.
5. (Later, ARA-native) adaptive runtime exposure; native sequence model per §6.
