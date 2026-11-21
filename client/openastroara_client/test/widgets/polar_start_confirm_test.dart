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
    expect(find.text('Point the tube 2–5° from the pole, with the counterweights down.'), findsOneWidget);
    expect(find.text('RA will turn about 10° twice toward the west (20° in all).'), findsOneWidget);
    expect(find.textContaining('Later steps'), findsNothing, reason: 'one measurement per Start (#2082): no later step');
    expect(find.text("Keep a hand on the mount's power switch."), findsOneWidget);
    expect(rig.started, isEmpty, reason: 'nothing moves while the user reads');

    await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
    await tester.pumpAndSettle();
    expect(rig.started.single.direction, PolarAlignDirections.west, reason: 'the start carries the direction the user saw');
    expect(rig.started.single.stage, PolarAlignStages.fine);
  });

  testWidgets('the confirm step says when the run measures with the main camera (#2082)', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.coarse,
      direction: PolarAlignDirections.east,
      stepDeg: 10,
      camera: PolarAlignCameras.main,
    ));
    await _open(tester, rig, stage: PolarAlignStages.coarse);
    expect(find.text('This measurement uses the main camera.'), findsOneWidget);
    expect(find.text('RA will turn about 10° twice toward the east (20° in all).'), findsOneWidget);
    await tester.tap(find.byKey(const Key('polar-start-confirm-cancel')));
    await tester.pumpAndSettle();

    final guide = _Rig(const PolarAlignStartPlan(stage: PolarAlignStages.fine, direction: PolarAlignDirections.east));
    await _open(tester, guide, stage: PolarAlignStages.fine);
    expect(find.text('This measurement uses the main camera.'), findsNothing);
  });

  testWidgets('a later start says how far RA has turned from where Polar Align started, and turns back (#2089)', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.fine,
      direction: PolarAlignDirections.west,
      newOrigin: false,
      originOffsetDeg: 20.2,
    ));
    await _open(tester, rig, stage: PolarAlignStages.fine);
    expect(find.text('RA is 20° east of where Polar Align started; these turns go back toward it.'), findsOneWidget);
    expect(find.textContaining("new start"), findsNothing);

    await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
    await tester.pumpAndSettle();
    expect(rig.started.single.newOrigin, isFalse, reason: 'the start says the user confirmed a continued start');
  });

  testWidgets('within 2° of the origin, turns that go away from it are not called a turn back (#2089)', (tester) async {
    // The rig turns the origin's first way within its 2° band: here 1.5° west of the origin, turning west again.
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.fine,
      direction: PolarAlignDirections.west,
      newOrigin: false,
      originOffsetDeg: -1.5,
    ));
    await _open(tester, rig, stage: PolarAlignStages.fine);
    expect(find.text('RA will turn about 10° twice toward the west (20° in all).'), findsOneWidget);
    expect(find.textContaining('go back toward it'), findsNothing);
    expect(find.textContaining("new start"), findsNothing);
  });

  testWidgets("a new start says the pose becomes Polar Align's start, and the limit (#2089)", (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(direction: PolarAlignDirections.east, sweepLimitDeg: 30));
    await _open(tester, rig);
    expect(
      find.text("This pose is Polar Align's new start: until the mount is moved some other way, RA stays within 30° of it."),
      findsOneWidget,
    );
    expect(find.textContaining('of where Polar Align started'), findsNothing);
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

  testWidgets("the confirm step gives NINA's guidance on the start field (#2083)", (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(direction: PolarAlignDirections.east));
    await _open(tester, rig);
    expect(
      find.text('Pick a field fairly high toward the pole for your hemisphere, at least about 15° above the horizon when you can.'),
      findsOneWidget,
    );
    expect(find.text('Choose a start whose turns do not cross the meridian, so the tube cannot swing into the pier.'), findsOneWidget);
  });

  testWidgets('Turn RA by hand starts a run that asks the rig to move nothing, with the direction shown (#2083)', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.fine,
      direction: PolarAlignDirections.west,
      handTurnAvailable: true,
    ));
    await _open(tester, rig, stage: PolarAlignStages.fine);
    expect(find.textContaining('with Turn RA by hand, Ara moves nothing'), findsOneWidget);

    await tester.tap(find.byKey(const Key('polar-start-confirm-hand')));
    await tester.pumpAndSettle();
    expect(rig.started.single.handTurn, isTrue);
    expect(rig.started.single.direction, PolarAlignDirections.west, reason: 'the turns the user makes go the way shown');
    expect(rig.started.single.stage, PolarAlignStages.fine);
  });

  testWidgets('an older rig, which would turn the mount with its motor, is offered no hand turn (#2083)', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(direction: PolarAlignDirections.east));
    await _open(tester, rig);
    expect(find.byKey(const Key('polar-start-confirm-hand')), findsNothing);
    expect(find.textContaining('Turn RA by hand'), findsNothing);

    await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
    await tester.pumpAndSettle();
    expect(rig.started.single.handTurn, isFalse, reason: 'Move the mount is a motor run');
  });

  testWidgets('the confirm step reads in Brazilian Portuguese', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(direction: PolarAlignDirections.east, stepDeg: 10, maxSweepDeg: 20));
    await _open(tester, rig, locale: const Locale('pt', 'BR'));
    expect(find.text('Antes de a montagem se mover'), findsOneWidget);
    expect(find.text('A AR vai girar cerca de 10° duas vezes em direção ao leste (20° no total).'), findsOneWidget);
    expect(find.text('Mover a montagem'), findsOneWidget);
  });

  testWidgets('the confirm step shows the motor-direction test first, and the start confirms it (#2081)', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.fine,
      direction: PolarAlignDirections.east,
      stepDeg: 10,
      directionTestDeg: 0.5,
    ));
    await _open(tester, rig, stage: PolarAlignStages.fine);

    expect(
      find.text('First it turns the mount 0.5° to learn the motor direction (not tested yet for this mount and driver).'),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const Key('polar-start-confirm-go')));
    await tester.pumpAndSettle();
    expect(rig.started.single.directionTestDeg, 0.5, reason: 'the start carries the test the user saw');
  });

  testWidgets('the motor-direction test distance uses the language decimal comma in pt-BR (#2081)', (tester) async {
    final rig = _Rig(const PolarAlignStartPlan(
      stage: PolarAlignStages.fine,
      direction: PolarAlignDirections.east,
      stepDeg: 10,
      directionTestDeg: 0.5,
    ));
    await _open(tester, rig, stage: PolarAlignStages.fine, locale: const Locale('pt', 'BR'));
    expect(find.textContaining('Primeiro ela gira a montagem 0,5°'), findsOneWidget);
  });
}
