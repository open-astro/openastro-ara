import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../theme/ara_colors.dart';
export 'polar_error_rating.dart';

// The Polar Align Routine's readout, drawn the same in the Full UI and
// Simple Mode: the bullseye, the turns, the bolt nudge and the side of the pole.

/// §45.10 dynamic bullseye zoom: the outer ring's radius in arcminutes for the
/// current total error — ~5° while far off, 30′ once under 1°, 5′ once under
/// 5′ (spec says 1′ under 5′; 5′ keeps the dot on-screen while the user
/// overshoots around zero), and the 2′ near-pole reticle under 2′
/// ([nearPoleReticle]). Pure — unit-tested.
double bullseyeRangeArcmin(double? totalErrorArcmin) {
  final total = totalErrorArcmin;
  if (total == null || total >= 60.0) return 300.0;
  if (total >= 5.0) return 30.0;
  if (total >= nearPoleReticleArcmin) return 5.0;
  return nearPoleReticleArcmin;
}

/// §45.16 near-pole reticle mode: inside this total error the bullseye
/// becomes a polar-scope reticle — a 2′ field with a ring every 30″ — so the
/// last arcminute is worked against ticks a solve can resolve, not against
/// one ring that spans the whole remaining error. 2′ is ~4× the per-frame
/// solve noise (§45.16 budget), so the dot moves between rings, not within
/// the noise.
const double nearPoleReticleArcmin = 2.0;

/// True when the bullseye draws the near-pole reticle.
bool nearPoleReticle(double? totalErrorArcmin) =>
    totalErrorArcmin != null && totalErrorArcmin < nearPoleReticleArcmin;

/// §45.16 per-frame noise, arcminutes: the floor under which a change
/// between two frames is scatter, not a knob turn. The solver's centroid is
/// ~1″; the 0.5′ is the mount's pointing jitter and flexure between frames
/// (5–30″), which is what a solve of a stationary camera actually sees.
const double polarSolveNoiseArcmin = 0.5;

/// §45.16 bolt-direction calibration nudge. Mounts differ in which way a
/// bolt moves the axis, and the panel's Raise/Lower/Move east/west words
/// assume the user finds the right one. After a frame, if ONE axis's error
/// grew by more than the solve noise (and by more than 10 % of what it was)
/// while the other stayed put, the user turned that bolt the wrong way:
/// name the axis and say to turn it the other way. Both axes moving is a
/// bump or a re-seat, not a bolt direction, and says nothing; nor does an
/// axis that crossed zero (the right way, too far — an overshoot, however
/// large), since "turn it back past where you started" would be wrong.
/// The axis to name: true altitude, false azimuth, null none.
/// Pure — unit-tested.
bool? boltDirectionNudge({
  required double? prevAlt,
  required double? prevAz,
  required double? alt,
  required double? az,
  double noiseArcmin = polarSolveNoiseArcmin,
}) {
  if (prevAlt == null || prevAz == null || alt == null || az == null) return null;
  bool grew(double before, double after) =>
      before.sign == after.sign &&
      after.abs() - before.abs() > math.max(noiseArcmin, before.abs() * 0.1);
  bool still(double before, double after) => (after - before).abs() <= noiseArcmin;
  if (grew(prevAlt, alt) && still(prevAz, az)) {
    return true;
  }
  if (grew(prevAz, az) && still(prevAlt, alt)) {
    return false;
  }
  return null;
}

/// §45.16 the bolt-direction nudge across frames (the full panel's and Simple
/// Mode's): kept for a few frames, cleared as soon as the named axis
/// improves.
class BoltNudgeTracker {
  double? _prevAlt;
  double? _prevAz;
  int _iteration = 0;
  int _untilIteration = 0;
  static const _frames = 3;

  /// The axis the amber line names (true altitude, false azimuth), or null.
  bool? nudge;

