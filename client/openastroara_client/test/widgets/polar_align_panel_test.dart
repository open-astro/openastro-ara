import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/night_mode_state.dart';
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
    if (putError != null) throw putError!;
    return settings = s;
  }
  Object? putError;
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

class _NightOn extends NightModeController {
  @override
  Future<bool> build() async => true;
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

    test('pole offset reads in arcseconds under 10′, then arcminutes, then degrees', () {
      expect(formatPoleOffset(0.8), ('48″', 'arcseconds from the pole'));
      expect(formatPoleOffset(-9.9), ('594″', 'arcseconds from the pole'));
      expect(formatPoleOffset(24.4), ('24′', 'arcminutes from the pole'));
      expect(formatPoleOffset(90), ('1.5°', 'degrees from the pole'));
    });

    test('worst-case drift is ~0.26″ per minute per arcminute of error', () {
      expect(maxDriftArcsec(1, 60), closeTo(0.2625, 1e-3));
      expect(maxDriftArcsec(-0.8, 300), closeTo(1.05, 1e-2));
    });

    test('rating tiers and Moon comparison', () {
      expect(polarErrorRating(0.8).$1, 'Excellent');
      expect(polarErrorRating(2.5).$1, 'Very good');
      expect(polarErrorRating(10).$1, 'Good');
      expect(polarErrorRating(30).$1, 'Rough');
      expect(polarErrorRating(31).$1, 'Far off');
      expect(moonWidthComparison(0.8), "1/39 of the Moon's width");
      expect(moonWidthComparison(46.5), "1.5× the Moon's width");
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
      final readout = find.byKey(const Key('polar-align-readout'));
      expect(readout, findsOneWidget);
      Finder inReadout(String text) => find.descendant(of: readout, matching: find.text(text));
      expect(inReadout('+15.6′'), findsOneWidget, reason: 'total error');
      expect(inReadout('−6.5′'), findsOneWidget, reason: 'altitude');
      expect(inReadout('+14.2′'), findsOneWidget, reason: 'azimuth');
      // Each axis carries its own knob direction.
      expect(inReadout('Raise'), findsOneWidget);
      expect(inReadout('Move west'), findsOneWidget);
      expect(find.byType(CustomPaint), findsWidgets); // the bullseye
    });


    testWidgets('adjusting explains the error in arcseconds and plain English', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        azErrorArcmin: 0.5,
        altErrorArcmin: -0.6,
        totalErrorArcmin: 0.8,
      )));
      await tester.pumpAndSettle();
      final card = find.byKey(const Key('polar-align-meaning'));
      expect(card, findsOneWidget);
      Finder inCard(String text) => find.descendant(of: card, matching: find.text(text));
      expect(inCard('48″'), findsOneWidget);
      expect(inCard('arcseconds from the pole'), findsOneWidget);
      expect(inCard('Excellent'), findsOneWidget);
      expect(inCard('up to 1.1″'), findsOneWidget);
      expect(inCard("1/39 of the Moon's width"), findsOneWidget);
    });

    testWidgets('night mode draws the readout at full brightness, not the zone hue', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(ProviderScope(
        overrides: [
          polarAlignApiProvider.overrideWithValue(api),
          polarAlignLiveProvider.overrideWith(() => _StubLiveNotifier(const PolarAlignLive(
                phase: PolarAlignStates.adjusting,
                azErrorArcmin: 20,
                altErrorArcmin: -30,
                totalErrorArcmin: 36.1,
              ))),
          nightModeProvider.overrideWith(_NightOn.new),
        ],
        child: const MaterialApp(home: Scaffold(body: SingleChildScrollView(child: PolarAlignPanel()))),
      ));
      await tester.pumpAndSettle();
      // 36.1′ is in the yellow zone by day; under the red filter only
      // luminance survives, so it renders in the brightest text colour.
      final total = tester.widget<Text>(find.text('+36.1′'));
      expect(total.style?.color, AraColors.textPrimary);
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

      final readout = find.byKey(const Key('polar-align-readout'));
      expect(readout, findsOneWidget);
      expect(find.descendant(of: readout, matching: find.text('−23.4′')), findsOneWidget);
      expect(find.descendant(of: readout, matching: find.text('Lower')), findsOneWidget);
      expect(find.descendant(of: readout, matching: find.text('Move east')), findsOneWidget);
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

    testWidgets('a failed save rolls the mode back to what the server has', (tester) async {
      final api = _FakePolarAlignClient()..putError = Exception('network down');
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 30)));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Single'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Single'));
      await tester.pumpAndSettle();
      expect(api.calls, ['put']);
      expect(api.settings.loopMode, PolarAlignLoopModes.loop);
      expect(find.byKey(const Key('polar-align-take-frame')), findsNothing,
          reason: 'the server still loops, so the panel must not offer Take Frame');
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
