/// §47 / #1146 — the two numbers a mosaic project record needs from a run.
library;

import 'dart:math' as math;

import '../models/sequence/imaging_run_body.dart';

/// How many light frames ONE panel owes before it counts as done: the run's
/// frame count for a single-filter run, or the sum of the filter plan's step
/// counts (each panel gets one loop per step — `buildTargetBlock` ignores
/// [frameCount] when a plan is in use).
int mosaicFramesPerPanel(int frameCount, List<FilterPlanStep>? filterPlan) {
  if (filterPlan == null || filterPlan.isEmpty) return math.max(1, frameCount);
  var sum = 0;
  for (final step in filterPlan) {
    sum += math.max(0, step.frameCount);
  }
  return math.max(1, sum);
}

/// Mean right ascension on the circle, in [0, 360): panels at 359° and 1°
/// average to 0°, not 180° (§47.3, a mosaic straddling 0h). Empty → 0.
double circularMeanRaDeg(Iterable<double> raDegs) {
  var x = 0.0, y = 0.0, n = 0;
  for (final ra in raDegs) {
    final r = ra * math.pi / 180.0;
    x += math.cos(r);
    y += math.sin(r);
    n++;
  }
  if (n == 0 || (x.abs() < 1e-12 && y.abs() < 1e-12)) return 0;
  final deg = math.atan2(y, x) * 180.0 / math.pi;
  var wrapped = deg % 360.0;
  if (wrapped < 0) wrapped += 360.0;
  // atan2 of a centre a hair below 0° lands at 359.999…: that is 0h.
  return wrapped >= 360.0 - 1e-9 ? 0.0 : wrapped;
}
