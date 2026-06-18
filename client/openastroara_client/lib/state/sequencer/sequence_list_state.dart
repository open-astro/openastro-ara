import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_summary.dart';
import '../../models/server.dart';
import '../../services/sequence_api.dart';
import '../saved_server_state.dart';

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
    final api = ref.watch(sequenceApiProvider);
    if (api == null) return null;
    return api.list();
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
      return api.list();
    });
    // Drop a stale write: a newer refresh was issued while this one was in flight.
    if (gen == _refreshGen) state = next;
  }
}

final sequenceListProvider = AsyncNotifierProvider.autoDispose<
    SequenceListNotifier, List<SequenceListItem>?>(SequenceListNotifier.new);
