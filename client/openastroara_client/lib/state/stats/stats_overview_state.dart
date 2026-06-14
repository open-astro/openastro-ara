import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/stats_overview.dart';
import '../../services/stats_overview_api.dart';
import '../saved_server_state.dart';

/// Builds a [StatsOverviewClient] for a server. Overridable in tests.
final statsOverviewApiFactoryProvider =
    Provider<StatsOverviewClient Function(AraServer)>(
  (ref) => StatsOverviewApi.new,
);

/// [StatsOverviewClient] bound to the active server (`savedServers.last`), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final statsOverviewApiProvider = Provider<StatsOverviewClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(statsOverviewApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50 catalog overview. `null` data means no server is
/// saved. Read-only; [refresh] re-reads on demand.
class StatsOverviewNotifier extends AsyncNotifier<StatsOverview?> {
  // Bumped on every build() and refresh(). A refresh captures the token before
  // awaiting and only writes state if it's still current — so a second refresh
  // tap, or a server switch that rebuilds mid-fetch, discards the stale result
  // instead of clobbering the live state with it.
  int _generation = 0;

  @override
  Future<StatsOverview?> build() async {
    _generation++;
    final api = ref.watch(statsOverviewApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  Future<void> refresh() async {
    final token = ++_generation;
    state = const AsyncValue.loading();
    final next = await AsyncValue.guard<StatsOverview?>(() async {
      final api = ref.read(statsOverviewApiProvider);
      if (api == null) return null;
      return api.fetch();
    });
    if (token != _generation) return;
    state = next;
  }
}

final statsOverviewProvider =
    AsyncNotifierProvider<StatsOverviewNotifier, StatsOverview?>(
        StatsOverviewNotifier.new);
