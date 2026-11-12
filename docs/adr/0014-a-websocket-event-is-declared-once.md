# A WebSocket event is declared once

Status: proposed

## Context

`WsEventCatalog` calls itself the single source of truth for the events on `/api/v1/stream`, but it is not on the emit path: the broadcaster's publish takes any string and a hand-built payload. On 2026-11-12 the daemon emitted 12 event types the catalog did not name (`sequence.failed` among them), the list behind `GET /api/v1/ws/catalog` left out 15 that were emitted, and 37 entries had no emitter. Payloads are built by hand at about 111 sites. `openapi.yaml` cannot carry WebSocket shapes, so the client has no machine contract: 22 files match event names as string literals and read payload maps key by key. The REST side has none of this, because its contract is generated and a test fails when it drifts.

## Decision

We decided (2026-11-12, [#1805](https://github.com/open-astro/openastro-ara/issues/1805)) that each event is declared once in the daemon, as its token and its payload shape, and that everything else reads that declaration: the emit path, the catalog endpoint, a committed snapshot and the client's event types. The broadcaster accepts only a declared event, so an undeclared event does not compile. The work runs in two steps. Step 1 moves every emitter to a declared token and leaves payloads hand-built. Step 2 gives each event family a typed payload record in the source-generated JSON context, one family per change. A generated snapshot of events and payload shapes is committed and drift-tested the way `openapi.yaml` is, and the client's typed events are generated from it, committed and drift-tested. The declaration holds only events that something emits; a planned event is added with its emitter.

## Rules

1. An event reaches the wire only through its declaration: no emitter passes an event type as a string. Scope: `OpenAstroAra.Server/Services/*.cs`, `OpenAstroAra.Server/Endpoints/*.cs`. Checked by: review (a publish call or emit helper that takes or forwards a `string` event type).
2. The declaration lists every event the daemon emits and no event that nothing emits. Scope: `OpenAstroAra.Server/Contracts/WsEvents/WsEventCatalog.cs`. Checked by: review (a declared event with no emitter in the same change, or an emitted token absent from the declaration).
3. The broadcaster assigns the sequence number and stores the payload as sent, so a replay after a resume sends the stored envelope. Scope: `OpenAstroAra.Server/Services/PlaceholderWsServices.cs`, `OpenAstroAra.Server/Endpoints/WebSocketEndpoints.cs`. Checked by: review (a sequence number assigned outside the broadcaster, or a replay that rebuilds a payload).
4. A payload that has a typed record is serialized through the source-generated JSON context. Scope: `OpenAstroAra.Server/AraJsonSerializerContext.cs`, `OpenAstroAra.Server/Services/*.cs`. Checked by: review (a hand-built payload for an event family that already has a typed record).
5. The client matches events through the generated event types and reads no payload map by key. Scope: `client/openastroara_client/lib/models/ws_event.dart`, `client/openastroara_client/lib/state/**/*.dart`, `client/openastroara_client/lib/widgets/**/*.dart`. Checked by: review (an event-name string literal or a `payload[` read in a file whose event family has generated types).

## Alternatives rejected

- **A test that walks emit sites**: no change to the emitters, but it is the promise the catalog already made and did not keep, and it says nothing about payloads.
- **Reject an unknown token at runtime**: small, but the drift shows up on a rig at night instead of in a build.
- **Tokens only, payloads hand-built for good**: a third of the work, but the client still has no payload contract.
- **A hand-written client checked against the snapshot**: no generator to own, but 22 files keep a hand-kept copy of each shape.
- **An AsyncAPI document**: a standard format, but more tooling than a snapshot only this client reads.

## Consequences

- Adding an event is one declaration, and the catalog endpoint, the snapshot and the client types follow from it.
- About 30 daemon files change in step 1, and every later event family change regenerates two committed files.
- The 36 entries with no emitter leave the catalog; `calibration.flats_generated` stays as the one planned entry and gets its emitter or goes.
- Typed payload records keep a Native AOT publish possible; the claim in about ten comments that hand-built payloads are required by an AOT gate is checked by a build in step 1 before any of them is reworded.

## How to reverse

Give the broadcaster its string overload back and stop regenerating the snapshot and the client types. The typed records and generated client types keep working as ordinary code; what is lost is the guarantee that the three agree.

## Links

- [#1805](https://github.com/open-astro/openastro-ara/issues/1805)
- [#1804](https://github.com/open-astro/openastro-ara/issues/1804) (the catalog drift fix)
- [`design/API_CONTRACT.md`](../../design/API_CONTRACT.md) (WebSocket wire protocol)
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §10.10, §17, §60.9
- ADR 0002
