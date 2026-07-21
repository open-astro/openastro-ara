# Integration Budget — "how many hours does this target actually need?"

**Status: DRAFT for review — no implementation until sign-off.**

## Why (the product thesis)

The software's promise — *Astrophotography Perfected* — is that everything
OBJECTIVE about a night is engineered for the user: what target fits their
optics, how to split the night across filters, how long each sub should be,
and how many total hours their sky needs for data worth processing. When
the chain holds, the only variable left is taste in processing. This
feature is the last objective link.

## The product claim

Complete the planning chain no competitor ships end-to-end:

> FOV fit (what to point at) → filter plan (how to split the night) →
> Glover subs (how long each frame) → **integration budget (how many hours,
> and when more stops paying)** → banked-hours progress across sessions.

The deliverable is two numbers per target, per filter approach, computed
from the user's actual rig and sky:

- **T_required** — hours to reach a stated quality tier.
- **Marginal-gain horizon** — the point where one more hour improves the
  stack by less than a stated threshold (default 2 %/h).

## What the math can and cannot claim (scope fence)

The model predicts **stack SNR on structure of a given surface brightness**.
It does not predict processing skill, palette appeal, or social reception —
an SHO widefield will out-view a technically superior LRGB Orion forever,
and the UI copy must never imply otherwise. Language in-app: "hours for a
clean stretch at this depth", never "hours for a good photo".

## The equations (all inputs already in the codebase)

Once subs are past the Glover floor (read noise swamped), per-pixel stack
SNR on a structure of surface brightness SB:

```
S_sky    = sky electrons/sec/pixel        ← EXISTING Glover sky-flux term
                                            (aperture, pixel scale, QE,
                                             filter bandwidth, SQM/Bortle)
S_target = same formula, SB in place of the sky magnitude
SNR(T)   = S_target · sqrt(T) / sqrt(S_target + S_sky)

T_required(SNR_goal) = SNR_goal² · (S_target + S_sky) / S_target²
marginal gain after T = SNR/(2T)   → horizon where SNR/(2T)/SNR < 2 %/h
                                     ⇔ T > 25 h · (threshold/2 %)⁻¹ … pure algebra
```

Narrowband: S_target uses only the line flux fraction; S_sky already
shrinks with bandwidth in the existing term — the same reason narrowband
"needs more hours but tolerates city skies" falls out with no new constants.

### Depth tiers (the anti-fake-precision device)

Catalog surface brightness is an AVERAGE. The faint structure people chase
runs 2–4 mag dimmer. One pretend-exact number would be a hallucination;
three tiers are honest:

| Tier | SB used | Meaning | SNR_goal |
|---|---|---|---|
| Core | SB_catalog | the bright body | 5 |
| Full structure | SB + 2 mag | shells, outer arms | 5 |
| Faint extensions | SB + 4 mag | halos, tendrils, IFN territory | 3 |

UI shows e.g. "core ~2 h · full ~9 h · faint ~35 h" — and a target whose
faint tier exceeds ~60 h says "faint extensions impractical from this sky"
rather than a silly number.

## Provenance — what each piece of math rests on

Three grades, kept visibly separate; every constant in the eventual
`integration_budget.dart` carries one of these citations in a comment.

**Theorems / textbook physics:**
- SNR ∝ √T — Poisson photon statistics.
- SNR(T) formula — the standard astronomical SNR equation (Howell,
  *Handbook of CCD Astronomy*; Merline & Howell 1995), read-noise/dark
  terms dropped under exactly the condition Glover's floor guarantees.
  The SAME equation powers NASA/STScI exposure-time calculators — HST's
  ETC and JWST's open-source engine **Pandeia** (Pontoppidan et al. 2016).
  P1's unit tests include 2–3 parity cases cross-checked against
  Pandeia-style worked examples so our Dart math is anchored to the
  professional implementation, not just to itself.
- Magnitude → photon flux — the Pogson relation, with the photometric
  zero point pinned to the published Vega-system calibration
  (Bessell 1998, ~1000 photons s⁻¹ cm⁻² Å⁻¹ at V = 0) instead of an
  anonymous constant.