  /// Compare this frame's errors with the previous frame's. Call on every
  /// live-state change; a seed or a new run resets the baseline.
  void track(PolarAlignLive live) {
    if (live.phase == PolarAlignStates.paused) return; // no solve: hold the baseline
    if (live.phase != PolarAlignStates.adjusting) {
      // Leaving adjust (stopped, failed, a new run's seed): the next adjust
      // frame starts a new baseline, so a new run whose first iteration
      // number matches the old run's is never compared across runs.
      _iteration = 0;
      _prevAlt = null;
      _prevAz = null;
      nudge = null;
      return;
    }
    if (live.iteration == _iteration) return;
    _iteration = live.iteration;
    final found = boltDirectionNudge(
        prevAlt: _prevAlt, prevAz: _prevAz, alt: live.altErrorArcmin, az: live.azErrorArcmin);
    if (found != null) {
      nudge = found;
      _untilIteration = live.iteration + _frames;
    } else if (nudge != null) {
      // Cleared as soon as the named axis actually improves by more than the
      // noise (the user turned the bolt back), else after a few frames.
      final before = nudge! ? _prevAlt : _prevAz;
      final after = nudge! ? live.altErrorArcmin : live.azErrorArcmin;
      final improved = before != null &&
          after != null &&
          before.abs() - after.abs() > polarSolveNoiseArcmin;
      if (improved || live.iteration > _untilIteration) nudge = null;
    }
    _prevAlt = live.altErrorArcmin;
    _prevAz = live.azErrorArcmin;
  }
}

/// Fractional dot offset inside the bullseye for the current error, drawn as
/// seen by someone facing the pole: x = azimuth, east of the pole to the right
/// facing the NCP and to the LEFT facing the SCP ([southern]); y = altitude
/// (above pole → up). Clamped to the ring edge so an off-scale dot stays
/// visible. Pure — unit-tested.
Offset bullseyeDotFraction(double? azErrArcmin, double? altErrArcmin, double rangeArcmin,
    {bool southern = false}) {
  final az = (azErrArcmin ?? 0) / rangeArcmin * (southern ? -1 : 1);
  final alt = (altErrArcmin ?? 0) / rangeArcmin;
  final len = math.sqrt(az * az + alt * alt);
  if (len <= 1.0) return Offset(az, -alt);
  return Offset(az / len, -alt / len);
}

/// Altitude knob direction for the current error (§45 design: decoupled
/// knob directions, since chasing one coupled 2-D error is what makes people
/// feel lost). Positive = axis above the pole → lower. Pure.
(IconData, String) polarAltHint(AppLocalizations l, double? alt) {
  if (alt == null) return (Icons.remove, '—');
  if (alt.abs() <= 0.05) return (Icons.check, l.polarHintOnTarget);
  return alt > 0 ? (Icons.arrow_downward, l.polarHintLower) : (Icons.arrow_upward, l.polarHintRaise);
}

/// Azimuth knob direction for the current error: positive = axis east of the
/// pole → move west. The arrow points the way that direction lies for someone
/// facing the pole: west is on the LEFT facing the NCP and on the RIGHT facing
/// the SCP ([southern]). 2026-10-17: a southern site read "Move east" beside an
/// arrow to the right. Pure — unit-tested.
(IconData, String) polarAzHint(AppLocalizations l, double? az, {required bool southern}) {
  if (az == null) return (Icons.remove, '—');
  if (az.abs() <= 0.05) return (Icons.check, l.polarHintOnTarget);
  final westIsLeft = !southern;
  return az > 0
      ? (westIsLeft ? Icons.arrow_back : Icons.arrow_forward, l.polarHintMoveWest)
      : (westIsLeft ? Icons.arrow_forward : Icons.arrow_back, l.polarHintMoveEast);
}

/// Format a signed arcminute value like `+14.2′` / `−23.4′`.
String formatArcmin(double? v) {
  if (v == null) return '—';
  final sign = v >= 0 ? '+' : '−';
  return '$sign${v.abs().toStringAsFixed(1)}′';
}

/// Worst-case declination drift, in arcseconds, that a polar error of
/// [arcmin] causes over [seconds]: error × Earth's rotation rate
/// (7.292e-5 rad/s), i.e. ~0.26″ per minute per arcminute of error.
/// Pure — unit-tested.
double maxDriftArcsec(double arcmin, double seconds) => arcmin.abs() * 60 * 7.2921e-5 * seconds;

/// [arcmin] as a share of the Moon's ~31′ width: `1/39 of the Moon's width`
/// or `1.5× the Moon's width`. Pure — unit-tested.
String moonWidthComparison(double arcmin) {
  const moon = 31.0;
  final a = arcmin.abs();
  if (a <= 0) return "0 × the Moon's width";
  if (a >= moon) return "${(a / moon).toStringAsFixed(1)}× the Moon's width";
  return "1/${(moon / a).round()} of the Moon's width";
}

/// Which side of the pole the mount axis sits on, from the rough stage's readout.
enum PolarSide { above, below, east, west }

