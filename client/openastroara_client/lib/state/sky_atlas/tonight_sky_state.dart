import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../saved_server_state.dart';

/// §36/§25.5 — the active server's Tonight's Sky ranking (curated objects above
/// the site horizon now, highest first). Empty when no server is connected.
/// Auto-disposed so the list refetches (re-ranks for the current time) each time
/// the Tonight's Sky view is opened; `ref.invalidate(tonightSkyProvider)` forces
/// a manual refresh.
final tonightSkyProvider =
    FutureProvider.autoDispose<List<TonightSkyObject>>((ref) async {
  // Await the saved-server list rather than collapsing a still-loading state to
  // []: that would resolve this provider to data([]) before the servers finished
  // loading and flash the "connect a server" empty state for a user who does have
  // one saved. Awaiting keeps Tonight's Sky in `loading` until the list is known.
  final servers = await ref.watch(savedServersProvider.future);
  if (servers.isEmpty) return const <TonightSkyObject>[];
  // Most-recently-saved server is the de-facto active one (same convention as
  // the other server-bound providers; a dedicated active-server provider lands
  // with §55.1 multi-server switching).
  final api = TonightSkyApi(servers.last);
  // Force-close on dispose so navigating away from the panel cancels an in-flight
  // fetch instead of leaving the socket open until the receive timeout.
  ref.onDispose(api.close);
  return api.fetch();
});