**Named conventions (citable thresholds, not derivations):**
- SNR 5 = "clean", 3 = "detectable" — the Rose criterion (A. Rose, 1948),
  standard across astronomy and medical imaging.
- Scattered moonlight — Krisciunas & Schaefer (1991), the model behind
  ESO's sky-brightness calculator; inputs come from the client's existing
  Dart moon ephemeris.
- The sub-length floor — Dr. Robin Glover's read-noise-swamping criterion
  (SharpCap), already shipped and attributed.

**Our judgment calls (documented as such, tunable):**
- Depth-tier offsets (+2/+4 mag), the 2 %/h horizon, and treating catalog
  surface brightness as the core tier's input.

### CMOS note ("CCD equation" is a historical name)

The equation is photon statistics + read noise; sensor architecture is
irrelevant (JWST's ETC applies it to HgCdTe arrays). Glover's method was
developed FOR modern CMOS. All CMOS-specific behavior (gain-dependent read
noise incl. the HCG/LCG dual-gain step, e-/ADU) enters through the
user-entered electronics values at their operating gain — which is why the
camera-electronics settings ask for exactly those. Amp glow and fixed-
pattern noise are calibration/dither concerns, invisible to SNR planning;
dark current on cooled CMOS is negligible next to sky flux (the same
condition that justified dropping the term). In-app and in-code language:
"the standard astronomical SNR equation", never "CCD equation".

## Anti-hallucination: validate BEFORE any user sees an hours figure

The model's testable intermediate is per-sub background level. The library
already stores **measured `backgroundAdu` and `medianAdu` per frame**, with
exposure, filter, gain, and timestamp. So:

**Phase V (validation gate, ships no UI):**

*Pedestal without calibration frames (decided 2026-07-19 — typical users
rarely shoot bias/darks/flats, so the harness must not depend on them):
per filter, regress measured background ADU against exposure length across
the library's frames — background = pedestal + rate × exposure. The
INTERCEPT is the camera's bias pedestal, the SLOPE is the measured sky
flux; they separate with zero calibration frames as long as the library
holds varied exposure lengths. A master bias, when present, just pins the
intercept. (Dry-run precedent: Joey's 9×20 s M42 L frames showed a
629–632 ADU background stable to 0.5 % over five minutes — the stability
this fit rides on — but a single exposure length can't split pedestal from
sky, which is what forced this design.)*

1. For every frame in users's real library: predict background ADU =
   S_sky(filter, SQM, optics) × exposure ÷ e-/ADU(gain) + offset.
2. Compare predicted vs measured across frames; fit one scalar calibration
   factor k (transmission/QE-curve losses the spec-sheet numbers miss);
   report the residual scatter.
3. **Gate:** proceed to UI only if |k| lands in a sane band (~0.4–1.5) and
   scatter over a night is < ~30 %. If not, the model is missing physics
   (moon, altitude, haze) and we iterate here — not in the UI.
4. k persists per profile (auto-recalibrated from recent frames), so the
   budget self-tunes to each rig instead of trusting spec sheets.

This phase doubles as the demo: "the model predicted last Tuesday's Ha
background within 12 %" is the credibility sentence for the feature.

### Dry run #2 — the Texas NGC 6188 campaign (2026-07-21, real data)

123 lights (LRGB 30 s + SHO 300 s) over a 9-day dark-site trip, full
calibration set, RedCat 91 + 2600MM gain 100, target forced to ~11°
altitude (dec −49° from lat 29.5° N). Findings, each now a design input:

- **Broadband PASSES.** Measured e⁻/s/px vs model @ SQM 21.9:
  L 0.79 (0.82) · R 0.29 (0.25) · G 0.25 (0.21) — within 5–20 %, and the
  L:R:G ratios track the bandwidth term independently.
- **Blue at 0.15 vs 0.29 predicted** — Rayleigh extinction at 5 airmasses
  (~1.4 mag in B). Second real-data vote for promoting the
  altitude/airmass term into v1's T_required (it also directly scales the
  TARGET signal: this campaign's 6.75 h of SHO ≈ ~2 h zenith-equivalent).
- **Narrowband: model conservative.** True background (p05) Ha 0.010 vs
  0.016 predicted; SII 0.004; OIII 0.001 — 13× darker than the flat
  "continuum × bandwidth" scaling. Dark-sky light is airglow-line
  dominated and the OIII band dodges the lines. Consequence: Phase V fits
  k PER FILTER KIND, not globally; the flat scaling stays as the
  never-optimistic upper bound on sky noise.
- **Background estimator must be a low percentile, not the median** — a
  frame-filling emission nebula contaminates the median (Ha med-rate 2.7×
  its p05-rate on this target). The daemon's frame-stats backgroundAdu
  should be verified to use sigma-clipped/percentile background before
  Phase V trusts it.
- **Stability verified:** bias 501.0 ADU on every frame; 300 s darks ALSO
  501.0 (zero measurable dark current at −10 °C — the dropped dark term
  verified on real hardware); night-to-night narrowband scatter 6–9 %
  across 3 nights, far inside the 30 % gate.

### Moonlight — IN v1 (decided 2026-07-19)

The client already carries a Dart Meeus-style moon ephemeris
(`tonight_sky_local.dart`: RA/Dec, up/down vs horizon, illuminated
fraction — no Python/Skyfield dependency needed). v1 adds the
**Krisciunas & Schaefer (1991)** scattered-moonlight model — the citable
standard, used by ESO's sky-brightness calculator — which maps (moon phase
angle, moon altitude, moon–target separation) → added sky brightness in
mag/arcsec², composed onto the SQM/Bortle base before the SNR math runs.
Two things fall out for free:
- Narrowband moon immunity: moonlight is broadband reflected sunlight, so
  the existing bandwidth term slashes it for a 6 nm filter — the model
  *states* the "shoot narrowband under the moon" folk wisdom.
