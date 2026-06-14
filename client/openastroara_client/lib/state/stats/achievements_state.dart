import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/achievements.dart';
import '../../services/achievements_api.dart';
import '../saved_server_state.dart';

/// Builds an [AchievementsClient] for a server. Overridable in tests.
final achievementsApiFactoryProvider =
    Provider<AchievementsClient Function(AraServer)>(
  (ref) => AchievementsApi.new,
);

/// [AchievementsClient] bound to the active server (`savedServers.last`), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final achievementsApiProvider = Provider<AchievementsClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(achievementsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50.19 achievements. `null` data means no server is
/// saved (the view shows a connect prompt); an error state surfaces a transport
/// failure with a retry. Read-only — [refresh] re-reads on demand.
class AchievementsNotifier extends AsyncNotifier<StatsAchievements?> {
  // Bumped on every build() (active-server change); a refresh captures it and
  // only writes state if it still matches, so a server switch mid-refresh can't
  // land a stale read from the old, now-closed Dio.
  int _generation = 0;

  @override
  Future<StatsAchievements?> build() async {
    _generation++;
    final api = ref.watch(achievementsApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-read the achievements, then swap the result in. Deliberately does NOT
  /// drop into a bare loading state first: that would blank `asData?.value` and
  /// flip the view back to its loading placeholder mid-refresh. Holding the old
  /// data until the new value (or error) lands keeps the records on screen —
  /// the view drives its own refresh spinner. (RP3 has no public
  /// copyWithPrevious/valueOrNull to preserve data *through* a loading state, so
  /// this matches the backup notifier's convention.) A server switch mid-flight
  /// discards the stale result via the generation guard.
  Future<void> refresh() async {
    if (!ref.mounted) return;
    final gen = _generation;
    final next = await AsyncValue.guard<StatsAchievements?>(() async {
      final api = ref.read(achievementsApiProvider);
      if (api == null) return null;
      return api.fetch();
    });
    if (ref.mounted && gen == _generation) state = next;
  }
}

final achievementsProvider =
    AsyncNotifierProvider<AchievementsNotifier, StatsAchievements?>(
        AchievementsNotifier.new);
