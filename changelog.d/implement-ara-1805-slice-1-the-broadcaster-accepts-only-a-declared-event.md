### Changed

- **WebSocket events are declared once**: the broadcaster's publish now accepts only a declared `WsEvent`, so an undeclared event type no longer compiles (#1805, step 1 of ADR 0014). Payloads stay hand-built. A committed `OpenAstroAra.Server/ws-events.json` snapshot of the declared events is drift-tested by `WsEventSnapshotTest`, and the client's `lib/models/ws_event_types.dart` is generated from it by `scripts/generate-ws-events-dart.py`.

### Removed

- **`calibration.flats_generated` leaves the WebSocket catalog**: `GET /api/v1/ws/catalog` no longer lists it, because nothing emits it (#1814). `WsEventCatalogCoverageTest` no longer carries a planned-event exception.
