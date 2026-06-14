import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/stats_target.dart';
import '../../services/stats_export_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [StatsExportClient] for a server. Overridable in tests.
final statsExportApiFactoryProvider =
    Provider<StatsExportClient Function(AraServer)>(
  (ref) => StatsExportApi.new,
);

/// [StatsExportClient] bound to the active server (`savedServers.last`), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final statsExportApiProvider = Provider<StatsExportClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(statsExportApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's imaged targets (busiest first). `null` data means no
/// server is saved. Read-only; [refresh] re-reads on demand.
class StatsTargetsNotifier extends AsyncNotifier<List<StatsTarget>?>
    with StatsRefreshMixin<List<StatsTarget>> {
  @override
  Future<List<StatsTarget>?> build() async {
    markBuild();
    final api = ref.watch(statsExportApiProvider);
    if (api == null) return null;
    return api.fetchTargets();
  }

  /// Re-reads the targets, keeping the previous list on screen if the read
  /// fails (the section shows a stale banner). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(statsExportApiProvider);
        return api?.fetchTargets();
      });
}

final statsTargetsProvider =
    AsyncNotifierProvider<StatsTargetsNotifier, List<StatsTarget>?>(
        StatsTargetsNotifier.new);
