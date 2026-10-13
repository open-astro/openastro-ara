import '../models/tracking_calibration.dart';

/// #1264 — the sub advisor's second number on a rig without a guider: the
/// mount's tracking ceiling next to the sky-limited one. Pure — unit-tested.
/// Null when the rig is guided (nothing to say).
String? unguidedCeilingLine({
  required bool guided,
  required TrackingCalibration? calibration,
  required double skyLimitedSeconds,
}) {
  if (guided) return null;
  if (calibration == null) {
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
