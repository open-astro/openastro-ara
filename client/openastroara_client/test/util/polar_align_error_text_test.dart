import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/util/polar_align_error_text.dart';

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  PolarAlignLive stopped(String? by, {String? message}) => PolarAlignLive(
    phase: PolarAlignStates.failed,
    errorReason: polarAlignSlewAbortedExternally,
    errorMessage: message ?? 'the daemon sentence',
    errorStoppedBy: by,
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
}
