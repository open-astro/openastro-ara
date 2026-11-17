import 'dart:math' as math;

import 'phd2_guide_log.dart';

enum GuideAxis { ra, dec }

/// What the one-line verdict on the Guide Stats card is built from, as data:
/// the weaker (larger-RMS) axis, and the axis with a steady drift if any.
class GuideVerdictInputs {
  /// Null when there are no samples or the two RMS values are equal.
  final GuideAxis? weakerAxis;

  /// The axis whose least-squares drift over the range is at least its RMS
  /// ([kSteadyDriftRatio]); RA wins a tie. Null: no steady drift.
  final GuideAxis? steadyDriftAxis;
  const GuideVerdictInputs({this.weakerAxis, this.steadyDriftAxis});
}

/// Drift counts as steady when the fitted line moves at least this many RMS
/// over the selected range.
const double kSteadyDriftRatio = 1.0;

/// Stats of a guide-log range: RMS per axis and total as PHD2's own display
/// has them (standard deviation about the mean). The Simple guiding chip's
/// RMS is measured from zero (`GuiderService.ComputeRms`), so it differs by
/// the mean offset, which the drift figure explains. Also peak per axis, RA
/// oscillation index (fraction of consecutive RA samples that changed sign;
/// like `GuideGraphStats`, a pair is not formed across an excluded frame),
/// and RA / Dec drift from a least-squares line per minute. Values are arcsec when the log
/// carries a pixel scale ([inArcsec]), else px.
class GuideLogStats {
  final bool inArcsec;
  final int samples;
  final double? rmsRa;
  final double? rmsDec;
  final double? rmsTotal;
  final double? peakRa;
  final double? peakDec;
  final double? raOscIndex;
  final double? raDriftPerMin;
  final double? decDriftPerMin;
  final GuideVerdictInputs verdict;

  const GuideLogStats({
    required this.inArcsec,
    required this.samples,
    this.rmsRa,
    this.rmsDec,
    this.rmsTotal,
    this.peakRa,
    this.peakDec,
    this.raOscIndex,
    this.raDriftPerMin,
    this.decDriftPerMin,
    this.verdict = const GuideVerdictInputs(),
  });

  /// Dither and star-lost frames are always left out; settling frames unless
  /// [includeSettling]. A null [pixelScaleArcsec] gives px.
  static GuideLogStats compute(
    List<Phd2LogFrame> frames, {
    double? pixelScaleArcsec,
    bool includeSettling = false,
  }) {
    final k = pixelScaleArcsec ?? 1.0;
    final used = [
      for (final f in frames)
        if (!f.dither &&
            !f.starLost &&
            f.raPx != null &&
            f.decPx != null &&
            (includeSettling || !f.settling))
          f,
    ];
    final n = used.length;
    if (n == 0) {
      return GuideLogStats(inArcsec: pixelScaleArcsec != null, samples: 0);
    }

    final t0 = used.first.at;
    final t = [for (final f in used) f.at.difference(t0).inMicroseconds / 6e7];
    final ra = [for (final f in used) f.raPx! * k];
    final dec = [for (final f in used) f.decPx! * k];

    final rmsRa = _std(ra);
    final rmsDec = _std(dec);
    // Pairs only between frames adjacent in [frames]: an excluded frame
    // between two kept ones resets the pair.
    var flips = 0, pairs = 0;
    double? prev;
    for (final f in frames) {
      if (!used.contains(f)) {
        prev = null;
        continue;
      }
      final r = f.raPx!;
      if (prev != null) {
        pairs++;
        if (r != 0 && prev != 0 && (r < 0) != (prev < 0)) flips++;
      }
      prev = r;
    }
    final raDrift = _slope(t, ra);
    final decDrift = _slope(t, dec);
    final span = t.last;

    GuideAxis? steady;
    if (n >= 3) {
      bool steadyOf(double? d, double rms) =>
          d != null &&
          d.abs() * span > 0 &&
          d.abs() * span >= kSteadyDriftRatio * rms;
      if (steadyOf(raDrift, rmsRa)) {
        steady = GuideAxis.ra;
      } else if (steadyOf(decDrift, rmsDec)) {
        steady = GuideAxis.dec;
      }
    }

    return GuideLogStats(
      inArcsec: pixelScaleArcsec != null,
      samples: n,
      rmsRa: rmsRa,
      rmsDec: rmsDec,
      rmsTotal: math.sqrt(rmsRa * rmsRa + rmsDec * rmsDec),
      peakRa: ra.map((v) => v.abs()).reduce(math.max),
      peakDec: dec.map((v) => v.abs()).reduce(math.max),
      raOscIndex: pairs == 0 ? null : flips / pairs,
      raDriftPerMin: raDrift,
      decDriftPerMin: decDrift,
      verdict: GuideVerdictInputs(
        weakerAxis: rmsRa == rmsDec
            ? null
            : (rmsRa > rmsDec ? GuideAxis.ra : GuideAxis.dec),
        steadyDriftAxis: steady,
      ),
    );
  }

  static double _std(List<double> v) {
    final mean = v.reduce((a, b) => a + b) / v.length;
    return math.sqrt(
      v.fold<double>(0, (s, x) => s + (x - mean) * (x - mean)) / v.length,
    );
  }

  /// Least-squares slope of y over x; null when x has no spread.
  static double? _slope(List<double> x, List<double> y) {
    final n = x.length;
    final mx = x.reduce((a, b) => a + b) / n;
    final my = y.reduce((a, b) => a + b) / n;
    var sxy = 0.0, sxx = 0.0;
    for (var i = 0; i < n; i++) {
      sxy += (x[i] - mx) * (y[i] - my);
      sxx += (x[i] - mx) * (x[i] - mx);
    }
    return sxx == 0 ? null : sxy / sxx;
  }
}
