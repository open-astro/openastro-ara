import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/catalog_object.dart';
import 'data_manager_state.dart';

/// Per-catalog request shape for the §36 Aladin marker overlay. A point overlay
/// of every HYG star (~120k) would be unreadable and heavy, so each catalog is
/// fetched with overlay-appropriate trimming:
///   • [maxMag] caps brightness (stars: naked-eye-ish, keeping the field legible);
///   • [limit] caps the count (DSOs often carry no magnitude, so they're capped
///     by count instead). The daemon also hard-caps at 50k regardless.
typedef CatalogOverlayRequest = ({double? maxMag, int? limit});

/// The catalog packages the overlay knows how to fetch + draw, keyed by package
/// id (must match the daemon's `SkyCatalogReader.HasParser` allow-list). Mag 7.0
/// keeps HYG to a few thousand of the brightest stars; OpenNGC is count-capped.
const Map<String, CatalogOverlayRequest> kCatalogOverlayRequests = {
  'hyg-stars': (maxMag: 7.0, limit: null),
  'openngc-dso': (maxMag: null, limit: 5000),
};

/// The combined marker set for every installed catalog package, fetched from
/// `GET /api/v1/data-manager/{id}/catalog`. Empty when no server is bound or no
/// catalog package is installed — [AladinView] listens and draws the markers.
///
/// Rebuilds when the Data Manager package list changes (an install/delete flips
/// `isInstalled`), so installing a catalog adds its overlay and deleting it
/// removes it without any manual refresh.
final skyAtlasCatalogProvider = FutureProvider<List<CatalogObject>>((ref) async {
  final api = ref.watch(dataManagerApiProvider);
  if (api == null) return const <CatalogObject>[];

  // Depend on the package list so an install/delete re-runs this fetch. Use the
  // loaded value only; while it's loading/errored, keep the previous overlay
  // (returning [] here would briefly clear the markers on every refresh).
  final packages = ref.watch(dataManagerPackagesProvider).asData?.value;
  if (packages == null) return const <CatalogObject>[];

  final installed = packages
      .where((p) => p.isInstalled && kCatalogOverlayRequests.containsKey(p.id))
      .toList(growable: false);
  if (installed.isEmpty) return const <CatalogObject>[];

  final fetched = await Future.wait(installed.map((p) {
    final req = kCatalogOverlayRequests[p.id]!;
    return api.getCatalog(p.id, maxMag: req.maxMag, limit: req.limit);
  }));

  // A 404 (package vanished between list + fetch) yields null — drop it; the
  // rest still draw.
  return fetched
      .whereType<List<CatalogObject>>()
      .expand((list) => list)
      .toList(growable: false);
});
