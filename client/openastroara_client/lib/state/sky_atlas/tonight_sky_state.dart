import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/tonight_sky_api.dart';
import '../saved_server_state.dart';

/// §36/§25.5 — the active server's Tonight's Sky ranking (curated objects above
/// the site horizon now, highest first). Empty when no server is connected.
/// Auto-disposed so the list refetches (re-ranks for the current time) each time
/// the Tonight's Sky view is opened; `ref.invalidate(tonightSkyProvider)` forces
/// a manual refresh.
final tonightSkyProvider =
    FutureProvider.autoDispose<List<TonightSkyObject>>((ref) async {
  final servers = ref.watch(savedServersProvider).maybeWhen(
        data: (list) => list,
        orElse: () => const <AraServer>[],
      );
  if (servers.isEmpty) return const <TonightSkyObject>[];
  // Most-recently-saved server is the de-facto active one (same convention as
  // the other server-bound providers; a dedicated active-server provider lands
  // with §55.1 multi-server switching).
  return TonightSkyApi(servers.last).fetch();
});