/// Rough stage: which side of the pole the axis sits. Null until both errors
/// are known; both fields null = on the pole (under half an arcminute each
/// way). Positive az = east of the pole as seen facing it, in both
/// hemispheres. Pure — the English sentence and Simple Mode's localised one
/// are built on it.
({PolarSide? vertical, PolarSide? side})? polarSideOfPole(double? altArcmin, double? azArcmin) {
  if (altArcmin == null || azArcmin == null) return null;
  final vertical = altArcmin.abs() < 0.5
      ? null
      : altArcmin > 0
          ? PolarSide.above
          : PolarSide.below;
  final side = azArcmin.abs() < 0.5
      ? null
      : azArcmin > 0
          ? PolarSide.east
          : PolarSide.west;
  return (vertical: vertical, side: side);
}

/// Rough stage: which side of the pole the axis sits, in words, in the user's
/// language — the one sentence the Full UI and Simple Mode share. Null until
/// both errors are known. Pure — unit-tested.
String? polarSideOfPoleText(AppLocalizations l10n, double? altArcmin, double? azArcmin) {
  final where = polarSideOfPole(altArcmin, azArcmin);
  if (where == null) return null;
  if (where.vertical == null && where.side == null) return l10n.polarSideOnPole;
  final parts = [
    if (where.vertical == PolarSide.above) l10n.polarSideAbove,
    if (where.vertical == PolarSide.below) l10n.polarSideBelow,
    if (where.side == PolarSide.east) l10n.polarSideEast,
    if (where.side == PolarSide.west) l10n.polarSideWest,
  ];
  final joined = parts.join(l10n.polarSideJoin);
  // "above the pole" but "east of the pole" / "above and east of the pole".
  return where.side == null ? l10n.polarSideSitsVertical(joined) : l10n.polarSideSits(joined);
}

/// The zooming bullseye: three concentric rings, cross-hairs, and the RA-axis
/// dot at [dotFraction] (unit square, (0,0) = pole).
class BullseyePainter extends CustomPainter {
  final Offset dotFraction;
  final Color zoneColor;

  /// §45.16 near-pole reticle: four rings 30″ apart over a 2′ field with
  /// 15″ ticks on the cross-hairs, and a smaller dot, instead of the three
  /// zoom rings.
  final bool reticle;

  /// The room left outside the outer ring.
  final double edgeInset;

  const BullseyePainter({required this.dotFraction, required this.zoneColor, this.reticle = false, this.edgeInset = 4});

  /// The outer ring's radius on a canvas of [size].
  double ringRadius(Size size) => math.min(size.width, size.height) / 2 - edgeInset;

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final radius = ringRadius(size);
    final ring = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1
      ..color = AraColors.textSecondary.withValues(alpha: 0.5);
    final rings = reticle ? const [1.0, 0.75, 0.5, 0.25] : const [1.0, 2 / 3, 1 / 3];
    for (final f in rings) {
      canvas.drawCircle(center, radius * f, ring);
    }
    canvas.drawLine(center - Offset(radius, 0), center + Offset(radius, 0), ring);
    canvas.drawLine(center - Offset(0, radius), center + Offset(0, radius), ring);
    if (reticle) {
      // 15″ ticks (every eighth of the field) on both cross-hairs.
      final tick = math.max(4.0, radius * 0.04);
      for (var i = 1; i < 8; i++) {
        final d = radius * i / 8;
        for (final s in const [-1.0, 1.0]) {
          canvas.drawLine(center + Offset(s * d, -tick), center + Offset(s * d, tick), ring);
          canvas.drawLine(center + Offset(-tick, s * d), center + Offset(tick, s * d), ring);
        }
      }
    }

    final dot = Paint()..color = zoneColor;
    final pos = center + Offset(dotFraction.dx * radius, dotFraction.dy * radius);
    canvas.drawCircle(pos, reticle ? math.max(6.0, radius * 0.035) : math.max(10.0, radius * 0.06), dot);
    // A subtle line from the dot back to the pole — the direction to drive it.
    final tether = Paint()
      ..strokeWidth = math.max(2.5, radius * 0.015)
      ..color = zoneColor.withValues(alpha: 0.4);
    canvas.drawLine(pos, center, tether);
  }

  @override
  bool shouldRepaint(covariant BullseyePainter old) =>
      old.dotFraction != dotFraction || old.zoneColor != zoneColor || old.reticle != reticle || old.edgeInset != edgeInset;
}
