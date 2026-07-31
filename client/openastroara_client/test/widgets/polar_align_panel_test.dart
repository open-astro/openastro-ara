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
  Future<PolarAlignSettings> getSettings() async => settings;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings s) async => s;
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

    test('formatArcmin renders signed arcminutes', () {
      expect(formatArcmin(14.23), '+14.2′');
      expect(formatArcmin(-23.41), '−23.4′');
      expect(formatArcmin(null), '—');
    });
  });

  group('PolarAlignPanel', () {
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

      await tester.tap(find.byKey(const Key('polar-align-done')));
      await tester.pumpAndSettle();
      expect(api.calls, ['complete']);
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
