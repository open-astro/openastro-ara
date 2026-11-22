import '../l10n/app_localizations.dart';
import '../state/polar_align/polar_align_state.dart';

/// The `polar_align.error` reason when a stop Polar Align did not send ended
/// the run (Stop Mount, Emergency Stop, the direction pad, a reaction, …).
const polarAlignSlewAbortedExternally = 'slew_aborted_externally';

/// #2081 the `polar_align.error` reason of a turn that went the other way
/// from the saved RA motor direction (never the Polar Aligner's
/// `wrong_direction`).
const polarAlignTurnWrongWay = 'turn_wrong_way';

/// #2107 why a failed polar alignment ended, in the app's language where the
/// app knows the cause: a stop from outside is worded from the event's
/// `stopped_by`, with the cut-power warning when the run could not confirm
/// its stop afterwards (#2066: the daemon's sentence carries it, and no
/// `mount_did_not_stop` follows). #2067 a reason the app can word in full
/// (`_guardText`) reads in the app's language too, with the same warnings;
/// #2081 a turn the wrong way is one of them, and says where to test the
/// direction again.
/// Anything else shows the daemon's own sentence, then its reason token; null
/// when the run did not fail with a reason.
String? polarAlignErrorText(AppLocalizations l, PolarAlignLive live) {
  final stoppedBy = live.errorStoppedBy;
  if (live.errorReason == polarAlignSlewAbortedExternally &&
      stoppedBy != null) {
    final words = l.polarAlignStoppedFromOutside(stoppedBy);
    return live.errorStopUnconfirmed
        ? '$words ${l.polarAlignStopNotConfirmed}'
        : words;
  }
  final worded = _guardText(l, live.errorReason);
  if (worded != null) {
    return _withStopWarnings(l, live, worded);
  }
  return live.errorMessage ?? live.errorReason;
}

/// #2067 the first sentence of the daemon's `AlignerCutPowerSentence`
/// (PolarAlignService.Aligner.cs). The daemon adds it to a failure's message
/// when the run's end could not confirm the Polar Aligner stopped, and no flag
/// says so: matched here so the app's own wording keeps that warning.
const _alignerNotStopped =
    'Ara could not confirm that the Polar Aligner stopped.';

/// #2067 the app's sentence for a `polar_align.error` reason whose cause is
/// one thing the app can say in full; null for a reason whose daemon text
/// carries what only the daemon knows (the solver's fix, a driver's fault, one
/// of several causes), which then shows as sent.
String? _guardText(AppLocalizations l, String? reason) => switch (reason) {
  'slew_stalled' => l.polarAlignErrorSlewStalled,
  'slew_timeout' => l.polarAlignErrorSlewTimeout,
  'dec_moved' => l.polarAlignErrorDecMoved,
  'pier_changed' => l.polarAlignErrorPierChanged,
  'turn_mismatch' => l.polarAlignErrorTurnMismatch,
  'pier_flip' => l.polarAlignErrorPierFlip,
  'tracking_off' => l.polarAlignErrorTrackingOff,
  'sweep_limit' => l.polarAlignErrorSweepLimit,
  polarAlignTurnWrongWay => l.polarAlignTurnWrongWay,
  'mount_parked' => l.refusalPolarAlignMountParked,
  'on_axis' => l.refusalPolarAlignOnAxis,
  'pointing_unknown' => l.refusalPolarAlignPointingUnknown,
  'pose_ambiguous' => l.refusalPolarAlignPoseAmbiguous,
  'mount_busy' => l.refusalPolarAlignMountBusy,
  'mount_did_not_stop' => l.polarAlignStopNotConfirmed,
  'aligner_did_not_stop' => l.polarAlignerStopNotConfirmed,
  _ => null,
};

/// The cut-power warnings the daemon's message carried: the mount's (the
/// event's `stop_unconfirmed`) and the Polar Aligner's.
String _withStopWarnings(
  AppLocalizations l,
  PolarAlignLive live,
  String words,
) {
  var out = words;
  if (live.errorStopUnconfirmed && live.errorReason != 'mount_did_not_stop') {
    out = '$out ${l.polarAlignStopNotConfirmed}';
  }
  if (live.errorReason != 'aligner_did_not_stop' &&
      (live.errorMessage?.contains(_alignerNotStopped) ?? false)) {
    out = '$out ${l.polarAlignerStopNotConfirmed}';
  }
  return out;
}
