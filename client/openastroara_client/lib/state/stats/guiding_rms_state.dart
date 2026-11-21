import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/guiding_rms.dart';
import '../../services/guiding_rms_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [GuidingRmsClient] for a server. Overridable in tests.
final guidingRmsApiFactoryProvider =
    Provider<GuidingRmsClient Function(AraServer)>(
  (ref) => GuidingRmsApi.new,
);

/// [GuidingRmsClient] bound to the active server ([activeServerProvider]), or
/// `null` when no server is saved. Closes the old Dio on a server change.
final guidingRmsApiProvider = Provider<GuidingRmsClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(guidingRmsApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50.7 guiding-RMS trend. `null` data means no server is
/// saved. Read-only; [refresh] re-reads on demand.
class GuidingRmsNotifier extends AsyncNotifier<GuidingRmsSeries?>
    with StatsRefreshMixin<GuidingRmsSeries> {
  @override
  Future<GuidingRmsSeries?> build() async {
    markBuild();
    final api = ref.watch(guidingRmsApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the guiding trend, keeping the previous series on screen if the
  /// read fails (the chart shows a stale chip). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(guidingRmsApiProvider);
        return api?.fetch();
      });
}

final guidingRmsProvider =
    AsyncNotifierProvider<GuidingRmsNotifier, GuidingRmsSeries?>(
        GuidingRmsNotifier.new);
