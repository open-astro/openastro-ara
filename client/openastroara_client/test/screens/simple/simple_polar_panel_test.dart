import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';

class _Api implements PolarAlignClient {
  final calls = <String>[];
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async => const PolarAlignSettings(targetToleranceArcmin: 1.0);
  @override
  Future<void> start() async => calls.add('start');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> complete() async => calls.add('complete');
  @override
  Future<void> requestCapture() async => calls.add('capture');
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings settings) async => settings;
  @override
  void close() {}
}

class _Live extends PolarAlignLiveNotifier {
  _Live(this.live);
  final PolarAlignLive live;
  @override
  PolarAlignLive build() => live;
}

const _adjusting = PolarAlignLive(
  phase: PolarAlignStates.adjusting,
  altErrorArcmin: 3.2,
  azErrorArcmin: 14.1,
  totalErrorArcmin: 14.4,
  zone: 'yellow',
);

Future<_Api?> _pump(WidgetTester tester, PolarAlignLive live, {bool server = true}) async {
  final api = server ? _Api() : null;
  await tester.pumpWidget(ProviderScope(
    overrides: [
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => _Live(live)),
    ],
    child: const MaterialApp(home: Scaffold(body: Center(child: SimplePolarPanel()))),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  group('simplePolarSummary', () {
    test('Ready before the routine has an error', () {
      expect(simplePolarSummary(const PolarAlignLive()), 'Ready');
      expect(simplePolarSummary(const PolarAlignLive(phase: PolarAlignStates.seeding)), 'Ready');
    });
    test('the total error while it runs, one decimal under 10′', () {
      expect(simplePolarSummary(_adjusting), '14′');
      expect(simplePolarSummary(const PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 3.24)), '3.2′');
    });
    test('Ready again once it stopped', () {
      expect(simplePolarSummary(const PolarAlignLive(phase: PolarAlignStates.stopped, totalErrorArcmin: 3.2)), 'Ready');
    });
  });

  test('simplePolarPhase names each phase', () {
    expect(simplePolarPhase(const PolarAlignLive()), 'Ready');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.seeding)), 'Finding the pole…');
    expect(simplePolarPhase(_adjusting), 'Adjusting');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.failed)), 'Polar alignment failed');
  });

  testWidgets('no server: Ready, and Start is off', (tester) async {
    await _pump(tester, const PolarAlignLive(), server: false);
    expect(find.text('Ready'), findsOneWidget);
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-polar-start'))).onPressed, isNull);
  });

  testWidgets('idle: Start starts the routine', (tester) async {
    final api = await _pump(tester, const PolarAlignLive());
    await tester.tap(find.byKey(const Key('simple-polar-start')));
    await tester.pump();
    expect(api!.calls, ['start']);
  });

  testWidgets('adjusting: the error, the turns, Stop; Done waits for the tolerance', (tester) async {
    final api = await _pump(tester, _adjusting);
    expect(find.text('14.4′'), findsOneWidget);
    expect(find.text('Lower 3.2′'), findsOneWidget);
    expect(find.text('Move west 14.1′'), findsOneWidget);
    expect(find.byKey(const Key('simple-polar-start')), findsNothing);
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-polar-done'))).onPressed, isNull);
    await tester.tap(find.byKey(const Key('simple-polar-stop')));
    await tester.pump();
    expect(api!.calls, ['stop']);
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

  testWidgets('failed solves say so', (tester) async {
    await _pump(tester, _adjusting.copyWith(consecutiveSolveFailures: 2));
    expect(find.text('No solve (2 in a row). Check the sky.'), findsOneWidget);
  });

  testWidgets('a failed routine shows why, and Start again', (tester) async {
    await _pump(tester, const PolarAlignLive(phase: PolarAlignStates.failed, errorMessage: 'The mount would not rotate.'));
    expect(find.text('The mount would not rotate.'), findsOneWidget);
    expect(find.byKey(const Key('simple-polar-start')), findsOneWidget);
  });
}
