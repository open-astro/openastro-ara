import '../services/tonight_sky_api.dart';

/// §36.8 "What-if run" — the session planner behind the Tonight's Sky panel.
///
/// The ranked list answers "what's good tonight?"; this answers the question
/// an imager actually plans around: *"I can shoot from 10pm to 1am — what
/// should I point at to come home with a finished image?"* It allocates the
/// user's window to the best target — or splits it across two — maximizing a
/// per-target value that mirrors the ranker's own scoring, and carries the
/// Glover optimal-sub figures through so each slice arrives as "N subs of S
/// seconds", ready to shoot.
///
/// Pure over an already-ranked [TonightSkyObject] list (the objects carry
/// their dark windows + hours-free scores from the local ranker), so it needs
/// no astronomy of its own and unit-tests without a clock seam.

/// One allocated slice of the user's window.
class SessionPlanTarget {
  final TonightSkyObject object;
  final DateTime startUtc;
  final DateTime endUtc;
  final double hours;

  /// Glover-optimal sub length (from the ranker's advice stack) and how many
  /// fit in [hours]; null when no filter set is configured.
  final double? subSeconds;
  final int? subCount;

  const SessionPlanTarget({
    required this.object,
    required this.startUtc,
    required this.endUtc,
    required this.hours,
    this.subSeconds,
    this.subCount,
  });
}

class SessionPlan {
  final List<SessionPlanTarget> targets;
  final double plannedHours;

  /// Why the plan came out this way (or empty), e.g. a second target being
  /// skipped because nothing else overlaps the window usefully.
  final List<String> notes;
  const SessionPlan(
      {required this.targets, required this.plannedHours, this.notes = const []});
}

// Mirror of the ranker's hours term so an allocated slice is valued the same
// way the nightly score values a dark window (25 pts saturating at 6 h).
const double _hoursWeight = 25.0;
const double _hoursSaturationHours = 6.0;

// A slice shorter than this isn't worth a target switch (slew, refocus,
// plate solve, guider settle all eat into it).
const double _minSliceHours = 0.75;

double _valueOf(TonightSkyObject o, double hours) {
  final base = o.hoursFreeScore ?? ((o.score ?? 0) * 0.75);
  return base +
      _hoursWeight * (hours.clamp(0, _hoursSaturationHours)) /
          _hoursSaturationHours;
}

(DateTime, DateTime)? _usable(
    TonightSkyObject o, DateTime winStart, DateTime winEnd) {
  final s = o.windowStartUtc;
  final e = o.windowEndUtc;
  if (s == null || e == null) return null;
  final start = s.isAfter(winStart) ? s : winStart;
  final end = e.isBefore(winEnd) ? e : winEnd;
  if (!end.isAfter(start)) return null;
  return (start, end);
}

double _hoursBetween(DateTime a, DateTime b) =>
    b.difference(a).inMinutes / 60.0;

SessionPlanTarget _slice(TonightSkyObject o, DateTime s, DateTime e) {
  final hours = _hoursBetween(s, e);
  final subS = o.optimalSubS;
  return SessionPlanTarget(
    object: o,
    startUtc: s,
    endUtc: e,
    hours: hours,
    subSeconds: subS,
    subCount: subS != null && subS > 0 ? (hours * 3600 / subS).floor() : null,
  );
}

/// Plan [targetCount] (1 or 2) targets into [windowStartUtc]–[windowEndUtc].
///
/// For two targets the window is split at the boundary that maximizes the
/// summed value of both slices (evaluated on a 15-min grid over the region
/// where both are shootable), never allocating a slice under 45 min — if no
/// pair beats the best single target meaningfully, the plan degrades to one
/// target with a note saying so.
SessionPlan planImagingSession({
  required List<TonightSkyObject> ranked,
  required DateTime windowStartUtc,
  required DateTime windowEndUtc,
  int targetCount = 1,
}) {
  final winStart = windowStartUtc.toUtc();
  final winEnd = windowEndUtc.toUtc();
  if (!winEnd.isAfter(winStart)) {
    return const SessionPlan(
        targets: [], plannedHours: 0, notes: ['The window ends before it starts.']);
  }

  // Candidates that overlap the window at all, with their usable interval.
  final candidates = <(TonightSkyObject, DateTime, DateTime)>[
    for (final o in ranked)
      if (_usable(o, winStart, winEnd) case final (DateTime, DateTime) u)
        (o, u.$1, u.$2),
  ];
  if (candidates.isEmpty) {
    return const SessionPlan(targets: [], plannedHours: 0, notes: [
      'Nothing on tonight\'s list is shootable inside that window — widen it '
          'or check the list\'s dark windows.'
    ]);
  }

  // Best single target = max value over its full usable interval.
  (TonightSkyObject, DateTime, DateTime)? bestSingle;
  var bestSingleValue = double.negativeInfinity;
  for (final c in candidates) {
    final v = _valueOf(c.$1, _hoursBetween(c.$2, c.$3));
    if (v > bestSingleValue) {
      bestSingleValue = v;
      bestSingle = c;
    }
  }

  if (targetCount <= 1) {
    final s = _slice(bestSingle!.$1, bestSingle.$2, bestSingle.$3);
    return SessionPlan(targets: [s], plannedHours: s.hours);
  }

  // Two targets: try every ordered pair from the top candidates, splitting on
  // a 15-min grid. Value = sum of both slices' values.
  final top = candidates.take(12).toList();
  List<SessionPlanTarget>? bestPair;
  var bestPairValue = double.negativeInfinity;
  for (final a in top) {
    for (final b in top) {
      if (identical(a, b)) continue;
      // A shoots first: its slice must START at its usable start; B takes over
      // no earlier than B's usable start.
      final earliest = b.$2.isAfter(a.$2) ? b.$2 : a.$2;
      final latest = a.$3.isBefore(b.$3) ? a.$3 : b.$3;
      for (var t = earliest;
          !t.isAfter(latest);
          t = t.add(const Duration(minutes: 15))) {
        final hoursA = _hoursBetween(a.$2, t);
        final hoursB = _hoursBetween(t.isAfter(b.$2) ? t : b.$2, b.$3);
        if (hoursA < _minSliceHours || hoursB < _minSliceHours) continue;
        final v = _valueOf(a.$1, hoursA) + _valueOf(b.$1, hoursB);
        if (v > bestPairValue) {
          bestPairValue = v;
          bestPair = [
            _slice(a.$1, a.$2, t),
            _slice(b.$1, t.isAfter(b.$2) ? t : b.$2, b.$3),
          ];
        }
      }
    }
  }

  if (bestPair == null) {
    final s = _slice(bestSingle!.$1, bestSingle.$2, bestSingle.$3);
    return SessionPlan(targets: [s], plannedHours: s.hours, notes: [
      'No second target fits ≥45 min in this window — planned one target.'
    ]);
  }
  return SessionPlan(
    targets: bestPair,
    plannedHours: bestPair.fold(0.0, (sum, t) => sum + t.hours),
  );
}
