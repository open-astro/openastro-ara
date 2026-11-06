# Tonight's Sky: filter score nudge and points layer

Two Tonight's Sky ideas were dropped:

- a ±3 nudge to the score from the filter/emission advice
- a points layer on top of the achievements

## Why this is out of scope

**Filter nudge.** The score already accounts for the filter set. The
ranker's "filter reality" multiplier cuts an emission target to ×0.85 when the
set has no narrowband glass (×0.75 under Bortle 5 or worse) and lifts it to
×1.05 when narrowband is present, and the moon penalty is halved with
narrowband glass. A second ±3 nudge driven by the filter advice would count
the same fact twice. The advice itself (narrowband, duoband or broadband)
already shows on each target's row.

**Points layer.** Achievements (streaks, milestone badges) already shipped
(`GET /stats/achievements`). Points would add a currency with nothing to
spend it on, and would push the user toward chasing a score instead of
imaging what the sky offers.

The mosaic/FOV picker from the same issue was kept and filed separately.

## Prior requests

- #1176: "Tonight's Sky scoring decisions: ±score nudge for filter advice, moon as a score input, framing-fit chip revival, gamification layer"
