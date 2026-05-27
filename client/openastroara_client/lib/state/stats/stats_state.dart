import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame.dart';
import '../library/library_state.dart';

/// Phase 12g.1 derives stats from the in-memory library demo data so the
/// dashboard shows real-looking numbers. 12g.2 swaps this for a service
/// hitting `/api/v1/stats/*` (per Phase 9 endpoint scaffold).

class StatsOverview {
  final int totalSessions;
  final int totalFrames;
  final Duration totalIntegration;
  final int totalTargets;
  final int totalNights;
  final double averageHfr;

  const StatsOverview({
    required this.totalSessions,
    required this.totalFrames,
    required this.totalIntegration,
    required this.totalTargets,
    required this.totalNights,
    required this.averageHfr,
  });
}

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

final statsOverviewProvider = Provider<StatsOverview>((ref) {
  final sessions = ref.watch(librarySessionsProvider);
  final allFrames = <CapturedFrame>[for (final s in sessions) ...s.frames];
  final lightFrames =
      allFrames.where((f) => f.frameType.toLowerCase() == 'light').toList();
  Duration total = Duration.zero;
  for (final f in lightFrames) {
    total += f.exposure;
  }
  final targets = sessions.map((s) => s.targetName).toSet();
  final nights = sessions
      .map((s) => '${s.date.year}-${s.date.month}-${s.date.day}')
      .toSet();
  final avgHfr = lightFrames.isEmpty
      ? 0.0
      : lightFrames.map((f) => f.hfr).reduce((a, b) => a + b) /
          lightFrames.length;

  return StatsOverview(
    totalSessions: sessions.length,
    totalFrames: allFrames.length,
    totalIntegration: total,
    totalTargets: targets.length,
    totalNights: nights.length,
    averageHfr: avgHfr,
  );
});

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
