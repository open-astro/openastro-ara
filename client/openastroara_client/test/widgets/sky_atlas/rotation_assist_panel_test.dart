import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/rotation_assist.dart';
import 'package:openastroara/state/rotation/rotation_assist_state.dart';
import 'package:openastroara/state/sky_atlas/plan_framing_state.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';
import 'package:openastroara/widgets/sky_atlas/rotation_assist_panel.dart';

/// Records start/stop calls instead of talking to a daemon.
class _Stub extends RotationAssistNotifier {
  final RotationAssistLive initial;
  final starts = <({double pa, double? exposure, String mode, int? bin})>[];
  int stops = 0;
  int confirms = 0;
  _Stub(this.initial);
  @override
  RotationAssistLive build() => initial;
  @override
  Future<void> start({
    required double positionAngleDeg,
    double? exposureSeconds,
    String mode = RotationAssistModes.loop,
    int? binning,
  }) async {
    starts.add((
      pa: positionAngleDeg,
      exposure: exposureSeconds,
      mode: mode,
      bin: binning,
    ));
  }

  @override
  Future<void> stop() async => stops++;

  @override
  Future<void> confirm() async => confirms++;
}

final _pngBytes = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=',
);

const _framed = PlanFraming(
  on: true,
  raDeg: 314.8,
  decDeg: 44.5,
  rotationDeg: 120,
  name: 'NGC 7000',
);

Future<(_Stub, ProviderContainer)> _pump(
  WidgetTester tester,
  RotationAssistLive live, {
  PlanFraming framing = _framed,
}) async {
  final stub = _Stub(live);
  final container = ProviderContainer(
    overrides: [rotationAssistProvider.overrideWith(() => stub)],
  );
  addTearDown(container.dispose);
  container.read(planFramingProvider.notifier).set(framing);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(
        home: Scaffold(body: Row(children: [RotationAssistPanel()])),
      ),
    ),
  );
  await tester.pump();
  return (stub, container);
}

