import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/sky_model_calibration.dart';
import '../../services/planning_api.dart';
import '../saved_server_state.dart';

/// Test seam: how a [PlanningApi] is built for the active server.
final planningApiFactoryProvider = Provider<PlanningApi Function(AraServer)>(
  (ref) => PlanningApi.new,
);

/// #1140 P2 — the rig's stored sky-model calibration. Null = never fitted (or
/// no server, or an older daemon). Best-effort read: the planner must keep
/// working offline, so an unreachable rig reads as "not fitted", never an error.
class SkyModelCalibrationNotifier extends AsyncNotifier<SkyModelCalibration?> {
  @override
  Future<SkyModelCalibration?> build() async {
    final server = ref.watch(activeServerProvider);
    if (server == null) return null;
    final api = ref.read(planningApiFactoryProvider)(server);
    try {
      return await api.read();
    } catch (e) {
      debugPrint('[planning] sky-model calibration read failed: $e');
      return null;
    } finally {
      api.close();
    }
  }

  /// Refit from the library on the daemon. Throws on failure.
  Future<SkyModelCalibration?> recalibrate() async {
    final server = ref.read(activeServerProvider);
    if (server == null) throw StateError('No active server');
    final api = ref.read(planningApiFactoryProvider)(server);
    try {
      final record = await api.recalibrate();
      if (ref.mounted) state = AsyncData(record);
      return record;
    } finally {
      api.close();
    }
  }

  /// Clear the stored record on the daemon. Throws on failure.
  Future<void> clear() async {
    final server = ref.read(activeServerProvider);
    if (server == null) throw StateError('No active server');
    final api = ref.read(planningApiFactoryProvider)(server);
    try {
      await api.clear();
    } finally {
      api.close();
    }
    if (ref.mounted) state = const AsyncData(null);
  }
}

final skyModelCalibrationProvider =
    AsyncNotifierProvider<SkyModelCalibrationNotifier, SkyModelCalibration?>(
      SkyModelCalibrationNotifier.new,
    );
