import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/fault_row.dart';
import '../ws/ws_providers.dart';
import 'faults_state.dart';

/// §42.5/§42.6 fault-history feeds over `GET /api/v1/faults` — the persisted
/// counterpart of the live standing-fault overlay in `faults_state.dart`.
/// Mirrors the `calibration_state.dart` list exemplar (refresh-generation
/// guard, cursor chain, WS-debounced live refresh).

/// Newest-first fault history for the feed panel. Null data = no server
/// bound; empty = server with a clean history.
class FaultFeedNotifier extends AsyncNotifier<List<FaultRow>?> {
  int _refreshGen = 0;
  String? _nextCursor;
  bool _hasMore = false;
  bool _loadingMore = false;
  // Set when the last loadMore() attempt threw, so the Load-more affordance can
  // show a retry/error state instead of silently doing nothing. Cleared at the
  // start of the next attempt.
  bool _loadMoreFailed = false;

  /// Whether a further page exists for the Load-more affordance.
  bool get hasMore => _hasMore;

  /// True when the most recent [loadMore] failed; the UI surfaces a retry.
  bool get loadMoreFailed => _loadMoreFailed;

  Timer? _wsDebounce;

  // A fault firing or a reaction outcome landing both change the history —
  // refresh shortly after (debounced: a reaction episode emits several
  // actions back-to-back).
  void _bindFaultEventRefresh() {
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null ||
          (event.type != FaultWsEvents.fault &&
              event.type != FaultWsEvents.actionTaken)) {
        return;
      }
      _wsDebounce?.cancel();
      _wsDebounce = Timer(const Duration(seconds: 2), refresh);
    });
    ref.onDispose(() => _wsDebounce?.cancel());
  }

  @override
  Future<List<FaultRow>?> build() async {
    final gen = ++_refreshGen;
    _bindFaultEventRefresh();
    final api = ref.watch(faultsApiProvider);
    if (api == null) return null;
    final page = await api.list(limit: 50);
    // Guard the cursor-state writes like refresh()/loadMore(): a superseded
    // build()'s STATE is discarded by Riverpod, but these field writes would
    // still land under the new server's list.
    if (gen == _refreshGen) {
      _nextCursor = page.nextCursor;
      // A has_more without a cursor would render a dead button — treat as end.
      _hasMore = page.hasMore && page.nextCursor != null;
    }
    return page.items;
  }

  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(faultsApiProvider);
    if (api == null) return;
    final next = await AsyncValue.guard(() async {
      final page = await api.list(limit: 50);
      if (gen == _refreshGen) {
        _nextCursor = page.nextCursor;
        _hasMore = page.hasMore && page.nextCursor != null;
      }
      return page.items;
    });
    // Guard the stale-write drop with ref.mounted too: an autoDispose teardown
    // during the await makes writing `state` throw StateError.
    if (ref.mounted && gen == _refreshGen) state = next;
  }

  Future<void> loadMore() async {
    final cursor = _nextCursor;
    if (!_hasMore || cursor == null || _loadingMore) return;
    _loadingMore = true;
    // Clear any prior failure: this fresh attempt owns the retry state.
    _loadMoreFailed = false;
    // Mint a generation so every writer is last-issued-wins (the calibration
    // exemplar's r5 lesson: an append during an in-flight refresh must not
    // clobber the fresh list with a stale cursor chain).
    final gen = ++_refreshGen;
    final api = ref.read(faultsApiProvider);
    final current = state.value;
    if (api == null || current == null) {
      _loadingMore = false;
      return;
    }
    try {
      final page = await api.list(limit: 50, cursor: cursor);
      // Also drop the write if disposed mid-await (autoDispose teardown).
      if (!ref.mounted || gen != _refreshGen) return;
      _nextCursor = page.nextCursor;
      _hasMore = page.hasMore && page.nextCursor != null;
      state = AsyncData([...current, ...page.items]);
    } catch (_) {
      // Even a malformed page must leave the loaded pages intact rather than
      // escape as an unhandled rejection (the exemplar's r4 lesson). Record the
      // failure (only for the still-current, still-mounted attempt) so the UI
      // can offer a retry.
      if (ref.mounted && gen == _refreshGen) _loadMoreFailed = true;
    } finally {
      _loadingMore = false;
    }
  }
}

/// autoDispose: torn down when no feed widget is mounted, so reopening always
/// refetches — and the api provider's onDispose(close) actually fires.
final faultFeedProvider =
    AsyncNotifierProvider.autoDispose<FaultFeedNotifier, List<FaultRow>?>(
        FaultFeedNotifier.new);

/// §42.6 — one session's fault timeline (newest-first). A single page is
/// deliberate: a session with 50+ faults has bigger problems than pagination.
final sessionFaultsProvider = FutureProvider.autoDispose
    .family<List<FaultRow>, String>((ref, sessionId) async {
  final api = ref.watch(faultsApiProvider);
  if (api == null) return const <FaultRow>[];
  final page = await api.list(limit: 50, sessionId: sessionId);
  return page.items;
});
