import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../models/stats/calendar_stats.dart';
import '../../services/calendar_api.dart';
import '../saved_server_state.dart';

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
class CalendarNotifier extends AsyncNotifier<CalendarStats?> {
  // Bumped on every build() and refresh(); a refresh only writes its result if
  // the token is still current, so a second refresh tap or a server switch that
  // rebuilds mid-fetch discards the stale result instead of clobbering state.
  // build() bumps the token but doesn't gate its own return on it — Riverpod
  // already discards a superseded build's future, and the refresh button is
  // disabled while a build is in flight, so there's no refresh-outlives-build
  // race to guard against from the UI.
  int _generation = 0;

  @override
  Future<CalendarStats?> build() async {
    _generation++;
    final api = ref.watch(calendarApiProvider);
    if (api == null) return null;
    return api.fetch();
  }

  Future<void> refresh() async {
    final token = ++_generation;
    state = const AsyncValue.loading();
    final next = await AsyncValue.guard<CalendarStats?>(() async {
      final api = ref.read(calendarApiProvider);
      if (api == null) return null;
      return api.fetch();
    });
    if (token != _generation) return;
    state = next;
  }
}

final calendarProvider =
    AsyncNotifierProvider<CalendarNotifier, CalendarStats?>(
        CalendarNotifier.new);
