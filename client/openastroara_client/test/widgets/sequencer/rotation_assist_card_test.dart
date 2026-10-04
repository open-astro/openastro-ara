import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/help/registry.dart';
import 'package:openastroara/models/rotation_assist.dart';
import 'package:openastroara/state/rotation/rotation_assist_state.dart';
import 'package:openastroara/widgets/help_icon.dart';
import 'package:openastroara/widgets/sequencer/rotation_assist_card.dart';

class _Stub extends RotationAssistNotifier {
  final RotationAssistLive initial;
  _Stub(this.initial);
  @override
  RotationAssistLive build() => initial;
  @override
  Future<void> refresh() async {}
}

Future<void> _pumpCard(WidgetTester tester, RotationAssistLive live) async {
  // A desktop-sized window: the card is a full-width band under the run band.
  tester.view.physicalSize = const Size(1400, 900);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  await tester.pumpWidget(_harness(live));
}

Widget _harness(RotationAssistLive live) => ProviderScope(
  overrides: [rotationAssistProvider.overrideWith(() => _Stub(live))],
  child: const MaterialApp(
    home: Scaffold(body: SizedBox(width: 1100, child: RotationAssistCard())),
  ),
);

// A 1×1 PNG so Image.memory has real bytes to decode.
final _pngBytes = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=',
);

void main() {
  testWidgets('hidden while no readout runs', (tester) async {
    await _pumpCard(tester, RotationAssistLive.idle);
    expect(find.byKey(const Key('rotation-assist-card')), findsNothing);
  });

  testWidgets('a running readout shows the delta, the advice and Resume', (
    tester,
  ) async {
    const latest = RotationAssistSample(
      seq: 2,
      solvedPositionAngleDeg: 287.5,
      deltaDeg: 11.5,
    );
    const status = RotationAssistStatus(
      active: true,
      state: 'running',
      targetPositionAngleDeg: 299,
      toleranceDeg: 1,
      seq: 2,
      latest: latest,
      recent: [
        RotationAssistSample(seq: 1, solvedPositionAngleDeg: 270, deltaDeg: 29),
        latest,
      ],
    );
    await _pumpCard(tester, const RotationAssistLive(status: status));
    expect(find.byKey(const Key('rotation-assist-card')), findsOneWidget);
    expect(find.text('+11.5°'), findsOneWidget);
    expect(find.text('Keep going'), findsOneWidget);
    expect(find.textContaining('target 299.0°'), findsOneWidget);
    expect(find.byKey(const Key('rotation-assist-resume')), findsOneWidget);
    expect(
      find.byKey(const Key('rotation-assist-show-on-sky')),
      findsOneWidget,
    );
  });

  testWidgets(
    'the solved frame shows with the overlay once the daemon has one',
    (tester) async {
      const latest = RotationAssistSample(
        seq: 3,
        solvedPositionAngleDeg: 287.5,
        deltaDeg: 11.5,
        frameWidth: 1024,
        frameHeight: 683,
      );
      const status = RotationAssistStatus(
        active: true,
        state: 'running',
        targetPositionAngleDeg: 299,
        seq: 3,
        latest: latest,
        recent: [latest],
        hasFrame: true,
        frameSeq: 3,
      );
      await tester.pumpWidget(
        _harness(
          RotationAssistLive(status: status, frame: _pngBytes, frameSeq: 3),
        ),
      );
      await tester.pump();
      expect(find.byKey(const Key('rotation-assist-frame')), findsOneWidget);
      expect(find.byKey(const Key('rotation-overlay')), findsOneWidget);
      expect(find.textContaining('the arrow is north'), findsOneWidget);
    },
  );

  testWidgets('the title carries a help icon for the readout', (tester) async {
    const status = RotationAssistStatus(
      active: true,
      state: 'running',
      targetPositionAngleDeg: 299,
      toleranceDeg: 1,
      seq: 1,
      latest: RotationAssistSample(
        seq: 1,
        solvedPositionAngleDeg: 287,
        deltaDeg: 12,
      ),
    );
    await _pumpCard(tester, const RotationAssistLive(status: status));
    final icon = tester.widget<HelpIcon>(find.byType(HelpIcon));
    expect(icon.helpKey, 'session.rotate_by_hand');
    expect(helpRegistry.containsKey(icon.helpKey), isTrue);
  });

  testWidgets('no frame yet: the card has no picture pane', (tester) async {
    const latest = RotationAssistSample(
      seq: 1,
      solvedPositionAngleDeg: 276,
      deltaDeg: 23,
    );
    const status = RotationAssistStatus(
      active: true,
      state: 'running',
      targetPositionAngleDeg: 299,
      seq: 1,
      latest: latest,
      recent: [latest],
    );
    await _pumpCard(tester, const RotationAssistLive(status: status));
    expect(find.byKey(const Key('rotation-assist-frame')), findsNothing);
  });
}
