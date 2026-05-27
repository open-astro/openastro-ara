import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame.dart';

/// Library state per playbook §40. Phase 12f.1 seeded in-memory demo
/// sessions (M42 + NGC 6188). Phase 12g.2 expanded the demo to 7
/// sessions across a 30-day window so the Stats dashboard charts have
/// usable data. Phase 12f.2 replaces this with a real `LibraryService`
/// hitting `/api/v1/sessions` + `/api/v1/frames`.

enum LibraryGrouping { bySession, byTarget, byDate }

class LibraryGroupingNotifier extends Notifier<LibraryGrouping> {
  @override
  LibraryGrouping build() => LibraryGrouping.bySession;
  void set(LibraryGrouping g) => state = g;
}

final libraryGroupingProvider =
    NotifierProvider<LibraryGroupingNotifier, LibraryGrouping>(
        LibraryGroupingNotifier.new);

final librarySessionsProvider = Provider<List<CaptureSession>>((ref) {
  return _demoSessions();
});

List<CapturedFrame> _lightsFor({
  required String prefix,
  required String filter,
  required int count,
  required DateTime base,
  required Duration exposure,
  required double hfrSeed,
  required int focusBase,
  required double sensorTempC,
  int starBase = 400,
}) {
  return [
    for (var i = 1; i <= count; i++)
      CapturedFrame(
        id: '$prefix-$filter-$i',
        filename: '${prefix}_${filter}_'
            '${base.toIso8601String().substring(0, 19).replaceAll('-', '').replaceAll(':', '')}'
            '_$i'
            '_${exposure.inSeconds}s.fits',
        exposure: exposure,
        gain: 100,
        offset: 50,
        filter: filter,
        bin: 1,
        frameType: 'Light',
        // HFR drifts up gently across the session — simulates §50.4 drift.
        hfr: hfrSeed + (i - 1) * 0.025,
        starCount: starBase - (i * 3),
        medianAdu: 1100 + (i % 4) * 30,
        backgroundAdu: 950 + (i % 5) * 10,
        sensorTempC: sensorTempC,
        // Focus position varies slightly with temperature (negative slope
        // matches a typical SCT setup — ~10 steps per °C).
        focusSteps: focusBase - (sensorTempC * 10).round() + (i % 3 - 1) * 5,
        capturedAt: base.add(Duration(minutes: i * 2)),
        rating: filter == 'L' && i <= 2 ? 4 : 0,
      ),
  ];
}

List<CaptureSession> _demoSessions() {
  return [
    CaptureSession(
      id: 'sess-m42',
      date: DateTime(2026, 5, 18),
      targetName: 'M42 — Orion Nebula',
      siteName: 'Backyard Texas',
      guidingRmsRa: 0.82,
      guidingRmsDec: 0.71,
      frames: _lightsFor(
        prefix: 'M42',
        filter: 'L',
        count: 6,
        base: DateTime(2026, 5, 18, 22, 14),
        exposure: const Duration(seconds: 120),
        hfrSeed: 1.42,
        focusBase: 14820,
        sensorTempC: -10,
        starBase: 487,
      ),
    ),
    CaptureSession(
      id: 'sess-ngc6188',
      date: DateTime(2026, 5, 12),
      targetName: 'NGC 6188 — Fighting Dragons',
      siteName: 'Backyard Texas',
      guidingRmsRa: 0.94,
      guidingRmsDec: 0.88,
      frames: [
        for (final f in ['Hα', 'OIII', 'SII'])
          ..._lightsFor(
            prefix: 'NGC6188',
            filter: f,
            count: 3,
            base: DateTime(2026, 5, 12, 3, 0),
            exposure: const Duration(seconds: 300),
            hfrSeed: 1.71,
            focusBase: 14910,
            sensorTempC: -10,
            starBase: 312,
          ),
      ],
    ),
    CaptureSession(
      id: 'sess-ngc7000',
      date: DateTime(2026, 5, 22),
      targetName: 'NGC 7000 — North America Nebula',
      siteName: 'Backyard Texas',
      guidingRmsRa: 0.65,
      guidingRmsDec: 0.74,
      frames: [
        ..._lightsFor(
          prefix: 'NGC7000',
          filter: 'Hα',
          count: 8,
          base: DateTime(2026, 5, 22, 23, 30),
          exposure: const Duration(seconds: 300),
          hfrSeed: 1.55,
          focusBase: 14840,
          sensorTempC: -10,
          starBase: 261,
        ),
        ..._lightsFor(
          prefix: 'NGC7000',
          filter: 'OIII',
          count: 4,
          base: DateTime(2026, 5, 23, 1, 15),
          exposure: const Duration(seconds: 300),
          hfrSeed: 1.63,
          focusBase: 14830,
          sensorTempC: -11,
          starBase: 248,
        ),
      ],
    ),
    CaptureSession(
      id: 'sess-m31',
      date: DateTime(2026, 5, 5),
      targetName: 'M31 — Andromeda Galaxy',
      siteName: 'Backyard Texas',
      guidingRmsRa: 0.71,
      guidingRmsDec: 0.66,
      frames: _lightsFor(
        prefix: 'M31',
        filter: 'L',
        count: 12,
        base: DateTime(2026, 5, 5, 22, 0),
        exposure: const Duration(seconds: 180),
        hfrSeed: 1.61,
        focusBase: 14810,
        sensorTempC: -5,
        starBase: 523,
      ),
    ),
    CaptureSession(
      id: 'sess-m51',
      date: DateTime(2026, 4, 28),
      targetName: 'M51 — Whirlpool Galaxy',
      siteName: 'Backyard Texas',
      guidingRmsRa: 1.04,
      guidingRmsDec: 0.92,
      frames: _lightsFor(
        prefix: 'M51',
        filter: 'L',
        count: 10,
        base: DateTime(2026, 4, 28, 23, 0),
        exposure: const Duration(seconds: 180),
        hfrSeed: 1.83,
        focusBase: 14790,
        sensorTempC: 0,
        starBase: 412,
      ),
    ),
    CaptureSession(
      id: 'sess-veil',
      date: DateTime(2026, 5, 25),
      targetName: 'NGC 6960 — Veil Nebula West',
      siteName: 'Backyard Texas',
      guidingRmsRa: 0.58,
      guidingRmsDec: 0.62,
      frames: _lightsFor(
        prefix: 'NGC6960',
        filter: 'Hα',
        count: 6,
        base: DateTime(2026, 5, 25, 23, 45),
        exposure: const Duration(seconds: 240),
        hfrSeed: 1.49,
        focusBase: 14850,
        sensorTempC: -12,
        starBase: 298,
      ),
    ),
    CaptureSession(
      id: 'sess-rosette',
      date: DateTime(2026, 4, 15),
      targetName: 'Caldwell 49 — Rosette Nebula',
      siteName: 'Backyard Texas',
      guidingRmsRa: 0.88,
      guidingRmsDec: 0.79,
      frames: _lightsFor(
        prefix: 'Rosette',
        filter: 'Hα',
        count: 5,
        base: DateTime(2026, 4, 15, 21, 30),
        exposure: const Duration(seconds: 300),
        hfrSeed: 1.75,
        focusBase: 14770,
        sensorTempC: 5,
        starBase: 234,
      ),
    ),
  ];
}
