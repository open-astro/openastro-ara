import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame.dart';
import '../library/library_state.dart';

/// The Targets section is now backed by the live `/api/v1/stats/targets`
/// endpoint (see `statsTargetsProvider`); the Overview + Achievements sections
/// are likewise live. Best Frames still derives from the in-memory library demo
/// data until its `/api/v1/stats/best-frames` wiring lands.

final bestFramesProvider = Provider<List<CapturedFrame>>((ref) {
  final sessions = ref.watch(librarySessionsProvider);
  final all = [for (final s in sessions) ...s.frames]
      .where((f) => f.frameType.toLowerCase() == 'light')
      .toList()
    ..sort((a, b) => a.hfr.compareTo(b.hfr));
  return all.take(10).toList();
});
