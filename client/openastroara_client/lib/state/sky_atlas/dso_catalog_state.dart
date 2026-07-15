import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/dso_catalog_service.dart';
import '../saved_server_state.dart';

final dsoCatalogServiceProvider =
    Provider<DsoCatalogService>((ref) => DsoCatalogService());

/// Best-effort mirror refresh: whenever a server becomes active, fetch the
/// daemon-hosted DSO catalog into the local cache. The state is a bump
/// counter [dsoCatalogProvider] watches so a landed refresh re-reads the
/// mirror. Materialized at the app root (main.dart) so it runs regardless of
/// which tab is open.
class DsoCatalogSyncNotifier extends Notifier<int> {
  /// In-flight guard: rapid reconnects/server switches re-run build(), and two
  /// overlapping refreshes would race writes to the same mirror file (review
  /// #847). The skipped refresh isn't retried — the NEXT server change (or
  /// cold start) refreshes; worst case is a stale-but-valid mirror.
  bool _refreshing = false;

  @override
  int build() {
    final server = ref.watch(activeServerProvider);
    if (server != null && !_refreshing) {
      _refreshing = true;
      final svc = ref.read(dsoCatalogServiceProvider);
      unawaited(svc.refreshFrom(server).then((fetched) {
        _refreshing = false;
        // ref.mounted guards the post-await write (server switch/dispose).
        if (fetched != null && ref.mounted) state = state + 1;
      }));
    }
    return 0;
  }
}

final dsoCatalogSyncProvider =
    NotifierProvider<DsoCatalogSyncNotifier, int>(DsoCatalogSyncNotifier.new);

/// The locally-mirrored planning catalog (openngc-dso culled to mag ≤ 12), or
/// empty when this machine never connected to a daemon with it installed —
/// the ranker then falls back to the starter list.
final dsoCatalogProvider = FutureProvider<List<PlanningDso>>((ref) async {
  ref.watch(dsoCatalogSyncProvider); // re-read after a refresh lands
  return ref.watch(dsoCatalogServiceProvider).loadCached();
});