void main() {
  const idleWithDefault = RotationAssistLive(
    status: RotationAssistStatus(
      defaultExposureSeconds: 3,
      autoBinning: 1,
      maxBinning: 1,
    ),
  );

  testWidgets('shows the framed target and its planned angle', (tester) async {
    await _pump(tester, idleWithDefault);
    expect(find.text('NGC 7000'), findsOneWidget);
    expect(find.text('120°'), findsOneWidget);
    expect(find.byKey(const Key('rotation-assist-intro')), findsOneWidget);
    // The exposure field is seeded from the daemon's default.
    final field = tester.widget<TextField>(
      find.byKey(const Key('rotation-assist-exposure')),
    );
    expect(field.controller!.text, '3');
  });

  testWidgets('without framing the start is disabled and says why', (
    tester,
  ) async {
    await _pump(tester, idleWithDefault, framing: PlanFraming.none);
    final btn = tester.widget<FilledButton>(
      find.byKey(const Key('rotation-assist-start')),
    );
    expect(btn.onPressed, isNull);
    expect(find.textContaining('Switch on Framing'), findsOneWidget);
  });

  testWidgets('Start loop sends the dial angle, the exposure and loop mode', (
    tester,
  ) async {
    final (stub, _) = await _pump(tester, idleWithDefault);
    await tester.enterText(
      find.byKey(const Key('rotation-assist-exposure')),
      '1.5',
    );
    await tester.pump();
    await tester.tap(find.byKey(const Key('rotation-assist-start')));
    await tester.pump();
    expect(stub.starts, [(pa: 120.0, exposure: 1.5, mode: 'loop', bin: null)]);
  });

  testWidgets('Single mode offers Take frame and sends single', (tester) async {
    final (stub, _) = await _pump(tester, idleWithDefault);
    await tester.tap(find.text('Single'));
    await tester.pump();
    expect(find.byKey(const Key('rotation-assist-start')), findsNothing);
    await tester.tap(find.byKey(const Key('rotation-assist-take-frame')));
    await tester.pump();
    expect(stub.starts.single.mode, 'single');
    expect(stub.starts.single.exposure, 3.0);
  });

  testWidgets('binning: auto suggests the exposure, a 4× chip sends 4 and '
      'quarters it twice; chips above the camera ceiling are greyed', (
    tester,
  ) async {
    const status = RotationAssistStatus(
      defaultExposureSeconds: 4,
      autoBinning: 2,
      maxBinning: 2,
    );
    final (stub, _) = await _pump(
      tester,
      const RotationAssistLive(status: status),
    );
    final field = find.byKey(const Key('rotation-assist-exposure'));
    // Auto = 2×: 4 s / 4 = 1 s suggested.
    expect(tester.widget<TextField>(field).controller!.text, '1');
    // 4× is above this camera's ceiling.
    final four = tester.widget<ChoiceChip>(
      find.byKey(const Key('rotation-assist-bin-4')),
    );
    expect(four.onSelected, isNull);
    await tester.tap(find.byKey(const Key('rotation-assist-bin-1')));
    await tester.pump();
    expect(tester.widget<TextField>(field).controller!.text, '4');
    await tester.tap(find.byKey(const Key('rotation-assist-start')));
    await tester.pump();
    expect(stub.starts.single.bin, 1);
    expect(stub.starts.single.exposure, 4.0);
  });

  testWidgets('Done is enabled once something solved and sends confirm', (
    tester,
  ) async {
    final (stubIdle, _) = await _pump(tester, idleWithDefault);
    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('rotation-assist-done')))
          .onPressed,
      isNull,
    );
    expect(stubIdle.confirms, 0);

    const solved = RotationAssistStatus(
      state: RotationAssistStates.stopped,
      targetPositionAngleDeg: 120,
      latest: RotationAssistSample(
        seq: 1,
        solvedPositionAngleDeg: 119.5,
        deltaDeg: 0.5,
      ),
      withinTolerance: true,
    );
    final (stub, _) = await _pump(
      tester,
      const RotationAssistLive(status: solved),
    );
    await tester.tap(find.byKey(const Key('rotation-assist-done')));
    await tester.pump();
    expect(stub.confirms, 1);
  });

  testWidgets('a dial moved away from the measured angle disables Done and '
      'marks the readout stale', (tester) async {
    const solved = RotationAssistStatus(
      state: RotationAssistStates.stopped,
      targetPositionAngleDeg: 10,
      latest: RotationAssistSample(
        seq: 1,
        solvedPositionAngleDeg: 9.5,
        deltaDeg: 0.5,
      ),
      withinTolerance: true,
    );
    final (stub, _) = await _pump(
      tester,
      const RotationAssistLive(status: solved),
      framing: const PlanFraming(
        on: true,
        raDeg: 314.8,
        decDeg: 44.5,
        rotationDeg: 40,
        name: 'NGC 7000',
      ),
    );
    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('rotation-assist-done')))
          .onPressed,
      isNull,
    );
    expect(find.byKey(const Key('rotation-assist-stale')), findsOneWidget);
    expect(find.textContaining('measure against 40°'), findsOneWidget);
    await tester.tap(
      find.byKey(const Key('rotation-assist-done')),
      warnIfMissed: false,
    );
    await tester.pump();
    expect(stub.confirms, 0);

    // A loop still running against the old angle shows the note too.
    await _pump(
      tester,
      const RotationAssistLive(
        status: RotationAssistStatus(
          active: true,
          state: RotationAssistStates.running,
          targetPositionAngleDeg: 10,
        ),
      ),
      framing: const PlanFraming(
        on: true,
        raDeg: 314.8,
        decDeg: 44.5,
        rotationDeg: 40,
        name: 'NGC 7000',
      ),
    );
    expect(find.byKey(const Key('rotation-assist-stale')), findsOneWidget);
  });

  testWidgets(
    'the confirmation outcomes read approved / not quite / checking',
    (tester) async {
      const check = RotationAssistSample(
        seq: 5,
        solvedPositionAngleDeg: 119.6,
        deltaDeg: 0.4,
      );
      await _pump(
        tester,
        const RotationAssistLive(
          status: RotationAssistStatus(
            state: RotationAssistStates.confirmed,
            targetPositionAngleDeg: 120,
            latest: check,
            confirmation: check,
            withinTolerance: true,
          ),
        ),
      );
      expect(find.text('Framing approved'), findsOneWidget);

      const off = RotationAssistSample(
        seq: 6,
        solvedPositionAngleDeg: 123,
        deltaDeg: -3,
      );
      await _pump(
        tester,
        const RotationAssistLive(
          status: RotationAssistStatus(
            state: RotationAssistStates.notConfirmed,
            targetPositionAngleDeg: 120,
            latest: off,
            confirmation: off,
          ),
        ),
      );
      expect(find.text('Not quite'), findsOneWidget);
      expect(find.textContaining('3.0° off'), findsOneWidget);

      await _pump(
        tester,
        const RotationAssistLive(
          status: RotationAssistStatus(
            state: RotationAssistStates.confirming,
            targetPositionAngleDeg: 120,
            latest: off,
          ),
        ),
      );
      expect(
        find.byKey(const Key('rotation-assist-confirming')),
        findsOneWidget,
      );
      expect(find.text('Checking at full resolution'), findsOneWidget);
    },
  );

  testWidgets('a bad exposure blocks the start', (tester) async {
    final (stub, _) = await _pump(tester, idleWithDefault);
    await tester.enterText(
      find.byKey(const Key('rotation-assist-exposure')),
      '90',
    );
    await tester.pump();
    expect(find.textContaining('between 0.01 and 60'), findsOneWidget);
    await tester.tap(find.byKey(const Key('rotation-assist-start')));
    await tester.pump();
    expect(stub.starts, isEmpty);
  });

  testWidgets('an exposure under the daemon\'s 0.01 s floor blocks the start', (
    tester,
  ) async {
    final (stub, _) = await _pump(tester, idleWithDefault);
    await tester.enterText(
      find.byKey(const Key('rotation-assist-exposure')),
      '0.005',
    );
    await tester.pump();
    expect(find.textContaining('between 0.01 and 60'), findsOneWidget);
    await tester.tap(find.byKey(const Key('rotation-assist-start')));
    await tester.pump();
    expect(stub.starts, isEmpty);
  });

  testWidgets('a running loop shows Stop and the readout with its frame', (
    tester,
  ) async {
    const latest = RotationAssistSample(
      seq: 2,
      solvedPositionAngleDeg: 100,
      deltaDeg: 20,
      frameWidth: 300,
      frameHeight: 200,
    );
    const status = RotationAssistStatus(
      active: true,
      state: RotationAssistStates.running,
      targetPositionAngleDeg: 120,
      toleranceDeg: 1,
      seq: 2,
      latest: latest,
      recent: [
        RotationAssistSample(seq: 1, solvedPositionAngleDeg: 90, deltaDeg: 30),
        latest,
      ],
      hasFrame: true,
      frameSeq: 2,
    );
    final (stub, _) = await _pump(
      tester,
      RotationAssistLive(status: status, frame: _pngBytes, frameSeq: 2),
    );
    expect(find.text('+20.0°'), findsOneWidget);
    expect(find.text('Keep going'), findsOneWidget);
    expect(find.byKey(const Key('rotation-assist-frame')), findsOneWidget);
    // Done is available mid-loop: the daemon stops the loop for the check.
    expect(
      tester
          .widget<FilledButton>(find.byKey(const Key('rotation-assist-done')))
          .onPressed,
      isNotNull,
    );
    await tester.tap(find.byKey(const Key('rotation-assist-stop')));
    await tester.pump();
    expect(stub.stops, 1);
  });

  testWidgets('a finished single shot keeps its result and offers another', (
    tester,
  ) async {
    const status = RotationAssistStatus(
      state: RotationAssistStates.stopped,
      mode: RotationAssistModes.single,
      exposureSeconds: 2,
      targetPositionAngleDeg: 120,
      latest: RotationAssistSample(
        seq: 1,
        solvedPositionAngleDeg: 119.5,
        deltaDeg: 0.5,
      ),
      withinTolerance: true,
    );
    await _pump(tester, const RotationAssistLive(status: status));
    expect(find.text('On target'), findsOneWidget);
    final btn = tester.widget<FilledButton>(
      find.byKey(const Key('rotation-assist-take-frame')),
    );
    expect(btn.onPressed, isNotNull);
  });

  testWidgets('close returns the sky to the plain view', (tester) async {
    final (_, container) = await _pump(tester, idleWithDefault);
    container
        .read(skyAtlasModeProvider.notifier)
        .set(SkyAtlasMode.rotateCamera);
    await tester.tap(find.byKey(const Key('rotation-assist-close')));
    await tester.pump();
    expect(container.read(skyAtlasModeProvider), SkyAtlasMode.catalogView);
  });
}
