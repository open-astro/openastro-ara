# Sky Album v1

A collector's album for imaged targets. Every catalog is a **Book** of **Cards**; a card you have imaged shows your own best frame and a **Tier**, a card you have not imaged yet is greyed out. The point is to make you want to go out and fill the gaps.

Status: specification, nothing built. Produced by the wayfinder map in [#1489](https://github.com/open-astro/openastro-ara/issues/1489); each section links the ticket that settled it. Implementation follows the slices in the last section.

## Vocabulary

Add to `GLOSSARY.md` in the first implementation slice:

- **Sky Album**: the screen that shows the Books.
- **Book**: one catalog's complete set of Cards (Messier, Caldwell).
- **Card**: one catalog object's slot in a Book.
- **Collected**: a Card at bronze or better.
- **Tier**: bronze, silver or gold, derived from the lights that contain the object.

## Scope

In v1: Messier (110) and Caldwell (109) Books; frames matched to catalog objects by field-of-view containment on the mount pointing saved per frame; tiers; a Simple Mode entry point; the Tonight's Sky tie-in; the catalog data fix that completes both Books.

Out of scope, by decision of the map: a full UI entry point, difficulty or global rarity, sky-survey thumbnails, plate-solve refinement of the frame centre, custom books, AstroBin, sharing, cryptographically signed provenance, rig pointing measurement, re-keying the Stats Targets section.

## Matching rule ([#1497](https://github.com/open-astro/openastro-ara/issues/1497), [#1499](https://github.com/open-astro/openastro-ara/issues/1499))

- A frame **contains** an object when the great-circle separation between the object's catalog position and the frame's pointing is at most half the frame's short side. FOV per axis is `width x pixel_scale_arcsec` and `height x pixel_scale_arcsec`. This is the inscribed circle: conservative, rotation independent, no sky position angle needed. Frame corners are never credited.
- Every object a frame contains is collected by it (M81 and M82 from one field), each with full exposure credit.
- A frame with no stored pointing, or no pixel scale, falls back to **alias matching**: its `target_name` compared against the object's id, name and `PlanningDso.aliases` after normalising both sides: lower-case, whitespace removed, and the words `messier` / `caldwell` folded to `m` / `c`, so `M31`, `M 31` and `Messier 31` all match.
- Pointing is stored only when the daemon's `FramePointing` is J2000. JNOW (SOFA missing), B1950 and J2050 frames store nothing and use the alias fallback.
- **Provenance ([#1502](https://github.com/open-astro/openastro-ara/issues/1502)):** only frames Ara captured fill a card. Ara writes `SWCREATE = 'OpenAstro Ara'` into every header; the drive scan also catalogs foreign frames (NINA archives) into recovered rows, and those never count. No signing in v1: nothing leaves the device, so a forged frame only fools its owner, and a key on an open-source SBC is the owner's anyway. Signed provenance is out of scope until sharing exists.

## Daemon ([#1499](https://github.com/open-astro/openastro-ara/issues/1499))

### New `frames` columns

Added with `AddColumnIfMissingAsync` next to the existing additive columns in `SqliteAraDatabase.cs`:

| Column | Type | Set by |
|---|---|---|
| `ra_deg` | REAL NULL | capture when J2000; backfill |
| `dec_deg` | REAL NULL | capture when J2000; backfill |
| `pixel_scale_arcsec` | REAL NULL | arcsec per stored pixel, for the binned image `width`/`height` describe. Capture: `206.265 x pixel_um x applied bin / effective focal_mm` from the profile optics. Backfill and rescan: `206.265 x XPIXSZ_um / FOCALLEN_mm`, where `XPIXSZ` is already multiplied by bin. The header writer bins `XPIXSZ` by the requested bin, so the two differ when a driver coerces the bin; the capture value is the right one |
| `updated_utc` | TEXT NULL | insert and every backfill write |
| `pointing_probed_utc` | TEXT NULL | capture (an Ara capture never needs its lossier header re-read), rescan recovery, and backfill whether or not the file had the cards |
| `captured_by_ara` | INTEGER NULL | 1 at capture; backfill sets 1 when `SWCREATE` starts with `OpenAstro Ara` (the XISF writer appends a version), else 0 |

`CameraService.RegisterFrameAsync` receives `job.Pointing` and the pixel scale; `FrameDto` gains the matching optional fields. `CaptureScanService.TryRecoverAsync` parses `OBJCTRA`/`OBJCTDEC` (via `ParseTargetCoordinates`), `EQUINOX`, `FOCALLEN` and `XPIXSZ` so a drive rescan fills the same columns for recovered rows.

### Backfill

One idempotent pass at startup, after the orphan scan, under the same guard as the startup scan in `Program.cs`:

1. `SELECT id, file_path FROM frames WHERE frame_type = 'light' AND pointing_probed_utc IS NULL`.
2. For each row, `FrameFileReader.ReadHeaderInfo`; take RA/Dec only when `EQUINOX` is `2000.0`; take pixel scale from `FOCALLEN` and `XPIXSZ` when both are present and positive; set `captured_by_ara` from `SWCREATE`.
3. Write what was found, set `pointing_probed_utc` when the header was read (whether or not it had the cards), bump `updated_utc` only when a value changed. If the read throws (file missing, drive not mounted), leave `pointing_probed_utc` unset so the row is probed again next boot.

Never routed through `POST /api/v1/stats/rebuild-catalog`: that endpoint deletes `frames` and `sessions` and re-inserts under new ids.

### Endpoint

`GET /api/v1/stats/album-frames?since=<utc>` in the §50 stats group, read only.

```json
{
  "totalLights": 4821,
  "frames": [
    {
      "frameId": "…",
      "raDeg": 10.6847, "decDeg": 41.2687,
      "width": 4144, "height": 2822, "bin": 1,
      "pixelScaleArcsec": 1.56,
      "capturedUtc": "2026-03-02T11:20:31Z",
      "exposureSeconds": 180,
      "filterName": "Ha",
      "targetName": "M31",
      "compositeScore": 0.83,
      "profileId": "…"
    }
  ]
}
```

- Rows are lights with `captured_by_ara = 1` only; foreign frames are never served. Without `since`, every such light. With `since`, lights whose `updated_utc` is later than it. `totalLights` is always the full count.
- `profileId` comes from the session join. `compositeScore` is `json_extract(quality_score_json, '$.composite')`, null when absent.
- `GET /api/v1/frames` and `GET /api/v1/stats/targets` are untouched.

## Client

### State

A provider family keyed by the active connection caches the album rows. On album open it fetches with the last `updated_utc` it holds as `since`, merges the returned rows into the cache by `frameId` (a `since` response also carries already-cached rows whose `updated_utc` moved, for example after the backfill), and refetches without `since` when the merged count does not equal `totalLights` (covers deletions). Rows are matched against `bundledCatalogProvider` (not the culled planning catalog, so every Book object is present), producing per-object aggregates:

| Field | Rule |
|---|---|
| frame count | lights that contain the object |
| integration hours | sum of `exposureSeconds` of those lights ([#1498](https://github.com/open-astro/openastro-ara/issues/1498): all lights, any filter, any rating) |
| first capture | min `capturedUtc` |
| best frame | max `compositeScore`, ties by latest capture |
| filters used | distinct `filterName`, in first-seen order |
| gear profile | `profileId` of the best frame, resolved to a name from the client's profile list, blank if gone |
| tier | none below 1 light; bronze at 1 light; silver at 1 h; gold at 5 h. Derived, never stored, can drop when frames are deleted |

### Layout ([#1500](https://github.com/open-astro/openastro-ara/issues/1500))

Files: `screens/album/`, `state/album/`, `widgets/album/`. Nothing imported from `screens/simple/`; the only shell-specific behaviour arrives as the callback below.

- App bar: "Sky Album", a segmented Book switch (Messier / Caldwell).
- Header line: `n / total collected` with per-tier counts.
- Tonight shelf (see tie-in) above the grid when it has cards.
- Binder grid: cards at 2.5:3.5, max about 130 px wide. Card front: best-frame thumbnail (via the existing frame thumbnail endpoint) or a grey silhouette icon per object type; catalog label (`M 31`, `C 14`); name. Border colour per tier, a subtle glow on gold, grey text on missing cards.
- Card back: in-place flip, the flipped card scaling up over its neighbours. Fields: first capture, integration and frame count, filters, gear profile, best-frame score, distance to the next tier; one action button, "Image this target" on a missing card, "Add more time" on a collected one. Tapping outside flips it back.
- Empty and failure states ([#1502](https://github.com/open-astro/openastro-ara/issues/1502)): with no lights at all the grid is fully grey with one caption, "Image a target to collect your first card"; the shelf still shows. If the fetch fails, show cached rows when present, else grey cards plus the app's usual error pill.

### Tonight's Sky tie-in ([#1501](https://github.com/open-astro/openastro-ara/issues/1501))

- The shelf is `tonightSkyProvider` (the ranking Simple Mode already shows as Tonight's best) filtered to the open Book's missing cards, in ranker order, capped at 8. No extra threshold. Hidden when the ranker returns nothing. Title: "N missing cards well placed tonight". Shelf cards flip like grid cards; there is no separate start button.
- Callback contract: the shell passes `void Function(PlanningDso target)`. The album fires it from the card back's action button, grid and shelf alike.
- Simple Mode's implementation: pop the album, set the planetarium selection the way `simpleSelectionOf` does for a Tonight's best tap (altitude and rise from the ranker when the object is in it, else from the catalog position only), and open the plan sheet with the target picked. Nothing is queued automatically.

### Entry point

A "Sky Album" item in the Simple Mode rail menu beside Library and Stats, pushing the screen the way `ImageLibraryScreen` is pushed from `simple_shell.dart`. No reverse link from the plan panel in v1.

## Catalog data fix ([#1500](https://github.com/open-astro/openastro-ara/issues/1500))

The bundled OpenNGC file lists NGC and IC objects only, so both Books come up short. Fix the data, never special-case the album:

| Missing | Object | Fix |
|---|---|---|
| M40 | Winnecke 4 | add a row |
| M45 | Pleiades (Mel 22) | add a row |
| M102 | NGC 5866 | set the `M` column on the existing row |
| C9 | Cave Nebula (Sh2-155) | add the `C 9` identifier, or alias the `sh2.csv` row |
| C14 | Double Cluster (NGC 869 / 884) | add the `C 14` identifier to one row and alias the other |
| C41 | Hyades (Mel 25) | add a row |
| C99 | Coalsack | add a row |

Search and Tonight's Sky gain these objects as a side effect. Positions and sizes come from OpenNGC's addendum where it has them.

## Tests

- Daemon: `RegisterFrameAsync` stores pointing only for J2000 and sets `captured_by_ara`; the backfill pass fills from a FITS header fixture, marks a NINA-style header (no Ara `SWCREATE`) as not Ara, sets `pointing_probed_utc` on a file without cards, and is a no-op on the second run; `album-frames` honours `since`, excludes foreign frames and reports `totalLights`.
- Client (pure Dart): containment on a synthetic frame (object at centre, at half the short side, just outside it, at a corner); alias fallback with `M31`, `M 31`, `Messier 31`; tier thresholds at the boundaries; aggregate fields; the merge-by-id + count reload rule, including a `since` response that only re-delivers cached rows.
- Album screen widget test with a fake callback and neither shell: a tap on a missing card's action calls the callback with that `PlanningDso`.
- Catalog: after the data fix, the Messier Book has 110 cards and the Caldwell Book 109.

## Slices

Each slice is one PR against `master`, in this order; each lists the spec section it implements.

1. Catalog data fix + `GLOSSARY.md` terms.
2. Daemon: columns, capture path, rescan parsing, backfill pass, tests.
3. Daemon: `album-frames` endpoint, tests.
4. Client: album state, matching, aggregates, tiers, pure tests.
5. Client: album screen, card flip, shelf, widget test with a fake callback.
6. Simple Mode: rail menu item, callback implementation.

## Known limits

- Frames captured with optics unset in the profile have no pixel scale and rely on the alias fallback.
- Gold at 5 h is fixed regardless of focal length; wide-field rigs reach it faster.
- A collected card whose object is outside the planning cull or below the horizon shows no altitude or rise when handed to the plan sheet.
