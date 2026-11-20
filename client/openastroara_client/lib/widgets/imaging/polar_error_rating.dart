import 'package:flutter/material.dart';

import '../../l10n/l10n.dart';
import '../../models/pa_residual.dart';
import '../../theme/ara_colors.dart';

/// §45.10 color zones: red > 1°, yellow 10′–1°, green < 10′. Pure — unit-tested.
Color zoneColor(double? totalErrorArcmin) {
  final total = totalErrorArcmin;
  if (total == null) return AraColors.textSecondary;
  if (total >= 60.0) return AraColors.accentError;
  if (total >= 10.0) return AraColors.accentBusy;
  return AraColors.accentConnected;
}

/// Total polar error in the unit people read most easily: arcseconds under
/// 10′ (`48″`), arcminutes under 1° (`24′`), degrees above (`1.5°`), with
/// the unit spelled out. Pure — unit-tested.
(String, String) formatPoleOffset(double arcmin) {
  final a = arcmin.abs();
  // Compare the ROUNDED figure, so 59.7′ reads 1.0° rather than "60′".
  final arcsec = (a * 60).round();
  if (arcsec < 600) return ('$arcsec″', 'arcseconds from the pole');
  final arcminutes = a.round();
  if (arcminutes < 60) return ('$arcminutes′', 'arcminutes from the pole');
  return ('${(a / 60).toStringAsFixed(1)}°', 'degrees from the pole');
}

/// Plain-English verdict for a total polar error (label, what it means): the
/// Polar Align quality card's bands, shared with the guiding strip's
/// residual-from-guiding readout. Pure — unit-tested.
(String, String) polarErrorRating(double arcmin) => _ratingByVerdict[polarVerdictFor(arcmin)]!;

/// The daemon's verdict token for a total error — the same bands as
/// `PolarAlignService.VerdictFor` (`excellent` ≤1′, `very_good` ≤3′, `good`
/// ≤10′, `rough` ≤30′, else `far_off`).
String polarVerdictFor(double arcmin) {
  final a = arcmin.abs();
  if (a <= 1) return 'excellent';
  if (a <= 3) return 'very_good';
  if (a <= 10) return 'good';
  if (a <= 30) return 'rough';
  return 'far_off';
}

/// The words for a verdict the daemon sent (`check_result.verdict`), so the
/// checked card shows the daemon's own call; null for a token this build does
/// not know (fall back to [polarErrorRating] on the total). Pure — unit-tested.
(String, String)? polarRatingForVerdict(String verdict) => _ratingByVerdict[verdict];

const _ratingByVerdict = <String, (String, String)>{
  'excellent': ('Excellent', "Polar alignment won't limit your exposures."),
  'very_good': ('Very good', 'Plenty for guided imaging.'),
  'good': ('Good', 'Fine with guiding; keep unguided exposures short.'),
  'rough': ('Rough', 'Keep adjusting — stars will drift in longer exposures.'),
  'far_off': ('Far off', 'Keep turning the knobs toward the arrows.'),
};

/// The icon for [polarErrorRating]'s band.
IconData polarErrorRatingIcon(double arcmin) {
  final a = arcmin.abs();
  if (a <= 1) return Icons.verified_outlined;
  if (a <= 3) return Icons.thumb_up_outlined;
  if (a <= 10) return Icons.check_circle_outline;
  if (a <= 30) return Icons.warning_amber_rounded;
  return Icons.error_outline;
}

String _clock(double seconds) {
  final s = seconds.round();
  return '${s ~/ 60}:${(s % 60).toString().padLeft(2, '0')}';
}

String _localTime(DateTime utc) {
  final t = utc.toLocal();
  return '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';
}

/// #1311 / #2072 — a finished polar-alignment residual in words: what the
/// figure measures (a lower bound from the guider's Dec corrections, taken
/// after Align) with its uncertainty, the drift and hour angle it came from,
/// and tonight's Polar Align result to compare. When the two disagree, both
/// causes are named. For tooltips.
String paResidualDetail(AppLocalizations l, PaResidual r) {
  final error = formatPoleOffset(r.paErrorMinArcmin ?? 0).$1;
  final uncertainty = r.uncertaintyArcmin;
  final duration = _clock(r.sampleSeconds);
  final drift = r.driftArcsecPerMin?.abs().toStringAsFixed(2);
  final ha = r.hourAngleHours;
  final hours = ha == null ? null : '${ha < 0 ? '−' : '+'}${ha.abs().toStringAsFixed(1)}';
  final align = r.alignErrorArcmin;
  final alignText = align == null ? null : formatPoleOffset(align).$1;
  final lines = <String>[
    uncertainty == null
        ? l.paResidualDetailFigure(duration, error)
        : l.paResidualDetailFigureSigma(duration, error, formatPoleOffset(uncertainty).$1),
    if (drift != null && hours != null)
      l.paResidualDetailDriftAtHourAngle(drift, hours)
    else if (drift != null)
      l.paResidualDetailDrift(drift)
    else if (hours != null)
      l.paResidualDetailHourAngle(hours),
    l.paResidualDetailLowerBound,
    if (r.aboveAlign && alignText != null)
      l.paResidualDetailDisagree(alignText, error)
    else if (!r.reliable)
      l.paResidualDetailNoisy,
    if (alignText != null)
      r.alignEndedUtc == null
          ? l.paResidualDetailAlign(alignText)
          : l.paResidualDetailAlignAt(alignText, _localTime(r.alignEndedUtc!)),
  ];
  return lines.join('\n');
}

/// What an `unavailable` reason means, in the user's words.
String paResidualReasonText(AppLocalizations l, String? reason) => switch (reason) {
  'lock_shift' => l.paResidualReasonLockShift,
  'ao' => l.paResidualReasonAo,
  'guiding_not_holding' => l.paResidualReasonGuidingNotHolding,
  'settle_failed' => l.paResidualReasonSettleFailed,
  'mount_not_tracking' => l.paResidualReasonMountNotTracking,
  'no_calibration' => l.paResidualReasonNoCalibration,
  'no_pixel_scale' => l.paResidualReasonNoPixelScale,
  _ => l.paResidualReasonNoFit,
};

/// "m:ss" for a measurement's progress.
String paResidualClock(double seconds) => _clock(seconds);
