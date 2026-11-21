import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event_types.dart';

// Samples are the JSON the daemon emits (EquipmentEventPublisherTest pins the
// same bytes on the C# side).
Map<String, dynamic> _json(String s) => jsonDecode(s) as Map<String, dynamic>;

void main() {
  test('camera.exposure_started payload parses a daemon sample', () {
    final p = CameraExposureStartedPayload.fromJson(_json(
        '{"frame_id":"0b9d3c0e-5a41-4c1e-9f0a-6d2f7c1a8e55","exposure_sec":120.5,'
        '"started_utc":"2026-10-04T21:30:15.2500000Z","kind":"light","filter_name":"Ha"}'));
    expect(p.frameId, '0b9d3c0e-5a41-4c1e-9f0a-6d2f7c1a8e55');
    expect(p.exposureSec, 120.5);
    expect(p.startedUtc, '2026-10-04T21:30:15.2500000Z');
    expect(p.kind, 'light');
    expect(p.filterName, 'Ha');
  });

  test('camera.exposure_started payload takes a null filter and a whole-number exposure', () {
    final p = CameraExposureStartedPayload.fromJson(_json(
        '{"frame_id":"f","exposure_sec":2,"started_utc":"t","kind":"analysis","filter_name":null}'));
    expect(p.exposureSec, 2.0);
    expect(p.filterName, isNull);
  });

  test('camera.exposure_complete payload parses a daemon sample', () {
    final p = CameraExposureCompletePayload.fromJson(_json(
        '{"frame_id":"f","exposure_sec":30,"started_utc":"t","kind":"flat","elapsed_ms":31250}'));
    expect(p.elapsedMs, 31250);
    expect(p.kind, 'flat');
  });

  test('camera.exposure_failed payload parses a daemon sample', () {
    final p = CameraExposureFailedPayload.fromJson(
        _json('{"frame_id":"f","kind":"light","reason":"cancelled"}'));
    expect(p.reason, 'cancelled');
  });

  test('a payload missing a required field throws a TypeError', () {
    expect(() => CameraExposureFailedPayload.fromJson(_json('{"frame_id":"f"}')),
        throwsA(isA<TypeError>()));
  });
}
