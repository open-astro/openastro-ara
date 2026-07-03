import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration/calibration_models.dart';
import '../../models/server.dart';
import '../../services/calibration_api.dart';
import '../saved_server_state.dart';

/// §39 calibration state — the factory → api → notifier trio mirrors
/// `sequence_list_state.dart` (the current exemplar for API-backed lists).

/// Builds a [CalibrationClient] for a server. Overridable in tests.
final calibrationApiFactoryProvider =
    Provider<CalibrationClient Function(AraServer)>((ref) => CalibrationApi.new);

/// [CalibrationClient] bound to the active server, or null when none is saved.
final calibrationApiProvider = Provider.autoDispose<CalibrationClient?>((ref) {
  final server =
      ref.watch(savedServersProvider.select((async) => async.maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          )));
  if (server == null) return null;
  final api = ref.watch(calibrationApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Imaging sessions that may need calibration frames (newest-first from the
/// server). Null data = no server bound; empty = server with no sessions.
class CalibrationSessionsNotifier
    extends AsyncNotifier<List<CalibrationSession>?> {
  // Last-issued-wins refresh guard (same shape as SequenceListNotifier): only
  // the most recent refresh may write state, and build() re-runs bump the
  // generation so an in-flight refresh against a stale server loses the race.
  int _refreshGen = 0;

  @override
  Future<List<CalibrationSession>?> build() async {
    _refreshGen++;
    final api = ref.watch(calibrationApiProvider);
    if (api == null) return null;
    return api.listSessions();
  }

  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(calibrationApiProvider);
    if (api == null) return;
    final next = await AsyncValue.guard(() => api.listSessions());
    if (gen == _refreshGen) state = next;
  }
}

/// autoDispose (like sequenceListProvider): torn down when the Calibration
/// screen pops, so reopening it always refetches instead of showing a stale
/// cached list — and the api provider's onDispose(close) actually fires.
final calibrationSessionsProvider = AsyncNotifierProvider.autoDispose<
    CalibrationSessionsNotifier,
    List<CalibrationSession>?>(CalibrationSessionsNotifier.new);

/// Dark-library status + entries. Null data = no server bound.
class DarkLibraryStatusNotifier extends AsyncNotifier<DarkLibraryState?> {
  int _refreshGen = 0;

  @override
  Future<DarkLibraryState?> build() async {
    _refreshGen++;
    final api = ref.watch(calibrationApiProvider);
    if (api == null) return null;
    return api.darkLibraryStatus();
  }

  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(calibrationApiProvider);
    if (api == null) return;
    final next = await AsyncValue.guard(() => api.darkLibraryStatus());
    if (gen == _refreshGen) state = next;
  }
}

final darkLibraryStatusProvider =
    AsyncNotifierProvider.autoDispose<DarkLibraryStatusNotifier, DarkLibraryState?>(
        DarkLibraryStatusNotifier.new);
