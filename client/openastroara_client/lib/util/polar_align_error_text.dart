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
/// `mount_did_not_stop` follows). #2081 a turn the wrong way is worded too,
/// with where to test the direction again, and keeps the cut-power warning
/// the same way. Anything else shows the daemon's
/// own sentence, then its reason token; null when the run did not fail with a
/// reason.
String? polarAlignErrorText(AppLocalizations l, PolarAlignLive live) {
  if (live.errorReason == polarAlignTurnWrongWay) {
    return live.errorStopUnconfirmed
        ? '${l.polarAlignTurnWrongWay} ${l.polarAlignStopNotConfirmed}'
        : l.polarAlignTurnWrongWay;
  }
  final stoppedBy = live.errorStoppedBy;
  if (live.errorReason == polarAlignSlewAbortedExternally &&
      stoppedBy != null) {
    final words = l.polarAlignStoppedFromOutside(stoppedBy);
    return live.errorStopUnconfirmed
        ? '$words ${l.polarAlignStopNotConfirmed}'
        : words;
  }
  return live.errorMessage ?? live.errorReason;
}
