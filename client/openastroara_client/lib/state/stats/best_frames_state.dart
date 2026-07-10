import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/best_frame.dart';
import '../../services/best_frames_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [BestFramesClient] for a server. Overridable in tests.
final bestFramesApiFactoryProvider =
    Provider<BestFramesClient Function(AraServer)>(
  (ref) => BestFramesApi.new,
);

/// [BestFramesClient] bound to the active server ([activeServerProvider]), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final bestFramesApiProvider = Provider<BestFramesClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(bestFramesApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's top-ranked frames (best composite-quality first). `null`
/// data means no server is saved. Read-only; [refresh] re-reads on demand.
class BestFramesNotifier extends AsyncNotifier<List<BestFrame>?>
    with StatsRefreshMixin<List<BestFrame>> {
  @override
  Future<List<BestFrame>?> build() async {
    markBuild();
    final api = ref.watch(bestFramesApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the best frames, keeping the previous list on screen if the read
  /// fails (the section shows a stale banner). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(bestFramesApiProvider);
        return api?.fetch();
      });
}

final bestFramesProvider =
    AsyncNotifierProvider<BestFramesNotifier, List<BestFrame>?>(
        BestFramesNotifier.new);
