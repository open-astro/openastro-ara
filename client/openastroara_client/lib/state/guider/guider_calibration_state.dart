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
  // Bumped on every build() (active-server change). Actions/refreshes capture it
  // and only write state if it still matches, so a server switch mid-action
  // can't land a stale read (or a spurious error from the old, now-closed Dio).
  int _generation = 0;

  @override
  Future<CalibrationStatusResponse?> build() async {
    _generation++;
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
    // Serialize on state.isLoading (like GuiderStatusNotifier): _run sets loading
    // up-front and only the trailing refresh() clears it, so the flag stays set
    // across the whole action → refresh window — a second tap can't start a
    // racing request mid-refresh.
    if (state.isLoading) return;
    final api = ref.read(guiderCalibrationApiProvider);
    if (api == null) return;
    final gen = _generation;
    state = const AsyncValue<CalibrationStatusResponse?>.loading();
    try {
      await action(api);
    } catch (e, st) {
      if (ref.mounted && gen == _generation) state = AsyncValue<CalibrationStatusResponse?>.error(e, st);
      return;
    }
    // Re-read the *current* client (not the captured one) — a server switch
    // mid-action would have closed `api`'s Dio; the generation guard in
    // refresh() then drops the write if it's for a superseded server.
    await refresh();
  }

  Future<void> refresh() async {
    if (!ref.mounted) return;
    final gen = _generation;
    final api = ref.read(guiderCalibrationApiProvider);
    final next = await AsyncValue.guard<CalibrationStatusResponse?>(() async {
      if (api == null) return null;
      return api.getStatus();
    });
    // Skip the write if disposed or rebuilt for a new server mid-flight.
    if (ref.mounted && gen == _generation) state = next;
  }
}

final guiderCalibrationProvider =
    AsyncNotifierProvider<GuiderCalibrationNotifier, CalibrationStatusResponse?>(
        GuiderCalibrationNotifier.new);
