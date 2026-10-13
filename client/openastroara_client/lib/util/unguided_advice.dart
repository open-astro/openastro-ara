import '../models/tracking_calibration.dart';

/// #1264 — the sub advisor's second number on a rig without a guider: the
/// mount's tracking ceiling next to the sky-limited one. Pure — unit-tested.
/// Null when the rig is guided (nothing to say).
String? unguidedCeilingLine({
  required bool guided,
  required TrackingCalibration? calibration,
  required double skyLimitedSeconds,
  // #1264 stage 1 — the prior the DAEMON resolved for this rig (its spec-sheet
  // maths with the site seeing and the latest polar-align residual, served on
  // GET /tracking/prior), used only before anything is measured. Read, never
  // recomputed, so the advisor's number is the probe's starting point.
  double? priorSeconds,
  String? priorSource,
}) {
  if (guided) return null;
  if (calibration == null) {
    if (priorSeconds != null) {
      final lower = priorSeconds < skyLimitedSeconds
          ? ' — the run uses this lower figure until it has measured'
          : ' — the sky limit above is the lower of the two';
      return 'Unguided: about ${formatExposureSeconds(priorSeconds)} '
          '(from ${priorSource ?? 'the prior'}; the first run measures it)$lower.';
    }
    return 'Unguided rig: the run measures how long a sub your mount holds '
        '(a Tracking Probe at the target) and shortens the subs to it.';
  }
  final ceiling = calibration.ceilingSeconds;
  final how = calibration.measured
      ? 'measured ${formatCalibrationDate(calibration.measuredUtc)}'
      : 'prior kept, not yet measured';
  final lower = ceiling < skyLimitedSeconds
      ? ' — the run uses this lower figure'
      : ' — the sky limit above is the lower of the two';
  return 'Unguided: about ${formatExposureSeconds(ceiling)} ($how)$lower.';
}