- Tonight-vs-generic budgets: the tiered T_required can be shown both for
  a dark night (the target's intrinsic cost) and for TONIGHT's actual moon.

K&S goes in the Provenance section's "theorems/citable" grade; its
validation rides the same Phase-V gate (moonlit frames in the library are
exactly the residuals the base model can't explain — fitting nights with
and without moon separates k from the moon term).

Still deliberately EXCLUDED from v1 (documented in-app as assumptions,
candidates for v2): target altitude/airmass, seasonal haze. v1 states
"near-zenith".

## Feature phases (after the validation gate passes)

- **P1 — math core.** `lib/util/integration_budget.dart`, pure functions +
  hard unit tests: T_required, tiers, horizon; golden cases (M42 core
  fast / NAN mid / IFN absurd) asserted against hand-computed values.
- **P2 — calibration store.** k per profile, fit from library frames;
  surfaced in Options → Imaging as "sky model calibration" with a
  "recalibrate from recent frames" action.
- **P3 — Tonight's Sky surfacing.** Per row: tiered budget line under the
  existing framing text ("~2 h core · ~9 h full · faint impractical"),
  filter-approach aware (the row's existing SHO/LRGB advice picks which
  budget leads). Detail popover shows the inputs (SB, sky, S_target/S_sky)
  so the number is auditable, never oracular.
- **P4 — banked hours.** Aggregate library integration per target name ×
  filter (frames already carry both): "6.2 h banked of ~9 h" + a thin
  progress bar. Name matching = the same normalization Tonight's Sky uses.
- **P5 — session planner handshake.** What-if-run ranks candidates by
  "closest to completing a tier with tonight's window" and flags
  "tier complete — diminishing returns" targets toward new objects
  (the never-captured steering deferred from #860 composes here).

Each phase is a separate commit set on a feature branch, gated tests per
phase, no PR until live-tested — per standing rules.

## Open questions

1. Default SNR goals (5/5/3) and the 2 %/h horizon: taste constants —
   expose in Advanced settings, or hard-code v1?
2. Banked hours: count only frames rated OK (§50 quality gate), or all?
3. Where does the budget belong besides Tonight's Sky — target plan
   dialog? Stats tab?
4. ~~v1 ignores the Moon?~~ RESOLVED 2026-07-19: moon is IN v1 via the
   Krisciunas & Schaefer model on the existing Dart ephemeris.
