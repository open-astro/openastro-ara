import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration_status.dart';
import '../../models/server.dart';
import '../../services/guider_calibration_api.dart';
import '../saved_server_state.dart';

/// Builds a [GuiderCalibrationClient] for a server. Overridable in tests.
final guiderCalibrationApiFactoryProvider =
    Provider<GuiderCalibrationClient Function(AraServer)>(
  (ref) => GuiderCalibrationApi.new,
);

/// [GuiderCalibrationClient] bound to the active server (`savedServers.last`),
/// or `null` when no server is saved. Closes the old Dio on a server change.
final guiderCalibrationApiProvider = Provider<GuiderCalibrationClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(guiderCalibrationApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's guider calibration-files status. `null` data means no
/// server is saved. Exposes build/toggle actions that re-read status after the
/// daemon accepts the request (builds are 202-Accepted, so the post-action
/// status may not yet reflect a still-running build).
class GuiderCalibrationNotifier extends AsyncNotifier<CalibrationStatusResponse?> {
  bool _busy = false;

  @override
  Future<CalibrationStatusResponse?> build() async {
    _busy = false;
    final api = ref.watch(guiderCalibrationApiProvider);
    if (api == null) return null;
    return api.getStatus();
  }

  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) =>
      _run((api) => api.buildDarkLibrary(
            frameCount: frameCount,
            minExposureMs: minExposureMs,
            maxExposureMs: maxExposureMs,
            clearExisting: clearExisting,
            notes: notes,
            loadAfter: loadAfter,
          ));

  Future<void> buildDefectMap({
    int exposureMs = 3000,
    int frameCount = 10,
    String? notes,
    bool loadAfter = true,
  }) =>
      _run((api) => api.buildDefectMap(
            exposureMs: exposureMs,
            frameCount: frameCount,
            notes: notes,
            loadAfter: loadAfter,
          ));

  Future<void> setDarkLibraryEnabled(bool enabled) =>
      _run((api) => api.setDarkLibraryEnabled(enabled));

  Future<void> setDefectMapEnabled(bool enabled) =>
      _run((api) => api.setDefectMapEnabled(enabled));

  /// Runs an action against the active client, then refreshes status. Ignores
  /// overlapping calls and surfaces failures as [AsyncError] so the UI can show
  /// them rather than leaving a stale value.
  Future<void> _run(Future<void> Function(GuiderCalibrationClient api) action) async {
    if (_busy) return;
    final api = ref.read(guiderCalibrationApiProvider);
    if (api == null) return;
    _busy = true;
    state = const AsyncValue<CalibrationStatusResponse?>.loading();
    try {
      await action(api);
    } catch (e, st) {
      if (ref.mounted) state = AsyncValue<CalibrationStatusResponse?>.error(e, st);
      return;
    } finally {
      _busy = false;
    }
    await refresh(api);
  }

  Future<void> refresh([GuiderCalibrationClient? client]) async {
    if (!ref.mounted) return;
    final api = client ?? ref.read(guiderCalibrationApiProvider);
    final next = await AsyncValue.guard<CalibrationStatusResponse?>(() async {
      if (api == null) return null;
      return api.getStatus();
    });
    // getStatus() can outlive the active server; don't write to a disposed notifier.
    if (ref.mounted) state = next;
  }
}

final guiderCalibrationProvider =
    AsyncNotifierProvider<GuiderCalibrationNotifier, CalibrationStatusResponse?>(
        GuiderCalibrationNotifier.new);
