import 'package:flutter/material.dart';

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
(String, String) polarErrorRating(double arcmin) {
  final a = arcmin.abs();
  if (a <= 1) {
    return ('Excellent', "Polar alignment won't limit your exposures.");
  }
  if (a <= 3) return ('Very good', 'Plenty for guided imaging.');
  if (a <= 10) {
    return ('Good', 'Fine with guiding; keep unguided exposures short.');
  }
  if (a <= 30) {
    return ('Rough', 'Keep adjusting — stars will drift in longer exposures.');
  }
  return ('Far off', 'Keep turning the knobs toward the arrows.');
}

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

/// #1311 — a finished polar-alignment residual in words: the figure (a lower
/// bound) with its uncertainty, the drift and hour angle it came from, and
/// tonight's Polar Align result to compare. For tooltips.
String paResidualDetail(PaResidual r) {
  final error = r.paErrorMinArcmin ?? 0;
  final uncertainty = r.uncertaintyArcmin;
  final drift = r.driftArcsecPerMin;
  final ha = r.hourAngleHours;
  final align = r.alignErrorArcmin;
  final lines = <String>[
    "Polar alignment left after Align, from the guider's Dec corrections over "
        '${_clock(r.sampleSeconds)} of guiding: at least '
        '${formatPoleOffset(error).$1}'
        '${uncertainty == null ? '' : ' (± ${formatPoleOffset(uncertainty).$1})'}.',
    [
      if (drift != null) 'Dec drift ${drift.abs().toStringAsFixed(2)}″/min',
      if (ha != null)
        'hour angle ${ha < 0 ? '−' : '+'}${ha.abs().toStringAsFixed(1)} h',
    ].join(' at '),
    'Guiding at one hour angle sees only part of the error, so the total can '
        'be larger.',
    if (!r.reliable) 'Guiding was too noisy for a firm figure.',
    if (align != null)
      'Polar Align measured ${formatPoleOffset(align).$1}'
          '${r.alignEndedUtc == null ? '' : ' at ${_localTime(r.alignEndedUtc!)}'}.',
  ];
  return lines.where((l) => l.isNotEmpty).join('\n');
}

/// "m:ss" for a measurement's progress.
String paResidualClock(double seconds) => _clock(seconds);
