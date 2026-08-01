# Guiding auto-tuning

Status: bounded implementation slice. Deterministic core, live guide-event collection, replay, unguided
characterization, bounded background experiments, interleaved baselines, Alpaca guide-rate control,
SQLite session history, Markdown reporting, PHD2 parameter control, REST status/proposal/apply/rollback,
WebSocket lifecycle and bounded telemetry-summary events, optional main-camera star-shape validation, and a
Flutter review panel are implemented.

## Scope

ARA owns the tuner. PHD2/openastro-guider remains the real-time guide engine. The tuner never issues guide
pulses and never uses an LLM. It consumes `GuideStep` events, derives actual monotonic cadence, separates raw and applied
guide distance, calculates robust motion features, classifies observed behavior, plans bounded candidates, and
uses a readback-verified PHD2 transaction for explicit apply.

## Current limitations

- Bootstrap confidence is implemented with deterministic moving blocks. Weather normalization still needs
  an observing-conditions telemetry source.
- Flutter currently exposes a compact panel and report dialog; full multi-screen charts remain follow-up work.
- Multi-star and rejected-star counts are optional: openastro-guider emits them when its multi-star list is
  available; single-star and legacy guider paths remain null.
- PPEC, Z-filter/LowPass2 search, automatic camera gain/binning changes, and dither optimization remain
  deliberately deferred.
- Alpaca telescope identity fields (description, driver info/version, interface version, supported actions,
  tracking rate, and side of pier) are captured as metadata. They remain priors; telemetry classification stays
  probabilistic.

Implemented safety additions include supported-exposure probing, multi-star-aware quality gating, tolerant
PHD2 calibration validation, continuous live abort checks, persisted profile policy caps, mount azimuth capture,
per-sample mount/parameter metadata, connected-safe safety-monitor gating, and independent RA/DEC guide-rate
candidate pairs.

These limits are reported by `/api/v1/guiding/autotune/capabilities` and must not be hidden by the client.

## Core calculations

```text
guide_scale = 206.265 * pixel_size_um * binning / focal_length_mm
expected_pulse_ms = 1000 * slope_arcsec_per_second * frame_interval_s / guide_rate_arcsec_per_second
hf_noise = 1.4826 * MAD(first_difference) / sqrt(2)
```

The analyzer uses robust detrending, percentiles, derivative tails, a non-uniform-time period grid,
harmonic power, entropy, stationarity, and zero-crossing/persistence features. No FFT is run on irregular
timestamps.

## REST

```text
GET  /api/v1/guiding/autotune/capabilities
GET  /api/v1/guiding/autotune/sessions/latest
GET  /api/v1/guiding/autotune/sessions/latest/report
POST /api/v1/guiding/autotune/sessions
POST /api/v1/guiding/autotune/sessions/latest/cancel
POST /api/v1/guiding/autotune/sessions/latest/apply
POST /api/v1/guiding/autotune/sessions/latest/rollback
GET  /api/v1/guiding/autotune/sessions/{sessionId}
GET  /api/v1/guiding/autotune/sessions/{sessionId}/report
POST /api/v1/guiding/autotune/sessions/{sessionId}/cancel
POST /api/v1/guiding/autotune/sessions/{sessionId}/apply
POST /api/v1/guiding/autotune/sessions/{sessionId}/rollback
```

`POST /sessions` creates a dry-run proposal by default. With `dry_run:false`, ARA owns a background experiment:
it disables guide output for native-motion capture, restores output in a `finally` path, re-plans from that
recording, evaluates bounded candidates with stabilization and interleaved baseline windows, persists every
candidate result and optionally persists raw telemetry windows, and restores the captured settings unless automatic apply is explicitly requested. The apply
endpoint acts only on a tested winner. Any failure restores exposure, algorithms, discovered parameters, DEC
mode, guide-output state, and Alpaca guide rates. Calibration is recorded; the guider API has no calibration
setter, so the transaction does not mutate it.

Set `use_main_camera_validation` to capture bounded frames through `IAnalysisFrameSource`. The validator measures
median star eccentricity (`1 - Roundness`) and adds it to the candidate score. Live validation requires a known
main image scale, a connected camera, and usable stars. An increase above 0.05 eccentricity is a critical
regression and vetoes the candidate. Dry-run mode never captures main-camera frames.

Session routes also accept a persisted `{sessionId}` for status, report, cancel, apply, and rollback. Mutating an
older session is rejected; only the current session owns live equipment state.

On server restart, an unfinished session becomes `Failed`; hardware work never resumes automatically. Its
persisted snapshot remains available for reconnect-and-rollback. Rollback executes all independent restore
operations and aggregates failures so one rejected driver write cannot suppress later restore attempts.

## Tests

`OpenAstroAra.Test/GuidingAutoTuneCoreTest.cs` covers scale, pulse physics, harmonic classification, low-data
uncertainty, image-scale safety, candidate variation, mount-prior loading, DEC reversal delay, bootstrap
confidence, telemetry persistence, replay, critical-regression rejection, main-camera star-shape validation,
empty exposure lists, drift, response latency, large-backlash planning, exposure selection, multi-star quality,
and calibration validation. `GuidingRollbackTransactionTest` covers failure injection and restart recovery;
`GuidingTelemetryCollectorTest` covers event ordering and optional fields. Hardware and live server tests still
need the repository's .NET SDK and guider simulator.
