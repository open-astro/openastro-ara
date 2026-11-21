import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/imaging_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_motor_direction_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart'
    show SectionNotLoadedException;
import 'package:openastroara/state/settings/migrated_sections.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';

import '../../../support/polar_align_section.dart';

Future<FakePolarAlignSection> _pump(
  WidgetTester tester, {
  FakePolarAlignSection? rig,
  Locale? locale,
}) async {
  final section = rig ?? FakePolarAlignSection();
  await tester.pumpWidget(
    ProviderScope(
      overrides: polarAlignSectionOverrides(section),
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(body: ImagingPolarPanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return section;
}

/// #2081 the rig's saved RA motor direction, with no HTTP.
class _DirectionRig implements PolarAlignMotorDirectionClient {
  _DirectionRig(this.state);
  PolarAlignMotorDirectionState? state;
  int retests = 0;

  @override
  Future<PolarAlignMotorDirectionState?> get() async => state;

  @override
  Future<void> retest() async {
    retests++;
    state = PolarAlignMotorDirectionState(
      matchesConnectedMount: state?.matchesConnectedMount == null ? null : false,
    );
  }

  @override
  void close() {}
}

Future<FakePolarAlignSection> _pumpWithDirection(
  WidgetTester tester,
  _DirectionRig direction, {
  FakePolarAlignSection? rig,
}) async {
  // Tall enough that the whole panel is built (the list is lazy).
  tester.view.physicalSize = const Size(1200, 3200);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final section = rig ?? FakePolarAlignSection();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(section),
        activeServerProvider.overrideWithValue(const AraServer(hostname: 'rig', port: 1)),
        polarAlignMotorDirectionApiFactoryProvider.overrideWithValue((_) => direction),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(body: ImagingPolarPanel()),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return section;
}

void main() {
  testWidgets('there is no Home switch: polar align never sends the mount Home (#2048)', (
    tester,
  ) async {
    await _pump(tester);
    expect(find.textContaining('Home'), findsNothing);
    expect(find.byType(Switch), findsNWidgets(2), reason: 'the Polar Aligner and stop-tracking switches only');
  });

  testWidgets(
    'the turn, verify step, rough exposure and tolerance each save their own field (#1736)',
    (tester) async {
      final api = await _pump(tester);
      final fields = find.byType(TextField);
      expect(fields, findsNWidgets(5), reason: 'and frames per point (#2082)');
      // Row order: tolerance, turn, verify step, rough exposure.
      final entered = ['2', '25', '20', '7.5'];
      for (var i = 0; i < 4; i++) {
        await tester.enterText(fields.at(i), entered[i]);
        await tester.testTextInput.receiveAction(TextInputAction.done);
        await tester.pumpAndSettle();
      }
      final last = api.puts.last;
      expect(last.targetToleranceArcmin, 2.0);
      expect(last.seedRotationDeg, 25.0);
      expect(last.checkStepDeg, 20.0);
      expect(last.coarseExposureSeconds, 7.5);
    },
  );

  testWidgets(
    'a failed load shows not loaded with Retry, and nothing can be saved (#1736)',
    (tester) async {
      final rig = FakePolarAlignSection(initial: null);
      await _pump(tester, rig: rig);
      expect(find.byKey(const Key('section-retry')), findsOneWidget);
      expect(find.byType(TextField), findsNothing);
      expect(find.byType(Switch), findsNothing);
      expect(rig.puts, isEmpty);

      rig.initial = rigPolarAlign;
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.byType(TextField), findsNWidgets(5));
    },
  );

  testWidgets(
    'the camera saves main, and frames per point saves a whole number from 1 to 5 (#2082)',
    (tester) async {
      final rig = await _pump(tester);
      await tester.tap(find.byKey(const Key('polar-knob-camera')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Main camera').last);
      await tester.pumpAndSettle();
      expect(rig.puts.single.camera, PolarAlignCameras.main);

      final frames = find.descendant(
        of: find.byKey(const Key('polar-knob-frames-per-point')),
        matching: find.byType(TextField),
      );
      for (final refused in ['6', '0', '2.5']) {
        await tester.enterText(frames, refused);
        await tester.testTextInput.receiveAction(TextInputAction.done);
        await tester.pumpAndSettle();
      }
      expect(rig.puts, hasLength(1), reason: 'out of range or not whole: nothing sent');
      await tester.enterText(frames, '5');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect((rig.puts.last.framesPerPoint, rig.puts.last.camera), (5, PolarAlignCameras.main),
          reason: 'saved over the rig copy the camera change left');
    },
  );

  testWidgets(
    'a turn of 200 or a tolerance of 50 is refused: no PUT, the field keeps the saved value (#1736)',
    (tester) async {
      final api = await _pump(tester);
      final fields = find.byType(TextField);
      // Row order: tolerance, turn, verify step, rough exposure.
      await tester.enterText(fields.at(1), '200');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      await tester.enterText(fields.at(0), '50');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.puts, isEmpty);
      expect(tester.widget<TextField>(fields.at(1)).controller!.text, '10');
      expect(tester.widget<TextField>(fields.at(0)).controller!.text, '1');
      expect(find.textContaining('Turn (°, 5 to 30)'), findsOneWidget);
    },
  );

  testWidgets(
    'in pt-BR the section and every knob label are translated (#1736)',
    (tester) async {
      await _pump(tester, locale: const Locale('pt', 'BR'));
      for (final english in [
        'Polar align',
        'Tolerance',
        'Turn',
        'Check step',
        'Rough step exposure',
        'Stop tracking when polar align ends',
        'Frames per point',
        'Guide camera',
      ]) {
        expect(find.textContaining(english), findsNothing, reason: english);
      }
      expect(find.text('Alinhamento polar'), findsOneWidget);
    },
  );

  testWidgets(
    'the Polar Aligner switch shows the profile value and saves polar_aligner_auto, keeping the other fields (#1971)',
    (tester) async {
      final rig = FakePolarAlignSection(
        initial: rigPolarAlign.copyWith(exposureSeconds: 4, checkStepDeg: 15, polarAlignerAuto: false),
      );
      await _pump(tester, rig: rig);
      expect(find.text("Correct with the Polar Aligner's motors"), findsOneWidget);
      expect(find.text('Off: Polar align shows the turns and you make them'), findsOneWidget);
      final aligner = find.byType(Switch).at(0);
      expect(tester.widget<Switch>(aligner).value, isFalse);
      await tester.tap(aligner);
      await tester.pumpAndSettle();
      final saved = rig.puts.single;
      expect(saved.polarAlignerAuto, isTrue);
      expect((saved.exposureSeconds, saved.checkStepDeg), (4.0, 15.0), reason: 'saved over the loaded section');
      expect(
        find.text('On a rig with a Polar Aligner, Polar align turns its motors after each measurement'),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'the stop-tracking switch shows the profile value and saves stop_tracking_when_done, keeping the other fields (#2048)',
    (tester) async {
      final rig = FakePolarAlignSection(initial: rigPolarAlign.copyWith(exposureSeconds: 4, checkStepDeg: 15));
      await _pump(tester, rig: rig);
      expect(find.text('Stop tracking when polar align ends'), findsOneWidget);
      expect(find.text('Every end of a run turns tracking off'), findsOneWidget);
      final stopTracking = find.byType(Switch).at(1);
      expect(tester.widget<Switch>(stopTracking).value, isTrue);
      await tester.tap(stopTracking);
      await tester.pumpAndSettle();
      final saved = rig.puts.single;
      expect(saved.stopTrackingWhenDone, isFalse);
      expect((saved.exposureSeconds, saved.checkStepDeg), (4.0, 15.0), reason: 'saved over the loaded section');
      expect(
        find.text('Off: a run that finishes gives back the tracking it found. A Stop always leaves tracking off'),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'Polar align: a load that fails after a rig change shows not loaded, and nothing is sent (ADR 0015)',
    (tester) async {
      final rig = await _pump(tester);
      expect(find.byType(Switch), findsNWidgets(2));
      // A different rig or profile: every section back to not loaded, and
      // this one's next load fails.
      rig.initial = null;
      final container = ProviderScope.containerOf(
        tester.element(find.byType(ImagingPolarPanel)),
      );
      resetMigratedSections(container.read);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('section-retry')), findsOneWidget);
      expect(find.byType(Switch), findsNothing, reason: 'no default shows');
      expect(find.byType(TextField), findsNothing);
      // A save from here on (the Imaging tab, Simple Mode) loads once more,
      // fails, and is refused with nothing sent.
      await expectLater(
        container
            .read(polarAlignSettingsProvider.notifier)
            .saveKnob((s) => s.copyWith(exposureSeconds: 5)),
        throwsA(isA<SectionNotLoadedException>()),
      );
      expect(rig.sent, isEmpty);
      expect(container.read(polarAlignSettingsProvider).value, isNull);
    },
  );

  testWidgets(
    'Polar align: a switch refused as stale keeps the change and offers Reload (ADR 0015)',
    (tester) async {
      final rig = await _pump(tester);
      rig.refuse = DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/polar-align'),
        response: Response(
          requestOptions: RequestOptions(path: '/api/v1/profile/polar-align'),
          statusCode: 412,
        ),
      );
      final stopTracking = find.byType(Switch).at(1);
      await tester.tap(stopTracking);
      await tester.pumpAndSettle();
      expect(rig.puts, isEmpty);
      expect(tester.widget<Switch>(find.byType(Switch).at(1)).value, isFalse, reason: 'the change stays on screen');
      expect(find.byKey(const Key('section-reload')), findsOneWidget);

      rig.refuse = null;
      await tester.tap(find.byKey(const Key('section-reload')));
      await tester.pumpAndSettle();
      expect(tester.widget<Switch>(find.byType(Switch).at(1)).value, isTrue, reason: "the rig's copy again");
      expect(find.byKey(const Key('section-reload')), findsNothing);
    },
  );

  testWidgets(
    'the turn direction saves east, west or automatic, keeping the other fields (#2081)',
    (tester) async {
      final rig = await _pumpWithDirection(
        tester,
        _DirectionRig(null),
        rig: FakePolarAlignSection(initial: rigPolarAlign.copyWith(checkStepDeg: 15)),
      );
      expect(find.text('Automatic'), findsOneWidget);
      await tester.tap(find.byKey(const Key('polar-knob-east-direction')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('West').last);
      await tester.pumpAndSettle();
      expect((rig.puts.last.eastDirection, rig.puts.last.checkStepDeg), (false, 15.0));
      expect(rig.puts.last.toJson()['east_direction'], false);

      await tester.tap(find.byKey(const Key('polar-knob-east-direction')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Automatic').last);
      await tester.pumpAndSettle();
      expect(rig.puts, hasLength(2));
      expect(rig.puts.last.eastDirection, isNull, reason: 'automatic is null on the wire, the hemisphere picks');
      expect(rig.puts.last.toJson().containsKey('east_direction'), isTrue, reason: 'sent, so the rig clears it');
    },
  );

  testWidgets(
    'the saved RA motor direction shows with its driver, and Test direction again forgets it (#2081)',
    (tester) async {
      final direction = _DirectionRig(
        const PolarAlignMotorDirectionState(
          saved: PolarAlignMotorDirection(sign: -1, driverInfo: 'AlpacaBridge iOptron', driverVersion: '6.0.0'),
          matchesConnectedMount: true,
        ),
      );
      await _pumpWithDirection(tester, direction);
      expect(
        find.text('Tested on AlpacaBridge iOptron 6.0.0: the motor\'s positive speed turns RA west.'),
        findsOneWidget,
      );

      await tester.tap(find.byKey(const Key('polar-motor-direction-retest')));
      await tester.pumpAndSettle();
      expect(direction.retests, 1);
      expect(find.text('Not tested yet: the next start first turns the mount 0.5° to learn it.'), findsOneWidget);
      expect(find.text('The next start tests the motor direction again.'), findsOneWidget);
      final retest = tester.widget<ButtonStyleButton>(find.byKey(const Key('polar-motor-direction-retest')));
      expect(retest.onPressed, isNull, reason: 'nothing saved to forget');
    },
  );

  testWidgets(
    'a saved direction for another driver says the next start tests again (#2081)',
    (tester) async {
      await _pumpWithDirection(
        tester,
        _DirectionRig(
          const PolarAlignMotorDirectionState(
            saved: PolarAlignMotorDirection(sign: 1, driverInfo: 'AlpacaBridge iOptron', driverVersion: '5.0.0'),
            matchesConnectedMount: false,
          ),
        ),
      );
      expect(
        find.text('Tested on another driver (AlpacaBridge iOptron 5.0.0): the next start tests it again with a 0.5° turn.'),
        findsOneWidget,
      );
    },
  );
}
