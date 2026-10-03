import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

/// Pure fake — records calls; scripted status/settings.
class _FakePolarAlignClient implements PolarAlignClient {
  final calls = <String>[];
  PolarAlignStatus? status;
  PolarAlignSettings settings = const PolarAlignSettings();

  @override
  Future<PolarAlignStatus?> getStatus() async => status;
  @override
  Future<void> start() async => calls.add('start');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> complete() async => calls.add('complete');
  @override
  Future<void> requestCapture() async => calls.add('capture');
  Uint8List? frame;
  @override
  Future<Uint8List?> getLiveFrame() async {
    calls.add('frame');
    return frame;
  }
  @override
  Future<PolarAlignSettings> getSettings() async => settings;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings s) async {
    calls.add('put');
    return settings = s;
  }
  @override
  void close() {}
}

/// Overridable live-state stub: exposes a setter, never touches the WS stream.
class _StubLiveNotifier extends PolarAlignLiveNotifier {
  final PolarAlignLive initial;
  _StubLiveNotifier(this.initial);
  @override
  PolarAlignLive build() => initial;
}

Widget _harness(_FakePolarAlignClient api, PolarAlignLive live) {
  return ProviderScope(
    overrides: [
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _StubLiveNotifier(live)),
    ],
    child: const MaterialApp(
      home: Scaffold(body: SingleChildScrollView(child: PolarAlignPanel())),
    ),
  );
}

Future<void> _expand(WidgetTester tester) async {
  await tester.tap(find.text('Polar Align'));
  await tester.pumpAndSettle();
}

