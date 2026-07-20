# Integration Budget — "how many hours does this target actually need?"

**Status: DRAFT for review — no implementation until Joey signs off.**

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

## Anti-hallucination: validate BEFORE any user sees an hours figure

The model's testable intermediate is per-sub background level. The library
already stores **measured `backgroundAdu` and `medianAdu` per frame**, with
exposure, filter, gain, and timestamp. So:

**Phase V (validation gate, ships no UI):**
1. For every frame in Joey's real library: predict background ADU =
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

Known systematics deliberately EXCLUDED from v1 (documented in-app as
assumptions, candidates for v2): moonlight (± 2–3 mag, nightly), target
altitude/airmass, seasonal haze. v1 states "moonless, near-zenith".

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

## Open questions for Joey

1. Default SNR goals (5/5/3) and the 2 %/h horizon: taste constants —
   expose in Advanced settings, or hard-code v1?
2. Banked hours: count only frames rated OK (§50 quality gate), or all?
3. Where does the budget belong besides Tonight's Sky — target plan
   dialog? Stats tab?
4. v1 ignores the Moon; acceptable, or is a simple "moon up = warn"
   rider wanted immediately?
