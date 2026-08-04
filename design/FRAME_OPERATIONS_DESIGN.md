# Rank 1 frame operations design

This note owns Rank 1 suggested PR 5: frame-library REST completion and frame
WebSocket lifecycle events. It builds on the atomic storage, bounded source
loading, preview cache, annotation, and analysis code in PR #899.

## Ownership

- Server catalog owner: `SqliteFrameRepository`.
- Async orchestration owner: `FrameOperationService`.
- Job/status owner: existing `IBatchJobService` and `/api/v1/jobs/{id}`.
- Client owner: the frame-library Riverpod provider in the next Flutter slice.
- Simulator seam: `ISourceImageDataFactory`, `IPreviewImageService`,
  `IFrameRepository`, and `IWsBroadcaster`; no camera hardware is needed.

## REST contracts

- `GET /api/v1/frames/{id}/preview` renders the default preview. Existing POST
  remains for request-specific rendering.
- `GET /api/v1/frames/{id}/metadata` returns catalog, storage, analysis,
  preview, checksum, CFA, and quarantine state.
- `POST /api/v1/frames/{id}/rebuild-preview` validates synchronously, queues a
  per-frame job, and returns `202 OperationAcceptedDto`.
- `POST /api/v1/frames/{id}/reanalyze` validates synchronously, queues a
  per-frame job, and returns `202 OperationAcceptedDto`.
- `POST /api/v1/frames/bulk/quarantine` applies or reverses catalog quarantine
  without moving or changing source bytes and returns `202`.
- Existing bulk rate, tag, move, and delete operations gain bounded validation
  and replay-safe idempotency.

Unknown frames return 404. Invalid values return 400. Missing source bytes,
idempotency-key reuse with a different request, and conflicting in-flight work
return 409. Unsupported image operations return 422. Responses use
ProblemDetails. Every route is documented in `OpenAstroAra.Server/openapi.yaml`.

## Idempotency and concurrency

An `Idempotency-Key` is scoped by operation. The first request fingerprint and
result are retained for 24 hours. Same-key/same-request retries return the same
operation ID or mutation result. Same-key/different-request retries fail with a
typed conflict; they never silently replay or execute a different mutation.
Terminal jobs remain queryable for the same 24-hour window, so a replay never
points at an already-pruned status resource.

Rebuild and reanalysis are single-flight per frame and operation kind. A second
identical request joins the active job. A different request receives 409. Jobs
are bounded by the existing batch-job service and are cancellable through the
existing jobs endpoint.

## WebSocket contracts

New catalog events:

- `frame.persist_started`
- `frame.persist_progress`
- `frame.analysis_started`
- `frame.preview_started`
- `frame.failed`
- `frame.quarantined`

Existing `frame.complete` and `frame.analyzed` remain wire-compatible. The
contract spelling `frame.preview_ready` is emitted together with the legacy
`frame.preview.ready` alias during the compatibility window. Every payload
includes `frame_id` and `session_id`; relevant payloads include safe state,
error code/message, or cache key. The WebSocket envelope supplies the monotonic
sequence. Source bytes never enter events.
Cache-hit preview reads update metadata silently and do not re-emit lifecycle
events; this prevents event-driven client invalidation from feeding back forever.

## Persistence and restart behavior

Schema version 7 adds nullable quarantine, preview, and analysis-state columns.
The migration uses idempotent column checks, preserves existing rows, tolerates
a partially applied migration, and is safe to invoke repeatedly. Storage truth
remains in `frame_storage_lifecycle`; `sha256` remains the source checksum.

Queued/running preview or analysis state is changed to `interrupted` during
startup initialization. No image work resumes automatically. The operator can
retry through an idempotent endpoint.

## Cancellation and rollback

Request cancellation can prevent queue admission. Once accepted, work uses the
job cancellation token, not the disconnected HTTP request token. Preview
cancellation persists `interrupted`; analysis cancellation persists `failed`
with the stable `analysis_cancelled` code. Unexpected failures persist a safe
code/message and emit `frame.failed`. Rebuild deletes only derived cache files.
Reanalysis updates durable measurements only after a valid detector result.
Quarantine runs in one SQLite transaction and never mutates source files.

## Verification

- Fresh, previous-version, partial, and repeated migration tests.
- Metadata, rebuild, reanalysis, quarantine, and bulk-idempotency tests.
- Same-key replay and conflicting-key tests.
- Per-frame concurrency and cancellation tests.
- WebSocket catalog, payload, ordering, failure, and terminal-event tests.
- Endpoint status-mapping tests.
- OpenAPI contract check, analyzer build, server/FITS/Stretch tests, format,
  Unicode, and ARM64 publish gates.

No settings, help icons, camera mutations, RAW support, or Flutter UI land in
this slice.
