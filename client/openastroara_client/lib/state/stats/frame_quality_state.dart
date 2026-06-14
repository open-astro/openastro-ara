import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/frame_quality.dart';
import '../../services/frame_quality_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [FrameQualityClient] for a server. Overridable in tests.
final frameQualityApiFactoryProvider =
    Provider<FrameQualityClient Function(AraServer)>(
  (ref) => FrameQualityApi.new,
);

/// [FrameQualityClient] bound to the active server (`savedServers.last`), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final frameQualityApiProvider = Provider<FrameQualityClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(frameQualityApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50.10 composite-quality histogram. `null` data means no
/// server is saved. Read-only; [refresh] re-reads on demand.
class FrameQualityNotifier extends AsyncNotifier<FrameQualityDistribution?>
    with StatsRefreshMixin<FrameQualityDistribution> {
  @override
  Future<FrameQualityDistribution?> build() async {
    markBuild();
    final api = ref.watch(frameQualityApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the distribution, keeping the previous histogram on screen if the
  /// read fails (the chart shows a stale banner). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(frameQualityApiProvider);
        return api?.fetch();
      });
}

final frameQualityProvider =
    AsyncNotifierProvider<FrameQualityNotifier, FrameQualityDistribution?>(
        FrameQualityNotifier.new);
