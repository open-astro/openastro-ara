import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../services/sequence_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// §28.12 `sequence.*` WS event-type tokens the run-state notifier consumes to
/// track a run live (mirrors `WsEventCatalog` on the server). Routing is by the
/// shared `sequence.` prefix; the specific tokens are listed for reference.
abstract final class SequenceWsEvents {
  static const prefix = 'sequence.';
  static const started = 'sequence.started';
  static const progress = 'sequence.progress';
  static const paused = 'sequence.paused';
  static const resumed = 'sequence.resumed';
  static const aborted = 'sequence.aborted';
  static const stopped = 'sequence.stopped';
  static const complete = 'sequence.complete';
  static const failed = 'sequence.failed';
}

/// Builds a [SequenceClient] for a server. Overridable in tests.
final sequenceApiFactoryProvider =
    Provider<SequenceClient Function(AraServer)>((ref) => SequenceApi.new);

/// [SequenceClient] bound to the active server (`savedServers.last`), or `null`
/// when no server is saved. autoDispose so the Dio client is released (and
/// re-created fresh) when the sequencer tab has no listeners. Closes the old
/// Dio on a server change.
final sequenceApiProvider = Provider.autoDispose<SequenceClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(sequenceApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's saved sequences (newest-first). `null` data means no
/// server is bound; an empty list means a connected server with no sequences yet.
class SequenceListNotifier extends AsyncNotifier<List<SequenceListItem>?> {
  // Bumped per refresh() call; only the most-recently-issued refresh writes
  // state, so two rapid refreshes can't let an older (slower) response overwrite
  // a newer one (last-issued-wins, not last-completed-wins).
  int _refreshGen = 0;

  @override
  Future<List<SequenceListItem>?> build() async {
    // Invalidate any in-flight refresh: Riverpod re-runs build() (e.g. on a
    // server change) on the SAME notifier instance, then sets fresh AsyncData.
    // Bumping the generation here means a refresh() awaiting the old (now-closed)
    // server loses the write race and can't clobber that fresh state.
    _refreshGen++;
    final api = ref.watch(sequenceApiProvider);
    if (api == null) return null;
    return _loadFirstPage(api);
  }

  /// Re-read the list (after a create/delete, or a manual refresh). Surfaces
  /// transport errors as an AsyncError state rather than leaving stale data.
  /// Deliberately does NOT flip to AsyncLoading first — the current list stays
  /// visible during the round-trip (no flicker); a superseded concurrent refresh
  /// is dropped via [_refreshGen].
  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(sequenceApiProvider);
    final next = await AsyncValue.guard<List<SequenceListItem>?>(() async {
      if (api == null) return null;
      return _loadFirstPage(api);
    });
    // Drop a stale write: a newer refresh was issued while this one was in flight.
    if (gen == _refreshGen) state = next;
  }

  /// Load the first page and return its items. Logs when the daemon reports more
  /// pages than fit in the default limit — this slice shows only the first page;
  /// the pagination/"load more" UI (using [SequencePage.nextCursor]) is a later
  /// slice, and this warning makes the truncation visible until then.
  Future<List<SequenceListItem>> _loadFirstPage(SequenceClient api) async {
    final page = await api.list();
    if (page.hasMore) {
      debugPrint('[sequencer] sequence list truncated to the first page '
          '(${page.items.length}); pagination is not wired yet.');
    }
    return page.items;
  }
}

final sequenceListProvider = AsyncNotifierProvider.autoDispose<
    SequenceListNotifier, List<SequenceListItem>?>(SequenceListNotifier.new);

/// Id of the sequence the user picked in the Load dialog (null = none loaded).
/// The tab's tree/run controls key off this. Riverpod 3.x removed
/// `StateProvider`, so this is a thin Notifier (matching [selectedNodeIdProvider]).
///
/// Clears itself when the active server changes or disconnects: an id from one
/// server must never stay live and drive `getSequence`/lifecycle calls against a
/// different one. We listen to the active-server selector (the keep-alive
/// `savedServersProvider`, NOT the autoDispose `sequenceApiProvider`) so this
/// keep-alive notifier doesn't pin an autoDispose provider open.
class SelectedSequenceIdNotifier extends Notifier<String?> {
  @override
  String? build() {
    ref.listen(
      savedServersProvider.select((async) => async.maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          )),
      (prev, next) {
        if (prev != next) state = null;
      },
    );
    return null;
  }

  void select(String? id) => state = id;
}

