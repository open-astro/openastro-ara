import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration/calibration_models.dart';
import '../../models/server.dart';
import '../../services/calibration_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// §39 calibration state — the factory → api → notifier trio mirrors
/// `sequence_list_state.dart` (the current exemplar for API-backed lists).

/// Builds a [CalibrationClient] for a server. Overridable in tests.
final calibrationApiFactoryProvider =
    Provider<CalibrationClient Function(AraServer)>((ref) => CalibrationApi.new);

/// [CalibrationClient] bound to the active server, or null when none is saved.
final calibrationApiProvider = Provider.autoDispose<CalibrationClient?>((ref) {
  final server =
      ref.watch(activeServerProvider);
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

  // §60.9 live refresh: coverage badges track a running capture (darks landing
  // flip MatchingDarksAvailable) — debounced 2 s like the library list.
  Timer? _wsDebounce;

  void _bindFrameCompleteRefresh() {
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'frame.complete') return;
      _wsDebounce?.cancel();
      _wsDebounce = Timer(const Duration(seconds: 2), refresh);
    });
    ref.onDispose(() => _wsDebounce?.cancel());
  }

  String? _nextCursor;
  bool _hasMore = false;
  bool _loadingMore = false;

  /// Whether a further page exists for the Load-more affordance.
  bool get hasMore => _hasMore;


  @override
  Future<List<CalibrationSession>?> build() async {
    final gen = ++_refreshGen;
    _bindFrameCompleteRefresh();
    final api = ref.watch(calibrationApiProvider);
    if (api == null) return null;
    final page = await api.listSessions();
    // Guard the cursor-state writes like refresh()/loadMore() (r6): Riverpod
    // discards a superseded build()'s STATE, but these field writes would
    // still land — a slow old-server response must not leave its cursor
    // chain under the new server's list.
    if (gen == _refreshGen) {
      _nextCursor = page.nextCursor;
      // A has_more without a cursor would render a dead button — treat as end.
      _hasMore = page.hasMore && page.nextCursor != null;
    }
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
        // A has_more without a cursor would render a dead button — treat as end.
      _hasMore = page.hasMore && page.nextCursor != null;
      }
      return page.items;
    });
    if (gen == _refreshGen) state = next;
  }

  /// Append the next page (mirrors LiveLibrarySessionsNotifier.loadMore).
  Future<void> loadMore() async {
    final cursor = _nextCursor;
    // The in-flight guard makes repeated taps a no-op (r1): two concurrent
    // calls would share the same cursor, double-append the page, and let the
    // slower response clobber the newer cursor.
    if (!_hasMore || cursor == null || _loadingMore) return;
    _loadingMore = true;
    // Mint a generation (r5): borrowing the ambient one let an append issued
    // DURING an in-flight refresh share its generation — the slower append
    // would then clobber the fresh refresh with a stale list + cursor chain.
    // Minting makes every writer last-issued-wins: the older refresh's write
    // is rejected, and `current` can no longer be replaced under our await.
    final gen = ++_refreshGen;
    final api = ref.read(calibrationApiProvider);
    final current = state.value;
    if (api == null || current == null) {
      _loadingMore = false;
      return;
    }
    try {
      final page = await api.listSessions(cursor: cursor);
      if (gen != _refreshGen) return;
      _nextCursor = page.nextCursor;
      // A has_more without a cursor would render a dead button — treat as end.
      _hasMore = page.hasMore && page.nextCursor != null;
      state = AsyncData([...current, ...page.items]);
    } catch (_) {
      // Catch-all like refresh()'s AsyncValue.guard (r4): even a TypeError
      // from a malformed page must leave the loaded pages + retry button
      // intact rather than escaping as an unhandled rejection.
    } finally {
      _loadingMore = false;
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

  // §39.8 build progress goes live: each captured dark bumps coverage.
  Timer? _wsDebounce;

  void _bindFrameCompleteRefresh() {
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'frame.complete') return;
      _wsDebounce?.cancel();
      _wsDebounce = Timer(const Duration(seconds: 2), refresh);
    });
    ref.onDispose(() => _wsDebounce?.cancel());
  }

  @override
  Future<DarkLibraryState?> build() async {
    _refreshGen++;
    _bindFrameCompleteRefresh();
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
