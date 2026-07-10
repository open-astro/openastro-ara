import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/focus_temp.dart';
import '../../services/focus_temp_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [FocusTempClient] for a server. Overridable in tests.
final focusTempApiFactoryProvider =
    Provider<FocusTempClient Function(AraServer)>(
  (ref) => FocusTempApi.new,
);

/// [FocusTempClient] bound to the active server ([activeServerProvider]), or `null`
/// when no server is saved. Closes the old Dio on a server change.
final focusTempApiProvider = Provider<FocusTempClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(focusTempApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50.4 focus-vs-temperature scatter. `null` data means no
/// server is saved. Read-only; [refresh] re-reads on demand.
class FocusTempNotifier extends AsyncNotifier<FocusTempSeries?>
    with StatsRefreshMixin<FocusTempSeries> {
  @override
  Future<FocusTempSeries?> build() async {
    markBuild();
    final api = ref.watch(focusTempApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the scatter, keeping the previous series on screen if the read
  /// fails (the chart shows a stale chip). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(focusTempApiProvider);
        return api?.fetch();
      });
}

final focusTempProvider =
    AsyncNotifierProvider<FocusTempNotifier, FocusTempSeries?>(
        FocusTempNotifier.new);
