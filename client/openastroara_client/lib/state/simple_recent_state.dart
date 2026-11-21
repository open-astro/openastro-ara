import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/custom_targets_service.dart';
import '../services/dso_catalog_service.dart';
import '../models/stats/stats_target.dart';
import '../util/tonight_sky_local.dart' show restOfNightSpan;
import 'settings/site_settings_state.dart';
import 'sky_atlas/custom_targets_state.dart';
import 'sky_atlas/tonight_sky_state.dart' show clockProvider, skyClockProvider;
import 'sky_atlas/dso_catalog_state.dart' show bundledCatalogProvider;
import 'stats/stats_targets_state.dart';

/// How many targets Plan's Recent list shows.
const int kRecentTargets = 5;

/// A target imaged or searched lately, with the position Plan needs.
typedef RecentTarget = ({
  String name,
  double raDeg,
  double decDeg,
  DateTime when,
});

/// The last [kRecentTargets] targets imaged ([stats]) or searched ([saved]),
/// newest first, one per designation. A stats row has no position, so
/// [resolve] gives it one from the catalog; a row it can't place is left out.
/// Pure — unit-tested.
List<RecentTarget> mergeRecentTargets(
  List<StatsTarget> stats,
  List<CustomTarget> saved,
  PlanningDso? Function(String name) resolve,
) {
  final byKey = <String, RecentTarget>{};
  void add(String name, double ra, double dec, DateTime when) {
    final key = designationKey(name);
    final old = byKey[key];
    if (old == null || when.isAfter(old.when)) {
      byKey[key] = (name: name, raDeg: ra, decDeg: dec, when: when);
    }
  }

  for (final t in saved) {
    add(t.name, t.raDeg, t.decDeg, t.savedUtc);
  }
  // Newest first, and stop once [kRecentTargets] entries are strictly newer
  // than the row: it (and every older one) can no longer reach the top five,
  // so old misses cost no catalog work.
  final imaged = [
    for (final t in stats)
      if (t.lastImagedUtc != null) t,
  ]..sort((a, b) => b.lastImagedUtc!.compareTo(a.lastImagedUtc!));
  for (final t in imaged) {
    final when = t.lastImagedUtc!;
    if (byKey.values.where((e) => e.when.isAfter(when)).length >=
        kRecentTargets) {
      break;
    }
    final d = resolve(t.targetName);
    if (d != null) add(t.targetName, d.raDeg, d.decDeg, when);
  }
  return ([
    ...byKey.values,
  ]..sort((a, b) => b.when.compareTo(a.when))).take(kRecentTargets).toList();
}

/// The bundle's name index, built once per catalog load: a
/// [findCatalogObject] scan per imaged target froze the Plan window (a miss
/// walks all 17k rows several times, ~190 ms each in debug).
final bundledNameIndexProvider = Provider<CatalogNameIndex>(
  (ref) =>
      CatalogNameIndex(ref.watch(bundledCatalogProvider).value ?? const []),
);

final simpleRecentProvider = Provider.autoDispose<List<RecentTarget>>((ref) {
  return mergeRecentTargets(
    ref.watch(statsTargetsProvider).value ?? const [],
    ref.watch(customTargetsProvider).value ?? const [],
    ref.watch(bundledNameIndexProvider).lookupOrContains,
  );
});

/// What is left of tonight (dark to dawn), re-worked on the sky clock's tick.
final simpleRestOfNightProvider =
    Provider.autoDispose<({DateTime from, DateTime to})?>((ref) {
      ref.watch(skyClockProvider);
      return restOfNightSpan(
        ref.watch(siteSettingsProvider).value,
        ref.watch(clockProvider)().toUtc(),
      );
    });
