import '../models/stats/stats_target.dart';
import '../services/tonight_sky_api.dart';

/// §Integration Budget P4/P5 — the library's integration already banked on a
/// Tonight's Sky object, summed over the stats targets whose normalized name
/// matches the object's id or name ("M 31", "M31" and "Andromeda Galaxy" all
/// find their hours). Null = nothing captured. Pure — unit-tested.
double? bankedHoursFor(Iterable<StatsTarget> targets, {required String id, required String name}) {
  final keys = {_norm(id), _norm(name)}..remove('');
  var sum = 0.0;
  var found = false;
  for (final t in targets) {
    if (keys.contains(_norm(t.targetName))) {
      sum += t.integrationHours;
      found = true;
    }
  }
  return found && sum > 0 ? sum : null;
}

/// Banked hours keyed by object id for the objects that have any.
Map<String, double> bankedHoursByObjectId(
  Iterable<StatsTarget>? targets,
  Iterable<TonightSkyObject> objects,
) {
  if (targets == null) return const {};
  final list = targets.toList();
  if (list.isEmpty) return const {};
  return {
    for (final o in objects)
      o.id: ?bankedHoursFor(list, id: o.id, name: o.name),
  };
}

String _norm(String s) => s.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]'), '');
