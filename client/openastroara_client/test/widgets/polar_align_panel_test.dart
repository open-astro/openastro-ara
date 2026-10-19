import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/night_mode_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/fit_pane.dart';
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
  void set(PolarAlignLive live) => state = live;
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

// The panel is always open; the title it once tapped is hidden while a routine
// runs on a short window, so this only settles.
Future<void> _expand(WidgetTester tester) async {
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
      expect(bullseyeRangeArcmin(2.0), 5.0);
      expect(bullseyeRangeArcmin(1.9), 2.0, reason: 'the §45.16 near-pole reticle');
      expect(bullseyeRangeArcmin(0.2), 2.0);
    });

    test('the near-pole reticle takes over under 2′', () {
      expect(nearPoleReticle(null), isFalse);
      expect(nearPoleReticle(2.0), isFalse);
      expect(nearPoleReticle(1.99), isTrue);
      expect(nearPoleReticle(0.0), isTrue);
    });

    test('a bolt turned the wrong way is named after one frame', () {
      // Altitude grew by 3′ (well over the 0.5′ noise), azimuth held.
      expect(
          boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 13.0, az: -4.2),
          contains('altitude bolt'));
      // Azimuth grew (more negative = further east), altitude held.
      expect(
          boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 10.3, az: -7.0),
          contains('azimuth bolt'));
      // Improvement, or growth inside the solve noise, says nothing.
      expect(boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 7.0, az: -4.0), isNull);
      expect(boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 10.4, az: -4.0), isNull);
      // Growth under 10 % of a large error is scatter at that scale.
      expect(boltDirectionNudge(prevAlt: 100.0, prevAz: 0.0, alt: 108.0, az: 0.0), isNull);
      // Both axes moved: a bump, not a bolt direction.
      expect(boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 14.0, az: -8.0), isNull);
      // Crossing zero (overshoot) is progress, not a wrong direction.
      expect(boltDirectionNudge(prevAlt: 1.0, prevAz: 0.0, alt: -0.8, az: 0.0), isNull);
      // No baseline (first adjust frame) says nothing.
      expect(boltDirectionNudge(prevAlt: null, prevAz: null, alt: 13.0, az: -4.0), isNull);
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

    test('south of the equator the bullseye is mirrored to the view facing the south pole', () {
      // Facing the SCP, east is on the LEFT: an axis east of the pole draws left of center.
      final south = bullseyeDotFraction(15, -15, 30, southern: true);
      expect(south.dx, closeTo(-0.5, 1e-9));
      expect(south.dy, closeTo(0.5, 1e-9));
    });

    test('azimuth arrows point the way the words say, for the pole the user faces', () {
      // 2026-10-17, southern site: "Move east" came with an arrow to the right, but facing the
      // south pole east is on the left. The words were right; the arrows assumed the NCP.
      expect(polarAzHint(5, southern: false), (Icons.arrow_back, 'Move west'));
      expect(polarAzHint(-5, southern: false), (Icons.arrow_forward, 'Move east'));
      expect(polarAzHint(5, southern: true), (Icons.arrow_forward, 'Move west'));
      expect(polarAzHint(-5, southern: true), (Icons.arrow_back, 'Move east'));
      expect(polarAzHint(0.01, southern: true), (Icons.check, 'On target'));
      expect(polarAzHint(null, southern: true), (Icons.remove, '—'));
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
      // Unit boundaries follow the rounded figure: never "600″" or "60′".
      expect(formatPoleOffset(9.995), ('10′', 'arcminutes from the pole'));
      expect(formatPoleOffset(59.7), ('1.0°', 'degrees from the pole'));
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
      // A tall window: a short one drops this card for the one-row readout.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
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

    testWidgets('a laptop window keeps the readout in one row under camera and bullseye', (tester) async {
      // 1080p at 150 %: a 1280×640 window leaves the Setup pane ≈ 918×536.
      tester.view.physicalSize = const Size(1280, 640);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(ProviderScope(
        overrides: [
          polarAlignApiProvider.overrideWithValue(_FakePolarAlignClient()),
          polarAlignLiveProvider.overrideWith(() => _StubLiveNotifier(const PolarAlignLive(
                phase: PolarAlignStates.adjusting,
                azErrorArcmin: 14.2,
                altErrorArcmin: -6.5,
                totalErrorArcmin: 15.6,
              ))),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: SizedBox(width: 918, height: 536, child: FitPane(padding: EdgeInsets.all(8), child: PolarAlignPanel())),
          ),
        ),
      ));
      await tester.pumpAndSettle();
      final readout = find.byKey(const Key('polar-align-readout'));
      expect(tester.widget(readout), isA<Row>(), reason: 'the one-row strip');
      expect(find.descendant(of: readout, matching: find.text('Rough')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-meaning')), findsNothing);
      expect(find.byKey(const Key('polar-align-done')), findsOneWidget);
      final scroll = tester.state<ScrollableState>(
          find.descendant(of: find.byType(FitPane), matching: find.byType(Scrollable)).first);
      expect(scroll.position.maxScrollExtent, 0);
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

    const solvedFrame = PolarAlignLive(
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
    );

    testWidgets('shows the last frame timings and solved pointing', (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
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

    testWidgets('a short window puts the timings and the pointing on one line', (tester) async {
      await tester.pumpWidget(_harness(_FakePolarAlignClient(), solvedFrame));
      await tester.pumpAndSettle();
      expect(find.text('1.0 s · capture 1.6 s · solve 0.5 s · RA 09h14m40s  Dec +87°10′56″'), findsOneWidget);
      expect(find.byKey(const Key('polar-align-last-solve')), findsNothing);
    });

    testWidgets('an active routine shows the guide camera live view', (tester) async {
      // A real 64×48 PNG, so the view decodes and lays out the frame.
      final png = (await tester.runAsync(() async {
        final recorder = ui.PictureRecorder();
        Canvas(recorder).drawRect(const Rect.fromLTWH(0, 0, 64, 48), Paint()..color = Colors.white);
        final image = await recorder.endRecording().toImage(64, 48);
        final data = await image.toByteData(format: ui.ImageByteFormat.png);
        return data!.buffer.asUint8List();
      }))!;
      final api = _FakePolarAlignClient()
        ..status = const PolarAlignStatus(state: PolarAlignStates.adjusting, currentErrorArcmin: 30)
        ..frame = png;
      await tester.pumpWidget(_harness(
          api, const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 30)));
      await tester.pumpAndSettle();

      expect(api.calls, contains('frame'));
      expect(find.byKey(const Key('polar-align-live-view')), findsOneWidget);
      // The 64-pixel frame scales up to its card instead of sitting at its own
      // pixel size (it stayed small in a big empty card at 4K).
      for (var i = 0; i < 5; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
        await tester.pump();
      }
      expect(tester.getRect(find.byKey(const Key('polar-align-live-view'))).width, greaterThan(100));
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

    testWidgets('under 2′ the bullseye becomes the near-pole reticle', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            iteration: 9,
            altErrorArcmin: 0.9,
            azErrorArcmin: -0.6,
            totalErrorArcmin: 1.1,
            zone: 'green',
          )));
      await tester.pumpAndSettle();
      final ring = tester.widget<Text>(find.byKey(const Key('polar-align-ring')));
      expect(ring.data, 'Reticle 2′ · rings 30″');
      final painter = tester
          .widgetList<CustomPaint>(find.byType(CustomPaint))
          .map((p) => p.painter)
          .whereType<BullseyePainter>()
          .single;
      expect(painter.reticle, isTrue);
    });

    testWidgets('a bolt turned the wrong way shows the nudge, which clears once it improves',
        (tester) async {
      final api = _FakePolarAlignClient();
      const first = PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        iteration: 1,
        altErrorArcmin: 10.0,
        azErrorArcmin: -4.0,
        totalErrorArcmin: 10.8,
        zone: 'yellow',
      );
      await tester.pumpWidget(_harness(api, first));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsNothing, reason: 'no baseline yet');

      final notifier = ProviderScope.containerOf(tester.element(find.byType(PolarAlignPanel)))
          .read(polarAlignLiveProvider.notifier) as _StubLiveNotifier;
      notifier.set(const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        iteration: 2,
        altErrorArcmin: 13.0,
        azErrorArcmin: -4.1,
        totalErrorArcmin: 13.6,
        zone: 'yellow',
      ));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsOneWidget);
      expect(find.textContaining('altitude bolt'), findsOneWidget);

      // The nudge stays while the next frames are within noise …
      notifier.set(const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        iteration: 3,
        altErrorArcmin: 13.1,
        azErrorArcmin: -4.0,
        totalErrorArcmin: 13.7,
        zone: 'yellow',
      ));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsOneWidget);

      // … and goes after its three frames once nothing is wrong any more.
      for (var i = 4; i <= 6; i++) {
        notifier.set(PolarAlignLive(
          phase: PolarAlignStates.adjusting,
          iteration: i,
          altErrorArcmin: 13.0 - i,
          azErrorArcmin: -4.0,
          totalErrorArcmin: 13.0 - i + 0.5,
          zone: 'yellow',
        ));
        await tester.pumpAndSettle();
      }
      expect(find.byKey(const Key('polar-align-nudge')), findsNothing);
    });
  });
}
