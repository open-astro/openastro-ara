import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame.dart';
import '../library/library_state.dart';

/// Phase 12g.1 derives stats from the in-memory library demo data so the
/// dashboard shows real-looking numbers. 12g.2 swaps this for a service
/// hitting `/api/v1/stats/*` (per Phase 9 endpoint scaffold).

class TargetRollup {
  final String targetName;
  final int frameCount;
  final Duration integration;
  final int sessionCount;
  final double averageHfr;
  const TargetRollup({
    required this.targetName,
    required this.frameCount,
    required this.integration,
    required this.sessionCount,
    required this.averageHfr,
  });
}

final targetRollupsProvider = Provider<List<TargetRollup>>((ref) {
  final sessions = ref.watch(librarySessionsProvider);
  final byTarget = <String, List<CaptureSession>>{};
  for (final s in sessions) {
    byTarget.putIfAbsent(s.targetName, () => []).add(s);
  }
  return byTarget.entries.map((e) {
    final all = [for (final s in e.value) ...s.frames];
    final lights =
        all.where((f) => f.frameType.toLowerCase() == 'light').toList();
    Duration integration = Duration.zero;
    for (final f in lights) {
      integration += f.exposure;
    }
    final hfrAvg = lights.isEmpty
        ? 0.0
        : lights.map((f) => f.hfr).reduce((a, b) => a + b) / lights.length;
    return TargetRollup(
      targetName: e.key,
      frameCount: all.length,
      integration: integration,
      sessionCount: e.value.length,
      averageHfr: hfrAvg,
    );
  }).toList()
    ..sort((a, b) => b.integration.compareTo(a.integration));
});

final bestFramesProvider = Provider<List<CapturedFrame>>((ref) {
  final sessions = ref.watch(librarySessionsProvider);
  final all = [for (final s in sessions) ...s.frames]
      .where((f) => f.frameType.toLowerCase() == 'light')
      .toList()
    ..sort((a, b) => a.hfr.compareTo(b.hfr));
  return all.take(10).toList();
});
