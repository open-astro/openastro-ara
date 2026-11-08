# The Sky Album collection cache is a high-water mark

Status: accepted

## Context

Captures are copied off the rig and deleted, so an album that mirrors the rig's `frames` table loses Cards and drops Tiers over time, and a thumbnail of a deleted frame is a grey placeholder. The card front shows premade Art instead of the user's frame, so the frames are needed only for the scoring.

## Decision

We decided (2026-11-05, [#1535](https://github.com/open-astro/openastro-ara/issues/1535)) that each client keeps its own collection cache of the frame rows it has ever been served, merged across rigs by frame id: a newer row replaces an older one, nothing is ever removed, and a Tier never drops.

## Rules

1. The collection cache merges rows from every rig by frame id; a newer row replaces an older one, and no row is ever removed. Scope: `client/openastroara_client/lib/state/album/album_state.dart`. Checked by: the album state test "fetch with since per rig, merge, never remove, keep rows on failure".
2. A Tier never drops when the rig deletes a frame. Scope: `client/openastroara_client/lib/state/album/album_matching.dart`. Checked by: the album matching test "a Tier computed before a deletion on the rig is the same after it".
3. `total_lights` is informational and never triggers a refetch. Scope: `client/openastroara_client/lib/state/album/*.dart`, `client/openastroara_client/lib/screens/album/*.dart`. Checked by: review (a fetch or refetch whose condition reads `totalLights`).

## Alternatives rejected

- **Mirror the rig** (the original spec): simple and always consistent with the rig, but the album forgets what the user deleted to free the Pi's disk, which defeats a collector's album.
- **An album ledger on the rig**: one shared truth for every client, but it has to be backed up with the rig and the rig is the thing that gets wiped or replaced.

## Consequences

- A per-client high-water mark (chosen) survives the rig's deletions and rebuilds, needs no daemon change, and matches the user's mental model that a card once earned stays earned.
- Two devices can disagree until each has talked to the rig; neither is wrong.
- There is no reset in v1, so a wrongly credited Card stays until app data is cleared.
- The spec's "refetch without `since` when the merged count differs from `total_lights`" rule is gone: `total_lights` is informational only.

## How to reverse

Drop the client cache and read the frames from the rig again. The Cards and Tiers earned from frames the rig has since deleted are lost with the cache.

## Links

- [#1535](https://github.com/open-astro/openastro-ara/issues/1535)
