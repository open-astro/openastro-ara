import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/widgets/imaging/polar_flow.dart';

const _result = PolarAlignCheckResult(
  altErrorArcmin: -0.4,
  azErrorArcmin: 0.3,
  totalErrorArcmin: 0.5,
  totalSigmaArcmin: 0.2,
  verdict: 'excellent',
);

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  test('polarFlowLine: each running step has its calm line; Ready and the result have none', () {
    String? line(PolarAlignLive live) => polarFlowLine(en, live);
    expect(line(const PolarAlignLive()), isNull);
    expect(
      line(const PolarAlignLive(phase: PolarAlignStates.seeding)),
      "Measuring the mount's axis…",
    );
    expect(
      line(
        const PolarAlignLive(
          phase: PolarAlignStates.seeding,
          measuringPoint: 3,
        ),
      ),
      'Measuring — point 3 of 3',
    );
    expect(
      line(const PolarAlignLive(phase: PolarAlignStates.adjusting)),
      'Adjusting',
    );
    expect(
      line(const PolarAlignLive(phase: PolarAlignStates.paused)),
      'Paused',
    );
    expect(
      line(const PolarAlignLive(phase: PolarAlignStates.homing)),
      'Going Home…',
    );
    expect(
      line(const PolarAlignLive(phase: PolarAlignStates.checking)),
      'Checking the alignment — 1 of 3',
      reason: 'the solve being taken',
    );
    expect(
      line(
        const PolarAlignLive(
          phase: PolarAlignStates.checking,
          checkSolvesDone: 3,
        ),
      ),
      'Checking the alignment — 3 of 3',
      reason: 'never past the last',
    );
    expect(
      line(
        const PolarAlignLive(
          phase: PolarAlignStates.homing,
          checkResult: _result,
        ),
      ),
      'Returning Home…',
    );
    expect(
      line(
        const PolarAlignLive(
          phase: PolarAlignStates.checked,
          checkResult: _result,
        ),
      ),
      isNull,
    );
    expect(
      polarFlowLine(
        pt,
        const PolarAlignLive(
          phase: PolarAlignStates.seeding,
          measuringPoint: 2,
        ),
      ),
      'Medindo — ponto 2 de 3',
    );
  });

  test(
    'polarReadyNote: what the last run left to say, or nothing before any run',
    () {
      expect(polarReadyNote(en, const PolarAlignLive()), isNull);
      expect(
        polarReadyNote(
          en,
          const PolarAlignLive(phase: PolarAlignStates.stopped),
        ),
        (text: 'Polar alignment stopped.', error: false),
      );
      expect(
        polarReadyNote(
          en,
          const PolarAlignLive(
            phase: PolarAlignStates.failed,
            errorReason: 'home_failed',
            errorMessage: 'x',
          ),
        ),
        (
          text: 'The mount did not go Home. Check the mount, send it Home, then start again.',
          error: true,
        ),
      );
      expect(
        polarReadyNote(
          en,
          const PolarAlignLive(phase: PolarAlignStates.failed),
        ),
        (text: 'Polar alignment failed', error: true),
        reason: 'a failure the rig gave no reason for',
      );
      expect(
        polarReadyNote(
          en,
          const PolarAlignLive(
            phase: PolarAlignStates.checked,
            checkResult: _result,
            resultDismissed: true,
          ),
        ),
        (
          text: 'Last result: Excellent, 0.5′ ± 0.2′ from the pole.',
          error: false,
        ),
      );
    },
  );

  test('every verdict has its word and its meaning, in English and pt-BR', () {
    const tokens = ['excellent', 'very_good', 'good', 'rough', 'far_off'];
    expect(
      [for (final t in tokens) polarVerdictLabel(en, t)],
      ['Excellent', 'Very good', 'Good', 'Rough', 'Far off'],
    );
    expect(
      polarVerdictLabel(en, 'splendid'),
      'Far off',
      reason: 'a token this build does not know',
    );
    final meanings = {for (final t in tokens) polarVerdictMeaning(en, t)};
    expect(meanings, hasLength(5), reason: 'one sentence each');
    for (final t in tokens) {
      expect(
        polarVerdictLabel(pt, t),
        isNot(polarVerdictLabel(en, t)),
        reason: t,
      );
      expect(
        polarVerdictMeaning(pt, t),
        isNot(polarVerdictMeaning(en, t)),
        reason: t,
      );
    }
    expect(
      polarVerdictMeaning(en, 'excellent'),
      "Polar alignment won't limit your exposures.",
    );
  });
}
