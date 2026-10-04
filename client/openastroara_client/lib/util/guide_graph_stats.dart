import 'dart:math' as math;

import '../state/guider/guide_step_state.dart';

/// The numbers PHD2's graph window shows beside the trace, computed over the
/// frames IN VIEW (PHD2 does the same): RMS per axis and total, the peak
/// excursion per axis, and the RA oscillation index — the fraction of
/// consecutive RA samples that changed sign (PHD2 flags > ~0.5 as
/// over-aggressive, < ~0.3 as under-corrected).
class GuideGraphStats {
  final double? rmsRa;
  final double? rmsDec;
  final double? rmsTotal;
  final double? peakRa;
  final double? peakDec;
  final double? raOscIndex;
  final int samples;

  const GuideGraphStats({
    this.rmsRa,
    this.rmsDec,
    this.rmsTotal,
    this.peakRa,
    this.peakDec,
    this.raOscIndex,
    this.samples = 0,
  });

  static const GuideGraphStats empty = GuideGraphStats();

  /// [pick] maps a step to its (ra, dec) in the graph's unit; a null axis is
  /// skipped for that axis (a lost-star frame).
  static GuideGraphStats compute(
    List<GuideStep> visible,
    (double? ra, double? dec) Function(GuideStep) pick,
  ) {
    var sumRa2 = 0.0, sumDec2 = 0.0, sumTot2 = 0.0;
    var nRa = 0, nDec = 0, nTot = 0;
    var peakRa = 0.0, peakDec = 0.0;
    var flips = 0, pairs = 0;
    double? prevRa;
    for (final s in visible) {
      final (ra, dec) = pick(s);
      if (ra != null) {
        sumRa2 += ra * ra;
        nRa++;
        peakRa = math.max(peakRa, ra.abs());
        if (prevRa != null) {
          pairs++;
          if ((ra < 0) != (prevRa < 0) && ra != 0 && prevRa != 0) flips++;
        }
        prevRa = ra;
      } else {
        prevRa = null;
      }
      if (dec != null) {
        sumDec2 += dec * dec;
        nDec++;
        peakDec = math.max(peakDec, dec.abs());
      }
      if (ra != null && dec != null) {
        sumTot2 += ra * ra + dec * dec;
        nTot++;
      }
    }
    return GuideGraphStats(
      rmsRa: nRa == 0 ? null : math.sqrt(sumRa2 / nRa),
      rmsDec: nDec == 0 ? null : math.sqrt(sumDec2 / nDec),
      rmsTotal: nTot == 0 ? null : math.sqrt(sumTot2 / nTot),
      peakRa: nRa == 0 ? null : peakRa,
      peakDec: nDec == 0 ? null : peakDec,
      raOscIndex: pairs == 0 ? null : flips / pairs,
      samples: math.max(nRa, nDec),
    );
  }
}
