import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/rotation_assist.dart';
import 'package:openastroara/state/rotation/rotation_assist_state.dart';
import 'package:openastroara/widgets/sequencer/rotation_assist_card.dart';

class _Stub extends RotationAssistNotifier {
  final RotationAssistLive initial;
  _Stub(this.initial);
  @override
  RotationAssistLive build() => initial;
  @override
  Future<void> refresh() async {}
}

Widget _harness(RotationAssistLive live) => ProviderScope(
  overrides: [rotationAssistProvider.overrideWith(() => _Stub(live))],
  child: const MaterialApp(
    home: Scaffold(body: SizedBox(width: 1100, child: RotationAssistCard())),
  ),
);

void main() {
  testWidgets('hidden while no readout runs', (tester) async {
    await tester.pumpWidget(_harness(RotationAssistLive.idle));
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
    await tester.pumpWidget(_harness(const RotationAssistLive(status: status)));
    expect(find.byKey(const Key('rotation-assist-card')), findsOneWidget);
    expect(find.text('+11.5°'), findsOneWidget);
    expect(find.text('Keep going'), findsOneWidget);
    expect(find.textContaining('target 299.0°'), findsOneWidget);
    expect(find.byKey(const Key('rotation-assist-resume')), findsOneWidget);
  });
}
