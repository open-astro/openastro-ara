import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/server_internal_state.dart';
import '../../services/server_internal_state_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [ServerInternalStateClient] for a server. Overridable in tests.
final serverInternalStateApiFactoryProvider =
    Provider<ServerInternalStateClient Function(AraServer)>(
  (ref) => ServerInternalStateApi.new,
);

final serverInternalStateApiProvider = Provider<ServerInternalStateClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(serverInternalStateApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §66.7 internal state. `null` data means no server is
/// saved. Read-only; [refresh] re-reads on demand (the panel's refresh button —
/// a second read is also what gives the server a CPU figure to report).
class ServerInternalStateNotifier extends AsyncNotifier<ServerInternalState?>
    with StatsRefreshMixin<ServerInternalState> {
  @override
  Future<ServerInternalState?> build() async {
    markBuild();
    final api = ref.watch(serverInternalStateApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(serverInternalStateApiProvider);
        return api?.fetch();
      });
}

final serverInternalStateProvider =
    AsyncNotifierProvider<ServerInternalStateNotifier, ServerInternalState?>(
        ServerInternalStateNotifier.new);
