import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/tracking_calibration.dart';
import '../../services/tracking_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// Test seam: how a [TrackingApi] is built for the active server.
final trackingApiFactoryProvider = Provider<TrackingApi Function(AraServer)>(
  (ref) => TrackingApi.new,
);

/// #1264 — the rig's stored unguided ceiling. Null = none measured yet (or no
/// server). Re-read when a probe completes, so the advisor and the Unguided
/// panel show tonight's figure without a relaunch.
class TrackingCalibrationNotifier extends AsyncNotifier<TrackingCalibration?> {
  @override
  Future<TrackingCalibration?> build() async {
    ref.listen(wsEventsProvider, (prev, next) {
      if (next.asData?.value.type == 'tracking_probe.completed') {
        ref.invalidateSelf();
      }
    });
    final server = ref.watch(activeServerProvider);
    if (server == null) return null;
    final api = ref.read(trackingApiFactoryProvider)(server);
    try {
      return await api.read();
    } catch (e) {
      // Best-effort read: an unreachable rig shows "not measured", never an
      // error card — the planner must keep working offline.
      debugPrint('[tracking] calibration read failed: $e');
      return null;
    } finally {
      api.close();
    }
  }

  /// Clear the stored ceiling on the daemon. Throws on failure.
  Future<void> recalibrate() async {
    final server = ref.read(activeServerProvider);
    if (server == null) throw StateError('No active server');
    final api = ref.read(trackingApiFactoryProvider)(server);
    try {
      await api.recalibrate();
    } finally {
      api.close();
    }
    // The panel may have closed while the request ran; the clear still worked.
    if (ref.mounted) state = const AsyncData(null);
  }
}

final trackingCalibrationProvider =
    AsyncNotifierProvider<TrackingCalibrationNotifier, TrackingCalibration?>(
      TrackingCalibrationNotifier.new,
    );
