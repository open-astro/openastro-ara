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
import 'package:openastroara/l10n/l10n.dart';

import '../support/imaging_sections.dart' show staleSave;
import '../support/polar_align_section.dart';

/// Pure fake — records calls; scripted status. The profile's polar-align
/// settings are a profile section (ADR 0015): [section], with no HTTP.
class _FakePolarAlignClient implements PolarAlignClient {
  final calls = <String>[];
  PolarAlignStatus? status;
  final section = FakePolarAlignSection();

  /// The rig's copy of the section.
  PolarAlignSettings get settings => section.initial!;
  set settings(PolarAlignSettings s) => section.initial = s;

  @override
  Future<PolarAlignStatus?> getStatus() async => status;
  @override
  Future<void> start({String? stage}) async => calls.add(stage == null ? 'start' : 'start:$stage');
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
      ...polarAlignSectionOverrides(api.section),
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

final _en = lookupAppLocalizations(const Locale('en'));

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
          isTrue);
      // Azimuth grew (more negative = further east), altitude held.
      expect(
          boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 10.3, az: -7.0),
          isFalse);
      // Improvement, or growth inside the solve noise, says nothing.
      expect(boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 7.0, az: -4.0), isNull);
      expect(boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 10.4, az: -4.0), isNull);
      // Growth under 10 % of a large error is scatter at that scale.
      expect(boltDirectionNudge(prevAlt: 100.0, prevAz: 0.0, alt: 108.0, az: 0.0), isNull);
      // Both axes moved: a bump, not a bolt direction.
      expect(boltDirectionNudge(prevAlt: 10.0, prevAz: -4.0, alt: 14.0, az: -8.0), isNull);
      // Crossing zero (overshoot) is progress, not a wrong direction — even
      // when the new magnitude is larger (review r2: 1.0 → −3.0 fired the nudge
      // and "turn it back past where you started" would have been wrong).
      expect(boltDirectionNudge(prevAlt: 1.0, prevAz: 0.0, alt: -0.8, az: 0.0), isNull);
      expect(boltDirectionNudge(prevAlt: 1.0, prevAz: 0.0, alt: -3.0, az: 0.0), isNull);
      expect(boltDirectionNudge(prevAlt: 0.0, prevAz: 2.0, alt: 0.0, az: -5.0), isNull);
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
      expect(polarAzHint(_en, 5, southern: false), (Icons.arrow_back, 'Move west'));
      expect(polarAzHint(_en, -5, southern: false), (Icons.arrow_forward, 'Move east'));
      expect(polarAzHint(_en, 5, southern: true), (Icons.arrow_forward, 'Move west'));
      expect(polarAzHint(_en, -5, southern: true), (Icons.arrow_back, 'Move east'));
      expect(polarAzHint(_en, 0.01, southern: true), (Icons.check, 'On target'));
      expect(polarAzHint(_en, null, southern: true), (Icons.remove, '—'));
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

    test('an error reads with its 1σ in English and pt-BR, and alone without one (#2072)', () {
      final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
      expect(polarErrorWithSigmaText(_en, '+0.2′', 1.1), '+0.2′ ± 1.1′');
      expect(polarErrorWithSigmaText(pt, '+0.2′', 1.14), '+0.2′ ± 1.1′');
      expect(polarErrorWithSigmaText(_en, '+0.2′', null), '+0.2′', reason: 'an older daemon sends no σ');
    });

    test("inside the tolerance but not with twice its 1σ: the text says it can't confirm and names larger turns, in English and pt-BR (#2072)", () {
      final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
      final en = polarUnconfirmedText(_en, 0.5, 0.4, 1.0)!;
      expect(en, startsWith("Inside your 1′ tolerance, but at ± 0.4′ this measurement can't confirm the alignment."));
      expect(en, contains('20° in place of 10°'));
      expect(polarUnconfirmedText(pt, 0.5, 0.4, 1.0), contains('não consegue confirmar o alinhamento'));
      expect(polarUnconfirmedText(_en, 0.5, 0.25, 1.0), isNull, reason: 'confirmed: 0.5 + 2 × 0.25 is inside 1′');
      expect(polarUnconfirmedText(_en, 1.2, 0.1, 1.0), isNull, reason: 'outside the tolerance on its own: the usual hint');
    });

    test('a check rates the error plus twice its 1σ: 0.2′ ± 1.3′ does not read Excellent (#2072)', () {
      const noisy = PolarAlignCheckResult(
          altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2,
          coneHalfAngleDeg: 5.0, turnMismatchDeg: 0.0, verdict: 'excellent', totalSigmaArcmin: 1.3);
      expect(polarCheckVerdict(noisy), 'very_good');
      expect(polarErrorRating(polarRatedArcmin(0.2, 1.3)).$1, isNot('Excellent'));
      const sure = PolarAlignCheckResult(
          altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2,
          coneHalfAngleDeg: 5.0, turnMismatchDeg: 0.0, verdict: 'excellent', totalSigmaArcmin: 0.3);
      expect(polarCheckVerdict(sure), 'excellent');
      const older = PolarAlignCheckResult(
          altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2,
          coneHalfAngleDeg: 5.0, turnMismatchDeg: 0.0, verdict: 'good');
      expect(polarCheckVerdict(older), 'good', reason: "no σ: the daemon's own token");
    });

    test("the rating takes the error and its 1σ at 0.01′, as the daemon's verdict does (#2072 review)", () {
      // 0.996 + 2 × 0.004 is 1.004 unrounded, but 1.00 + 2 × 0.00 at 0.01′:
      // the daemon rates 1.00, Excellent, and so does the client.
      expect(polarRatedArcmin(0.996, 0.004), 1.0);
      expect(polarVerdictFor(polarRatedArcmin(0.996, 0.004)), 'excellent');
      expect(polarRatedArcmin(0.2, 1.3), closeTo(2.8, 1e-9));
    });

    test('aligned needs the error plus twice its 1σ inside the tolerance; no σ judges the error alone (#2072)', () {
      expect(polarAligned(0.5, 0.25, 1.0), isTrue, reason: '0.5 + 2 × 0.25 is exactly the tolerance');
      expect(polarAligned(0.5, 0.26, 1.0), isFalse);
      expect(polarAligned(0.2, 1.1, 1.0), isFalse, reason: '0.2′ ± 1.1′ is not aligned');
      expect(polarAligned(0.9, null, 1.0), isTrue);
      expect(polarAligned(null, 0.1, 1.0), isFalse);
      expect(polarAligned(0.5, 0.1, null), isFalse, reason: 'no tolerance until the rig answers');
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


    testWidgets('the live readout says what it measures, and warns where it reads altitude poorly (#2072)', (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      const live = PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        azErrorArcmin: 2.0,
        altErrorArcmin: -1.0,
        totalErrorArcmin: 2.24,
      );
      await tester.pumpWidget(_harness(_FakePolarAlignClient(), live));
      await tester.pumpAndSettle();
      final measures = find.byKey(const Key('polar-align-readout-measures'));
      expect(measures, findsOneWidget);
      expect(tester.widget<Text>(measures).data, allOf(contains('refracted pole'), contains('cos alt applied')));
      expect(find.byKey(const Key('polar-align-readout-caution')), findsNothing, reason: 'a sound readout');

      final notifier = ProviderScope.containerOf(tester.element(find.byType(PolarAlignPanel)))
          .read(polarAlignLiveProvider.notifier) as _StubLiveNotifier;
      notifier.set(live.copyWith(readoutCaution: PolarAlignReadoutCautions.altitudeWeak));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-readout-caution')), findsOneWidget);
      expect(find.text(_en.polarReadoutCautionAltitudeWeak), findsOneWidget);
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

    testWidgets('idle offers Start and a Check that says how far it turns the mount (#2082)', (tester) async {
      final api = _FakePolarAlignClient()..settings = rigPolarAlign.copyWith(checkStepDeg: 15);
      await tester.pumpWidget(_harness(api, const PolarAlignLive()));
      await _expand(tester);
      expect(find.byKey(const Key('polar-align-start-fine')), findsNothing, reason: 'Start is the fine measurement');
      expect(
        find.text('Check turns the mount 2 × 15° and takes three frames to measure the alignment.'),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('polar-align-start-check')));
      await tester.pumpAndSettle();
      expect(api.calls, ['start:check']);
    });

    testWidgets('the start card offers only Start and Check, also after the guide camera could not solve', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.failed, errorReason: 'seed_solve_failed', errorMessage: 'the seed-a seed frame failed to solve',
      )));
      await _expand(tester);
      expect(find.byKey(const Key('polar-align-error-banner')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-start')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-start-check')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-start-rough')), findsNothing);
      expect(find.byKey(const Key('polar-preflight')), findsNothing);
      expect(find.byKey(const Key('polar-align-stages')), findsNothing);
    });

    testWidgets('a noisy measurement says so under the live readout and under the check (#2082)', (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      const live = PolarAlignLive(
        phase: PolarAlignStates.adjusting, stage: PolarAlignStages.fine,
        azErrorArcmin: 2.0, altErrorArcmin: -1.0, totalErrorArcmin: 2.24,
      );
      await tester.pumpWidget(_harness(_FakePolarAlignClient(), live));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-noisy')), findsNothing, reason: 'a steady measurement');

      final notifier = ProviderScope.containerOf(tester.element(find.byType(PolarAlignPanel)))
          .read(polarAlignLiveProvider.notifier) as _StubLiveNotifier;
      notifier.set(live.copyWith(measurementNoisy: true));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-noisy')), findsOneWidget);
      expect(find.text(_en.polarNoisyMeasurement), findsOneWidget);

      notifier.set(const PolarAlignLive(
        phase: PolarAlignStates.checked, stage: PolarAlignStages.check,
        totalErrorArcmin: 0.8, altErrorArcmin: 0.5, azErrorArcmin: 0.6,
        checkResult: PolarAlignCheckResult(
            altErrorArcmin: 0.5, azErrorArcmin: 0.6, totalErrorArcmin: 0.8, verdict: 'excellent', noisy: true),
      ));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-check-noisy')), findsOneWidget);
    });

    testWidgets('a run on the main camera shows its solves without a guide-camera picture (#2082)', (tester) async {
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final api = _FakePolarAlignClient()..frame = Uint8List.fromList([1, 2, 3]);
      await tester.pumpWidget(_harness(api, PolarAlignLive(
        phase: PolarAlignStates.adjusting, stage: PolarAlignStages.fine, camera: PolarAlignCameras.main,
        azErrorArcmin: 2.0, altErrorArcmin: -1.0, totalErrorArcmin: 2.24,
        lastFrame: PolarAlignFrameInfo.fromJson({'frame_id': 'live-1', 'solved': true, 'exposure_seconds': 3.0}),
      )));
      await tester.pumpAndSettle();
      expect(find.text('Main camera'.toUpperCase()), findsOneWidget);
      expect(find.text('Main-camera solves (no preview)'), findsOneWidget);
      expect(api.calls, isNot(contains('frame')), reason: 'the guider has no picture of this run');
    });

    testWidgets('the adjust screen offers Check once inside the tolerance, and a tap starts the check', (
      tester,
    ) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.adjusting, stage: PolarAlignStages.fine,
        iteration: 9, altErrorArcmin: 0.3, azErrorArcmin: -0.2, totalErrorArcmin: 0.36, zone: 'green',
      )));
      await _expand(tester);
      expect(
        find.text('Check turns the mount 2 × 10° and takes three frames to measure the alignment.'),
        findsOneWidget,
      );
      await tester.ensureVisible(find.byKey(const Key('polar-align-check')));
      await tester.tap(find.byKey(const Key('polar-align-check')));
      await tester.pumpAndSettle();
      expect(api.calls, contains('start:check'));
    });

    testWidgets('checking counts the solves', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.checking, stage: PolarAlignStages.check, checkSolvesDone: 2,
      )));
      await tester.pump(); // a spinner runs: no settle
      expect(find.text('Check solve 2 of 3'), findsOneWidget);
      expect(find.byKey(const Key('polar-align-abort-checking')), findsOneWidget);
    });

    testWidgets('checked shows the verdict, the disagreement with the fine stage, and Done', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.checked, stage: PolarAlignStages.check, checkSolvesDone: 3,
        altErrorArcmin: -2.1, azErrorArcmin: 4.0, totalErrorArcmin: 4.52, zone: 'green',
        checkResult: PolarAlignCheckResult(
          altErrorArcmin: -2.1, azErrorArcmin: 4.0, totalErrorArcmin: 4.52,
          coneHalfAngleDeg: 5.0, turnMismatchDeg: 0.02,
          fineErrorArcmin: 0.8, fineDeltaArcmin: 3.72, verdict: 'good',
        ),
      )));
      await _expand(tester);
      expect(find.byKey(const Key('polar-align-check-result')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-check-verdict')), findsOneWidget);
      expect(find.byKey(const Key('polar-align-check-disagree')), findsOneWidget, reason: '3.7′ apart is worth a warning');
      expect(find.text(_en.polarCheckMeasures), findsOneWidget, reason: "#2072 the check's figure says what it measures");
      await tester.ensureVisible(find.byKey(const Key('polar-align-check-done')));
      await tester.tap(find.byKey(const Key('polar-align-check-done')));
      await tester.pumpAndSettle();
      expect(api.calls, contains('complete'));
      await tester.ensureVisible(find.byKey(const Key('polar-align-adjust-again')));
      await tester.tap(find.byKey(const Key('polar-align-adjust-again')));
      await tester.pumpAndSettle();
      expect(api.calls, contains('start:fine'));
    });

    testWidgets("the check verdict shows its total with the verdict's own 1σ (#2072)", (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.checked, stage: PolarAlignStages.check, checkSolvesDone: 3,
        altErrorArcmin: 0.3, azErrorArcmin: 0.4, totalErrorArcmin: 0.5, totalSigmaArcmin: 1.0, zone: 'green',
        checkResult: PolarAlignCheckResult(
          altErrorArcmin: 0.3, azErrorArcmin: 0.4, totalErrorArcmin: 0.5,
          coneHalfAngleDeg: 5.0, turnMismatchDeg: 0.02, verdict: 'excellent', totalSigmaArcmin: 0.6,
        ),
      )));
      await _expand(tester);
      final total = tester.widget<Text>(find.byKey(const Key('polar-align-check-total')));
      expect(total.data, '+0.5′ ± 0.6′', reason: "the verdict's σ, as Simple Mode shows it");
    });

    testWidgets('the check card badge rates the error plus twice its 1σ: 0.2′ ± 1.3′ does not read Excellent (#2072)', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.checked, stage: PolarAlignStages.check, checkSolvesDone: 3,
        altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2, totalSigmaArcmin: 1.3, zone: 'green',
        checkResult: PolarAlignCheckResult(
          altErrorArcmin: 0.1, azErrorArcmin: 0.17, totalErrorArcmin: 0.2,
          coneHalfAngleDeg: 5.0, turnMismatchDeg: 0.02, verdict: 'excellent', totalSigmaArcmin: 1.3,
        ),
      )));
      await _expand(tester);
      final badge = tester.widget<Text>(find.byKey(const Key('polar-align-check-verdict')));
      expect(badge.data, 'Very good', reason: '0.2 + 2 × 1.3 = 2.8′');
    });

    testWidgets('idle shows Start, and a tap posts the start at once: no confirm step', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive()));
      await tester.pumpAndSettle();
      await _expand(tester);

      final start = find.byKey(const Key('polar-align-start'));
      expect(start, findsOneWidget);
      await tester.tap(start);
      await tester.pumpAndSettle();
      expect(find.byType(Dialog), findsNothing);
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

    testWidgets("the readout shows the total with its 1σ; inside the tolerance but not with twice it, Done is allowed and the screen says it can't confirm (#2072)",
        (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            stage: PolarAlignStages.fine,
            altErrorArcmin: 0.3,
            azErrorArcmin: 0.4,
            totalErrorArcmin: 0.5,
            totalSigmaArcmin: 0.4,
            zone: 'green',
          )));
      await _expand(tester);

      expect(find.text('+0.5′ ± 0.4′'), findsOneWidget);
      expect(find.byKey(const Key('polar-align-unconfirmed')), findsOneWidget);
      expect(find.textContaining("this measurement can't confirm the alignment"), findsOneWidget);
      final check = tester.widget<FilledButton>(find.byKey(const Key('polar-align-check')));
      expect(check.onPressed, isNotNull, reason: 'Check measures afresh');
      // Done is not held by the σ (Joey, 2026-11-21).
      await tester.ensureVisible(find.byKey(const Key('polar-align-done')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-align-done')));
      await tester.pumpAndSettle();
      expect(api.calls, ['complete']);
    });

    testWidgets('with the error plus 2σ inside the tolerance the screen does not call it unconfirmed (#2072)', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.adjusting,
            stage: PolarAlignStages.fine,
            altErrorArcmin: 0.3,
            azErrorArcmin: 0.4,
            totalErrorArcmin: 0.5,
            totalSigmaArcmin: 0.25,
            zone: 'green',
          )));
      await _expand(tester);

      expect(find.byKey(const Key('polar-align-unconfirmed')), findsNothing, reason: '0.5 + 2 × 0.25 is exactly the 1′ tolerance');
    });

    testWidgets('the live quality card rates the error plus twice its 1σ: 0.2′ ± 1.3′ does not read Excellent (#2072)', (tester) async {
      // A tall window: a short one drops this card for the one-row readout.
      tester.view.physicalSize = const Size(1400, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        azErrorArcmin: 0.17,
        altErrorArcmin: 0.1,
        totalErrorArcmin: 0.2,
        totalSigmaArcmin: 1.3,
      )));
      await tester.pumpAndSettle();
      final card = find.byKey(const Key('polar-align-meaning'));
      Finder inCard(String text) => find.descendant(of: card, matching: find.text(text));
      expect(inCard('Excellent'), findsNothing);
      expect(inCard('Very good'), findsOneWidget);
    });

    testWidgets('single mode shows Take Frame and posts a capture request', (tester) async {
      final api = _FakePolarAlignClient()
        ..settings = rigPolarAlign.copyWith(loopMode: PolarAlignLoopModes.single);
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
      expect(api.section.sent, hasLength(1));
      expect(api.settings.loopMode, PolarAlignLoopModes.single);
      expect(find.byKey(const Key('polar-align-take-frame')), findsOneWidget);
    });

    testWidgets('a failed save rolls the mode back to what the server has', (tester) async {
      final api = _FakePolarAlignClient();
      api.section.refuse = Exception('network down');
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 30)));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Single'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Single'));
      await tester.pumpAndSettle();
      expect(api.section.sent, hasLength(1));
      expect(api.section.puts, isEmpty);
      expect(api.settings.loopMode, PolarAlignLoopModes.loop);
      expect(find.byKey(const Key('polar-align-take-frame')), findsNothing,
          reason: 'the server still loops, so the panel must not offer Take Frame');
    });

    testWidgets("an exposure refused as stale shows the rig's copy again, and the next one saves (ADR 0015)",
        (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(api, const PolarAlignLive()));
      await tester.pumpAndSettle();
      final loads = api.section.loads;
      api.section.refuse = staleSave();

      await tester.enterText(find.byKey(const Key('polar-align-exposure')), '5');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.section.puts, isEmpty);
      expect(api.section.loads, loads + 1, reason: "the rig's copy is loaded again");
      final field = find.byKey(const Key('polar-align-exposure'));
      expect(tester.widget<TextField>(field).controller!.text, isNot('5'),
          reason: 'the rig still takes 1 s frames: 5 s must not stay on screen');
      expect(tester.widget<TextField>(field).controller!.text, '1.0');

      api.section.refuse = null;
      await tester.enterText(find.byKey(const Key('polar-align-exposure')), '5');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(api.section.puts.single.exposureSeconds, 5.0);
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

    testWidgets(
        'the capture settings wait for the profile from the rig: Not loaded with Retry, and Done waits too (ADR 0015)',
        (tester) async {
      final api = _FakePolarAlignClient();
      api.section.initial = null; // the rig does not answer
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 0.5)));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-exposure')), findsNothing, reason: 'no default exposure shows');
      expect(find.byKey(const Key('polar-align-mode')), findsNothing);
      expect(find.byKey(const Key('section-retry')), findsOneWidget);
      expect(tester.widget<ButtonStyleButton>(find.byKey(const Key('polar-align-done'))).enabled, isFalse,
          reason: 'no tolerance is made up to judge the error against');
      expect(api.section.sent, isEmpty);

      api.settings = rigPolarAlign;
      await tester.ensureVisible(find.byKey(const Key('section-retry')));
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-exposure')), findsOneWidget);
      expect(tester.widget<ButtonStyleButton>(find.byKey(const Key('polar-align-done'))).enabled, isTrue);
    });

    testWidgets("Check's caption names only the rig's own step: none until the profile loads (ADR 0015)",
        (tester) async {
      final api = _FakePolarAlignClient();
      api.section.initial = null; // the rig does not answer
      await tester.pumpWidget(_harness(api, const PolarAlignLive()));
      await _expand(tester);
      expect(find.byKey(const Key('polar-align-check-caption')), findsNothing,
          reason: 'no made-up step is said before the mount turns');
      expect(find.byKey(const Key('polar-align-start-check')), findsOneWidget);

      api.settings = rigPolarAlign.copyWith(checkStepDeg: 20);
      await tester.ensureVisible(find.byKey(const Key('section-retry')).first);
      await tester.tap(find.byKey(const Key('section-retry')).first);
      await tester.pumpAndSettle();
      expect(
        find.text('Check turns the mount 2 × 20° and takes three frames to measure the alignment.'),
        findsOneWidget,
      );
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

    test('the checked card shows the verdict the daemon sent', () {
      // One band table: the daemon's token picks the words, and the local bands
      // agree with it for every total.
      expect(polarRatingForVerdict('excellent')!.$1, 'Excellent');
      expect(polarRatingForVerdict('far_off')!.$1, 'Far off');
      expect(polarRatingForVerdict('splendid'), isNull);
      for (final total in [0.5, 1.0, 2.9, 3.0, 9.9, 10.0, 29.9, 30.0, 31.0]) {
        expect(polarRatingForVerdict(polarVerdictFor(total)), polarErrorRating(total), reason: '$total′');
      }
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

    // #2107 the full UI words a stop from outside from its stopped_by too.
    testWidgets('Advanced: a run the fault reaction ended names the fault reaction, not Stop Mount', (tester) async {
      final api = _FakePolarAlignClient();
      await tester.pumpWidget(_harness(
          api,
          const PolarAlignLive(
            phase: PolarAlignStates.failed,
            errorReason: 'slew_aborted_externally',
            errorMessage: 'the mount was stopped from outside polar alignment (Stop Mount)',
            errorStoppedBy: 'fault_reaction',
          )));
      await tester.pumpAndSettle();
      await _expand(tester);
      expect(find.textContaining(_en.polarAlignStoppedFromOutside('fault_reaction')), findsOneWidget);
      expect(find.textContaining('Stop Mount'), findsNothing);
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

      // … and goes the moment altitude actually improves by more than the noise.
      notifier.set(const PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        iteration: 4,
        altErrorArcmin: 11.0,
        azErrorArcmin: -4.0,
        totalErrorArcmin: 11.7,
        zone: 'yellow',
      ));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsNothing);
    });

    testWidgets('the nudge expires after a few frames of no change, and a new run starts clean',
        (tester) async {
      final api = _FakePolarAlignClient();
      PolarAlignLive at(int i, double alt, {String phase = PolarAlignStates.adjusting}) => PolarAlignLive(
          phase: phase, iteration: i, altErrorArcmin: alt, azErrorArcmin: -4.0, totalErrorArcmin: alt.abs() + 0.5);
      await tester.pumpWidget(_harness(api, at(1, 10.0)));
      await tester.pumpAndSettle();
      final notifier = ProviderScope.containerOf(tester.element(find.byType(PolarAlignPanel)))
          .read(polarAlignLiveProvider.notifier) as _StubLiveNotifier;
      notifier.set(at(2, 13.0));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsOneWidget);
      for (var i = 3; i <= 5; i++) {
        notifier.set(at(i, 13.0 + 0.1 * i));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('polar-align-nudge')), findsOneWidget, reason: 'frame $i');
      }
      notifier.set(at(6, 13.6));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsNothing, reason: 'expired');

      // Stop, then a new run whose first adjust frame is iteration 1 again, then a
      // frame 2 that is worse than the OLD run's last frame but not than frame 1:
      // no cross-run comparison, no false nudge.
      notifier.set(at(0, 13.6, phase: PolarAlignStates.stopped));
      await tester.pumpAndSettle();
      notifier.set(at(1, 20.0));
      await tester.pumpAndSettle();
      notifier.set(at(2, 20.2));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-align-nudge')), findsNothing);
    });
  });
}
