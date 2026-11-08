# A profile section is declared once

Status: proposed

## Context

The daemon lists each profile section separately in `IProfileStore`, both stores, the snapshot's Capture and Apply, the normalizer and the share ledger. A section missing from one list fails silently: selecting a profile kept the previous rig's unguided settings because Apply had no entry for them (#1264). Defaults are written more than once, and nothing ties them together. `ProfileShareService` says that a new field ships in the share file by default, and its ledger says "ALL 13 sections" while the snapshot has more.

## Decision

We decided (2026-11-08, [#1697](https://github.com/open-astro/openastro-ara/issues/1697)) that each profile section is declared once, in a typed C# table: its getter, its setter, its default and its share policy. Capture, Apply, normalization and the share strip walk that table. The work stays inside the daemon and leaves the wire contract unchanged. Where the existing defaults differ, the normalizer's value is kept, because it is what a fresh or healed profile gets; a test lists every difference before the migration, and that PR names each one.

## Rules

1. A profile section is added by one entry in the section table; Capture, Apply, normalization and share read that table. Scope: `OpenAstroAra.Server/Services/ProfileStoreSnapshot.cs`, `OpenAstroAra.Server/Services/ProfileSnapshotNormalizer.cs`, `OpenAstroAra.Server/Services/ProfileShareService.cs`. Checked by: review (a section named by hand in Capture, Apply, normalization or the share strip).
2. A section's default is stated once, in the section table. Scope: `OpenAstroAra.Server/Services/InMemoryProfileStore.cs`, `OpenAstroAra.Server/Services/FileProfileStore.cs`, `OpenAstroAra.Server/Services/ProfileSnapshotNormalizer.cs`. Checked by: review (a section default written outside the table).
3. Every section states a share policy, and every field of a section's DTO is classified as kept or stripped. Scope: `OpenAstroAra.Server/Services/ProfileShareService.cs`, `OpenAstroAra.Server/Contracts/ProfileDtos.cs`. Checked by: review (a section or DTO field with no classification, or no failing test guarding it).
4. Declaring sections once does not change the REST contract. Scope: `OpenAstroAra.Server/openapi.yaml`, `OpenAstroAra.Server/Endpoints/ProfileEndpoints.cs`. Checked by: the OpenAPI contract snapshot test "The_committed_spec_matches_what_the_daemon_generates".

## Alternatives rejected

- **A source generator from attributes on the DTOs**: the most automatic, and it could cover the routes too, but a generator is the hardest part of this to debug.
- **Reflection at start-up**: the least code, but it pushes errors to run time and runs against the source-generated JSON the daemon already uses.
- **Generating the client's section code too**: removes the client's share of the cost, but it is far larger and couples the Dart build to the C# table.
- **A share policy per section only**: simpler, but a new field inside a kept section would still ship by default.

## Consequences

- A section missed in one list stops being possible, and one round-trip test replaces the hand-written Capture and Apply checks.
- A new field cannot reach the share file unclassified.
- Adding a section still costs a DTO, a route and the client's files.
- Two store adapters stay at the `IProfileStore` seam.

## How to reverse

Write each section back out into the stores, the snapshot, the normalizer and the share ledger, and restate its default in each place. The round-trip and classification tests stop applying.

## Links

- [#1697](https://github.com/open-astro/openastro-ara/issues/1697)
- [#1264](https://github.com/open-astro/openastro-ara/issues/1264) (the section Apply missed)
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §37 (profile setup)
