# A client profile section is loaded before it is saved

Status: proposed

## Context

The client keeps each profile section in a notifier that starts from hard-coded defaults, and a save writes the whole section. Nothing records whether the section was ever read from the rig. Each settings panel repeats its own load and save scaffold, and on 2026-11-12 two of fifteen refused a save after a failed load. Reading the code, a failed load followed by Save writes the client's defaults over the rig's values, and the client's default file name template differs from the daemon's. Nothing resets the sections when the rig or the active profile changes, so one rig's values can be written to another. The daemon accepts a section write with no check that the writer saw the current copy. Offline, eight planning sections are seeded from a cache file through the same path as a real load.

## Decision

We decided (2026-11-12, [#1807](https://github.com/open-astro/openastro-ara/issues/1807)) that a profile section in the client is one module that knows where its values came from: nowhere, the offline cache or the rig. Only a section loaded from the rig can be saved. On Save the module retries the load once and, if that fails, refuses and writes nothing. The client holds no defaults for a section the rig owns: the daemon's normalizer is the one owner (ADR 0012), and a section that is not loaded shows that state. A change of rig or of active profile returns every section to not loaded, in one place. The daemon returns a hash of the section as an `ETag` and checks `If-Match`; the hash is derived, so nothing new is stored. On a mismatch the app writes nothing, keeps the person's edits on screen and offers a reload; it does not merge. The module does not track unsaved edits. Storage pilots it, with the version check added to its one endpoint by hand; the other sections follow once ADR 0012's section table exists, then Simple Mode's writes, and the daemon then refuses a section write that carries no version.

## Rules

1. A section is saved only after it was loaded from the rig; a failed load is retried once and then the save is refused with nothing written. Scope: `client/openastroara_client/lib/state/settings/*.dart`, `client/openastroara_client/lib/screens/settings/panels/*.dart`. Checked by: review (a migrated panel or notifier that can reach a PUT while its section is not loaded from the rig).
2. A section seeded from the offline cache is readable and is never saved. Scope: `client/openastroara_client/lib/state/profile_cache_state.dart`, `client/openastroara_client/lib/state/settings/*.dart`. Checked by: review (a cache seed that a migrated section reports as loaded from the rig).
3. The client writes no default value for a migrated section. Scope: `client/openastroara_client/lib/state/settings/*.dart`, `client/openastroara_client/lib/services/profile_api.dart`, `client/openastroara_client/lib/settings/registry.dart`. Checked by: review (a default for a migrated section's field in a notifier, a JSON reader or the settings registry).
4. A change of rig or of active profile resets every section to not loaded in one place. Scope: `client/openastroara_client/lib/state/settings/*.dart`, `client/openastroara_client/lib/state/profile_management_state.dart`, `client/openastroara_client/lib/state/saved_server_state.dart`. Checked by: review (a section that keeps its values across a rig or profile change, or a per-panel reset).
5. The daemon answers a section read with a derived `ETag` and refuses a write whose `If-Match` names an older copy. Scope: `OpenAstroAra.Server/Endpoints/ProfileEndpoints.cs`. Checked by: review (a migrated section endpoint with a stored version, or one that accepts a stale `If-Match`).
6. After a refused write the app keeps the person's edits and offers a reload, and it never merges or overwrites. Scope: `client/openastroara_client/lib/screens/settings/panels/*.dart`. Checked by: review (a migrated panel that drops edits or retries the write after a version mismatch).

## Alternatives rejected

- **Guard each panel**: no new module, but it is the scaffold thirteen panels already got wrong.
- **Keep client defaults in one place**: offline panels would show values, but the client would still restate what the daemon owns.
- **Client guard only, no wire change**: smaller, but Simple Mode's unloaded writes and any second client stay able to overwrite.
- **A stored revision counter**: cheap to compare, but a second fact to keep in step with the section and a profile migration.
- **One version for the whole profile**: one header, but a save in one panel would invalidate every other open panel.
- **Track unsaved edits too**: Save could be disabled when nothing changed, but every setter in eighteen notifiers would change.

## Consequences

- The load-before-save rule is tested once in state, with no widget.
- A settings panel with no rig shows a not-loaded state where it showed defaults; planning keeps working from the cache.
- The REST contract gains `ETag` and `If-Match` on profile sections; the daemon accepts a write without one while Simple Mode's writes have not moved, which leaves those writes unguarded in that period.
- The rollout past Storage waits for ADR 0012.

## How to reverse

Remove the `If-Match` check from the daemon, which older clients never sent, and give each notifier its defaults back. The panels then need their own guards again.

## Links

- [#1807](https://github.com/open-astro/openastro-ara/issues/1807)
- [#1806](https://github.com/open-astro/openastro-ara/issues/1806) (the Guider panel's unguarded save)
- ADR 0004, ADR 0005, ADR 0012
