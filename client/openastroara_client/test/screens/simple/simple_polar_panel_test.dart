import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_night_setup.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

class _Api implements PolarAlignClient {
  final calls = <String>[];
  PolarAlignSettings settings = const PolarAlignSettings(targetToleranceArcmin: 1.0);
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async => settings;
  @override
  Future<void> start() async => calls.add('start');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> complete() async => calls.add('complete');
  @override
  Future<void> requestCapture() async => calls.add('capture');
  @override
  Future<Uint8List?> getLiveFrame() async {
    calls.add('frame');
    return null;
  }
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings next) async {
    calls.add('save ${next.exposureSeconds}');
    return settings = next;
  }
  @override
  void close() {}
}

class _FailingApi extends _Api {
  @override
  Future<void> start() async => throw StateError('no camera');
}

class _SimplePrefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

class _Live extends PolarAlignLiveNotifier {
  _Live(this.live);
  final PolarAlignLive live;
  @override
  PolarAlignLive build() => live;
  void frame(PolarAlignLive next) => state = next;
}

_Live _liveOf(WidgetTester tester) =>
    ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel))).read(polarAlignLiveProvider.notifier) as _Live;

const _adjusting = PolarAlignLive(
  phase: PolarAlignStates.adjusting,
  altErrorArcmin: 3.2,
  azErrorArcmin: 14.1,
  totalErrorArcmin: 14.4,
  zone: 'yellow',
);

