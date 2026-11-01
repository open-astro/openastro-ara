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
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings next) async {
    calls.add('save ${next.exposureSeconds} ${next.loopMode}');
    return settings = next;
  }
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

Future<_Api?> _pump(WidgetTester tester, PolarAlignLive live,
    {bool server = true, String loopMode = PolarAlignLoopModes.loop, Widget child = const SimplePolarPanel()}) async {
  final api = server ? (_Api()..settings = PolarAlignSettings(loopMode: loopMode)) : null;
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
    expect(simplePolarSummary(const PolarAlignSettings()), '1 s\nLoop');
    expect(simplePolarSummary(const PolarAlignSettings(exposureSeconds: 45, loopMode: PolarAlignLoopModes.single)),
        '45 s\nSingle');
  });

  test('simplePolarPhase names each phase', () {
    expect(simplePolarPhase(const PolarAlignLive()), 'Ready');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.seeding)), 'Finding the pole…');
    expect(simplePolarPhase(_adjusting), 'Adjusting');
    expect(simplePolarPhase(const PolarAlignLive(phase: PolarAlignStates.failed)), 'Polar alignment failed');
  });

  testWidgets('idle: only the settings, no Ready and no Start (the shutter starts it)', (tester) async {
    final api = await _pump(tester, const PolarAlignLive());
    expect(find.text('Ready'), findsNothing);
    expect(find.text('Start'), findsNothing);
    expect(find.text('1 s'), findsOneWidget);
    await tester.tap(find.text('Single'));
    await tester.pumpAndSettle();
    expect(api!.calls, ['save 1.0 single']);
  });

  testWidgets('adjusting: the error, the turns, no Stop (the shutter stops it); Done waits for the tolerance', (tester) async {
    final api = await _pump(tester, _adjusting);
    expect(find.text('14.4′'), findsOneWidget);
    expect(find.text('Lower 3.2′'), findsOneWidget);
    expect(find.text('Move west 14.1′'), findsOneWidget);
    expect(find.text('1 s'), findsOneWidget, reason: 'the settings stay, for the next frame');
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-polar-done'))).onPressed, isNull);
    expect(find.byKey(const Key('simple-polar-stop')), findsNothing);
    expect(find.byKey(const Key('simple-polar-capture')), findsNothing, reason: 'Loop shoots on its own');
    expect(api!.calls, isEmpty);
  });

  testWidgets('Single running: Take frame takes the next one', (tester) async {
    final api = await _pump(tester, _adjusting, loopMode: PolarAlignLoopModes.single);
    await tester.tap(find.byKey(const Key('simple-polar-capture')));
    await tester.pump();
    expect(api!.calls, ['capture']);
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
  });

  group('the shutter runs the routine', () {
    Future<_Api?> shutter(WidgetTester tester, PolarAlignLive live, {String loopMode = PolarAlignLoopModes.loop, bool server = true}) =>
        _pump(tester, live, loopMode: loopMode, server: server, child: const SimplePolarShutter());

    testWidgets('idle: starts it', (tester) async {
      final api = await shutter(tester, const PolarAlignLive());
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
    testWidgets('Single running: the square stops it too', (tester) async {
      final api = await shutter(tester, _adjusting, loopMode: PolarAlignLoopModes.single);
      expect(find.bySemanticsLabel('Stop'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(api!.calls, ['stop']);
    });
    testWidgets('no server: off', (tester) async {
      await shutter(tester, const PolarAlignLive(), server: false);
      final ink = tester.widget<InkResponse>(find.byKey(const Key('simple-shutter')));
      expect(ink.onTap, isNull);
    });
  });
}
