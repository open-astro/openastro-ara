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

  String? _nextCursor;
  bool _hasMore = false;

  /// Whether a further page exists for the Load-more affordance.
  bool get hasMore => _hasMore;

  @override
  Future<List<CalibrationSession>?> build() async {
    _refreshGen++;
    final api = ref.watch(calibrationApiProvider);
    if (api == null) return null;
    final page = await api.listSessions();
    _nextCursor = page.nextCursor;
    _hasMore = page.hasMore;
    return page.items;
  }

  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(calibrationApiProvider);
    if (api == null) return;
    final next = await AsyncValue.guard(() async {
      final page = await api.listSessions();
      if (gen == _refreshGen) {
        _nextCursor = page.nextCursor;
        _hasMore = page.hasMore;
      }
      return page.items;
    });
    if (gen == _refreshGen) state = next;
  }

  /// Append the next page (mirrors LiveLibrarySessionsNotifier.loadMore).
  Future<void> loadMore() async {
    final cursor = _nextCursor;
    if (!_hasMore || cursor == null) return;
    final gen = _refreshGen;
    final api = ref.read(calibrationApiProvider);
    final current = state.value;
    if (api == null || current == null) return;
    try {
      final page = await api.listSessions(cursor: cursor);
      if (gen != _refreshGen) return;
      _nextCursor = page.nextCursor;
      _hasMore = page.hasMore;
      state = AsyncData([...current, ...page.items]);
    } on Exception {
      // Loaded pages stay; the button remains for a retry.
    }
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
