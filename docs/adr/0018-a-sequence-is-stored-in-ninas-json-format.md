# A sequence is stored in NINA's JSON format, and NINA's engine runs it

Status: accepted

## Context

Ara is a port of NINA, and the sequencer is the largest part that came across unchanged: containers, instructions, triggers and conditions, and the engine that runs them. NINA saves a sequence as JSON written by Newtonsoft.Json, where every node carries a `$type` token with its .NET class name. Users arrive with NINA sequence files they want to keep.

The daemon's REST types use System.Text.Json with a source-generated context (`AraJsonSerializerContext`) and snake_case names, and the server was set up to publish as Native AOT.

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §0 rule 2 and §38.1) that a sequence is stored and exchanged in NINA's JSON schema as it is, with its `$type` class names, and that NINA's sequencer engine runs it. Ara adds one field, `"schemaVersion": "openastroara-sequence-v1"`, which the daemon checks on upload. Writing uses Newtonsoft.Json with `TypeNameHandling.All`. Reading never sets `TypeNameHandling`: each `$type` token goes through `NinaTypeRemapper`, which maps the `NINA.*` namespaces to `OpenAstroAra.*`, and through the creation converters, which build only sequencer types the factory knows. A token that names no known type becomes an unknown-item placeholder and the load goes on.

An Ara-native sequence model, with NINA JSON as an import adapter, was written down and deferred until after the first release (retired `design/PORT_DECISIONS.md`, entry "Smarter native sequence model", last present at commit `51cba5c40`).

## Rules

1. A sequence is serialized by `SequenceJsonConverter` in NINA's schema with `$type` on every node; no second writer and no second on-disk or on-wire sequence format exists. Scope: `OpenAstroAra.Sequencer/Serialization/*.cs`, `OpenAstroAra.Server/Services/SequenceBodyDeserializer.cs`. Checked by: `OpenAstroAra.Test/SequenceJsonRoundTripTest.cs` and `OpenAstroAra.Test/NinaImportFidelityTest.cs`.
2. Reading a sequence never sets `TypeNameHandling`; a `$type` token is resolved only through `NinaTypeRemapper` and the creation converters. Scope: `OpenAstroAra.Sequencer/Serialization/*.cs`. Checked by: `OpenAstroAra.Test/SequenceBodyDeserializerTest.cs` (`TryDeserialize_unknown_type_yields_graceful_degradation_not_throw`), and review (no `TypeNameHandling` in a `JsonSerializerSettings` passed to a deserialize call).
3. A sequence body the daemon accepts declares `schemaVersion` `openastroara-sequence-v1`. Scope: `OpenAstroAra.Server/Services/SequenceSchemaValidator.cs`. Checked by: `OpenAstroAra.Test/SequenceSchemaValidatorTest.cs`.

## Alternatives rejected

- **An Ara-native sequence model, with NINA JSON as an import adapter**: Ara would own the format and could add its own nodes, and the `$type` coupling to .NET class names would go. It was deferred: the NINA-shaped sequencer stays for the first release, and a native model would keep NINA's engine under it and keep NINA import working throughout.
- **Port the converter to System.Text.Json with `[JsonPolymorphic]`**: it would let the daemon publish as Native AOT again. It needs a type discriminator declared for every NINA container, instruction, trigger and condition, and the project file calls it a substantial follow-up.

## Consequences

- A NINA sequence file loads with its structure intact, and the import path is a namespace remap plus an equipment remap.
- Native AOT publishing is paused. The server's project file names the sequence body's reflection-based converter as the reason ([#1184](https://github.com/open-astro/openastro-ara/issues/1184)). Newtonsoft.Json is also used outside the sequencer (the PHD2 client among others), so the sequence format is not the only work between the daemon and AOT.
- Renaming or moving a sequencer class changes the saved format. Every such rename needs a remap entry, or saved sequences stop loading that node.

## How to reverse

Moving to an Ara-native format means a new model and schema, a migration for every saved sequence and the active checkpoint, a NINA importer that maps into the new model, and a new editor model in the client. The engine can stay. Dropping only Newtonsoft is the smaller step: port `SequenceJsonConverter` and the creation converters to System.Text.Json with a discriminator table, keep the `$type` spelling on disk, then turn AOT back on.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §0 (port, not redesign), §38 (sequence file format and NINA import)
- `OpenAstroAra.Server/OpenAstroAra.Server.csproj` (the comment on why AOT is paused)
- [#1184](https://github.com/open-astro/openastro-ara/issues/1184)
- [Simple Mode's queue is one sequence on the rig](0010-simple-mode-queue-is-one-sequence-on-the-rig.md)
