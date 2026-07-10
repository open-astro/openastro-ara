import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/achievements.dart';
import '../../services/achievements_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds an [AchievementsClient] for a server. Overridable in tests.
final achievementsApiFactoryProvider =
    Provider<AchievementsClient Function(AraServer)>(
  (ref) => AchievementsApi.new,
);

/// [AchievementsClient] bound to the active server ([activeServerProvider]), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final achievementsApiProvider = Provider<AchievementsClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(achievementsApiFactoryProvider)(server);
  // On a server change this disposes the old client, force-aborting any fetch()
  // still in flight. Riverpod cancels the superseded build() future at the same
  // time, so the resulting DioException is discarded rather than surfaced.
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50.19 achievements. `null` data means no server is
/// saved (the view shows a connect prompt); an error state surfaces a transport
/// failure with a retry. Read-only — [refresh] re-reads on demand.
class AchievementsNotifier extends AsyncNotifier<StatsAchievements?>
    with StatsRefreshMixin<StatsAchievements> {
  @override
  Future<StatsAchievements?> build() async {
    markBuild();
    final api = ref.watch(achievementsApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the achievements, keeping the previous records on screen if the
  /// read fails (the view shows a stale banner) rather than blanking to an
  /// error. A server switch mid-refresh discards the result — success or
  /// failure — via the generation guard. See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(achievementsApiProvider);
        return api?.fetch();
      });
}

final achievementsProvider =
    AsyncNotifierProvider<AchievementsNotifier, StatsAchievements?>(
        AchievementsNotifier.new);
