import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../models/server.dart';
import '../../services/library_api.dart';
import '../saved_server_state.dart';

/// §40 live library state (12f.2) — the factory → api → notifier trio
/// mirroring `sequence_list_state.dart`. The demo data in `library_state.dart`
/// now only feeds the Stats dashboard (until §50 live-wiring).

/// Builds a [LibraryClient] for a server. Overridable in tests.
final libraryApiFactoryProvider =
    Provider<LibraryClient Function(AraServer)>((ref) => LibraryApi.new);

/// [LibraryClient] bound to the active server, or null when none is saved.
final libraryApiProvider = Provider.autoDispose<LibraryClient?>((ref) {
  final server =
      ref.watch(savedServersProvider.select((async) => async.maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          )));
  if (server == null) return null;
  final api = ref.watch(libraryApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The catalog's sessions, newest-first. Null data = no server bound; empty =
/// a server whose catalog has no sessions yet. autoDispose so reopening the
/// Image Library always refetches.
class LiveLibrarySessionsNotifier
    extends AsyncNotifier<List<LibrarySession>?> {
  // Last-issued-wins refresh guard (same shape as SequenceListNotifier).
  int _refreshGen = 0;

  @override
  Future<List<LibrarySession>?> build() async {
    _refreshGen++;
    final api = ref.watch(libraryApiProvider);
    if (api == null) return null;
    return api.listSessions();
  }

  Future<void> refresh() async {
    final gen = ++_refreshGen;
    final api = ref.read(libraryApiProvider);
    if (api == null) return;
    final next = await AsyncValue.guard(() => api.listSessions());
    if (gen == _refreshGen) state = next;
  }
}

final liveLibrarySessionsProvider = AsyncNotifierProvider.autoDispose<
    LiveLibrarySessionsNotifier,
    List<LibrarySession>?>(LiveLibrarySessionsNotifier.new);

/// Per-session frame strip, loaded lazily as each card builds. Family-keyed by
/// session id; autoDispose so strips are released with the screen.
final sessionFramesProvider = FutureProvider.autoDispose
    .family<List<LibraryFrameItem>, String>((ref, sessionId) async {
  final api = ref.watch(libraryApiProvider);
  if (api == null) return const [];
  return api.sessionFrames(sessionId);
});
