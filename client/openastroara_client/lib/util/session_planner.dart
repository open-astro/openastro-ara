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

/// Real-night overheads charged against each slice so the sub counts describe
/// a night that actually happens, not an idealized one. Defaults are typical;
/// the dialog feeds the user's own PHD2/autofocus settings in.
class SessionOverheads {
  /// Per-target startup: slew + PLATE SOLVE + centering + initial focus +
  /// guider settle before the first sub.
  final double setupMinutesPerTarget;

  /// Dither cadence + the guider settle each dither costs (0/disabled = none).
  final bool ditherEnabled;
  final int ditherEveryNFrames;
  final double ditherSettleSec;

  /// Periodic autofocus: a run every [autofocusEveryHours] costing
  /// [autofocusRunSec] (0 hours = never).
  final double autofocusEveryHours;
  final double autofocusRunSec;

  const SessionOverheads({
    this.setupMinutesPerTarget = 6,
    this.ditherEnabled = true,
    this.ditherEveryNFrames = 1,
    this.ditherSettleSec = 10,
    this.autofocusEveryHours = 2,
    this.autofocusRunSec = 60,
  });

  /// Subs of [subSeconds] that fit into [sliceHours] after setup, dither
  /// settles, and autofocus runs. Never negative.
  int subsIn(double sliceHours, double subSeconds) {
    if (subSeconds <= 0) return 0;
    var budget = sliceHours * 3600 - setupMinutesPerTarget * 60;
    if (autofocusEveryHours > 0) {
      budget -= (sliceHours / autofocusEveryHours).floor() * autofocusRunSec;
    }
    if (budget <= 0) return 0;
    final perSub = subSeconds +
        (ditherEnabled && ditherEveryNFrames > 0
            ? ditherSettleSec / ditherEveryNFrames
            : 0.0);
    return (budget / perSub).floor();
  }
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

SessionPlanTarget _slice(TonightSkyObject o, DateTime s, DateTime e,
    SessionOverheads overheads) {
  final hours = _hoursBetween(s, e);
  final subS = o.optimalSubS;
  return SessionPlanTarget(
    object: o,
    startUtc: s,
    endUtc: e,
    hours: hours,
    subSeconds: subS,
    subCount:
        subS != null && subS > 0 ? overheads.subsIn(hours, subS) : null,
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
  SessionOverheads overheads = const SessionOverheads(),
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
    final s = _slice(bestSingle!.$1, bestSingle.$2, bestSingle.$3, overheads);
    return SessionPlan(targets: [s], plannedHours: s.hours);
  }

  var slices = <SessionPlanTarget>[
    _slice(bestSingle!.$1, bestSingle.$2, bestSingle.$3, overheads)
  ];
  final notes = <String>[];
  final wanted = targetCount.clamp(1, 4);

  // Greedy refinement: while more targets are wanted, find the single best
  // "split one existing slice, hand part of it to an unused candidate" move
  // (both orders, 15-min grid, each piece ≥ 45 min) and apply it. Stops with a
  // note when no split improves total value — the window is honestly too short
  // for another worthwhile target.
  final usableOf = {
    for (final c in candidates) c.$1.id: (c.$2, c.$3),
  };
  while (slices.length < wanted) {
    final used = {for (final t in slices) t.object.id};
    List<SessionPlanTarget>? bestSlices;
    var bestGain = 0.0;
    for (var i = 0; i < slices.length; i++) {
      final s0 = slices[i];
      final currentValue = _valueOf(s0.object, s0.hours);
      final own = usableOf[s0.object.id]!;
      for (final c in candidates.take(12)) {
        if (used.contains(c.$1.id)) continue;
        for (final existingFirst in [true, false]) {
          final firstUsable = existingFirst ? own : (c.$2, c.$3);
          final secondUsable = existingFirst ? (c.$2, c.$3) : own;
          final firstObj = existingFirst ? s0.object : c.$1;
          final secondObj = existingFirst ? c.$1 : s0.object;
          final lo = firstUsable.$1.isAfter(s0.startUtc)
              ? firstUsable.$1
              : s0.startUtc;
          final hi = s0.endUtc;
          for (var t = lo;
              !t.isAfter(hi);
              t = t.add(const Duration(minutes: 15))) {
            final h1 = _hoursBetween(lo, t);
            final s2start =
                t.isAfter(secondUsable.$1) ? t : secondUsable.$1;
            final s2end =
                hi.isBefore(secondUsable.$2) ? hi : secondUsable.$2;
            final h2 = _hoursBetween(s2start, s2end);
            if (h1 < _minSliceHours || h2 < _minSliceHours) continue;
            final gain = _valueOf(firstObj, h1) +
                _valueOf(secondObj, h2) -
                currentValue;
            if (gain > bestGain) {
              bestGain = gain;
              bestSlices = [
                ...slices.sublist(0, i),
                _slice(firstObj, lo, t, overheads),
                _slice(secondObj, s2start, s2end, overheads),
                ...slices.sublist(i + 1),
              ];
            }
          }
        }
      }
    }
    if (bestSlices == null) {
      notes.add(slices.length == 1
          ? 'No second target fits ≥45 min in this window — planned one target.'
          : 'The window supports ${slices.length} worthwhile targets — '
              'adding more would shortchange all of them.');
      break;
    }
    slices = bestSlices;
  }
  slices.sort((a, b) => a.startUtc.compareTo(b.startUtc));
  return SessionPlan(
    targets: slices,
    plannedHours: slices.fold(0.0, (sum, t) => sum + t.hours),
    notes: notes,
  );
}