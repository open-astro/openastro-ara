import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame.dart';

/// Library state per playbook §40. Phase 12f.1 seeds in-memory demo
/// sessions (M42 + NGC 6188) so the UI renders something. Phase 12f.2
/// replaces this with a real `LibraryService` that hits
/// `/api/v1/sessions` + `/api/v1/frames`.

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

List<CaptureSession> _demoSessions() {
  final m42Frames = [
    for (var i = 1; i <= 6; i++)
      CapturedFrame(
        id: 'm42-l-$i',
        filename: 'M42_L_2026-05-18T22:14:${(32 + i).toString().padLeft(2, '0')}_120s.fits',
        exposure: const Duration(seconds: 120),
        gain: 100,
        offset: 50,
        filter: 'L',
        bin: 1,
        frameType: 'Light',
        hfr: 1.42 + (i % 3) * 0.05,
        starCount: 487 - i,
        medianAdu: 1284,
        backgroundAdu: 1102,
        sensorTempC: -10,
        focusSteps: 14820,
        capturedAt: DateTime(2026, 5, 18, 22, 14, 32 + i),
        rating: i <= 2 ? 4 : 0,
      ),
  ];

  final ngcFrames = [
    for (final f in ['Hα', 'OIII', 'SII'])
      for (var i = 1; i <= 3; i++)
        CapturedFrame(
          id: 'ngc-$f-$i',
          filename: 'NGC6188_${f}_2026-05-12T03:0$i:00_300s.fits',
          exposure: const Duration(seconds: 300),
          gain: 100,
          offset: 50,
          filter: f,
          bin: 1,
          frameType: 'Light',
          hfr: 1.71 + i * 0.05,
          starCount: 312,
          medianAdu: 980,
          backgroundAdu: 870,
          sensorTempC: -10,
          focusSteps: 14910,
          capturedAt: DateTime(2026, 5, 12, 3, i, 0),
        ),
  ];

  return [
    CaptureSession(
      id: 'sess-m42',
      date: DateTime(2026, 5, 18),
      targetName: 'M42 — Orion Nebula',
      siteName: 'Backyard Texas',
      frames: m42Frames,
    ),
    CaptureSession(
      id: 'sess-ngc6188',
      date: DateTime(2026, 5, 12),
      targetName: 'NGC 6188 — Fighting Dragons',
      siteName: 'Backyard Texas',
      frames: ngcFrames,
    ),
  ];
}
