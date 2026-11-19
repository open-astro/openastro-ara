import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/widgets/imaging/polar_start_confirm.dart';

/// The rig's side of the confirm step: the plan it answers, and the starts it got.
class _Rig implements PolarAlignClient {
  _Rig(this.plan);
  final PolarAlignStartPlan plan;
  final planned = <String?>[];
  final started = <PolarAlignStartPlan>[];
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async {
    planned.add(stage);
    return plan;
  }

  @override
  Future<void> start(PolarAlignStartPlan plan) async => started.add(plan);
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

Future<void> _open(WidgetTester tester, _Rig rig, {String? stage, Locale locale = const Locale('en')}) async {
  await tester.pumpWidget(ProviderScope(
    child: MaterialApp(
      locale: locale,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: Builder(
          builder: (context) => TextButton(
            key: const Key('go'),
            onPressed: () => confirmAndStartPolarAlign(context, rig, stage: stage),
            child: const Text('Start'),
          ),
        ),
      ),
    ),
  ));
  await tester.tap(find.byKey(const Key('go')));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('the confirm step shows where to point, the turns and their direction, and starts only on Move the mount (#2048)',
      (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.fine,
      direction: PolarAlignDirections.west,
      stepDeg: 10,
      maxSweepDeg: 20,
    ));
    await _open(tester, rig, stage: PolarAlignStages.fine);

    expect(rig.planned, [PolarAlignStages.fine], reason: 'the direction comes from the rig, read from the mount');
    expect(find.text('Before the mount moves'), findsOneWidget);
    expect(find.text('Point the tube near the pole, with the counterweights down.'), findsOneWidget);
    expect(find.text('RA will turn about 10° twice toward the west (20° in all).'), findsOneWidget);
    expect(find.text('Later steps turn back over the same arc: never more than 20° from where it starts.'), findsOneWidget);
    expect(find.text("Keep a hand on the mount's power switch."), findsOneWidget);
    expect(rig.started, isEmpty, reason: 'nothing moves while the user reads');

    await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
    await tester.pumpAndSettle();
    expect(rig.started.single.direction, PolarAlignDirections.west, reason: 'the start carries the direction the user saw');
    expect(rig.started.single.stage, PolarAlignStages.fine);
  });

  testWidgets('Cancel starts nothing', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(direction: PolarAlignDirections.east));
    await _open(tester, rig);
    await tester.tap(find.byKey(const Key('polar-start-confirm-cancel')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('polar-start-confirm')), findsNothing);
    expect(rig.started, isEmpty);
  });

  testWidgets('a check-only start has no later step to turn back, and says the check step', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.check,
      direction: PolarAlignDirections.east,
      stepDeg: 15,
      maxSweepDeg: 30,
    ));
    await _open(tester, rig, stage: PolarAlignStages.check);
    expect(find.text('RA will turn about 15° twice toward the east (30° in all).'), findsOneWidget);
    expect(find.textContaining('Later steps'), findsNothing);
  });

  testWidgets('the confirm step reads in Brazilian Portuguese', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(direction: PolarAlignDirections.east, stepDeg: 10, maxSweepDeg: 20));
    await _open(tester, rig, locale: const Locale('pt', 'BR'));
    expect(find.text('Antes de a montagem se mover'), findsOneWidget);
    expect(find.text('A AR vai girar cerca de 10° duas vezes em direção ao leste (20° no total).'), findsOneWidget);
    expect(find.text('Mover a montagem'), findsOneWidget);
  });
}
