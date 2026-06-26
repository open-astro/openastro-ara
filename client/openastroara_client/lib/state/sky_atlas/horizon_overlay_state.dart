import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/horizon_api.dart';
import '../saved_server_state.dart';

/// §36 — the active server's local horizon projected onto the equatorial sky for
/// the Aladin overlay (the profile's flat horizon altitude swept around the
/// compass, plus the zenith and N/E/S/W cardinals). Null when no server is
/// connected or the server returns no horizon. [AladinView] listens and draws it.
///
/// Auto-disposed so the horizon refetches (recomputes for the current sidereal
/// time) each time the Planning view is opened; `ref.invalidate(horizonProvider)`
/// forces a manual refresh. A user-selectable planning time lands in a later slice.
final horizonProvider = FutureProvider.autoDispose<Horizon?>((ref) async {
  // Await the saved-server list rather than collapsing a still-loading state to
  // null: that would resolve to "no horizon" before the servers finished loading
  // and skip drawing for a user who does have one saved.
  final servers = await ref.watch(savedServersProvider.future);
  if (servers.isEmpty) return null;
  // Most-recently-saved server is the de-facto active one (same convention as the
  // other server-bound providers; a dedicated active-server provider lands with
  // §55.1 multi-server switching).
  final api = HorizonApi(servers.last);
  // Force-close on dispose so navigating away cancels an in-flight fetch instead
  // of leaving the socket open until the receive timeout.
  ref.onDispose(api.close);
  return api.fetch();
});