void main() {
  group('bullseye pure helpers', () {
    test('range zooms in as the error shrinks', () {
      expect(bullseyeRangeArcmin(null), 300.0);
      expect(bullseyeRangeArcmin(120), 300.0);
      expect(bullseyeRangeArcmin(59.9), 30.0);
      expect(bullseyeRangeArcmin(5.0), 30.0);
      expect(bullseyeRangeArcmin(4.9), 5.0);
      expect(bullseyeRangeArcmin(0.2), 5.0);
    });

    test('zone colors follow the §45.10 thresholds', () {
      expect(zoneColor(90), AraColors.accentError);
      expect(zoneColor(60), AraColors.accentError);
      expect(zoneColor(59.9), AraColors.accentBusy);
      expect(zoneColor(10), AraColors.accentBusy);
      expect(zoneColor(9.9), AraColors.accentConnected);
      expect(zoneColor(null), AraColors.textSecondary);
    });

    test('dot fraction maps az right / alt up and clamps off-scale errors', () {
      final inRange = bullseyeDotFraction(15, -15, 30);
      expect(inRange.dx, closeTo(0.5, 1e-9));
      expect(inRange.dy, closeTo(0.5, 1e-9),
          reason: 'negative alt (axis below the pole) draws below center — positive canvas y');
      final clamped = bullseyeDotFraction(300, 400, 30);
      expect(clamped.distance, closeTo(1.0, 1e-9));
    });

    test('RA/Dec render as sexagesimal', () {
      expect(formatRaHms(138.6667), '09h14m40s');
      expect(formatRaHms(359.9999), '00h00m00s');
      expect(formatDecDms(87.1822), '+87°10′56″');
      expect(formatDecDms(-5.5), '−05°30′00″');
    });

    test('formatArcmin renders signed arcminutes', () {
      expect(formatArcmin(14.23), '+14.2′');
      expect(formatArcmin(-23.41), '−23.4′');
      expect(formatArcmin(null), '—');
    });
  });

  group('PolarAlignPanel', () {
    testWidgets('the live readout is always visible (no collapse)',
        (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        azErrorArcmin: 14.2,
        altErrorArcmin: -6.5,
        totalErrorArcmin: 15.6,
      )));
      await tester.pumpAndSettle();
      // No tap to expand: the bullseye + Az/Alt/Total readout are on the page.
      expect(find.byKey(const Key('polar-align-readout')), findsOneWidget);
      expect(find.textContaining('Az: +14.2′'), findsOneWidget);
      expect(find.textContaining('Alt: −6.5′'), findsOneWidget);
      expect(find.textContaining('Total: +15.6′'), findsOneWidget);
      expect(find.byType(CustomPaint), findsWidgets); // the bullseye
    });


    testWidgets('idle shows Start and posts start', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive()));
      await tester.pumpAndSettle();
      await _expand(tester);

      final start = find.byKey(const Key('polar-align-start'));
      expect(start, findsOneWidget);
      await tester.tap(start);
      await tester.pumpAndSettle();
      expect(api.calls, ['start']);
    });

    testWidgets('adjusting shows the readout and gates Done on tolerance', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            iteration: 5,
            altErrorArcmin: 14.2,
            azErrorArcmin: -23.4,
            totalErrorArcmin: 27.3,
            zone: 'yellow',
          )));
      await tester.pumpAndSettle();
      await _expand(tester);

      expect(find.byKey(const Key('polar-align-readout')), findsOneWidget);
      expect(find.textContaining('Az: −23.4′'), findsOneWidget);
      final done = tester.widget<FilledButton>(find.byKey(const Key('polar-align-done')));
      expect(done.onPressed, isNull, reason: '27.3′ is outside the 1′ tolerance — Done disabled');

      await tester.ensureVisible(find.byKey(const Key('polar-align-abort')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-align-abort')));
      await tester.pumpAndSettle();
      expect(api.calls, ['stop']);
    });

    testWidgets('in-tolerance Done posts complete', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            altErrorArcmin: 0.3,
            azErrorArcmin: 0.4,
            totalErrorArcmin: 0.5,
            zone: 'green',
          )));
      await tester.pumpAndSettle();
      await _expand(tester);

      await tester.ensureVisible(find.byKey(const Key('polar-align-done')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-align-done')));
      await tester.pumpAndSettle();
      expect(api.calls, ['complete']);
    });

    testWidgets('single mode shows Take Frame and posts a capture request', (tester) async {
      final api = _FakePolarAlignClient()
        ..settings = const PolarAlignSettings(loopMode: PolarAlignLoopModes.single);
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            totalErrorArcmin: 30,
          )));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.byKey(const Key('polar-align-take-frame')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-align-take-frame')));
      await tester.pumpAndSettle();
      expect(api.calls, ['capture']);
    });

    testWidgets('loop mode has no Take Frame; switching to Single saves it', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 30)));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-take-frame')), findsNothing);

      await tester.ensureVisible(find.text('Single'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Single'));
      await tester.pumpAndSettle();
      expect(api.calls, ['put']);
      expect(api.settings.loopMode, PolarAlignLoopModes.single);
      expect(find.byKey(const Key('polar-align-take-frame')), findsOneWidget);
    });

    testWidgets('a new exposure is saved; an invalid one is refused', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive()));
      await tester.pumpAndSettle();

      await tester.enterText(find.byKey(const Key('polar-align-exposure')), '2.5');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.settings.exposureSeconds, 2.5);

      await tester.enterText(find.byKey(const Key('polar-align-exposure')), '0');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.settings.exposureSeconds, 2.5);
      expect(find.textContaining('Exposure must be'), findsOneWidget);
    });

    testWidgets('shows the last frame timings and solved pointing', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            totalErrorArcmin: 30,
            lastFrame: PolarAlignFrameInfo(
              frameId: 'live-4',
              solved: true,
              exposureSeconds: 1,
              captureMs: 1620,
              solveMs: 480,
              raDeg: 138.6667,
              decDeg: 87.1822,
            ),
          )));
      await tester.pumpAndSettle();
      expect(find.text('Last frame: 1.0 s exposure · capture 1.6 s · solve 0.5 s'), findsOneWidget);
      expect(find.text('Solved: RA 09h14m40s  Dec +87°10′56″'), findsOneWidget);
    });

    testWidgets('an active routine shows the guide camera live view', (tester) async {
      // 1x1 PNG — any image format Image.memory decodes works for the view.
      const png = <int>[
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
      ];
      final api = _FakePolarAlignClient()
        ..status = const PolarAlignStatus(state: PolarAlignStates.adjusting, currentErrorArcmin: 30)
        ..frame = Uint8List.fromList(png);
      await tester.pumpWidget(_harness(
          api, const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 30)));
      await tester.pumpAndSettle();

      expect(api.calls, contains('frame'));
      expect(find.byKey(const Key('polar-align-live-view')), findsOneWidget);
      // The other controls stay alongside it.
      expect(find.byKey(const Key('polar-align-exposure')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-mode')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-readout')), findsOneWidget);
    });

    testWidgets('paused shows the no-solve banner', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.paused,
            totalErrorArcmin: 12,
            consecutiveSolveFailures: 5,
          )));
      await tester.pumpAndSettle();
      await _expand(tester);
      expect(find.byKey(const Key('polar-align-paused-banner')), findsOneWidget);
    });

    testWidgets('failed shows the error banner with the reason', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.failed,
            errorReason: 'seed_solve_failed',
            errorMessage: 'check focus',
          )));
      await tester.pumpAndSettle();
      await _expand(tester);
      expect(find.byKey(const Key('polar-align-error-banner')), findsOneWidget);
      expect(find.textContaining('seed_solve_failed'), findsOneWidget);
    });
  });
}
