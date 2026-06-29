# Tonight's Sky — equipment-aware target planner (§36.8 / §55.1)

Status: design locked (2026-06-29). Supersedes the placeholder `TonightSkyService`
(20 hardcoded objects ranked by current altitude). Points/gamification are
**explicitly out of scope** (back-burnered by the user).

## Intent

Tell the user *what is worth shooting tonight, with their rig, from their site* —
and **advise, don't dictate**. The list is ranked by a transparent "worth" score,
but nothing that clears a low visibility bar is hidden. The guiding anecdote: a
nebula at 11° altitude with only a 3-hour window can still be the best image of the
night ("Dragons"), so a naive altitude/duration sort that buries it is wrong. We
surface the trade-off (low, short, but spectacular at this focal length) rather than
discarding it.

Equipment-awareness is the core differentiator:
- At **448 mm** a big bright galaxy may be too small to be worth it, but a wide
  nebula frames beautifully — so galaxies are de-emphasised unless they actually fill
  enough of the frame to look good.
- At **3000 mm** Orion is absurdly oversized — don't lead with it; surface faint,
  small targets that fit the frame.

So the same sky produces a different ranked list per optical train. We **advise** by
score; we **never silently discount** a target the user might want — low-scoring ones
sort to the bottom with their reason shown, not removed.

## Inputs (all already in the daemon)

- **Site** — `SiteSettingsDto`: `LatitudeDeg`, `LongitudeDeg`, `ElevationM`,
  `DefaultHorizonAltitudeDeg`, `BortleClass`, `TypicalSeeingArcsec`,
  `TwilightDefinition` (civil/nautical/astronomical).
- **Optics** — `OpticsSettingsDto`: `FocalLengthMm`, `ReducerFactor`,
  `SensorWidthPx`/`SensorHeightPx`, `PixelSizeUm`. Pixel scale =
  `206.265 × PixelSizeUm ÷ (FocalLengthMm × ReducerFactor)`; FOV (arcmin) =
  `sensor_px × pixel_scale ÷ 60`. Mosaic: caller-supplied tile grid (e.g. 2×2)
  multiplies the effective FOV.
- **Catalog** — OpenNGC via `SkyCatalogService`/`SkyCatalogReader` (semicolon CSV,
  installed by `SkyDataInstaller`/`DataManagerService` to `{dataRoot}/{id}/catalog.csv`).
  Columns we use: `Name`, `Type`, `RA`/`Dec` (J2000), `V-Mag`/`B-Mag`, `MajAx`/`MinAx`
  (arcmin), `PosAng`, `SurfBr` (mag/arcsec²), common name. ~13k objects → the smart
  cull replaces the hardcoded 20.

## Per-object computed fields

For each catalog object, at the active site for tonight's dark window:

1. **Visibility window tonight** — the contiguous interval the object is above
   `DefaultHorizonAltitudeDeg` *and* the sun is below the twilight threshold. Empty →
   not up tonight (dropped). Uses `AltitudeFromHourAngleDeg` + a sun-altitude model;
   transit time/alt from `MaxAltitudeDeg` and LST.
2. **Max integration hours** — the length of that window (optionally clipped to where
   altitude ≥ a usable floor, e.g. 30° for high-airmass quality). This is the "10 h vs
   1 h" signal the user asked for.
3. **Framing fit** — object size (`MajAx`×`MinAx`, with `PosAng`) vs the FOV (incl.
   mosaic) as the fill fraction `r = major ÷ short FOV side`. Four honest bands:
   `tooSmall` (≪ frame — a 20′ cluster at 448 mm), `framesWell` (comfortable, with margin),
   `fillsFrame` (genuinely fills), `tooBig` (overflows — Orion at 3000 mm; mosaic may rescue it).
   See the revised band thresholds under slice 2 below.
4. **Worth score (0–100, transparent)** — a documented weighted blend, NOT hidden:
   framing fit (dominant — the equipment-aware part), integration hours available,
   peak altitude / airmass, surface brightness vs `BortleClass` (faint targets penalised
   under bright skies, but not zeroed), magnitude. Each object carries its score
   **and the component breakdown** so the UI can explain "why 90 / why 40".
