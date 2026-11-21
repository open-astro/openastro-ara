import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/util/polar_align_error_text.dart';

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

  test('a turn the wrong way reads in both languages and says where to test again (#2081)', () {
    const live = PolarAlignLive(
      phase: PolarAlignStates.failed,
      errorReason: polarAlignTurnWrongWay,
      errorMessage: 'the daemon sentence',
    );
    expect(polarAlignErrorText(en, live), en.polarAlignTurnWrongWay);
    expect(polarAlignErrorText(en, live), contains('Test direction again'));
    expect(polarAlignErrorText(pt, live), contains('Testar a direção de novo'));
  });
}