Future<_Api?> _pump(WidgetTester tester, PolarAlignLive live,
    {bool server = true, Widget child = const SimplePolarPanel()}) async {
  final api = server ? (_Api()..settings = const PolarAlignSettings()) : null;
  await tester.pumpWidget(ProviderScope(
    overrides: [
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _Live(live)),
    ],
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  test('simplePolarSummary: the settings, tight enough for the 60 px chip', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(simplePolarSummary(const PolarAlignSettings(), en), '1 s');
    expect(simplePolarSummary(const PolarAlignSettings(exposureSeconds: 45), en), '45 s');
  });

  test('simplePolarPhase names each phase', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(simplePolarPhase(const PolarAlignLive(), en), 'Ready');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.seeding), en), 'Finding the pole…');
    expect(simplePolarPhase(_adjusting, en), 'Adjusting');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.failed), en), 'Polar alignment failed');
  });

  testWidgets('idle: only the settings, no Ready and no Start (the shutter starts it)', (tester) async {
    final api = await _pump(tester, const PolarAlignLive());
    expect(find.text('Ready'), findsNothing);
    expect(find.text('Start'), findsNothing);
    expect(find.text('1 s'), findsOneWidget);
    expect(find.text('Single'), findsNothing, reason: 'the guider loops its own frames (#1611)');
    expect(api!.calls, isEmpty);
  });

  testWidgets('adjusting: the error, the turns, no Stop (the shutter stops it); Done waits for the tolerance', (tester) async {
    final api = await _pump(tester, _adjusting);
    expect(find.text('14.4′'), findsOneWidget);
    expect(find.text('Lower 3.2′'), findsOneWidget);
    expect(find.text('Move west 14.1′'), findsOneWidget);
    expect(find.text('1 s'), findsOneWidget, reason: 'the settings stay, for the next frame');
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-polar-done'))).onPressed, isNull);
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-polar-done'))).style?.backgroundColor?.resolve({}),
        AraColors.accentConnected, reason: 'Done is green');
    expect(find.byKey(const Key('simple-polar-stop')), findsNothing);
    expect(find.byKey(const Key('simple-polar-capture')), findsNothing, reason: 'Loop shoots on its own');
    expect(api!.calls, isEmpty);
  });

  testWidgets('the turns read at a glance: big arrows beside a big bullseye, like the full UI', (tester) async {
    await _pump(tester, _adjusting);
    for (final axis in ['alt', 'az']) {
      final arrow = find.descendant(of: find.byKey(Key('simple-polar-turn-$axis')), matching: find.byType(Icon));
      expect(tester.widget<Icon>(arrow).size, greaterThanOrEqualTo(40), reason: axis);
    }
    final bullseye = find.byKey(const Key('simple-polar-bullseye'));
    expect(tester.getSize(bullseye).width, greaterThanOrEqualTo(180));
  });

  testWidgets('while it runs, the whole panel fits the room beside the rail on an iPad (388 pt)', (tester) async {
    await _pump(tester, _adjusting);
    expect(tester.getSize(find.byType(SimplePolarPanel)).height, lessThanOrEqualTo(388));
  });

  testWidgets('running: no Take frame (the guider reads every frame, #1611)', (tester) async {
    await _pump(tester, _adjusting);
    expect(find.byKey(const Key('simple-polar-capture')), findsNothing);
  });

  testWidgets('inside the tolerance Done completes it', (tester) async {
    final api = await _pump(
      tester,
      const PolarAlignLive(phase: PolarAlignStates.adjusting, altErrorArcmin: 0.3, azErrorArcmin: -0.5, totalErrorArcmin: 0.6),
    );
    await tester.tap(find.byKey(const Key('simple-polar-done')));
    await tester.pump();
    expect(api!.calls, ['complete']);
  });

  testWidgets('in Night Setup, polar align is the last step: Done reads Done · Start imaging', (tester) async {
    final api = _Api()..settings = PolarAlignSettings();
    final c = ProviderContainer(overrides: [
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive(
          phase: PolarAlignStates.adjusting, altErrorArcmin: 0.3, azErrorArcmin: -0.5, totalErrorArcmin: 0.6))),
    ]);
    addTearDown(c.dispose);
    c.read(nightSetupProvider.notifier).back(); // no-op outside Night Setup
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarPanel()))),
    ));
    await tester.pumpAndSettle();
    expect(find.text('Done'), findsOneWidget, reason: 'outside Night Setup');
    c.read(nightSetupProvider.notifier).start();
    c.read(nightSetupProvider.notifier).next(); // past the plan, to Focus
    c.read(nightSetupProvider.notifier).next(); // past Focus, to polar align
    await tester.pumpAndSettle();
    expect(c.read(nightSetupProvider), NightSetupStep.polarAlign);
    expect(find.text('Done · Start imaging'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-polar-done')));
    await tester.pumpAndSettle();
    expect(api.calls.last, 'complete');
  });

  testWidgets('a bolt turned the wrong way gets the full UI\'s nudge', (tester) async {
    PolarAlignLive at(int i, double alt, double az) => PolarAlignLive(
        phase: PolarAlignStates.adjusting, iteration: i, altErrorArcmin: alt, azErrorArcmin: az, totalErrorArcmin: 10);
    // The iPad, as the shell tests.
    tester.view.physicalSize = const Size(1024, 768);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await _pump(tester, at(1, 10, -4));
    final live = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)))
        .read(polarAlignLiveProvider.notifier) as _Live;
    expect(find.textContaining('got worse'), findsNothing);
    live.frame(at(2, 13, -4.2));
    await tester.pumpAndSettle();
    expect(find.text('Altitude got worse. Turn that bolt back past where you started.'), findsOneWidget);
  });

  testWidgets('each live readout refetches the guide camera frame (#1611: no per-frame event)', (tester) async {
    // The shell's image reads simplePolarFrameProvider; hold it the way the shell does.
    final api = await _pump(tester, _adjusting);
    final container = ProviderScope.containerOf(tester.element(find.byType(SimplePolarPanel)));
    final sub = container.listen(simplePolarFrameProvider, (_, __) {});
    addTearDown(sub.close);
    await tester.pumpAndSettle();
    int frames() => api!.calls.where((c) => c == 'frame').length;
    final before = frames();
    final live = _liveOf(tester);
    live.frame(_adjusting.copyWith(iteration: _adjusting.iteration + 1));
    await tester.pumpAndSettle();
    expect(frames(), before + 1, reason: 'a new iteration is a new guider picture');
    live.frame(_adjusting.copyWith(iteration: _adjusting.iteration + 1, altErrorArcmin: 1.0));
    await tester.pumpAndSettle();
    expect(frames(), before + 1, reason: 'the same iteration is the same picture');
  });

  testWidgets('a lost star says so while paused', (tester) async {
    await _pump(tester, _adjusting.copyWith(phase: PolarAlignStates.paused));
    expect(find.text('The guider cannot see the star. Check the sky.'), findsOneWidget);
  });

  testWidgets('a failed routine shows why, and Start again', (tester) async {
    await _pump(tester, const PolarAlignLive(phase: PolarAlignStates.failed, errorMessage: 'The mount would not rotate.'));
    expect(find.text('The mount would not rotate.'), findsOneWidget);
  });

  group('the shutter runs the routine', () {
    Future<_Api?> shutter(WidgetTester tester, PolarAlignLive live, {bool server = true}) =>
        _pump(tester, live, server: server, child: const SimplePolarShutter());

    testWidgets('a start that fails says so in a pill', (tester) async {
      final api = _FailingApi();
      final c = ProviderContainer(overrides: [
        polarAlignApiProvider.overrideWithValue(api),
        polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
        simpleModePrefsProvider.overrideWithValue(_SimplePrefs()),
        // Simple Mode on: the Experimental switch too, or the full UI shows (snackbars).
        simpleModeFeaturePrefsProvider.overrideWithValue(_SimplePrefs()),
      ]);
      addTearDown(c.dispose);
      await c.read(simpleModeProvider.future);
      await c.read(simpleModeFeatureProvider.future);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarShutter()))),
      ));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
      expect(find.byType(SnackBar), findsNothing);
    });

    testWidgets('idle: starts it', (tester) async {
      final api = await shutter(tester, const PolarAlignLive());
      expect(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byIcon(Icons.play_arrow)),
          findsOneWidget, reason: 'a routine starts with play');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, ['start']);
    });
    testWidgets('Loop running: the square stops it', (tester) async {
      final api = await shutter(tester, _adjusting);
      expect(find.bySemanticsLabel('Stop'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, ['stop']);
    });
    testWidgets('the stop square is red, the start disc white', (tester) async {
      Color fill() => tester
          .widgetList<Container>(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byType(Container)))
          .map((c) => (c.decoration as BoxDecoration?)?.color)
          .whereType<Color>()
          .single;
      await shutter(tester, _adjusting);
      expect(fill(), AraColors.accentError);
    });
    testWidgets('idle: the start triangle is white', (tester) async {
      await shutter(tester, const PolarAlignLive());
      expect(
          tester
              .widget<Icon>(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byType(Icon)))
              .color,
          AraColors.textPrimary);
    });
    testWidgets('the main camera busy: off, and a tap says why (user, 2026-11-04: it turns the mount)', (tester) async {
      final api = await shutter(tester, const PolarAlignLive());
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, isEmpty, reason: 'no start under a frame');
      expect(c.read(simpleMessagesProvider).single.title, 'The main camera is busy: wait for its frame, or stop it.');
      c.read(captureProgressProvider.notifier).reset();
      await tester.pump(const Duration(seconds: 11));
    });

    testWidgets('no server: off, and a tap says why (user, 2026-11-04)', (tester) async {
      await shutter(tester, const PolarAlignLive(), server: false);
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(c.read(simpleMessagesProvider).single.title, 'Rig not connected');
      await tester.pump(const Duration(seconds: 11));
    });
  });
}
