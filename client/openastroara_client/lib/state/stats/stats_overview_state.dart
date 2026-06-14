import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/stats_overview.dart';
import '../../services/stats_overview_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

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
class StatsOverviewNotifier extends AsyncNotifier<StatsOverview?>
    with StatsRefreshMixin<StatsOverview> {
  @override
  Future<StatsOverview?> build() async {
    markBuild();
    final api = ref.watch(statsOverviewApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the overview, keeping the previous totals on screen if the read
  /// fails (the section shows a stale banner). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(statsOverviewApiProvider);
        return api?.fetch();
      });
}

final statsOverviewProvider =
    AsyncNotifierProvider<StatsOverviewNotifier, StatsOverview?>(
        StatsOverviewNotifier.new);
