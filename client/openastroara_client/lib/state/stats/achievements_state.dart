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
  // On a server change this disposes the old client, force-aborting any fetch()
  // still in flight. Riverpod cancels the superseded build() future at the same
  // time, so the resulting DioException is discarded rather than surfaced.
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

  /// Re-read the achievements and swap the result in **only on success**. On
  /// failure the exception propagates to the caller and `state` is left
  /// untouched, so the last-good records stay on screen (the view shows a stale
  /// banner) rather than blanking to an error. This is deliberate: RP3 has no
  /// public copyWithPrevious to carry the previous value *through* an
  /// `AsyncError`, so an error state here would drop `asData?.value` to null and
  /// the records would vanish. The initial no-data error is still owned by
  /// [build]. A server switch mid-flight discards the result via the generation
  /// guard.
  Future<void> refresh() async {
    if (!ref.mounted) return;
    final gen = _generation;
    final api = ref.read(achievementsApiProvider);
    final result = api == null ? null : await api.fetch();
    if (ref.mounted && gen == _generation) state = AsyncData(result);
  }
}

final achievementsProvider =
    AsyncNotifierProvider<AchievementsNotifier, StatsAchievements?>(
        AchievementsNotifier.new);
