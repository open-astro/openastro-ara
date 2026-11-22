import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/util/polar_align_error_text.dart';

// #2067 the daemon's AlignerCutPowerSentence (PolarAlignService.Aligner.cs),
// the whole message of every aligner_did_not_stop.
const alignerCutPower =
    'Ara could not confirm that the Polar Aligner stopped. Cut the Polar '
    "Aligner's power if it still moves; Ara sends it Stop again before it "
    'moves it next.';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  PolarAlignLive stopped(
    String? by, {
    String? message,
    bool stopUnconfirmed = false,
  }) => PolarAlignLive(
    phase: PolarAlignStates.failed,
    errorReason: polarAlignSlewAbortedExternally,
    errorMessage: message ?? 'the daemon sentence',
    errorStoppedBy: by,
    errorStopUnconfirmed: stopUnconfirmed,
  );

  // #2107 the pad stop's end had only the daemon's English; it reads in pt-BR now.
  test('a pad stop reads in pt-BR', () {
    final words = polarAlignErrorText(pt, stopped('pad_stop'));
    expect(words, pt.polarAlignStoppedFromOutside('pad_stop'));
    expect(words, contains('direcional'));
  });

  test('each named stop has its own sentence in both languages', () {
    const reasons = [
      'user_request',
      'emergency_stop',
      'watchdog',
      'park',
      'findhome',
      'pad_stop',
      'fault_reaction',
      'pose_unverified',
    ];
    for (final l in [en, pt]) {
      final sentences = reasons
          .map((r) => polarAlignErrorText(l, stopped(r)))
          .toSet();
      expect(
        sentences,
        hasLength(reasons.length),
        reason: 'no two stops share a sentence',
      );
      expect(
        sentences,
        isNot(contains(l.polarAlignStoppedFromOutside('something_new'))),
      );
    }
    expect(
      polarAlignErrorText(en, stopped('fault_reaction')),
      isNot(contains('Stop Mount')),
    );
  });

  // #2066 the daemon's sentence for a named stop whose run could not confirm
  // its stop ends in "cut the mount's power", and no mount_did_not_stop
  // follows: the app's own wording keeps that warning.
  test('a named stop the run could not confirm keeps the cut-power warning, in both languages', () {
    for (final l in [en, pt]) {
      final words = polarAlignErrorText(
        l,
        stopped('pad_stop', stopUnconfirmed: true),
      )!;
      expect(words, startsWith(l.polarAlignStoppedFromOutside('pad_stop')));
      expect(words, endsWith(l.polarAlignStopNotConfirmed));
    }
    expect(en.polarAlignStopNotConfirmed, contains("Cut the mount's power"));
    expect(
      polarAlignErrorText(en, stopped('pad_stop')),
      isNot(contains(en.polarAlignStopNotConfirmed)),
      reason: 'a confirmed stop has no warning',
    );
  });

  test('a stop the daemon did not name, or another failure, shows the daemon sentence', () {
    expect(
      polarAlignErrorText(en, stopped(null, message: 'stopped (Stop Mount)')),
      'stopped (Stop Mount)',
    );
    const other = PolarAlignLive(
      phase: PolarAlignStates.failed,
      errorReason: 'seed_solve_failed',
      errorMessage: 'check focus',
      errorStoppedBy: 'pad_stop',
    );
    expect(polarAlignErrorText(en, other), 'check focus');
  });

  // #2067 the guards' reasons had only the daemon's English.
  PolarAlignLive failed(
    String reason, {
    String message = 'the daemon sentence',
    bool stopUnconfirmed = false,
  }) => PolarAlignLive(
    phase: PolarAlignStates.failed,
    errorReason: reason,
    errorMessage: message,
    errorStopUnconfirmed: stopUnconfirmed,
  );

  test('each guard reason reads in the app language, in en and pt-BR', () {
    final cases = <String, String Function(AppLocalizations)>{
      'slew_stalled': (l) => l.polarAlignErrorSlewStalled,
      'slew_timeout': (l) => l.polarAlignErrorSlewTimeout,
      'dec_moved': (l) => l.polarAlignErrorDecMoved,
      'pier_changed': (l) => l.polarAlignErrorPierChanged,
      'turn_mismatch': (l) => l.polarAlignErrorTurnMismatch,
      'pier_flip': (l) => l.polarAlignErrorPierFlip,
      'tracking_off': (l) => l.polarAlignErrorTrackingOff,
      'hand_turn_too_far': (l) => l.polarAlignErrorHandTurnTooFar,
      'sweep_limit': (l) => l.polarAlignErrorSweepLimit,
      'mount_parked': (l) => l.refusalPolarAlignMountParked,
      'on_axis': (l) => l.refusalPolarAlignOnAxis,
      'pointing_unknown': (l) => l.refusalPolarAlignPointingUnknown,
      'pose_ambiguous': (l) => l.refusalPolarAlignPoseAmbiguous,
      'mount_busy': (l) => l.refusalPolarAlignMountBusy,
      'mount_did_not_stop': (l) => l.polarAlignStopNotConfirmed,
      'aligner_did_not_stop': (l) => l.polarAlignerStopNotConfirmed,
    };
    for (final MapEntry(key: reason, value: sentence) in cases.entries) {
      for (final l in [en, pt]) {
        expect(
          polarAlignErrorText(
            l,
            failed(
              reason,
              message: reason == 'aligner_did_not_stop'
                  ? alignerCutPower
                  : 'the daemon sentence',
            ),
          ),
          sentence(l),
          reason: '$reason in ${l.localeName}',
        );
      }
      expect(sentence(pt), isNot(sentence(en)), reason: '$reason pt-BR');
    }
  });

  test('a guard reason keeps the cut-power warnings the daemon sent, in both languages', () {
    for (final l in [en, pt]) {
      expect(
        polarAlignErrorText(l, failed('dec_moved', stopUnconfirmed: true)),
        '${l.polarAlignErrorDecMoved} ${l.polarAlignStopNotConfirmed}',
      );
      expect(
        polarAlignErrorText(
          l,
          failed(
            'pier_flip',
            message: 'the mount flipped. $alignerCutPower',
          ),
        ),
        '${l.polarAlignErrorPierFlip} ${l.polarAlignerStopNotConfirmed}',
      );
      expect(
        polarAlignErrorText(
          l,
          failed('mount_did_not_stop', stopUnconfirmed: true),
        ),
        l.polarAlignStopNotConfirmed,
        reason: 'the warning is not said twice',
      );
    }
  });

  test('an aligner_did_not_stop says the Polar Aligner warning once, in both languages', () {
    for (final l in [en, pt]) {
      final words = polarAlignErrorText(
        l,
        failed('aligner_did_not_stop', message: alignerCutPower),
      )!;
      expect(words, l.polarAlignerStopNotConfirmed);
      expect(
        l.polarAlignerStopNotConfirmed.allMatches(words),
        hasLength(1),
        reason: 'the warning is not said twice in ${l.localeName}',
      );
    }
  });

  test('a reason the app cannot word still shows the daemon sentence', () {
    for (final reason in ['slew_failed', 'solver_configuration', 'brand_new']) {
      expect(polarAlignErrorText(pt, failed(reason)), 'the daemon sentence');
    }
  });
}