5. **Reasons** — short tags ("fills frame", "10 h tonight", "low — 11°, short window
   but bright") so the client advises rather than dictates.

## Ranking philosophy

- Sort by `Score` descending — the recommendation.
- **Do not hard-filter on altitude or window length.** Anything with a non-empty
  window above the site horizon stays in the list; low/short ones fall to the bottom
  *with their reason visible*. The only drops are "never up tonight" and (optionally) a
  user-set magnitude floor.
- A bigger panel is fine (user: "It's ok if the screen is bigger"). Default surfaces a
  generous N; the rest are one scroll away, not gone.

## Wire shape

Expand `TonightSkyObjectDto` (additive — keep existing fields for the current client):
add `SizeMajArcmin`, `SizeMinArcmin`, `PosAngleDeg`, `SurfaceBrightness`,
`WindowStartUtc`, `WindowEndUtc`, `TransitUtc`, `IntegrationHours`, `FramingFit`
(enum: `too_small`/`good`/`too_big`), `Score`, and `ScoreReasons` (string list).
Request gains optional `focalLengthMm`/`sensor`/`mosaic` overrides (else use the active
profile's optical train) and `atUtc`. Endpoint stays `GET /api/v1/planning/tonight`.

## Slice plan (each a sub-PR, driven to bot ✅)

- **Slice 1 (server, catalog+timing)** — replace the hardcoded `Catalog` with an
  OpenNGC-backed cull via `SkyCatalogService`; compute the visibility window, transit,
  and integration hours; expand the DTO with size/timing/hours. Sun-altitude model +
  twilight. Tests: window math, transit, hours, twilight gating, no-catalog fallback.
  **Known slice-1 boundary:** the inclusion gate is still "above the horizon at the query
  instant" (altitude ranking carried over from the placeholder), so a not-yet-risen target
  at a sunset query is omitted even though its window fields would describe a fine night.
  The per-object window/transit/hours are computed for the listed (currently-up) objects.
  Surfacing not-yet-risen targets — the design's "include anything with a window tonight,
  rank by worth" — lands with slice 2's scoring, which replaces the altitude gate.
- **Slice 2 (server, equipment-aware scoring)** — DONE (2026-06-29). FOV/framing fit
  from the optical train; the transparent `Score` + `ScoreReasons`; the inclusion gate
  replaced (window-based, not altitude-now); `RemainingHours`. Chosen weights/thresholds
  recorded below. Tests: 448 mm vs 3000 mm produce different orderings; the "low but
  bright" target is present (not dropped); a not-yet-risen target with a window is
  included; score is bounded + explained by its component tags. (Mosaic + per-request
  optics/mosaic overrides deferred to slice 3 — slice 2 always uses the active profile's
  train, a single 1×1 tile.)

  **Score weights (0–100, tunable — `TonightSkyService` constants):**
  | Component | Weight | Quality factor `q∈[0,1]` |
  |---|---|---|
  | Framing fit | **35** | `q(r)` from the fill fraction `r = majorAxis ÷ shortFovSide`: ramps linearly to a plateau of **1.0** across the fill band (`r/0.50`, capped at 1 for `0.50 ≤ r ≤ 1.0`) then tapers `1.0/r` past full frame; Unknown → 0.5 (neutral); floored at **0.15** so it's never zeroed. Rewards *real* fill, so a target that fills the frame outranks a small one that merely fits |
  | Integration hours | **25** | `min(hours / 6, 1)` — linear, saturates at 6 dark hours |
  | Peak altitude / airmass | **20** | `max(0, sin(peakAlt))` — `sin(alt) ≈ 1/airmass` (1 overhead, ~0.5 at 30°, 0 at horizon) |
  | Surface brightness vs Bortle | **12** | `clamp((skyMag − SB + 4) / 4, 0.15, 1)`; `skyMag ≈ 22 − (Bortle−1)·0.5` mag/arcsec². Faint-under-bright penalised, floored at 0.15, never zeroed |
  | Magnitude | **8** | `clamp((12 − mag) / 12, 0, 1)` — brighter a touch higher |

  Score = Σ(weight · q), clamped to [0,100]. Each component emits a short reason tag with
  its rounded point contribution (e.g. `"fills the frame (+35)"`, `"5 h dark window (+21)"`)
  so the UI can explain *why 90 / why 40*.

  **Framing bands (revised 2026-06-29 — `r = majorAxis ÷ short FOV side`):** four states keyed to how
  much of the frame the object actually spans, so the label is honest for the rig (the original
  `0.10/0.80` two-band split wrongly called a ~20′ cluster in a 120′ frame "fills the frame"):
  `r < 0.33` → `TooSmall` (lost in the frame — a 20′ cluster at 448 mm), `0.33 ≤ r < 0.50` →
  `FramesWell` (a comfortable subject with margin), `0.50 ≤ r ≤ 1.00` → `FillsFrame` (genuinely fills —
  the NA Nebula's ~120′ at 448 mm), `r > 1.00` → `TooBig` (overflows the short side — a mosaic can rescue
  it). No recorded size → `Unknown`. Wire values (all-lowercase, §60.6): `toosmall`/`frameswell`/
  `fillsframe`/`toobig`/`unknown`. The **"fills" cutoff (0.50) and "small" cutoff (0.33) are tunable** —
  on-device review at 448 mm (2026-06-29) confirmed these feel right (NA Nebula leads, 14–25′ clusters
  drop to "Small").

  **`RemainingHours`** — dark time still ahead of the query instant in the object's window
  tonight: `max(0, windowEnd − max(atUtc, windowStart))`. A past window → 0; a not-yet-
  started window → its full length (all ahead); an in-progress window → from now to its
  end. Always ≤ `IntegrationHours`.

  **Inclusion gate** — an object is listed iff it has a non-empty dark window anywhere in
  the ±12 h span (NOT merely "above the horizon at `atUtc`"), so not-yet-risen targets are
  surfaced. A cheap pre-filter drops objects whose geometric upper culmination never clears
  the horizon (`MaxAltitudeDeg < horizon`) before the costly ±12 h scan; the precomputed-
  once sun/LST sample grid is unchanged from slice 1.

  **Enum wire shape** — `FramingFit` serializes all-lowercase per the §60.6 convention
  (`unknown`/`toosmall`/`good`/`toobig`), not the illustrative `too_small` snake_case in
  the "Wire shape" section above.
- **Slice 3 (client panel)** — richer Tonight's Sky list: per-object window/transit,
  hours, framing-fit chip, score with the "why" breakdown, recenter-atlas + add-to-
  sequence. ✅ panel + model + tests built (PR #614).
  - **Planning-tab mount (3b) — DONE (2026-06-29).** User picked **side-by-side**
    ("like the other panels"). In `tonightsSky` mode `StellariumView` lays the
    planetarium (kept in its `Expanded`, so its rect shrinks and the native overlay
    bounds recompute via the existing bounds logic) and `TonightSkyPanel` (fixed
    340px, left-divider) in a `Row` — the panel gets its own rect, never overlaid on
    the webview (occlusion-safe; the native webview composites ABOVE Flutter, so an
    overlaid panel wouldn't reliably paint, esp. the Linux GTK overlay). The search
    bar's "Tonight's Sky" button toggles `skyAtlasModeProvider` (filled when open,
    outlined when closed); `catalogView` renders the planetarium full-bleed as before.
    The in-page `{'type':'tonight'}` command is **no longer sent** — it would open the
    page's own duplicate Tonight drawer; the docked Flutter panel is the Tonight UI now.
  - **Recentre-the-planetarium (3b) — DONE (2026-06-29).** Re-added the per-row
    `Icons.my_location` button. It writes `{'type':'goto','ra':<deg>,'dec':<deg>}` to
    the new `planetariumCommandProvider` (a `Notifier<Map<String,Object?>?>` seam,
    always-notifies); `StellariumView` `ref.listen`s it and forwards over the
    `StellariumServer` loopback to the page's `/aracmd` `goto` handler (which centres
    directly on the coordinates via `pointRaDec`, no name lookup). `skyTargetProvider`
    is deliberately NOT used — the planetarium doesn't read it.
  - **STILL OPEN (3b follow-ups / fold into slice 4):**
    - **Per-request optics + mosaic overrides — server DONE (slice 4a, 2026-06-29).**
      `GET /api/v1/planning/tonight` now takes optional `focalLengthMm`/`reducer`/
      `sensorW`/`sensorH`/`pixelUm` (each per-field-merged over the active profile's optics;
      any supplied must be > 0) and `mosaicX`/`mosaicY` (default 1, range [1,20], enlarge the
      framing FOV per axis). Absent → profile optics at 1×1 (no behaviour change; no profile
      read on the common path). `GetTonight`/`Rank` gained optional `opticsOverride`/
      `mosaicTilesX`/`mosaicTilesY` params (default to profile/1×1). **Client FOV/mosaic
      controls (a picker that sends these params) — still TODO (slice 4b).**
    - **Profile the slice-2 window scan against the real installed OpenNGC catalog** —
      slice 2 runs the ±12h 288-sample scan over all geometrically-up candidates (vs
      slice 1's currently-visible only); bounded by the mag≤12 cull + MaxAltitude
      pre-filter, expected <100ms, but confirm on-device.
- **Slice 4 (polish)** — custom-horizon (terrain) integration if `UseCustomHorizon`;
  moon avoidance / separation as a score input; per-target "best window" highlight.

## Explicitly deferred

Points / achievements / gamification (the "Dragons = 90, Orion = 40, points for hours
and not blowing out cores" idea) — back-burnered by the user 2026-06-28. The
**transparent 0–100 score** above is the non-gamified core; a points layer could later
build on it without schema churn.
