import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/calendar_stats.dart';
import '../../services/calendar_api.dart';
import '../saved_server_state.dart';
import 'stats_refresh_mixin.dart';

/// Builds a [CalendarClient] for a server. Overridable in tests.
final calendarApiFactoryProvider =
    Provider<CalendarClient Function(AraServer)>(
  (ref) => CalendarApi.new,
);

/// [CalendarClient] bound to the active server (`savedServers.last`), or `null`
/// when no server is saved. Closes the old Dio on a server change.
final calendarApiProvider = Provider<CalendarClient?>((ref) {
  final server = ref.watch(savedServersProvider.select((async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      )));
  if (server == null) return null;
  final api = ref.watch(calendarApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The active server's §50.6 capture calendar. `null` data means no server is
/// saved. Read-only; [refresh] re-reads on demand.
class CalendarNotifier extends AsyncNotifier<CalendarStats?>
    with StatsRefreshMixin<CalendarStats> {
  @override
  Future<CalendarStats?> build() async {
    markBuild();
    final api = ref.watch(calendarApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  /// Re-reads the calendar, keeping the previous heatmap on screen if the read
  /// fails (the chart shows a stale chip). See [StatsRefreshMixin].
  Future<void> refresh() => refreshUsing(() async {
        final api = ref.read(calendarApiProvider);
        return api?.fetch();
      });
}

final calendarProvider =
    AsyncNotifierProvider<CalendarNotifier, CalendarStats?>(
        CalendarNotifier.new);
