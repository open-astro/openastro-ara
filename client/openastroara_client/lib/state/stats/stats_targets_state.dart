import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/stats_target.dart';
import '../../services/stats_export_api.dart';
import '../saved_server_state.dart';

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
class StatsTargetsNotifier extends AsyncNotifier<List<StatsTarget>?> {
  // Bumped on every build() and refresh(); a refresh only writes its result if
  // the token is still current, so a second refresh tap or a server switch that
  // rebuilds mid-fetch discards the stale result instead of clobbering state.
  int _generation = 0;

  @override
  Future<List<StatsTarget>?> build() async {
    _generation++;
    final api = ref.watch(statsExportApiProvider);
    if (api == null) return null;
    return api.fetchTargets();
  }

  Future<void> refresh() async {
    final token = ++_generation;
    state = const AsyncValue.loading();
    final next = await AsyncValue.guard<List<StatsTarget>?>(() async {
      final api = ref.read(statsExportApiProvider);
      if (api == null) return null;
      return api.fetchTargets();
    });
    if (token != _generation) return;
    state = next;
  }
}

final statsTargetsProvider =
    AsyncNotifierProvider<StatsTargetsNotifier, List<StatsTarget>?>(
        StatsTargetsNotifier.new);
