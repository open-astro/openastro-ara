import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/sequencer/run_state_badge.dart';

void main() {
  test('state→color mapping is the canonical run palette', () {
    expect(RunStateBadge.colorFor(SequenceRunState.running), AraColors.accentConnected); // working: green (DESIGN.md status rule)
    expect(RunStateBadge.colorFor(SequenceRunState.starting), AraColors.accentInfo); // on its way: blue
    expect(RunStateBadge.colorFor(SequenceRunState.paused), AraColors.accentInfo);
    // DESIGN.md status rule: a plan paused waiting for you needs a look, nothing
    // failed: amber, as Simple Mode's plan pill.
    expect(RunStateBadge.colorFor(SequenceRunState.pausedAwaitingUser),
        AraColors.accentBusy);
    expect(RunStateBadge.colorFor(SequenceRunState.failed), AraColors.accentError);
    expect(RunStateBadge.colorFor(SequenceRunState.aborting), AraColors.accentError);
    expect(RunStateBadge.colorFor(SequenceRunState.completed),
        AraColors.textSecondary);
    expect(RunStateBadge.colorFor(SequenceRunState.stopped),
        AraColors.textSecondary);
  });

  test('needs-attention gets product copy, not the enum name', () {
    expect(RunStateBadge.labelFor(SequenceRunState.pausedAwaitingUser),
        'needs attention');
    expect(RunStateBadge.labelFor(SequenceRunState.running), 'running');
  });

  testWidgets('renders nothing for idle/null', (tester) async {
    await tester.pumpWidget(const MaterialApp(
        home: Row(children: [RunStateBadge(null), RunStateBadge(SequenceRunState.idle)])));
    expect(find.byType(Container), findsNothing);
  });

  testWidgets('#1596 a reason rides next to the state label', (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: Scaffold(
        body: RunStateBadge(SequenceRunState.completed,
            reason: 'Skipped, nothing was imaged: M31 did not run'),
      ),
    ));
    expect(find.textContaining('completed'), findsOneWidget);
    expect(find.textContaining('M31 did not run'), findsOneWidget);
  });

  test('#1596 the run state carries the reason from REST and clears it on a WS null', () {
    final rest = SequenceRunStateInfo.fromJson(const {
      'state': 'running', 'reason_code': 'meridian', 'reason_text': 'Passing meridian',
    });
    expect(rest.reasonText, 'Passing meridian');
    final done = rest.applyWsProgress(const {'reason_code': null, 'reason_text': null});
    expect(done.reasonCode, isNull);
    expect(rest.applyWsProgress(const {'state': 'running'}).reasonText, 'Passing meridian');
  });
}
