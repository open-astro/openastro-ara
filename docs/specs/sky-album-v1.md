# Sky Album v1

A collector's album for imaged targets. Every catalog is a **Book** of **Cards**; a card you have imaged shows its **Art** in colour and a **Tier**, a card you have not imaged yet shows the same Art dimmed. The point is to make you want to go out and fill the gaps.

Status: slices 1 to 5 built; slice 6 open. Revised 2026-11-05 by [#1535](https://github.com/open-astro/openastro-ara/issues/1535): premade Art on the front, a client-side collection cache, and the baseball-card back. Produced by the wayfinder map in [#1489](https://github.com/open-astro/openastro-ara/issues/1489); each section links the ticket that settled it. Implementation follows the slices in the last section.

## Vocabulary

Add to `GLOSSARY.md` in the first implementation slice:

- **Sky Album**: the screen that shows the Books.
- **Book**: one catalog's complete set of Cards (Messier, Caldwell).
- **Card**: one catalog object's slot in a Book.
- **Collected**: a Card at bronze or better.
- **Tier**: bronze, silver or gold, derived from the lights the Card has recorded.
- **Art**: the premade picture on a Card's front, never the user's capture.
- **Night**: the observing night a light belongs to, by the noon rule.
- **Placeholder**: what a Card shows when it has no Art.

## Scope

In v1: Messier (110) and Caldwell (109) Books; frames matched to catalog objects by field-of-view containment on the mount pointing saved per frame; tiers; a Simple Mode entry point; the Tonight's Sky tie-in; the catalog data fix that completes both Books; premade Art per Card with a collection cache on the client ([#1535](https://github.com/open-astro/openastro-ara/issues/1535)).

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
  "total_lights": 4821,
  "frames": [
    {
      "frame_id": "…",
      "ra_deg": 10.6847, "dec_deg": 41.2687,
      "width": 4144, "height": 2822, "bin": 1,
      "pixel_scale_arcsec": 1.56,
      "captured_utc": "2026-03-02T11:20:31Z",
      "updated_utc": "2026-03-02T11:20:31Z",
      "exposure_seconds": 180,
      "filter_name": "Ha",
      "target_name": "M31",
      "composite_score": 0.83,
      "profile_id": "…"
    }
  ]
}
```

- Rows are lights with `captured_by_ara = 1` only; foreign frames are never served. Without `since`, every such light. With `since`, lights whose `updated_utc` is later than it; the client sends the newest `updated_utc` it holds for that rig as `since`. `total_lights` is always the full count. Keys are snake_case, as every daemon reply (the `SnakeCaseLower` policy).
- `profile_id` comes from the session join. `composite_score` is `json_extract(quality_score_json, '$.composite')`, null when absent.
- `GET /api/v1/frames` and `GET /api/v1/stats/targets` are untouched.

## Client

### State

The collection is the user's, not the rig's ([#1535](https://github.com/open-astro/openastro-ara/issues/1535)): one **collection cache** per device, merged across every rig the device connects to, in a JSON file in the app support directory (the pattern `safety_alarm_state` uses). It stores the frame rows themselves: `frame_id`, the matched object ids, `captured_utc`, `exposure_seconds`, `filter_name`, `updated_utc`. On album open the client fetches with the newest `updated_utc` it holds for that rig as `since` and merges by `frame_id`: a row with a newer `updated_utc` replaces the cached one (a pointing backfill can add objects to a frame); nothing is ever removed, so a full reply with fewer rows than the cache does not delete, and `total_lights` is informational only. The cache is a high-water mark: a Tier never drops, and deleting frames on the rig does not subtract (ADR 0001). There is no reset in v1; clearing app data is the only way. Rows are matched against `bundledCatalogProvider` (not the culled planning catalog, so every Book object is present), producing per-object aggregates:

| Field | Rule |
|---|---|
| frame count | lights that contain the object |
| integration hours | sum of `exposure_seconds` of those lights ([#1498](https://github.com/open-astro/openastro-ara/issues/1498): all lights, any filter, any rating) |
| first capture | min `captured_utc` |
| nights | lights grouped by Night: `captured_utc` in the site's time zone (settings), anything before local noon belongs to the previous evening, the `preflightNightKey` rule. Per Night: light count, the most common `exposure_seconds` (the "sub"), hours |
| filters used | distinct `filter_name`, in first-seen order |
| tier | none below 1 light; bronze at 1 light; silver at 1 h; gold at 5 h. Derived, never stored, never drops (the cache keeps every light) |

### Layout ([#1500](https://github.com/open-astro/openastro-ara/issues/1500))

Files: `screens/album/`, `state/album/`, `widgets/album/`. Nothing imported from `screens/simple/`; the only shell-specific behaviour arrives as the callback below.

- App bar: the album icon and "Sky Album", a segmented Book switch (Messier / Caldwell).
- A filter beside the header counts: All / Collected / Missing (decided 2026-11-05). It narrows the grid only; the counts stay the Book's, and the shelf hides under Collected.
- Header line: `n / total collected` with per-tier counts.
- Tonight shelf (see tie-in) above the grid when it has cards.
- Binder grid: cards at 2.5:3.5, max about 130 px wide. Card front ([#1535](https://github.com/open-astro/openastro-ara/issues/1535)): the Card's Art shown whole on black (never cropped), in colour when Collected and dimmed to grey while missing; the Placeholder, a grey silhouette icon per object type, when the Card has no Art; catalog label (`M 31`, `C 14`); name. Border colour per tier, a subtle glow on gold, grey text on missing cards.
- Card back ([#1535](https://github.com/open-astro/openastro-ara/issues/1535), the baseball-card back): in-place flip, the flipped card scaling up over its neighbours. From top: a name banner in the Tier colour with the catalog-number badge, the name in capitals and the set number (catalog number over Book size, `63 / 109`); a bio line from the bundled catalog row and the manifest (type, constellation, V magnitude, major axis, distance); the stats table, one row per Night (night, lights, sub, hours), the latest four Nights, then a totals row stating the full count (`Total · 9 nights`); a "Did you know?" line from the manifest; one action button, "Image this target" on a missing card, "Add more time" on a collected one; the art credit line (`Art: ESO · CC BY 4.0`). Tapping outside it, or on the card again, flips it back.
- Empty and failure states ([#1502](https://github.com/open-astro/openastro-ara/issues/1502)): with no lights at all the grid is fully grey with one caption, "Image a target to collect your first card"; the shelf still shows. If the fetch fails, show cached rows when present, else grey cards plus the app's usual error pill.

### Tonight's Sky tie-in ([#1501](https://github.com/open-astro/openastro-ara/issues/1501))

- The shelf is `tonightSkyProvider` (the ranking Simple Mode already shows as Tonight's best) filtered to the open Book's missing cards, in ranker order, capped at 8. No extra threshold. Hidden when the ranker returns nothing, when no album rows have loaded (every card would look missing) and under the Collected filter. Title: "N missing cards well placed tonight". Shelf cards flip like grid cards; there is no separate start button.
- Callback contract: the shell passes `void Function(PlanningDso target)`. The album fires it from the card back's action button, grid and shelf alike.
- Simple Mode's implementation: pop the album, set the planetarium selection the way `simpleSelectionOf` does for a Tonight's best tap (altitude and rise from the ranker when the object is in it, else from the catalog position only), and open the plan sheet with the target picked. Nothing is queued automatically.

### Entry point

A "Sky Album" item in the Simple Mode rail menu beside Library and Stats, pushing the screen the way `ImageLibraryScreen` is pushed from `simple_shell.dart`. No reverse link from the plan panel in v1.

## Art and data ([#1535](https://github.com/open-astro/openastro-ara/issues/1535))

- **Licences:** CC BY (any version), CC0 and public domain only, amateur Wikimedia Commons photos included. No ShareAlike, NonCommercial or NoDerivatives: the card crops and dims the Art.
- **Framing:** cut to the card's picture shape (5:6) with the whole object inside, on black. Wide objects are turned upright. A source photo that crops the object itself is rejected.
- **Coverage:** decided per object. A Card with no acceptable Art shows the Placeholder.
- **Format:** one WebP per Card at 560×672 (about 31 KB each, about 7 MB for 219 Cards), bundled with the app so the album works offline.
- **Manifest:** one JSON row per object: source URL, credit, licence, crop box, distance, the "Did you know?" line. A script in `scripts/` downloads, crops and writes the WebP from it; the outputs are committed, so builds need no network. Trivia and distances are drafted from the OpenNGC notes and standard references and reviewed per Card from a contact sheet.
- **Credits:** the line on each Card's back, and every credit with its source link registered with Flutter's licence registry, so the existing About page lists them.

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

Search gains these objects as a side effect, and so does Tonight's Sky except M40 and C41: `planningCull` drops M40 as a star (`**`) and C41 as an open cluster with no magnitude, so the shelf never offers those two. Positions and sizes come from OpenNGC's addendum where it has them.

## Tests

- Daemon: `RegisterFrameAsync` stores pointing only for J2000 and sets `captured_by_ara`; the backfill pass fills from a FITS header fixture, marks a NINA-style header (no Ara `SWCREATE`) as not Ara, sets `pointing_probed_utc` on a file without cards, and is a no-op on the second run; `album-frames` honours `since`, excludes foreign frames and reports `total_lights`.
- Client (pure Dart): containment on a synthetic frame (object at centre, at half the short side, just outside it, at a corner); alias fallback with `M31`, `M 31`, `Messier 31`; tier thresholds at the boundaries; aggregate fields; merge by `frame_id`, including a `since` response that only re-delivers cached rows. (The count reload rule is gone: the collection-cache bullet below replaces it.)
- Album screen widget test with a fake callback and neither shell: a tap on a missing card's action calls the callback with that `PlanningDso`.
- Catalog: after the data fix, the Messier Book has 110 cards and the Caldwell Book 109.
- Collection cache ([#1535](https://github.com/open-astro/openastro-ara/issues/1535), pure Dart): a newer `updated_utc` replaces a cached row; a full reply with fewer rows removes nothing; a Tier never drops; Night grouping across local midnight and on a spring-forward day; the sub column picks the most common exposure.
- Card widgets: a Card with no Art shows the Placeholder; a missing Card shows dimmed Art; the back shows the latest four Nights and a totals row with the full count; every Card's credit is in the licence registry.

## Slices

Each slice is one PR against `master`, in this order; each lists the spec section it implements.

1. Catalog data fix + `GLOSSARY.md` terms.
2. Daemon: columns, capture path, rescan parsing, backfill pass, tests.
3. Daemon: `album-frames` endpoint, tests.
4. Client: album state, matching, aggregates, tiers, pure tests.
5. Client: album screen, card flip, shelf, widget test with a fake callback.
6. Simple Mode: rail menu item, callback implementation.
7. Art and data ([#1535](https://github.com/open-astro/openastro-ara/issues/1535)): manifest, script, the 219-Card Art set with contact-sheet review, trivia and distances, credits in About.
8. Client ([#1535](https://github.com/open-astro/openastro-ara/issues/1535)): collection cache with the merge rule and Night grouping, card front on Art plus Placeholder, the baseball-card back. Removes slice 4's count reload (`needsFullReload` and its test).

## Known limits

- Frames captured with optics unset in the profile have no pixel scale and rely on the alias fallback.
- Gold at 5 h is fixed regardless of focal length; wide-field rigs reach it faster.
- The collection cache has no reset in v1; a wrongly credited Card stays until app data is cleared.
- Distance and the "Did you know?" line are curated by hand per Card, so they are only as current as the manifest.
- A collected card whose object is outside the planning cull or below the horizon shows no altitude or rise when handed to the plan sheet.
