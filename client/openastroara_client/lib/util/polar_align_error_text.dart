import '../l10n/app_localizations.dart';
import '../state/polar_align/polar_align_state.dart';

/// The `polar_align.error` reason when a stop Polar Align did not send ended
/// the run (Stop Mount, Emergency Stop, the direction pad, a reaction, …).
const polarAlignSlewAbortedExternally = 'slew_aborted_externally';

/// #2107 why a failed polar alignment ended, in the app's language where the
/// app knows the cause: a stop from outside is worded from the event's
/// `stopped_by`. Anything else shows the daemon's own sentence, then its
/// reason token; null when the run did not fail with a reason.
String? polarAlignErrorText(AppLocalizations l, PolarAlignLive live) {
  final stoppedBy = live.errorStoppedBy;
  if (live.errorReason == polarAlignSlewAbortedExternally &&
      stoppedBy != null) {
    return l.polarAlignStoppedFromOutside(stoppedBy);
  }
  return live.errorMessage ?? live.errorReason;
}