final selectedSequenceIdProvider =
    NotifierProvider<SelectedSequenceIdNotifier, String?>(
        SelectedSequenceIdNotifier.new);

/// Live run state of the currently-selected sequence (null = nothing selected,
/// no server, or no active run for it). Rebuilds when the selection or server
/// changes; [refresh] re-reads after a lifecycle action (start/pause/abort).
class SequenceRunStateNotifier extends AsyncNotifier<SequenceRunStateInfo?> {
  Future<SequenceRunStateInfo?> _read() {
    final id = ref.read(selectedSequenceIdProvider);
    final api = ref.read(sequenceApiProvider);
    if (id == null || api == null) return Future.value(null);
    return api.getRunState(id);
  }

  @override
  Future<SequenceRunStateInfo?> build() {
    // watch (not read) so a selection/server change rebuilds the run state.
    final selectedId = ref.watch(selectedSequenceIdProvider);
    ref.watch(sequenceApiProvider);

    // Live updates: fold `sequence.*` WS frames for the selected sequence onto
    // the current run state so the toolbar/status line track a run in real time
    // (previously the state only refreshed on selection or a lifecycle action).
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event != null) _applyWsEvent(event, selectedId);
    });

    return _read();
  }

  /// Fold one WS frame into [state] when it's a `sequence.*` event for the
  /// selected sequence. Synchronous (called from a ref.listen callback while the
  /// notifier is alive), so writing `state` here is safe without a mounted guard.
  void _applyWsEvent(WsEvent event, String? selectedId) {
    if (selectedId == null) return;
    if (!event.type.startsWith(SequenceWsEvents.prefix)) return;
    // Skip only while there's no value at all — the initial REST read still in
    // flight. Once there's ever been a value (incl. a null "no active run", or a
    // refresh error that kept its previous value), keep applying so live
    // tracking survives a transient REST failure.
    if (!state.hasValue) return;
    // Ignore a frame that names a different sequence; a missing or non-String
    // id is treated as absent and applied (best-effort).
    final payloadId = event.payload['sequence_id'];
    if (payloadId is String && payloadId != selectedId) return;
    // `state.value` (not `asData?.value`): after a refresh error the state is an
    // AsyncError that still carries the previous value (copyWithPrevious below).
    final current = state.value ?? const SequenceRunStateInfo();
    state = AsyncData(current.applyWsProgress(event.payload));
  }

  /// Re-read the run state (after a lifecycle transition). On failure the last
  /// good value is retained (the error is logged, not promoted to a value-less
  /// AsyncError) so `state.hasValue` stays true and the live WS stream keeps
  /// updating the status line instead of freezing on a transient REST hiccup.
  /// A real transport failure of the action itself is surfaced by the toolbar's
  /// own SnackBar, not here.
  Future<void> refresh() async {
    final next = await AsyncValue.guard(_read);
    // Guard against disposal during the await (autoDispose + tab switch): writing
    // `state` after the notifier is gone throws StateError.
    if (!ref.mounted) return;
    if (next.hasError) {
      debugPrint('[sequencer] run-state refresh failed: ${next.error}');
    } else {
      state = next;
    }
  }
}

final sequenceRunStateProvider = AsyncNotifierProvider.autoDispose<
    SequenceRunStateNotifier, SequenceRunStateInfo?>(SequenceRunStateNotifier.new);

/// True while a sequence lifecycle command (start/pause/resume/abort) is
/// in-flight, so the toolbar can disable the controls and a rapid double-tap
/// can't fire two concurrent commands. Intentionally keep-alive (NOT autoDispose
/// like [sequenceRunStateProvider]): the busy flag must survive the brief windows
/// where the run-state notifier rebuilds; the autoDispose run-state provider
/// instead guards its post-await writes with `ref.mounted` in [refresh].
class SequenceCommandBusyNotifier extends Notifier<bool> {
  @override
  bool build() => false;
  void setBusy(bool value) => state = value;
}

final sequenceCommandBusyProvider =
    NotifierProvider<SequenceCommandBusyNotifier, bool>(
        SequenceCommandBusyNotifier.new);
